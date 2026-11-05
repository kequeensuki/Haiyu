namespace Haiyu.ABI.Services.GameDataExport.Rebuild;

/// <summary>
/// 标准 CRC-32（多项式 0xEDB88320，反射），等同 zlib.crc32。
///
/// 游戏用它做 KCP 段尾校验（握手响应里的 <c>crcCheck</c> 开启时）。
/// 多项式与算法经 OfflineClient 的 IDA 实证（<c>sub_142EA5360</c>）。
/// </summary>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return ~crc;
    }
}
