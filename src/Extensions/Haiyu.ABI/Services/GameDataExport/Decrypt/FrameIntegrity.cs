using Haiyu.ABI.Services.GameDataExport.Rebuild;

namespace Haiyu.ABI.Services.GameDataExport.Decrypt;

/// <summary>业务帧校验独立于 KCP 数据报 CRC；压缩帧的 OrigSize 已由切帧器剥离。</summary>
internal static class FrameIntegrity
{
    internal static bool IsValid(GatewayFrame frame) =>
        frame.Type == GatewayMessageType.Exception || frame.Crc == Crc32.Compute(frame.Payload);
}
