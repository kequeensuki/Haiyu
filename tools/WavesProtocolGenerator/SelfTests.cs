namespace WavesProtocolGenerator;
internal static class SelfTests
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-generator-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string fixture = """
            namespace Aki.Protocol {
              public enum EMessageId { Probe = 123 }
              public enum EdgeValues { Minimum = -2147483648, Zero = 0, Alias = 0 }
              public class NetDefine {
                public static IMessage CreateMessageById(int id) { switch(id >> 10) { case 0: return NetDefine.CreateRange_0(id); default: return null; } }
                private static IMessage CreateRange_0(int id) { switch(id) { case 123: return CreateMessage_123(); default: return null; } }
                private static IMessage CreateMessage_123() { return new Probe(); }
              }
              [NetMessage(EMessageId.Probe)] public sealed class Probe : IMessage<Probe> {
                // public const int FakeFieldNumber = 9;
                public const int ValueFieldNumber = 1;
                public int Value { get; set; }
                public bool HasValue { get; }
                void InternalMergeFrom() { this.Value = input.ReadSInt32(); }
                void InternalWriteTo() { output.WriteRawTag(8); output.WriteSInt32(this.Value); }
              }
            }
            """;
        try
        {
            var file = Path.Combine(root, "Probe.cs");
            File.WriteAllText(file, fixture);
            var model = new SourceReader().Read(root, "test");
            if (model.Messages.Single().Fields.Single().Type != "sint32" || !model.Messages[0].Fields[0].Optional)
                throw new Exception("scalar/presence extraction failed");
            if (model.Enums.Single().Values[0].Number != int.MinValue) throw new Exception("negative enum boundary failed");
            var first = Emitter.Render(model);
            File.WriteAllText(file, fixture.Replace("\n", "\r\n"));
            var second = Emitter.Render(new SourceReader().Read(root, "test"));
            if (!first.OrderBy(p => p.Key).SequenceEqual(second.OrderBy(p => p.Key)))
                throw new Exception("line ending reproducibility failed");
            File.WriteAllText(file, fixture.Replace("WriteRawTag(8)", "WriteRawTag(16)"));
            var rejected = false;
            try
            {
                new SourceReader().Read(root, "test");
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            if (!rejected)
                throw new Exception("inconsistent write tag accepted");
            File.WriteAllText(file, fixture.Replace("ReadSInt32()", "ReadInt32()"));
            rejected = false;
            try
            {
                new SourceReader().Read(root, "test");
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            if (!rejected)
                throw new Exception("inconsistent wire encoding accepted");
            File.WriteAllText(file, fixture.Replace("switch(id) { case 123: return CreateMessage_123(); default: return null; }",
                "if (id != 123) { return null; } else { return CreateMessage_123(); }"));
            if (!new SourceReader().Read(root, "test").Ids.SequenceEqual(model.Ids))
                throw new Exception("conditional factory route extraction failed");
            var aliases = fixture.Replace("Probe = 123", "Probe = 123, SecondProbe = 124")
                .Replace("[NetMessage(EMessageId.Probe)]", "[NetMessage(EMessageId.Probe), NetMessage(EMessageId.SecondProbe)]")
                .Replace("case 123: return CreateMessage_123();", "case 124: return CreateMessage_124(); case 123: return CreateMessage_123();")
                .Replace("private static IMessage CreateMessage_123()", "private static IMessage CreateMessage_124() { return new Probe(); } private static IMessage CreateMessage_123()");
            File.WriteAllText(file, aliases);
            var aliasModel = new SourceReader().Read(root, "test");
            if (aliasModel.Ids.Count != 2 || !Emitter.Render(aliasModel)["WavesMessageIds.cs"].Contains("SecondProbe = 124"))
                throw new Exception("multiple IDs for one type were lost");
            Console.WriteLine("PASS Roslyn syntax, encoding/tag cross-check, presence, deterministic CRLF/LF generation");
            return 0;
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
