using System.Diagnostics.CodeAnalysis;
using ABI.Models;
using ABIRuntime.Abstractions;
using MemoryPack;

namespace ABIRuntime.Runtime;

/// <summary>上下层转递：MemoryPack 请求 → 强类型 Service → 二进制进度/响应。</summary>
internal sealed class ServiceInvoker<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TProgress,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse
>(IPrivilegedService<TRequest, TProgress, TResponse> service) : IServiceInvoker
    where TResponse : class
{
    public async ValueTask InvokeAsync(
        Stream pipe,
        PipeMessage request,
        CancellationToken cancellationToken
    )
    {
        ABIMemoryPack.EnsureFormatters();
        TRequest input = MemoryPackSerializer.Deserialize<TRequest>(
            request.Payload,
            ABIMemoryPack.Options
        )!;

        if (input is null)
            throw new InvalidDataException("Service 请求数据为空。");

        var progress = new HostProgress<TProgress>(pipe, request);

        TResponse output = await service
            .ExecuteAsync(input, progress, cancellationToken)
            .ConfigureAwait(false);

        byte[] payload = MemoryPackSerializer.Serialize(output, ABIMemoryPack.Options);
        // 业务取消可表示“收尾并返回”。取消后仍给最终结果 2 秒发送宽限，
        // 小于客户端的 5 秒宽限；正常结果不新增超时。取消在写入中发生也适用。
        using var resultTimeout = new CancellationTokenSource();
        using var stopRegistration = cancellationToken.Register(
            () => resultTimeout.CancelAfter(TimeSpan.FromSeconds(2)));
        await PipeProtocol
            .WriteAsync(
                pipe,
                new PipeMessage(
                    PipeMessageKind.Result,
                    request.RequestId,
                    request.Version,
                    request.Operation,
                    payload,
                    100,
                    "操作完成"
                ),
                resultTimeout.Token
            )
            .ConfigureAwait(false);
    }
}
