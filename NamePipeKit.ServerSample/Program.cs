// ════════════════════════════════════════════════════════════════════════════
//  文件：Program.cs（服务端示例）
//  作用：启动服务端，注册三种 Handler：
//          - SUBSCRIBE / UNSUBSCRIBE（EventBus 订阅）
//          - STREAM（流式响应）
//          - CALLMETHOD（默认已注册）
//  定时推送：每秒向 heartbeat 推，每 5 秒向 orders 推。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Threading;
using NamePipeKit.Handling;
using NamePipeKit.Logging;
using NamePipeKit.Server;
using NamePipeKit.ServerSample.Services;

namespace NamePipeKit.ServerSample
{
    internal static class Program
    {
        private const string PipeName = "NamePipeKit.Sample.v1";

        private static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var logger = new ConsolePipeLogger();
            var eventBus = new EventBus();

            using (var server = new PipeServerBuilder(PipeName)
                .UseLogger(logger)
                .UseMaxInstances(100)
                .ScanAssemblies(typeof(Program).Assembly)
                // ★ 注册新 ContentType Handler（新增能力不修改框架）
                .AddHandler(new SubscribeHandler(eventBus))
                .AddHandler(new UnsubscribeHandler(eventBus))
                .AddHandler(new StreamHandler())
                // MESSAGE 订阅
                .OnMessageReceived((s, h, d) =>
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("[消息] sender=" + s + " header=" + h + " data=" + d);
                    Console.ResetColor();
                })
                .Build())
            {
                server.StartAsync();
                logger.Info("服务端已启动：" + PipeName);
                logger.Info("每秒推 heartbeat，每 5 秒推 orders");

                // ★ 定时推送（EventBus 是业务侧持有的）
                var t1 = new Timer(_ => eventBus.Publish("heartbeat",
                    new { ts = DateTime.Now.ToString("HH:mm:ss") }), null, 1000, 1000);
                var t2 = new Timer(_ => eventBus.Publish("orders",
                    new { orderId = "ORD-" + Guid.NewGuid().ToString("N").Substring(0, 6), status = "PAID" }),
                    null, 5000, 5000);

                var exit = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; exit.Set(); };
                exit.Wait();

                t1.Dispose(); t2.Dispose();
                server.Stop();
            }
        }
    }
}