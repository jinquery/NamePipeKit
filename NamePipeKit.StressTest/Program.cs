// ════════════════════════════════════════════════════════════════════════════
//  文件：Program.cs
//  作用：压测入口。★ 内嵌服务端时额外注册 StressStreamHandler（stream 模式）。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Server;

namespace NamePipeKit.StressTest
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "NamePipeKit 压测";

            var opts = StressOptions.Parse(args);

            var useDefault = (args == null || args.Length == 0);
            if (useDefault) opts.AutoServer = true;
            else if (opts.Help)
            {
                StressOptions.PrintHelp();
                Console.WriteLine();
                Console.WriteLine("按回车退出……");
                Console.ReadLine();
                return 0;
            }

            int workerMin, ioMin;
            ThreadPool.GetMinThreads(out workerMin, out ioMin);

            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║             NamePipeKit 压测 / 并发测试程序                   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            if (useDefault)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  （未传入命令行参数，使用默认配置）");
                Console.ResetColor();
            }
            Console.WriteLine("  管道名          ：" + opts.PipeName);
            Console.WriteLine("  服务器          ：" + opts.ServerName + (opts.AutoServer ? "（内嵌）" : "（外部）"));
            Console.WriteLine("  并发客户端数    ：" + opts.Concurrency);
            Console.WriteLine("  压测模式        ：" + opts.Mode);       // ★ 显示模式
            if (opts.Mode == "stream")
                Console.WriteLine("  每流 chunk 数   ：" + opts.StreamChunkCount);
            else
                Console.WriteLine("  调用方法        ：" + opts.MethodName);
            Console.WriteLine("  ThreadPool 最小 ：" + workerMin + " worker / " + ioMin + " io");
            if (opts.DurationSeconds.HasValue)
                Console.WriteLine("  持续时间        ：" + opts.DurationSeconds.Value + " 秒");
            else
                Console.WriteLine("  总请求数        ：" + opts.TotalRequests + "（每客户端 " + opts.RequestsPerClient + "）");
            Console.WriteLine("  每客户端预热    ：" + opts.WarmupPerClient + " 次");
            Console.WriteLine("──────────────────────────────────────────────────────────────");

            PipeServer server = null;
            if (opts.AutoServer)
            {
                var maxInstances = Math.Max(80, opts.Concurrency + 20);
                var builder = new PipeServerBuilder(opts.PipeName)
                    .UseLogger(new SilentPipeLogger())
                    .UseMaxInstances(maxInstances)
                    .ScanAssemblies(typeof(StressService).Assembly)
                    // ★ 关键：注册流式 Handler，stream 模式才能用
                    .AddHandler(new StressStreamHandler());

                server = builder.Build();
                server.StartAsync();
                Console.WriteLine("[内嵌服务端] 已启动，maxInstances=" + maxInstances);
                Console.WriteLine("──────────────────────────────────────────────────────────────");
                await Task.Delay(300);
            }

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine();
                Console.WriteLine("收到 Ctrl+C，正在停止压测……");
                cts.Cancel();
            };

            try
            {
                var runner = new StressRunner(opts);
                var result = await runner.RunAsync(cts.Token).ConfigureAwait(false);
                PrintResult(result);
            }
            catch (OperationCanceledException) { Console.WriteLine("压测已取消。"); }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("压测异常：" + ex);
                Console.ResetColor();
            }
            finally
            {
                if (server != null)
                {
                    server.Stop();
                    server.Dispose();
                    Console.WriteLine("[内嵌服务端] 已停止。");
                }
            }

            Console.WriteLine();
            Console.WriteLine("按回车退出……");
            Console.ReadLine();
            return 0;
        }

        private static void PrintResult(StressResult r)
        {
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                          压测结果                             ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine("  总请求数         ：" + r.TotalRequests.ToString("N0"));
            Console.WriteLine("  成功             ：" + r.Succeeded.ToString("N0"));
            Console.WriteLine("  失败             ：" + r.Failed.ToString("N0"));
            Console.WriteLine("  耗时             ：" + r.Elapsed.TotalSeconds.ToString("F2") + " 秒");
            Console.WriteLine();
            Console.WriteLine("  ──────────  吞吐  ──────────");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  QPS              ：" + r.Qps.ToString("N2"));
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  ──────────  客户端 RTT（毫秒） ──────────");
            Console.WriteLine("  Min              ：" + r.MinMs.ToString("F2"));
            Console.WriteLine("  Avg              ：" + r.AvgMs.ToString("F2"));
            Console.WriteLine("  P50              ：" + r.P50Ms.ToString("F2"));
            Console.WriteLine("  P90              ：" + r.P90Ms.ToString("F2"));
            Console.WriteLine("  P95              ：" + r.P95Ms.ToString("F2"));
            Console.WriteLine("  P99              ：" + r.P99Ms.ToString("F2"));
            Console.WriteLine("  Max              ：" + r.MaxMs.ToString("F2"));

            if (r.ErrorSamples.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  ──────────  错误样本（前 10 条） ──────────");
                Console.ForegroundColor = ConsoleColor.Yellow;
                foreach (var e in r.ErrorSamples) Console.WriteLine("    · " + e);
                Console.ResetColor();
            }
            Console.WriteLine("══════════════════════════════════════════════════════════════");
        }
    }
}