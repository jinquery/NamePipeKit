// ════════════════════════════════════════════════════════════════════════════
//  文件：MessageHandler.cs
//  作用：处理 ContentType = "MESSAGE"（纯消息）。
//  通过事件把消息分发给订阅者；没有订阅者时直接返回成功。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.Handling
{
    /// <summary>MESSAGE 处理器。</summary>
    public sealed class MessageHandler : IPipeRequestHandler
    {
        /// <summary>消息到达事件：(sender, header, data)。</summary>
        public event Action<string, string, string> MessageReceived;

        public string ContentType { get { return PipeContentTypes.Message; } }

        public Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
        {
            try
            {
                var h = MessageReceived;
                if (h != null) h(request.Sender ?? "", request.MessageHeader, request.Data);
                return Task.FromResult(PipeResponse.Ok());
            }
            catch (Exception ex)
            {
                return Task.FromResult(PipeResponse.Fail(PipeStatusCode.ServerError, ex.Message));
            }
        }
    }
}