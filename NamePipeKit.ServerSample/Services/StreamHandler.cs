// ════════════════════════════════════════════════════════════════════════════
//  文件：StreamHandler.cs
//  作用：处理 ContentType = "STREAM" 的流式请求。
//  用法：PipeResponse.Stream(headerData, streamFactory) 返回流式响应。
//        PipeServer 会在写完 Response 头后，后台执行 streamFactory：
//          push(chunk)  → 生成一个 ResponseChunk 帧推给客户端
//          complete()   → 生成一个 ResponseEnd 帧
//  扩展：想加新的流式方法，只需在 streamFactory 里写不同逻辑；
//        完全不需要修改框架任何代码。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.ServerSample.Services
{
    /// <summary>STREAM 处理器。</summary>
    public sealed class StreamHandler : IPipeRequestHandler
    {
        public string ContentType { get { return PipeContentTypes.Stream; } }

        public Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
        {
            // 用 MethodName 区分不同的流式方法
            if (request.MethodName == "stream.counter")
            {
                // 参数：总共推几个 chunk
                var count = request.Args != null && request.Args.Length > 0
                    ? Convert.ToInt32(request.Args[0].ToString()) : 20;

                // ★ 流式响应：头里返回元数据，流式体推 chunk
                return Task.FromResult(PipeResponse.Stream(
                    headerData: new { streamType = "counter", count = count },
                    streamFactory: async (push, complete) =>
                    {
                        try
                        {
                            for (int i = 0; i < count; i++)
                            {
                                await Task.Delay(100).ConfigureAwait(false);
                                var text = "tick " + i + " @ " + DateTime.Now.ToString("HH:mm:ss.fff");
                                push(Encoding.UTF8.GetBytes(text));
                            }
                        }
                        finally
                        {
                            complete();  // 无论成功失败都推结束帧
                        }
                    }));
            }

            return Task.FromResult(PipeResponse.Fail(
                PipeStatusCode.MethodNotFound, "未知流式方法：" + request.MethodName));
        }
    }
}