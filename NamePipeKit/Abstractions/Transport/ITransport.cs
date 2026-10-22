// ════════════════════════════════════════════════════════════════════════════
//  文件：ITransport.cs
//  作用：传输层抽象。
//  IPipeClientTransport 提供两个发送方法：
//    - SendAndReceiveAsync：普通请求，返回一个响应帧
//    - SendStreamAsync：流式请求，chunk 通过回调流式交付
//  两者走同一个请求队列，单 Worker 线程串行处理。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.Abstractions.Transport
{
    public interface IPipeConnection : IDisposable
    {
        string ConnectionId { get; }
        Task<PipeFrame> ReadFrameAsync(CancellationToken ct);
        Task WriteFrameAsync(PipeFrame frame, CancellationToken ct);
        void SwitchCodec(IFrameCodec codec);
    }

    public interface IPipeServerTransport : IDisposable
    {
        Task ListenAsync(Func<IPipeConnection, CancellationToken, Task> connectionHandler,
            CancellationToken cancellationToken);
    }

    public interface IPipeClientTransport : IDisposable
    {
        Task ConnectAsync(CancellationToken ct);
        Task<PipeFrame> SendAndReceiveAsync(PipeFrame frame, CancellationToken ct);

        /// <summary>流式请求：chunk 通过 onChunk 回调交付，返回 Task 在流结束时完成。</summary>
        Task SendStreamAsync(PipeFrame frame, Action<byte[]> onChunk, CancellationToken ct);
    }
}