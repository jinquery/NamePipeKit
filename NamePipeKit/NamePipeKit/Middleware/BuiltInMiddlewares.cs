// ════════════════════════════════════════════════════════════════════════════
//  文件：BuiltInMiddlewares.cs
//  作用：内置三件套中间件（异常兜底 / 日志 / 计时）。
//  执行顺序（由外到内）：Exception → Logging → Timing → Handler。
//  扩展：新增中间件实现 IPipeMiddleware + AddMiddleware。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.Middleware
{
    /// <summary>异常兜底：把任何未处理异常转为标准错误响应，避免连接被破坏。</summary>
    public sealed class ExceptionMiddleware : IPipeMiddleware
    {
        public async Task<PipeResponse> InvokeAsync(PipeContext ctx, PipeRequestDelegate next)
        {
            try { return await next(ctx).ConfigureAwait(false); }
            catch (Exception ex)
            {
                ctx.Logger.Error("[" + ctx.TraceId + "] 未处理异常", ex);
                return PipeResponse.Fail(PipeStatusCode.ServerError, ex.Message);
            }
        }
    }

    /// <summary>日志：打印请求概要（生产环境可以换成只记录 Error）。</summary>
    public sealed class LoggingMiddleware : IPipeMiddleware
    {
        public async Task<PipeResponse> InvokeAsync(PipeContext ctx, PipeRequestDelegate next)
        {
            ctx.Logger.Info("[" + ctx.TraceId + "] → " + ctx.Request.ContentType
                + "/" + ctx.Request.MethodName + " from=" + ctx.Request.Sender);
            var resp = await next(ctx).ConfigureAwait(false);
            ctx.Logger.Info("[" + ctx.TraceId + "] ← " + resp.Code + " " + resp.Message);
            return resp;
        }
    }

    /// <summary>计时：把处理耗时写入 ctx.Items["__duration_ms"]，由 PipeServer 转存到响应。</summary>
    public sealed class TimingMiddleware : IPipeMiddleware
    {
        public async Task<PipeResponse> InvokeAsync(PipeContext ctx, PipeRequestDelegate next)
        {
            var sw = Stopwatch.StartNew();
            try { return await next(ctx).ConfigureAwait(false); }
            finally { sw.Stop(); ctx.Items["__duration_ms"] = sw.ElapsedMilliseconds; }
        }
    }
}