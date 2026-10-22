// ════════════════════════════════════════════════════════════════════════════
//  文件：Program.cs（客户端示例）
//  作用：演示四种能力。
//  本版本：流式调用用 StreamWithCallbackAsync（回调式），C# 7.3 兼容。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Text;
using System.Threading.Tasks;
using NamePipeKit.Client;
using NamePipeKit.Logging;

namespace NamePipeKit.ClientSample
{
    internal static class Program
    {
        private const string PipeName = "NamePipeKit.Sample.v1";

        private static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var logger = new ConsolePipeLogger();

            using (var client = new PipeClient(PipeName, ".", "demo-client", logger))
            {
                // ── 1. 普通方法调用 ──
                Console.WriteLine("1) 普通方法调用");
                var orderNo = await client.InvokeAsync<string>("order.create", "u001", 99.5m);
                Console.WriteLine("   order.create → " + orderNo);

                // ── 2. 订阅推送 ──
                Console.WriteLine();
                Console.WriteLine("2) 订阅 heartbeat / orders");
                client.PushReceived += body =>
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("[推送] " + Encoding.UTF8.GetString(body));
                    Console.ResetColor();
                };
                await client.SubscribeAsync("heartbeat");
                await client.SubscribeAsync("orders");
                await Task.Delay(6000);

                // ── 3. 流式响应（★ 回调式，兼容 C# 7.3）──
                Console.WriteLine();
                Console.WriteLine("3) 流式响应：stream.counter（10 个 chunk，每 100ms 一个）");
                int n = 0;
                await client.StreamWithCallbackAsync(
                    "stream.counter",
                    new object[] { 10 },
                    chunk =>
                    {
                        n++;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("[chunk " + n + "] " + Encoding.UTF8.GetString(chunk));
                        Console.ResetColor();
                    });
                Console.WriteLine("流结束，共 " + n + " 个 chunk");

                // ── 4. 取消订阅 ──
                await client.UnsubscribeAsync("heartbeat");
                Console.WriteLine();
                Console.WriteLine("已取消 heartbeat，3 秒内只应看到 orders 推送");
                await Task.Delay(3000);
            }

            Console.WriteLine("按回车退出");
            Console.ReadLine();
        }
    }
}