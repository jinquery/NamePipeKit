using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

/// <summary>
/// NamedPipe 裸测（同步 API · 单连接
/// </summary>
internal static class Program
{
    private const string PipeName = "NakedBenchmarkPipe";
    private const int N = 10000;
    private const int BufSize = 8192;

    private static void Main()
    {
        Console.WriteLine("=== NamedPipe 裸测（同步 API · 单连接）===");
        Console.WriteLine("请求数：" + N);
        Console.WriteLine();

        // 服务端（后台线程）
        var serverThread = new Thread(ServerLoop) { IsBackground = true };
        serverThread.Start();
        Thread.Sleep(200);

        using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None))
        {
            client.Connect(5000);
            Console.WriteLine("已连接服务端");

            var buf = new byte[BufSize];
            var payload = System.Text.Encoding.UTF8.GetBytes("hello-world");

            // 预热 200 次
            for (int i = 0; i < 200; i++) DoRound(client, buf, payload);
            Console.WriteLine("预热完成");
            Console.WriteLine();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) DoRound(client, buf, payload);
            sw.Stop();

            var qps = N / sw.Elapsed.TotalSeconds;
            var rtt = sw.Elapsed.TotalMilliseconds / N;

            Console.WriteLine("──── 结果 ────");
            Console.WriteLine("总请求  ：" + N.ToString("N0"));
            Console.WriteLine("耗时    ：" + sw.Elapsed.TotalSeconds.ToString("F2") + " 秒");
            Console.WriteLine("QPS     ：" + qps.ToString("N0"));
            Console.WriteLine("单次 RTT：" + rtt.ToString("F3") + " ms");
            Console.WriteLine();

            if (qps >= 10000)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> 结论：NamedPipe 同步 API 极快（" + qps.ToString("N0") + " QPS）");
                Console.WriteLine(">>> 你的框架里 40ms 延迟是 async/ThreadPool 引起的，可以彻底修好。");
            }
            else if (qps >= 2000)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(">>> 结论：NamedPipe 同步 API 中等（" + qps.ToString("N0") + " QPS）");
                Console.WriteLine(">>> 你的框架应该能优化到这个量级。");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(">>> 结论：NamedPipe 本机就很慢（" + qps.ToString("N0") + " QPS）");
                Console.WriteLine(">>> 是 Windows NamedPipe 本身的限制，换 TCP/共享内存会好很多。");
            }
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("按回车退出……");
        Console.ReadLine();
    }

    // 客户端一次请求：写 4B 长度 + payload，读 4B 长度 + payload
    private static void DoRound(NamedPipeClientStream client, byte[] buf, byte[] payload)
    {
        // 发送
        BitConverter.GetBytes(payload.Length).CopyTo(buf, 0);
        payload.CopyTo(buf, 4);
        client.Write(buf, 0, 4 + payload.Length);
        client.Flush();

        // 接收：先读长度
        ReadExact(client, buf, 4);
        int len = BitConverter.ToInt32(buf, 0);
        if (len < 0 || len > BufSize)
            throw new InvalidDataException("响应长度非法：" + len);

        // 再读 payload
        ReadExact(client, buf, len);
    }

    // 服务端：读 4B 长度 + payload，回写 4B 长度 + payload（★ 这次要对称！）
    private static void ServerLoop()
    {
        using (var server = new NamedPipeServerStream(
            PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None))
        {
            server.WaitForConnection();

            var buf = new byte[BufSize];
            var lenBuf = new byte[4];

            while (true)
            {
                try
                {
                    // 读长度
                    ReadExact(server, lenBuf, 4);
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len < 0 || len > BufSize)
                        throw new InvalidDataException("请求长度非法：" + len);

                    // 读 payload
                    ReadExact(server, buf, len);

                    // ★ 回写：4B 长度 + payload（与客户端请求格式一致）
                    BitConverter.GetBytes(len).CopyTo(lenBuf, 0);
                    server.Write(lenBuf, 0, 4);
                    server.Write(buf, 0, len);
                    server.Flush();
                }
                catch { return; }
            }
        }
    }

    // 读满 count 字节；★ 加边界检查
    private static void ReadExact(Stream s, byte[] buf, int count)
    {
        if (count < 0 || count > buf.Length)
            throw new ArgumentException("count 越界：" + count + "，buf.Length：" + buf.Length);

        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}