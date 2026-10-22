// ════════════════════════════════════════════════════════════════════════════
//  文件：StressService.cs
//  作用：
//    - StressService：普通 [PipeService]，提供 echo / add 方法（call 模式压测）
//    - StressStreamHandler：★ 新增，处理 ContentType="STREAM"（stream 模式压测）
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Attributes;
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.StressTest
{
    /// <summary>普通业务服务（用于 call 模式压测）。</summary>
    [PipeService]
    public sealed class StressService
    {
        [PipeMethod("stress.echo")] public string Echo(string input) { return input; }
        [PipeMethod("stress.add")] public long Add(long a, long b) { return a + b; }

        /// <summary>模拟异步慢方法（可选，压测用）。</summary>
        [PipeMethod("stress.sleep")]
        public async Task<string> SleepAsync(int ms)
        {
            await Task.Delay(ms).ConfigureAwait(false);
            return "ok";
        }
    }

    /// <summary>
    /// ★ 新增：处理 ContentType="STREAM" 的流式压测 Handler。
    /// <para>
    /// 每次请求推 <c>args[0]</c> 个 64 字节 chunk（默认 10 个）。
    /// 客户端用 <c>StreamAsync("stress.stream", new object[] { N })</c> 触发。
    /// </para>
    /// </summary>
    public sealed class StressStreamHandler : IPipeRequestHandler
    {
        private static readonly byte[] Payload = Encoding.UTF8.GetBytes(new string('x', 64));

        public string ContentType { get { return PipeContentTypes.Stream; } }

        public Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
        {
            var count = request.Args != null && request.Args.Length > 0
                ? Convert.ToInt32(request.Args[0].ToString())
                : 10;

            // 返回流式响应：头里带元数据，流式体推 N 个 chunk
            return Task.FromResult(PipeResponse.Stream(
                headerData: new { chunks = count },
                streamFactory: (push, complete) =>
                {
                    try
                    {
                        for (int i = 0; i < count; i++) push(Payload);
                    }
                    finally { complete(); }
                    return Task.CompletedTask;
                }));
        }
    }
}