using System;
using System.Threading.Tasks;
using NamePipeKit.Client;
using NamePipeKit.Logging;
using NamePipeKit.Sample.Services;
using NamePipeKit.Server;

namespace NamePipeKit.Sample
{
    internal static class Program
    {
        private const string PipeName = "NamePipeKit.Sample.v1";

        private static async Task Main()
        {
            var logger = new ConsolePipeLogger();

            // ====== 启动服务端 ======
            using (var server = new PipeServerBuilder(PipeName)
                .UseLogger(logger)
                .ScanAssemblies(typeof(OrderService).Assembly)
                .ScanEntryAssembly()
                .UseMaxInstances(10)
                .Build())
            {
                var listener = server.StartAsync(); // 后台运行
                logger.Info("服务端已启动");
                await Task.Delay(200);

                // ====== 客户端调用 ======
                using (var client = new PipeClient(PipeName, ".", "demo-client", logger))
                {
                    // 1. 调用带返回值的方法
                    var orderNo = await client.InvokeAsync<string>("order.create", "u001", 99.5m);
                    Console.WriteLine("创建订单：" + orderNo);

                    // 2. 调用另一个方法
                    var status = await client.InvokeAsync<string>("order.status", orderNo);
                    Console.WriteLine("订单状态：" + status);

                    // 3. 数组参数
                    var total = await client.InvokeAsync<decimal>("order.total", new decimal[] { 1.0m, 2.5m, 3.75m });
                    Console.WriteLine("总额：" + total);

                    // 4. 无返回值调用
                    await client.InvokeAsync("system.ping", "hello");

                    // 5. 发送消息
                    await client.SendMessageAsync("heartbeat", "hello-message");

                    // 6. 错误调用
                    try { await client.InvokeAsync("not.exist"); }
                    catch (InvalidOperationException ex) { Console.WriteLine("预期错误：" + ex.Message); }
                }

                Console.WriteLine("演示结束，按回车退出。");
                Console.ReadLine();

                server.Stop();
            }
        }
    }
}