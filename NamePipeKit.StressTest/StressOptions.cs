// ════════════════════════════════════════════════════════════════════════════
//  文件：StressOptions.cs
//  作用：压测配置。所有字段支持命令行参数传入；无参数时走默认。
//  ★ 新增：Mode 字段，支持 "call"（普通调用）/ "stream"（流式响应）两种模式。
// ════════════════════════════════════════════════════════════════════════════
using System;

namespace NamePipeKit.StressTest
{
    /// <summary>压测配置。</summary>
    public sealed class StressOptions
    {
        public string PipeName { get; set; } = "NamePipeKit.Stress.v1";
        public string ServerName { get; set; } = ".";

        /// <summary>并发客户端数。</summary>
        public int Concurrency { get; set; } = 50;

        /// <summary>每客户端请求数（未指定 Duration 时生效）。</summary>
        public int RequestsPerClient { get; set; } = 1000;

        /// <summary>按持续时间压测（秒），设置后 RequestsPerClient 被忽略。</summary>
        public int? DurationSeconds { get; set; }

        /// <summary>调用方法名（call 模式）。</summary>
        public string MethodName { get; set; } = "stress.echo";

        /// <summary>方法参数（call 模式）。</summary>
        public object[] Args { get; set; } = new object[] { "hello" };

        /// <summary>
        /// ★ 压测模式。
        /// <list type="bullet">
        ///   <item><c>"call"</c>（默认）：普通 CALLMETHOD 调用；</item>
        ///   <item><c>"stream"</c>：流式响应，每次请求推 ChunkCount 个 chunk。</item>
        /// </list>
        /// </summary>
        public string Mode { get; set; } = "call";

        /// <summary>★ 流式模式下每个流推多少个 chunk（默认 10）。</summary>
        public int StreamChunkCount { get; set; } = 10;

        /// <summary>每客户端预热次数。</summary>
        public int WarmupPerClient { get; set; } = 3;

        /// <summary>进度打印间隔（毫秒）。</summary>
        public int ProgressIntervalMs { get; set; } = 1000;

        /// <summary>是否启动内嵌服务端。</summary>
        public bool AutoServer { get; set; } = false;

        /// <summary>显示帮助。</summary>
        public bool Help { get; set; } = false;

        public long TotalRequests { get { return (long)Concurrency * RequestsPerClient; } }

        /// <summary>命令行参数解析。</summary>
        public static StressOptions Parse(string[] args)
        {
            var o = new StressOptions();
            if (args == null) return o;

            foreach (var a in args)
            {
                var raw = a.Trim();
                if (raw.Length == 0) continue;
                if (raw == "-h" || raw == "--help" || raw == "/?") { o.Help = true; continue; }

                int eq = raw.IndexOf('=');
                if (!raw.StartsWith("--") || eq <= 0) continue;

                var key = raw.Substring(2, eq - 2).ToLowerInvariant();
                var val = raw.Substring(eq + 1);
                switch (key)
                {
                    case "pipe": o.PipeName = val; break;
                    case "server": o.ServerName = val; break;
                    case "concurrency": o.Concurrency = int.Parse(val); break;
                    case "requests": o.RequestsPerClient = int.Parse(val); break;
                    case "duration": o.DurationSeconds = int.Parse(val); break;
                    case "method": o.MethodName = val; break;
                    case "args": o.Args = val.Length == 0 ? new object[0] : (object[])val.Split(','); break;
                    case "mode": o.Mode = val.ToLowerInvariant(); break;   // ★
                    case "chunks": o.StreamChunkCount = int.Parse(val); break; // ★
                    case "warmup": o.WarmupPerClient = int.Parse(val); break;
                    case "progress": o.ProgressIntervalMs = int.Parse(val); break;
                    case "auto-server": o.AutoServer = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase); break;
                    default: Console.WriteLine("[警告] 未知参数：" + raw); break;
                }
            }

            if (o.Concurrency <= 0) o.Concurrency = 1;
            if (o.RequestsPerClient <= 0) o.RequestsPerClient = 1;
            if (o.WarmupPerClient < 0) o.WarmupPerClient = 0;
            if (o.ProgressIntervalMs < 200) o.ProgressIntervalMs = 200;
            if (o.StreamChunkCount <= 0) o.StreamChunkCount = 1;
            return o;
        }

        /// <summary>打印帮助。</summary>
        public static void PrintHelp()
        {
            Console.WriteLine("NamePipeKit 压测程序");
            Console.WriteLine();
            Console.WriteLine("无参数运行：默认内嵌服务端 + 50 并发 + 每客户端 1000 次（call 模式）");
            Console.WriteLine();
            Console.WriteLine("命令行参数：");
            Console.WriteLine("  --pipe=<name>          命名管道名（默认 NamePipeKit.Stress.v1）");
            Console.WriteLine("  --concurrency=<n>      并发客户端数（默认 50）");
            Console.WriteLine("  --requests=<n>         每客户端请求数（默认 1000）");
            Console.WriteLine("  --duration=<sec>       按持续时间压测（秒）");
            Console.WriteLine("  --method=<name>        call 模式的方法名（默认 stress.echo）");
            Console.WriteLine("  --args=<v1,v2,...>     call 模式的参数，逗号分隔");
            Console.WriteLine("  --mode=call|stream     ★ 压测模式（默认 call）");
            Console.WriteLine("  --chunks=<n>           ★ stream 模式每流 chunk 数（默认 10）");
            Console.WriteLine("  --warmup=<n>           预热次数（默认 3）");
            Console.WriteLine("  --progress=<ms>        进度间隔（默认 1000）");
            Console.WriteLine("  --auto-server=true     启动内嵌服务端");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  # 普通 call 压测（默认）");
            Console.WriteLine("  dotnet run -c Release -- --auto-server=true --concurrency=50 --requests=1000");
            Console.WriteLine();
            Console.WriteLine("  # 流式压测，每流 10 个 chunk");
            Console.WriteLine("  dotnet run -c Release -- --auto-server=true --mode=stream --concurrency=50 --requests=200");
        }
    }
}