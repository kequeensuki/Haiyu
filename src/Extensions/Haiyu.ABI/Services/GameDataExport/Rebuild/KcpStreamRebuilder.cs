namespace Haiyu.ABI.Services.GameDataExport.Rebuild;

/// <summary>
/// 把一个方向的 KCP 段拼成连续字节流。
///
/// ★ 范围说明：这里<b>不做</b>连接维护——没有窗口、没有重传、没有 ACK、没有超时。
///   只做三件事：按 <c>sn</c> 排序、按 <c>sn</c> 去重、顺序拼接。
///   KCP 的重传会让同一 sn 出现多次，必须去重，否则流会错位（实测出站 216 段中
///   有 11 个重复 sn，不去重会导致后续全部帧切分失败）。
///
/// ★ 每个方向必须用独立实例：出站与入站各有自己的 sn 序列。
/// </summary>
public sealed class KcpStreamRebuilder
{
    private readonly Dictionary<uint, byte[]> _segments = new();
    private readonly object _gate = new();

    /// <summary>已收到的不同 sn 数量。</summary>
    public int SegmentCount
    {
        get
        {
            lock (_gate)
                return _segments.Count;
        }
    }

    /// <summary>放入一个 PUSH 段（其它 cmd 自动忽略）。</summary>
    public void Add(KcpSegment segment)
    {
        if (segment.Cmd != KcpSegment.CmdPush)
            return;

        if (segment.Payload.Length == 0)
            return;

        lock (_gate)
        {
            // 重复 sn（重传）保留首次到达的那份
            _segments.TryAdd(segment.Sn, segment.Payload);
        }
    }

    /// <summary>
    /// 按 sn 升序拼出完整字节流。
    /// 每次都从头重建——数据量不大（一次会话几十 KB），比维护增量状态可靠。
    /// </summary>
    public byte[] Build()
    {
        KeyValuePair<uint, byte[]>[] snapshot;
        lock (_gate)
            snapshot = _segments.OrderBy(pair => pair.Key).ToArray();

        var total = snapshot.Sum(pair => (long)pair.Value.Length);
        if (total == 0)
            return [];

        var buffer = new byte[total];
        var offset = 0;
        foreach (var (_, payload) in snapshot)
        {
            Array.Copy(payload, 0, buffer, offset, payload.Length);
            offset += payload.Length;
        }

        return buffer;
    }

    public void Clear()
    {
        lock (_gate)
            _segments.Clear();
    }
}
