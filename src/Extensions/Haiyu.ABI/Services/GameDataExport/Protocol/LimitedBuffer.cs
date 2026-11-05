namespace Haiyu.ABI.Services.GameDataExport.Protocol;

/// <summary>约束单条消息展开后的 JSON 大小，避免有界队列之外的序列化临时缓冲无限增长。</summary>
internal sealed class OutputLimitException(string message) : IOException(message);

internal sealed class LimitedBuffer(int maximum) : MemoryStream
{
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Position + buffer.Length > maximum) throw new OutputLimitException("json_size_limit");
        base.Write(buffer);
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Position + count > maximum) throw new OutputLimitException("json_size_limit");
        base.Write(buffer, offset, count);
    }
}
