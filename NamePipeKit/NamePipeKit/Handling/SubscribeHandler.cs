// ════════════════════════════════════════════════════════════════════════════
//  文件：SubscribeHandler.cs
//  作用：处理 "SUBSCRIBE" / "UNSUBSCRIBE" 两种 ContentType。
//  关键：把 ctx.Connection 加入/移除 EventBus 订阅表。
//  后续业务侧 EventBus.Publish(topic, data) 时即可向该连接推 ServerPush。
// ════════════════════════════════════════════════════════════════════════════
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.Handling
{
    /// <summary>订阅处理器。</summary>
    public sealed class SubscribeHandler : IPipeRequestHandler
    {
        private readonly EventBus _bus;
        public SubscribeHandler(EventBus bus) { _bus = bus; }
        public string ContentType { get { return PipeContentTypes.Subscribe; } }

        public Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
        {
            var topic = request.Data;
            if (string.IsNullOrEmpty(topic))
                return Task.FromResult(PipeResponse.Fail(PipeStatusCode.BadRequest, "缺少 topic"));
            _bus.Subscribe(topic, ctx.Connection);
            return Task.FromResult(PipeResponse.Ok(new { subscribed = topic }));
        }
    }

    /// <summary>取消订阅处理器。</summary>
    public sealed class UnsubscribeHandler : IPipeRequestHandler
    {
        private readonly EventBus _bus;
        public UnsubscribeHandler(EventBus bus) { _bus = bus; }
        public string ContentType { get { return PipeContentTypes.Unsubscribe; } }

        public Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
        {
            var topic = request.Data;
            if (!string.IsNullOrEmpty(topic))
                _bus.Unsubscribe(topic, ctx.Connection);
            return Task.FromResult(PipeResponse.Ok());
        }
    }
}