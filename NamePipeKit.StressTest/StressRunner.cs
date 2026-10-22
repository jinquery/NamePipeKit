// ════════════════════════════════════════════════════════════════════════════
//  文件：StressRunner.cs
//  作用：压测执行器 + 停顿探测器。
//
//  【停顿探测器】压测期间，一个后台线程每 50ms 检查一次时间间隔。
//    如果两次检查之间实际过了 >300ms，说明整个进程被暂停了（不是某个请求慢，
//    而是所有线程同时被冻结）。这能解释 P99 异常和 Max 达数十秒的现象。
//
//    每次暂停记录：
//      - 发生时间（HH:mm:ss.fff）
//      - 实际间隔（毫秒）
//      - 当时的 GC 次数（Gen0/Gen1/Gen2）
//      - 当时的 ThreadPool 活跃线程数
//      - 当时已完成的请求数（便于对应进度行）
//
//    C# 7.3 兼容，无 NuGet 依赖。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Client;

namespace NamePipeKit.StressTest
{
    /// <summary>压测执行器（含停顿探测）。</summary>
    public sealed class StressRunner
    {
        private readonly StressOptions _opts;
        private int _firstFailPrinted = 0;

        /// <summary>一次暂停事件的快照。</summary>
        private sealed class PauseEvent
        {
            public DateTime Timestamp;
            public double GapMs;
            public int Gen0;
            public int Gen1;
            public int Gen2;
            public int ThreadPoolInUse;
            public long DoneRequests;
        }

        public StressRunner(StressOptions opts)
        {
            _opts = opts ?? throw new ArgumentNullException("opts");
        }

        /// <summary>运行压测（含停顿探测）。</summary>
        public async Task<StressResult> RunAsync(CancellationToken ct)
        {
            // ── 1. 探测服务端 ──
            await ProbeServerAsync(ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("──────────────────────────────────────────────────────────────");
            Console.WriteLine("  预热中（每客户端 " + _opts.WarmupPerClient + " 次）……");
            Console.WriteLine("──────────────────────────────────────────────────────────────");

            if (_opts.WarmupPerClient > 0)
            {
                var warmupTasks = new Task[_opts.Concurrency];
                for (int i = 0; i < _opts.Concurrency; i++)
                {
                    var idx = i;
                    warmupTasks[i] = Task.Run(() => WarmupOneAsync(idx, ct));
                }
                await Task.WhenAll(warmupTasks).ConfigureAwait(false);
            }

            Console.WriteLine("预热完成，开始正式压测……");
            Console.WriteLine();

            // ── 2. 客户端数据结构 ──
            var perClientLatencies = new List<double>[_opts.Concurrency];
            var perClientErrors = new List<string>[_opts.Concurrency];
            for (int i = 0; i < _opts.Concurrency; i++)
            {
                perClientLatencies[i] = new List<double>(1024);
                perClientErrors[i] = new List<string>(4);
            }

            long totalDone = 0;
            long totalFail = 0;

            var sw = Stopwatch.StartNew();
            var endTime = _opts.DurationSeconds.HasValue
                ? DateTime.UtcNow.AddSeconds(_opts.DurationSeconds.Value)
                : (DateTime?)null;

            // ══════════════════════════════════════════════════════════════
            //  ★ 停顿探测器启动
            // ══════════════════════════════════════════════════════════════
            var pauses = new List<PauseEvent>();
            var pauseLock = new object();
            var pauseStop = new ManualResetEventSlim(false);

            // 记录启动时刻的 GC 计数（结束时用来算总增量）
            int startGen0 = GC.CollectionCount(0);
            int startGen1 = GC.CollectionCount(1);
            int startGen2 = GC.CollectionCount(2);

            var pauseThread = new Thread(() =>
            {
                DateTime last = DateTime.UtcNow;
                while (!pauseStop.Wait(50))  // 每 50ms 醒一次；stop 时立即返回
                {
                    var now = DateTime.UtcNow;
                    var gap = (now - last).TotalMilliseconds;
                    last = now;

                    // 正常间隔 ≈ 50ms，偏差 < 250ms 忽略（调度抖动是正常的）。
                    // 实际间隔 > 300ms 才说明整个进程真的被暂停了。
                    if (gap > 300)
                    {
                        int w, io;
                        ThreadPool.GetAvailableThreads(out w, out io);
                        int workerMax, ioMax;
                        ThreadPool.GetMaxThreads(out workerMax, out ioMax);

                        var ev = new PauseEvent
                        {
                            Timestamp = now,
                            GapMs = gap,
                            Gen0 = GC.CollectionCount(0),
                            Gen1 = GC.CollectionCount(1),
                            Gen2 = GC.CollectionCount(2),
                            ThreadPoolInUse = workerMax - w,   // 活跃 worker 数
                            DoneRequests = Interlocked.Read(ref totalDone)
                        };
                        lock (pauseLock) { pauses.Add(ev); }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "PauseDetector"
            };
            pauseThread.Start();

            // ── 3. 进度循环 ──
            var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var progressTask = ProgressLoopAsync(
                () => Interlocked.Read(ref totalDone),
                () => Interlocked.Read(ref totalFail),
                sw, endTime, progressCts.Token);

            // ── 4. 启动所有客户端 ──
            var tasks = new Task[_opts.Concurrency];
            for (int i = 0; i < _opts.Concurrency; i++)
            {
                var idx = i;
                tasks[i] = Task.Run(() => RunOneClientAsync(
                    idx, perClientLatencies[idx], perClientErrors[idx],
                    endTime, ct,
                    () => { Interlocked.Increment(ref totalDone); },
                    () => { Interlocked.Increment(ref totalFail); }));
            }

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception ex) { Console.WriteLine("[压测] 客户端异常：" + ex.Message); }

            sw.Stop();

            // ── 5. 停止探测器 ──
            pauseStop.Set();
            try { pauseThread.Join(1000); } catch { }

            try { progressCts.Cancel(); } catch { }
            try { await progressTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { progressCts.Dispose(); }

            // ══════════════════════════════════════════════════════════════
            //  ★ 打印停顿诊断报告
            // ══════════════════════════════════════════════════════════════
            int endGen0 = GC.CollectionCount(0);
            int endGen1 = GC.CollectionCount(1);
            int endGen2 = GC.CollectionCount(2);

            PrintPauseReport(pauses, sw.Elapsed,
                endGen0 - startGen0, endGen1 - startGen1, endGen2 - startGen2);

            return BuildResult(perClientLatencies, perClientErrors, totalDone, totalFail, sw.Elapsed);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ★ 停顿诊断报告
        // ══════════════════════════════════════════════════════════════════════
        private static void PrintPauseReport(List<PauseEvent> pauses, TimeSpan elapsed,
            int dGen0, int dGen1, int dGen2)
        {
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                   停顿诊断报告                                ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

            Console.WriteLine("  压测总时长      ：" + elapsed.TotalSeconds.ToString("F2") + " 秒");
            Console.WriteLine("  GC 总次数       ：Gen0=" + dGen0 + " Gen1=" + dGen1 + " Gen2=" + dGen2);
            Console.WriteLine("  停顿次数(>300ms)：" + pauses.Count);
            Console.WriteLine();

            if (pauses.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✓ 未检测到全局停顿。P99/Max 偏高是单请求排队，不是进程暂停。");
                Console.ResetColor();
                return;
            }

            // 统计
            double totalPauseMs = 0;
            double maxPauseMs = 0;
            int gen2PauseCount = 0;
            int prevGen2 = -1;

            for (int i = 0; i < pauses.Count; i++)
            {
                totalPauseMs += pauses[i].GapMs;
                if (pauses[i].GapMs > maxPauseMs) maxPauseMs = pauses[i].GapMs;
            }

            // 归因：判断每次暂停期间 Gen2 是否增长
            for (int i = 0; i < pauses.Count; i++)
            {
                if (prevGen2 >= 0 && pauses[i].Gen2 > prevGen2) gen2PauseCount++;
                prevGen2 = pauses[i].Gen2;
            }

            Console.WriteLine("  最长单次停顿    ：" + maxPauseMs.ToString("F0") + " ms");
            Console.WriteLine("  累计停顿时长    ：" + totalPauseMs.ToString("F0") + " ms （占 "
                + (totalPauseMs / elapsed.TotalMilliseconds * 100).ToString("F1") + "% 总时长）");
            Console.WriteLine("  其中 Gen2 增长  ：" + gen2PauseCount + " 次（说明是 GC 引起的）");
            Console.WriteLine();

            // 打印前 20 条明细
            Console.WriteLine("  ──────────  停顿明细（前 20 条） ──────────");
            int show = Math.Min(20, pauses.Count);
            for (int i = 0; i < show; i++)
            {
                var p = pauses[i];
                Console.ForegroundColor = p.GapMs > 5000 ? ConsoleColor.Red
                                       : p.GapMs > 1000 ? ConsoleColor.Yellow
                                       : ConsoleColor.Gray;

                Console.WriteLine(string.Format(
                    "  [{0:HH:mm:ss.fff}]  暂停 {1,8:F0}ms  GC={2}/{3}/{4}  TP={5,3}  done={6:N0}",
                    p.Timestamp, p.GapMs, p.Gen0, p.Gen1, p.Gen2,
                    p.ThreadPoolInUse, p.DoneRequests));
                Console.ResetColor();
            }

            if (pauses.Count > show)
                Console.WriteLine("  …… 省略 " + (pauses.Count - show) + " 条");

            // 归因结论
            Console.WriteLine();
            Console.WriteLine("  ──────────  结论  ──────────");
            if (gen2PauseCount >= pauses.Count / 2)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ✗ 大部分停顿伴随 Gen2 GC 计数增长 → GC 是主因");
                Console.WriteLine("     解决：给压测项目和服务端都加 ServerGC");
                Console.WriteLine("     csproj 里加：<ServerGarbageCollection>true</ServerGarbageCollection>");
                Console.ResetColor();
            }
            else if (maxPauseMs > 5000)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ✗ 存在 >5 秒的超长停顿，但 GC 计数没有对应增长");
                Console.WriteLine("     可能：杀毒软件 / Windows 更新 / 内存换页 / 其他进程抢占");
                Console.WriteLine("     建议：任务管理器看 CPU 占用，关掉 Defender 实时监控再跑一次");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine("  停顿都在 300~1000ms 之间，累计占比不大。");
                Console.WriteLine("  QPS 偏低可能来自排队，不是暂停。");
            }

            Console.WriteLine("══════════════════════════════════════════════════════════════");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  以下为原有逻辑，一字未改
        // ══════════════════════════════════════════════════════════════════════

        private async Task ProbeServerAsync(CancellationToken ct)
        {
            Console.WriteLine();
            Console.WriteLine("  正在探测服务端……");
            using (var client = CreateClient(0))
            {
                for (int i = 0; i < 30; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        await client.InvokeAsync("stress.echo", "hello").ConfigureAwait(false);
                        Console.WriteLine("  服务端已就绪");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (i % 5 == 0)
                            Console.WriteLine("  探测中（" + (i + 1) + "/30）："
                                              + ex.GetType().Name + " - " + ex.Message);
                        try { await Task.Delay(500, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                throw new TimeoutException("服务端在 15 秒内不可用");
            }
        }

        private async Task WarmupOneAsync(int clientIndex, CancellationToken ct)
        {
            using (var client = CreateClient(clientIndex))
            {
                for (int i = 0; i < _opts.WarmupPerClient; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    try { await InvokeOneAsync(client).ConfigureAwait(false); }
                    catch { }
                }
            }
        }

        private async Task RunOneClientAsync(int clientIndex, List<double> latencies, List<string> errors,
            DateTime? endTime, CancellationToken ct, Action onSuccess, Action onFail)
        {
            using (var client = CreateClient(clientIndex))
            {
                long reqIndex = 0;
                while (!ct.IsCancellationRequested)
                {
                    if (endTime.HasValue)
                    {
                        if (DateTime.UtcNow >= endTime.Value) break;
                    }
                    else if (reqIndex >= _opts.RequestsPerClient) break;

                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await InvokeOneAsync(client).ConfigureAwait(false);
                        sw.Stop();
                        latencies.Add(sw.Elapsed.TotalMilliseconds);
                        onSuccess();
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        latencies.Add(sw.Elapsed.TotalMilliseconds);
                        if (errors.Count < 10) errors.Add(ex.GetType().Name + ": " + ex.Message);
                        onFail();

                        if (Interlocked.CompareExchange(ref _firstFailPrinted, 1, 0) == 0)
                        {
                            var old = Console.ForegroundColor;
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine();
                            Console.WriteLine("══════════════ 首次失败 ══════════════");
                            Console.WriteLine("  时间：" + DateTime.Now.ToString("HH:mm:ss.fff"));
                            Console.WriteLine("  客户端：" + clientIndex);
                            Console.WriteLine("  类型：" + ex.GetType().FullName);
                            Console.WriteLine("  消息：" + ex.Message);
                            if (ex.InnerException != null)
                                Console.WriteLine("  内层：" + ex.InnerException.GetType().FullName
                                                  + ": " + ex.InnerException.Message);
                            Console.WriteLine("  堆栈：" + ex.StackTrace);
                            Console.WriteLine("══════════════════════════════════════");
                            Console.ForegroundColor = old;
                            Console.WriteLine();
                        }
                    }
                    reqIndex++;
                }
            }
        }

        private PipeClient CreateClient(int clientIndex)
        {
            return new PipeClient(
                _opts.PipeName,
                _opts.ServerName,
                "stress-" + clientIndex.ToString("D4"),
                new SilentPipeLogger());
        }

        private async Task InvokeOneAsync(PipeClient client)
        {
            if (_opts.Mode == "stream")
            {
                int chunkCount = 0;
                await client.StreamWithCallbackAsync(
                    "stress.stream",
                    new object[] { _opts.StreamChunkCount },
                    chunk => { chunkCount++; },
                    CancellationToken.None).ConfigureAwait(false);

                if (chunkCount != _opts.StreamChunkCount)
                    throw new InvalidOperationException(
                        "chunk 数不符：期望 " + _opts.StreamChunkCount + " 实际 " + chunkCount);
            }
            else
            {
                await client.InvokeAsync(_opts.MethodName, _opts.Args).ConfigureAwait(false);
            }
        }

        private async Task ProgressLoopAsync(Func<long> getDone, Func<long> getFail,
            Stopwatch sw, DateTime? endTime, CancellationToken ct)
        {
            try { await Task.Delay(_opts.ProgressIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                var done = getDone();
                var fail = getFail();
                var elapsed = sw.Elapsed.TotalSeconds;
                var qps = elapsed > 0 ? done / elapsed : 0;

                string progress;
                if (_opts.DurationSeconds.HasValue && endTime.HasValue)
                {
                    var remain = (endTime.Value - DateTime.UtcNow).TotalSeconds;
                    if (remain < 0) remain = 0;
                    progress = string.Format(
                        "[{0:hh\\:mm\\:ss}] done={1:N0} fail={2} qps={3:N0} 剩余 {4:F1}s",
                        sw.Elapsed, done, fail, qps, remain);
                }
                else
                {
                    var total = _opts.TotalRequests;
                    var pct = total > 0 ? (done * 100.0 / total) : 0;
                    progress = string.Format(
                        "[{0:hh\\:mm\\:ss}] done={1:N0}/{2:N0} ({3:F1}%) fail={4} qps={5:N0}",
                        sw.Elapsed, done, total, pct, fail, qps);
                }
                Console.WriteLine(progress);

                try { await Task.Delay(_opts.ProgressIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private StressResult BuildResult(List<double>[] perClientLatencies,
            List<string>[] perClientErrors, long done, long fail, TimeSpan elapsed)
        {
            int totalCount = 0;
            for (int i = 0; i < perClientLatencies.Length; i++)
                totalCount += perClientLatencies[i].Count;

            var all = new double[totalCount];
            int k = 0;
            for (int i = 0; i < perClientLatencies.Length; i++)
            {
                var list = perClientLatencies[i];
                for (int j = 0; j < list.Count; j++) all[k++] = list[j];
            }
            Array.Sort(all);

            var r = new StressResult
            {
                TotalRequests = done + fail,
                Succeeded = done,
                Failed = fail,
                Elapsed = elapsed,
                Qps = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0,
                MinMs = all.Length > 0 ? all[0] : 0,
                MaxMs = all.Length > 0 ? all[all.Length - 1] : 0,
                AvgMs = ComputeAvg(all),
                P50Ms = StressResult.Percentile(all, 50),
                P90Ms = StressResult.Percentile(all, 90),
                P95Ms = StressResult.Percentile(all, 95),
                P99Ms = StressResult.Percentile(all, 99)
            };

            var seen = new HashSet<string>();
            for (int i = 0; i < perClientErrors.Length && r.ErrorSamples.Count < 10; i++)
            {
                for (int j = 0; j < perClientErrors[i].Count && r.ErrorSamples.Count < 10; j++)
                {
                    var msg = perClientErrors[i][j];
                    if (seen.Add(msg)) r.ErrorSamples.Add(msg);
                }
            }
            return r;
        }

        private static double ComputeAvg(double[] sorted)
        {
            if (sorted.Length == 0) return 0;
            double sum = 0;
            for (int i = 0; i < sorted.Length; i++) sum += sorted[i];
            return sum / sorted.Length;
        }
    }
}