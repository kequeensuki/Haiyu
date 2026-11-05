using WavesProtocolGenerator;

if (args.Contains("--self-test"))
    return SelfTests.Run();
if (args.Contains("--help") || args.Length == 0)
{
    Console.WriteLine("WavesProtocolGenerator --source <DecompiledSource/Aki.Protocol> --output <Haiyu.ABI/Protocol> --commit <source commit> --protoc <protoc executable> [--check]");
    return args.Length == 0 ? 2 : 0;
}

try
{
    string Get(string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + option);
    }

    var model = new SourceReader().Read(Path.GetFullPath(Get("--source")), Get("--commit"));
    var output = Path.GetFullPath(Get("--output"));
    var files = Emitter.Render(model);
    var staging = Path.Combine(Path.GetTempPath(), "haiyu-protocol-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);
    try
    {
        File.WriteAllText(Path.Combine(staging, "WavesGameData.proto"), files["WavesGameData.proto"]);
        var start = new System.Diagnostics.ProcessStartInfo(Get("--protoc"))
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--proto_path=" + staging);
        start.ArgumentList.Add("--descriptor_set_out=" + Path.Combine(staging, "schema.pb"));
        start.ArgumentList.Add("WavesGameData.proto");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot launch protoc");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException("protoc validation failed: " + error);
    }
    finally
    {
        Directory.Delete(staging, true);
    }

    var changed = files.Where(p => !File.Exists(Path.Combine(output, p.Key)) || File.ReadAllText(Path.Combine(output, p.Key)).Replace("\r\n", "\n") != p.Value).Select(p => p.Key).ToArray();
    Console.WriteLine($"Messages={model.Messages.Length}, enums={model.Enums.Length}, fields={model.Messages.Sum(m => m.Fields.Length)}, registered={model.Ids.Count}, source={model.SourceHash}");
    if (args.Contains("--check"))
    {
        foreach (var file in changed)
            Console.WriteLine("DIFF " + file);
        return changed.Length == 0 ? 0 : 1;
    }

    if (changed.Length == 0)
    {
        Console.WriteLine("Artifacts up to date");
        return 0;
    }

    files = files.Where(p => changed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
    Directory.CreateDirectory(output);
    var previous = files.Keys.ToDictionary(name => name, name => File.Exists(Path.Combine(output, name)) ? File.ReadAllBytes(Path.Combine(output, name)) : null);
    try
    {
        foreach (var(name, text)in files)
            File.WriteAllText(Path.Combine(output, name + ".tmp"), text, new System.Text.UTF8Encoding(false));
        foreach (var name in files.Keys)
            File.Move(Path.Combine(output, name + ".tmp"), Path.Combine(output, name), true);
    }
    catch
    {
        foreach (var(name, bytes)in previous)
            if (bytes != null)
                File.WriteAllBytes(Path.Combine(output, name), bytes);
            else
                File.Delete(Path.Combine(output, name));
        throw;
    }
    finally
    {
        foreach (var name in files.Keys)
            File.Delete(Path.Combine(output, name + ".tmp"));
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
