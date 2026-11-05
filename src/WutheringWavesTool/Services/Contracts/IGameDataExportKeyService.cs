namespace Haiyu.Services.Contracts;

/// <summary>校验用户提供的 RSA 私钥 PEM 文件，并保存为当前 Windows 用户的普通凭据。</summary>
public interface IGameDataExportKeyService
{
    /// <summary>成功后替换已有密钥；取消或校验失败不写入凭据。不返回私钥内容。</summary>
    Task ImportAsync(string filePath, CancellationToken token = default);
}
