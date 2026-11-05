using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Decrypt;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>
/// playerId 解析状态机（v2 报告 §2.7）。
///
/// <para>
/// playerId 是按玩家分类落盘的前提（<c>GameDataExport\{playerId}\</c>），
/// 解不出意味着后续流程全部受阻——<b>快速失败优于带病运行</b>：
/// 超时或解析失败即终止会话，不落盘、不降级。
/// </para>
///
/// <para>
/// 数据源：<c>15380 BasicInfoNotify</c> 的 <c>Id</c> 字段（field 15，int32）。
/// 反编译实证：<c>PlayerInfoController.BasicInfoNotifyHandle</c> 的
/// <c>instance.SetId(basicInfoNotify.Id)</c>；时序上登录后立即下发
/// （先于背包数据），密钥解出后 1 秒内必有。
/// </para>
///
/// <para>状态流转：</para>
/// <list type="bullet">
///   <item>WaitingKey：会话密钥未解出——不计时（没密钥什么都解不了，属正常等待）。</item>
///   <item>WaitingId：密钥已解、等 BasicInfoNotify——60s 超时判定在此期间运行。</item>
///   <item>Resolved：已取得 playerId（&gt;0）。会话内重登出现新的 BasicInfoNotify
///       时更新值并返回「已变更」，驱动落盘目录切换。</item>
///   <item>Failed：解析失败（两次）/ Id=0 / 超时——调用方终止会话。</item>
/// </list>
///
/// <para>
/// ★ 超时计时用调用方的推进节拍（<see cref="OnTick"/>），不持有定时器线程——
///   抓包循环每个数据报驱动一次，无新增线程。
/// </para>
/// </summary>
public sealed class PlayerIdResolver
{
    /// <summary>密钥解出后等待 BasicInfoNotify 的超时（正常 1s 内必有，60s 为宽限）。</summary>
    private const int WaitForIdTimeoutSeconds = 60;

    /// <summary>解析失败重试上限（第 1 次失败可能是半帧拼接，第 2 次失败即终止）。</summary>
    private const int MaxParseFailures = 2;

    /// <summary>疑似 BasicInfoNotify 的帧但解析失败时，缓存载荷供重试（半帧等后续）。</summary>
    private byte[]? _pendingPayload;

    public enum Phase
    {
        /// <summary>会话密钥未解出，无法解析任何加密帧——不计时。</summary>
        WaitingKey,

        /// <summary>密钥已解，等待 BasicInfoNotify（受 60s 超时约束）。</summary>
        WaitingId,

        /// <summary>已取得 playerId。</summary>
        Resolved,

        /// <summary>快速失败：调用方必须终止会话（不落盘）。</summary>
        Failed,
    }

    /// <summary>当前阶段。</summary>
    public Phase Current { get; private set; } = Phase.WaitingKey;

    /// <summary>已取得的 playerId（<see cref="Current"/> 为 Resolved 前无意义）。</summary>
    public int PlayerId { get; private set; }

    /// <summary>失败原因（<see cref="Current"/> 为 Failed 时有值，用于进度文案）。</summary>
    public string? FailureReason { get; private set; }

    private long _keyResolvedTick;
    private int _parseFailures;

    /// <summary>会话密钥已解出——开始 60s 计时（由 TryAdvance 在首次解出时调用一次）。</summary>
    public void NotifyKeyResolved()
    {
        if (Current == Phase.WaitingKey)
        {
            Current = Phase.WaitingId;
            _keyResolvedTick = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 重登衔接：新会话开始（新 112 解出新密钥）——回到等待新 BasicInfoNotify。
    /// 已持有的旧 playerId 继续可用（旧目录已落盘），但新数据要等新 id 归档。
    /// </summary>
    public void NotifyRelogin()
    {
        Current = Phase.WaitingId;
        _keyResolvedTick = Environment.TickCount64;
        _pendingPayload = null;
        _parseFailures = 0;
        FailureReason = null;
        // PlayerId 保留旧值：新的 id 解析成功前，仅供历史展示，新数据必须等 Current=Resolved 才能落盘
    }

    /// <summary>
    /// 抓包循环节拍：WaitingId 阶段检查 60s 超时。
    /// </summary>
    /// <returns>进入 <see cref="Phase.Failed"/> 时返回 false（调用方终止会话）。</returns>
    public bool OnTick()
    {
        if (Current != Phase.WaitingId)
            return true;

        var elapsedSeconds = (Environment.TickCount64 - _keyResolvedTick) / 1000;
        if (elapsedSeconds >= WaitForIdTimeoutSeconds)
        {
            Fail($"等待 BasicInfoNotify 超时（{elapsedSeconds} 秒）。请确认游戏为最新版本；若持续出现请反馈。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 喂入一个已解密的帧：msgId 匹配 BasicInfoNotify 时尝试提取 playerId。
    /// </summary>
    /// <returns>进入 <see cref="Phase.Failed"/> 时返回 false（调用方终止会话）。</returns>
    public bool TryFeed(GatewayFrame frame)
    {
        if (Current == Phase.Failed)
            return false;

        if (frame.MsgId != MsgIdFilter.BasicInfoNotify)
            return true;

        // 半帧拼接：上次疑似失败的载荷与本次相同长度时合并重试——
        // 简化处理：直接以本帧为准重试（KCP 重组保证帧完整性，重组后的帧不会是半帧；
        // 解析失败只可能是字段异常）。保留计数即可。
        var parseFail = !TryParseId(frame.Payload, out var playerId);
        if (parseFail)
        {
            _parseFailures++;
            if (_parseFailures >= MaxParseFailures)
            {
                Fail("BasicInfoNotify 解析失败（重试后仍失败）。请确认游戏为最新版本；若持续出现请反馈。");
                return false;
            }
            return true; // 再等一条 BasicInfoNotify（服务器会随状态变更重发）
        }

        if (playerId <= 0)
        {
            // int32 默认值 = 非法 playerId（proto3 未序列化该字段或数据异常）
            _parseFailures++;
            if (_parseFailures >= MaxParseFailures)
            {
                Fail("BasicInfoNotify 的 playerId 为 0（非法值）。请确认游戏为最新版本；若持续出现请反馈。");
                return false;
            }
            return true;
        }

        _pendingPayload = null;
        _parseFailures = 0;
        PlayerId = playerId;
        Current = Phase.Resolved;
        return true;
    }

    /// <summary>解析 BasicInfoNotify 载荷，提取 field 15 的 Id。</summary>
    private static bool TryParseId(ReadOnlySpan<byte> payload, out int playerId)
    {
        playerId = 0;
        try
        {
            var parsed = BasicInfoNotify.Parser.ParseFrom(payload);
            playerId = parsed.Id;
            return true;
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private void Fail(string reason)
    {
        FailureReason = reason;
        Current = Phase.Failed;
    }
}
