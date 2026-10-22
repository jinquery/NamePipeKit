// ════════════════════════════════════════════════════════════════════════════
//  文件：IPipeHandling.cs
//  作用：请求处理核心抽象——Handler、Middleware、Context。
//  设计原则：
//    - Handler 按 ContentType 路由，新增类型 = 新增类；
//    - Middleware 洋葱模型，横切能力（鉴权/限流/日志）通过中间件注入；
//    - Context 提供当前连接引用，让 Handler 可主动推送。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Abstractions.Transport;

namespace NamePipeKit.Abstractions.Handling
{
    /// <summary>
    /// 请求处理器。每种 ContentType 对应一个 Handler。
    /// <para><b>扩展方式</b>：新增 ContentType = 实现本接口 + AddHandler 注册。</para>
    /// </summary>
    public interface IPipeRequestHandler
    {
        /// <summary>本 Handler 处理的 ContentType（与 PipeRequest.ContentType 对应）。</summary>
        string ContentType { get; }

        /// <summary>业务处理入口。</summary>
        Task<PipeResponse> HandleAsync(
            PipeRequest request, PipeContext context, CancellationToken cancellationToken);
    }

    /// <summary>管道中间件（洋葱模型）。用于鉴权、限流、日志、耗时统计等。</summary>
    public interface IPipeMiddleware
    {
        /// <summary>
        /// 中间件执行逻辑。
        /// <para>必须调用 <c>await next(ctx)</c> 才能继续执行内层，返回值即为整个管道的结果。</para>
        /// </summary>
        Task<PipeResponse> InvokeAsync(PipeContext context, PipeRequestDelegate next);
    }

    /// <summary>中间件链的委托。</summary>
    public delegate Task<PipeResponse> PipeRequestDelegate(PipeContext context);

    /// <summary>
    /// 请求处理上下文。贯穿整条管道，供 Handler / Middleware 共享数据。
    /// </summary>
    public sealed class PipeContext
    {
        /// <summary>当前连接 ID。</summary>
        public string ConnectionId { get; private set; }

        /// <summary>
        /// 【关键】当前连接引用。
        /// <para>
        /// Handler 可以用它主动推送 ServerPush 帧（比如订阅后立即推一条欢迎消息）。
        /// 这也是 SubscribeHandler 能把自己加入 EventBus 的前提。
        /// </para>
        /// </summary>
        public IPipeConnection Connection { get; private set; }

        /// <summary>本次请求的强类型视图。</summary>
        public PipeRequest Request { get; private set; }

        /// <summary>服务实例工厂（默认 Activator.CreateInstance，可注入 DI）。</summary>
        public Func<Type, object> ServiceFactory { get; private set; }

        /// <summary>日志器。</summary>
        public IPipeLogger Logger { get; private set; }

        /// <summary>追踪 ID，贯穿一次请求全链路。</summary>
        public string TraceId { get; private set; }

        /// <summary>中间件之间共享的临时数据（如 TimingMiddleware 的耗时）。</summary>
        public Dictionary<string, object> Items { get; private set; }

        public PipeContext(string connectionId, IPipeConnection connection, PipeRequest request,
            Func<Type, object> serviceFactory, IPipeLogger logger, string traceId)
        {
            ConnectionId = connectionId;
            Connection = connection;
            Request = request;
            ServiceFactory = serviceFactory;
            Logger = logger;
            TraceId = traceId;
            Items = new Dictionary<string, object>();
        }
    }
}