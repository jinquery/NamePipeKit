// ════════════════════════════════════════════════════════════════════════════
//  文件：NamedPipeClientTransport.cs
//  作用：命名管道客户端传输层（单 Worker 线程）。
//
//  【性能模型：单线程】
//    每客户端 1 个专用 Worker 线程，所有请求串行处理：
//      从队列取 → 写请求 → 循环读响应帧 → 分发 → 下一请求
//    流式请求不是单独路径，而是"同一个循环里多读几帧"，因此不需要双线程。
//
//  【为什么不用双线程】
//    双线程（Writer + Reader）多一倍线程，上下文切换翻倍，
//    50 并发下 QPS 从 937 掉到 619（实测）。单线程足够支撑流式和推送。
//
//  【分发逻辑】
//    Response      → 完成当前请求的 TCS（普通请求）
//    ResponseChunk → 调 onChunk（当前请求是流式）
//    ResponseEnd   → 标记当前流式请求完成
//    ServerPush    → 触发 PushReceived 事件，不阻塞队列
//    Heartbeat     → 忽略
// ════════════════════════════════════════════════════════════════════════════
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Abstractions.Transport;
using NamePipeKit.Protocol;
using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace NamePipeKit.Transport
{
    /// <summary>命名管道客户端传输层（单 Worker 线程）。</summary>
    public sealed class NamedPipeClientTransport : IPipeClientTransport
    {
        private const int ConnectTimeoutMs = 10000;
        private const int MaxConnectRetries = 3;
        private const int QueueCapacity = 100000;

        private readonly string _pipeName;
        private readonly string _serverName;
        private readonly IPipeLogger _logger;
        private readonly IFrameCodec _codec;

        /// <summary>请求队列：单 Worker 线程串行消费。</summary>
        private readonly BlockingCollection<RequestItem> _queue =
            new BlockingCollection<RequestItem>(new ConcurrentQueue<RequestItem>(), QueueCapacity);

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _streamLock = new object();
        private readonly Thread _workerThread;
        private NamedPipeClientStream _stream;
        private bool _disposed;

        /// <summary>ServerPush 帧到达时触发。</summary>
        public Action<byte[]> PushReceived { get; set; }

        /// <summary>队列元素：普通请求带 Tcs；流式请求额外带 OnChunk。</summary>
        private sealed class RequestItem
        {
            public PipeFrame Frame;
            public TaskCompletionSource<PipeFrame> Tcs;   // 普通请求返回响应帧；流式请求完成时为 null
            public Action<byte[]> OnChunk;                 // 流式请求：每个 chunk 的回调
            public bool IsStream;                          // true = 流式
        }

        public NamedPipeClientTransport(string pipeName, string serverName, IPipeLogger logger,
            IFrameCodec codec = null)
        {
            _pipeName = pipeName;
            _serverName = serverName;
            _logger = logger;
            _codec = codec ?? new Protocol.FrameCodecV1(logger);

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "PipeWorker"
            };
            _workerThread.Start();
        }

        public Task ConnectAsync(CancellationToken ct) { return Task.CompletedTask; }

        /// <inheritdoc />
        public Task<PipeFrame> SendAndReceiveAsync(PipeFrame frame, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException("NamedPipeClientTransport");

            var tcs = new TaskCompletionSource<PipeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new RequestItem { Frame = frame, Tcs = tcs, IsStream = false };

            try
            {
                if (!_queue.TryAdd(item, 10000, ct))
                {
                    tcs.TrySetException(new InvalidOperationException("请求队列已满"));
                }
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(); }
            catch (InvalidOperationException) { tcs.TrySetException(new ObjectDisposedException("Transport")); }

            return tcs.Task;
        }

        /// <inheritdoc />
        public Task SendStreamAsync(PipeFrame frame, Action<byte[]> onChunk, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException("NamedPipeClientTransport");
            if (onChunk == null) throw new ArgumentNullException("onChunk");

            var tcs = new TaskCompletionSource<PipeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new RequestItem { Frame = frame, Tcs = tcs, OnChunk = onChunk, IsStream = true };

            try
            {
                if (!_queue.TryAdd(item, 10000, ct))
                    tcs.TrySetException(new InvalidOperationException("请求队列已满"));
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(); }
            catch (InvalidOperationException) { tcs.TrySetException(new ObjectDisposedException("Transport")); }

            // 流完成后返回的 Task 只关心"完成"或"异常"，返回值无用
            return tcs.Task;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  单 Worker 线程：串行处理请求，一个请求处理完才拿下一个
        // ══════════════════════════════════════════════════════════════════════
        private void WorkerLoop()
        {
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        ProcessOne(item);
                    }
                    catch (Exception ex)
                    {
                        item.Tcs.TrySetException(ex);
                        InvalidateStream();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Error("[Worker] 异常退出", ex); }
        }

        /// <summary>
        /// 处理一个请求：写 → 循环读（可能多帧）→ 完成。
        /// 流式请求在这个循环里多次迭代 chunk，普通请求只读到 Response 就结束。
        /// </summary>
        private void ProcessOne(RequestItem item)
        {
            var stream = GetOrConnectStream();

            // 1. 写请求
            _codec.Write(stream, item.Frame);

            // 2. 读响应（可能 1 帧，也可能 Response + N×Chunk + End）
            while (true)
            {
                var frame = _codec.Read(stream);

                switch (frame.MessageType)
                {
                    case PipeMessageType.Response:
                        // 普通请求的响应，或流式请求的"流开始头"
                        if (!item.IsStream)
                        {
                            item.Tcs.TrySetResult(frame);
                            return;
                        }
                        // 流式：继续读 chunk
                        break;

                    case PipeMessageType.ResponseChunk:
                        // 只有流式请求会有；直接用回调处理，避免中间态
                        if (item.IsStream && item.OnChunk != null)
                        {
                            try { item.OnChunk(frame.Body); }
                            catch (Exception ex) { _logger.Warn("[Stream] chunk 回调异常：" + ex.Message); }
                        }
                        break;

                    case PipeMessageType.ResponseEnd:
                        // 流式结束
                        if (item.IsStream) item.Tcs.TrySetResult(null);
                        return;

                    case PipeMessageType.ServerPush:
                        // 服务端主动推送：不阻塞当前请求，处理完继续读
                        var h = PushReceived;
                        if (h != null)
                        {
                            try { h(frame.Body); }
                            catch (Exception ex) { _logger.Warn("[Push] 回调异常：" + ex.Message); }
                        }
                        break;

                    case PipeMessageType.Heartbeat:
                        // 心跳：忽略
                        break;

                    default:
                        _logger.Warn("[Worker] 忽略未知帧类型：" + frame.MessageType);
                        break;
                }
            }
        }

        private NamedPipeClientStream GetOrConnectStream()
        {
            lock (_streamLock)
            {
                if (_stream != null && _stream.IsConnected) return _stream;
                TryDisposeStream();

                Exception last = null;
                for (int i = 1; i <= MaxConnectRetries; i++)
                {
                    NamedPipeClientStream s = null;
                    try
                    {
                        s = new NamedPipeClientStream(_serverName, _pipeName,
                            PipeDirection.InOut, PipeOptions.Asynchronous);
                        s.Connect(ConnectTimeoutMs);
                        _stream = s;
                        return s;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        if (s != null) { try { s.Dispose(); } catch { } }
                        _logger.Warn("[Connect] 第 " + i + " 次失败：" + ex.Message);
                        if (i < MaxConnectRetries) Thread.Sleep(200 * i);
                    }
                }
                throw last;
            }
        }

        private void InvalidateStream() { lock (_streamLock) { TryDisposeStream(); } }

        private void TryDisposeStream()
        {
            try { if (_stream != null) _stream.Dispose(); } catch { }
            _stream = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            try { _queue.CompleteAdding(); } catch { }
            InvalidateStream();
            try { _cts.Dispose(); } catch { }
        }
    }
}