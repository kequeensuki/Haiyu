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
///
/// ★ 内存纪律（v2 持续监控）：
///   ① <see cref="Add"/> 对长存的 PUSH 段做一次拷贝（解析输入是 ArrayPool 租借
///      缓冲的切片，数据报处理完即归还——流必须拥有自己的内存）；
///   ② <see cref="ConsumedBytes"/> 水位 + 增量视图 <see cref="BuildIncremental"/> +
///      <see cref="Compact"/>：长会话下每轮只解析新字节、存储只保留未消费部分，
///      不再无界增长（v1 的 Build() 全量重拼是 O(N²)，持续监控下不可运行）；
///   ③ 会话结束时调用 <see cref="Clear"/>（宿主常驻进程，防残留引用）。
///
/// ★ 虚拟流模型：全部段按 sn 升序依次排布成一条逻辑流；水位是逻辑流上的
///   字节位移。断档（sn 缺失）不阻塞——与 v1 的 Build() 语义一致（按现有段拼），
///   缺失段重传到达后其内容自然并入后续视图。
/// </summary>
public sealed class KcpStreamRebuilder
{
    private readonly Dictionary<uint, byte[]> _segments = new();
    private readonly object _gate = new();
    private long _consumedBytes;

    /// <summary>已收到的不同 sn 数量。</summary>
    public int SegmentCount
    {
        get
        {
            lock (_gate)
                return _segments.Count;
        }
    }

    /// <summary>
    /// 已解析消费的水位（逻辑流字节位移）。
    /// 调用方解析完 <see cref="BuildIncremental"/> 的视图后调用
    /// <see cref="AdvanceConsumed"/> 推进、<see cref="Compact"/> 收缩存储。
    /// </summary>
    public long ConsumedBytes
    {
        get
        {
            lock (_gate)
                return _consumedBytes;
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
            if (_segments.ContainsKey(segment.Sn))
                return;

            // ★ 长存拷贝：解析输入是租借缓冲的切片，流必须拥有独立内存。
            //   （ACK/WASK/WINS 与重复段已在此前排除，零分配。）
            _segments[segment.Sn] = segment.Payload.ToArray();
        }
    }

    /// <summary>
    /// 按 sn 升序拼出水位之后的全部字节（增量视图）。
    /// 没有新数据返回空数组。调用方解析完应
    /// <see cref="AdvanceConsumed"/>(视图长度) → <see cref="Compact"/>。
    /// </summary>
    public byte[] BuildIncremental()
    {
        lock (_gate)
        {
            var (buffer, _) = BuildIncrementalLocked();
            return buffer;
        }
    }

    /// <summary>
    /// 按 sn 升序拼出完整字节流（v1 语义保留：pcap 回放工具与一次性导出用）。
    /// 监控循环的取数路径请用 <see cref="BuildIncremental"/>。
    /// </summary>
    public byte[] Build()
    {
        lock (_gate)
        {
            KeyValuePair<uint, byte[]>[] snapshot = _segments.OrderBy(pair => pair.Key).ToArray();
            var total = snapshot.Sum(pair => (long)pair.Value.Length);
            if (total == 0)
                return [];

            var buffer = new byte[total];
            var offset = 0;
            foreach (var (_, payload) in snapshot)
            {
                Buffer.BlockCopy(payload, 0, buffer, offset, payload.Length);
                offset += payload.Length;
            }

            return buffer;
        }
    }

    /// <summary>
    /// 按 sn 升序拼出「指定水位之后」的字节视图（外部水位驱动版，v2 监控循环用）。
    /// 与 <see cref="BuildIncremental"/>（内部水位）不同：水位由调用方持有
    /// （PumpState.Watermarks），解析完成由调用方推进——流本身保持无状态水位。
    /// 没有新数据返回空数组。
    /// </summary>
    public byte[] BuildViewFrom(long watermark)
    {
        lock (_gate)
        {
            var ordered = _segments.OrderBy(pair => pair.Key).ToArray();

            long cursor = 0;
            var copyFrom = -1;
            var trimBytes = 0L;
            for (var i = 0; i < ordered.Length; i++)
            {
                var start = cursor;
                var end = cursor + ordered[i].Value.Length;
                cursor = end;

                if (end <= watermark)
                    continue;

                copyFrom = i;
                trimBytes = Math.Max(0, watermark - start);
                break;
            }

            if (copyFrom < 0)
                return [];

            var selected = ordered[copyFrom..];
            var total = selected.Sum(pair => (long)pair.Value.Length) - trimBytes;
            if (total <= 0)
                return [];

            var buffer = new byte[total];
            var offset = 0;
            for (var i = 0; i < selected.Length; i++)
            {
                var payload = selected[i].Value;
                var skip = i == 0 ? (int)trimBytes : 0;
                if (skip >= payload.Length)
                    continue;

                Buffer.BlockCopy(payload, skip, buffer, offset, payload.Length - skip);
                offset += payload.Length - skip;
            }

            return buffer;
        }
    }

    /// <summary>把水位推进 <paramref name="bytes"/> 字节。</summary>
    public void AdvanceConsumed(long bytes)
    {
        lock (_gate)
            _consumedBytes += bytes;
    }

    /// <summary>
    /// 收缩存储：丢弃<b>整体</b>位于水位之前的段（跨界段保留——半帧还在等后续）。
    /// 被丢弃的前缀字节数同步从水位中扣除（坐标系平移：幸存段从新 0 起算）。
    /// </summary>
    public void Compact()
    {
        lock (_gate)
        {
            var ordered = _segments.OrderBy(pair => pair.Key).ToArray();
            long cursor = 0;
            long droppedBytes = 0;
            _segments.Clear();
            foreach (var (sn, payload) in ordered)
            {
                var end = cursor + payload.Length;
                cursor = end;

                if (end <= _consumedBytes)
                {
                    droppedBytes += payload.Length; // 整体已消费，丢弃并平移坐标系
                    continue;
                }

                _segments[sn] = payload;
            }

            _consumedBytes -= droppedBytes;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _segments.Clear();
            _consumedBytes = 0;
        }
    }

    /// <summary>水位之后的新增视图（调用方须持锁）。跨界段从水位处裁剪起返回。</summary>
    private (byte[] Buffer, long Consumed) BuildIncrementalLocked()
    {
        var ordered = _segments.OrderBy(pair => pair.Key).ToArray();

        // 定位水位：跳过整体已消费的段，跨界段从段内偏移裁剪
        long cursor = 0;
        var copyFrom = -1;
        var trimBytes = 0L;
        for (var i = 0; i < ordered.Length; i++)
        {
            var start = cursor;
            var end = cursor + ordered[i].Value.Length;
            cursor = end;

            if (end <= _consumedBytes)
                continue; // 整体已消费

            copyFrom = i;
            trimBytes = Math.Max(0, _consumedBytes - start); // 跨界段裁掉水位前部分
            break;
        }

        if (copyFrom < 0)
            return ([], _consumedBytes); // 全部都在水位之前

        var selected = ordered[copyFrom..];
        var total = selected.Sum(pair => (long)pair.Value.Length) - trimBytes;
        if (total <= 0)
            return ([], _consumedBytes);

        var buffer = new byte[total];
        var offset = 0;
        for (var i = 0; i < selected.Length; i++)
        {
            var payload = selected[i].Value;
            var skip = i == 0 ? (int)trimBytes : 0;
            if (skip >= payload.Length)
                continue; // 首段被完全裁剪（理论上不会：end<=consumed 已被跳过）

            Buffer.BlockCopy(payload, skip, buffer, offset, payload.Length - skip);
            offset += payload.Length - skip;
        }

        return (buffer, _consumedBytes);
    }
}
