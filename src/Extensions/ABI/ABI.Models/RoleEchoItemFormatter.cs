using MemoryPack;

namespace ABI.Models;

/// <summary>
/// 使用已注册实例 formatter：MemoryPack 1.21.4 的嵌套 WritePackable 静态接口分派
/// 在 .NET 10 Linux NativeAOT 下会崩溃（角色声骸引用 GamePhantomItem 路径）。
/// 不改变 wire 格式；空槽与完整实例均通过相同的 GamePhantomItem formatter 处理。
/// </summary>
internal sealed class RoleEchoItemFormatterAttribute : MemoryPackCustomFormatterAttribute<RoleEchoItemFormatter, GamePhantomItem>
{
    public override RoleEchoItemFormatter GetFormatter() => new();
}

internal sealed class RoleEchoItemFormatter : MemoryPackFormatter<GamePhantomItem>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref GamePhantomItem? value) =>
        writer.WriteValue(value);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref GamePhantomItem? value) =>
        reader.ReadValue(ref value);
}
