// ════════════════════════════════════════════════════════════════════════════
//  文件：PipeServer.cs
//  作用：服务端核心编排。读帧 → 转请求 → 走中间件管道 → 写响应/推 chunk。
//  关键：
//    1. _pipeline 构造时构建一次，5 万请求不再重复构建闭包；
//    2. PipeContext 传入 conn，Handler 可主动推 ServerPush；
//    3. 响应带 StreamFactory 时启动后台线程推 chunk。
// ════════════════════════════════════════════════════════════════════════════
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Abstractions.Serialization;
using NamePipeKit.Abstractions.Transport;
using NamePipeKit.Handling;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NamePipeKit.Server
{
    /// <summary>服务端核心。</summary>
    public sealed class PipeServer : IDisposable
    {
        private readonly IPipeServerTransport _transport;
        private readonly IPipeSerializer _serializer;
        private readonly IPipeLogger _logger;
        private readonly HandlerRegistry _handlers;
        private readonly IPipeMiddleware[] _middlewares;
        private readonly Func<Type, object> _serviceFactory;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>★ 缓存的中间件管道，构造时构建一次，所有请求复用。</summary>
        private readonly PipeRequestDelegate _pipeline;

        private bool _disposed;

        internal PipeServer(IPipeServerTransport transport, IPipeSerializer serializer,
            IPipeLogger logger, HandlerRegistry handlers, IPipeMiddleware[] middlewares,
            Func<Type, object> serviceFactory)
        {
            _transport = transport;
            _serializer = serializer;
            _logger = logger;
            _handlers = handlers;
            _middlewares = middlewares;
            _serviceFactory = serviceFactory;
            _pipeline = BuildPipelineOnce();
        }

        /// <summary>启动服务端（返回 Task 一直运行到 Stop 或异常）。</summary>
        public Task StartAsync()
        {
            _logger.Info("[PipeServer] 启动");
            return _transport.ListenAsync(HandleConnectionAsync, _cts.Token);
        }

        /// <summary>停止服务端。</summary>
        public void Stop()
        {
            _cts.Cancel();
            _transport.Dispose();
            _logger.Info("[PipeServer] 已停止");
        }

        /// <summary>单连接主循环：读一帧 → 处理 → 写响应/推流，直到连接断开。</summary>
        private async Task HandleConnectionAsync(IPipeConnection connection, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                PipeFrame frame;
                try { frame = await connection.ReadFrameAsync(ct).ConfigureAwait(false); }
                catch (EndOfStreamException) { break; }                       // 对端关闭
                catch (OperationCanceledException) { break; }                 // 服务端停止
                catch (Exception ex)
                {
                    _logger.Warn("[" + connection.ConnectionId + "] 读帧异常", ex);
                    break;
                }

                // 心跳：原样回显
                if (frame.MessageType == PipeMessageType.Heartbeat)
                {
                    var hb = new PipeFrame
                    {
                        MessageType = PipeMessageType.Heartbeat,
                        SequenceId = frame.SequenceId
                    };
                    await connection.WriteFrameAsync(hb, ct).ConfigureAwait(false);
                    continue;
                }

                // 只处理 Request，其它帧忽略
                if (frame.MessageType != PipeMessageType.Request) continue;

                PipeResponse response;
                try { response = await ProcessAsync(connection, frame, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.Error("请求处理异常", ex);
                    response = PipeResponse.Fail(PipeStatusCode.ServerError, ex.Message);
                }

                try { await WriteResponseAsync(connection, frame.SequenceId, response, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.Warn("写响应失败", ex);
                    break;
                }
            }
        }

        /// <summary>从帧解析请求 → 构建上下文 → 走管道 → 返回响应。</summary>
        private async Task<PipeResponse> ProcessAsync(IPipeConnection conn, PipeFrame frame, CancellationToken ct)
        {
            PipeRequestHeader header;
            PipeRequestBody body;
            try
            {
                header = _serializer.Deserialize<PipeRequestHeader>(frame.Header) ?? new PipeRequestHeader();
                body = _serializer.Deserialize<PipeRequestBody>(frame.Body) ?? new PipeRequestBody();
            }
            catch (Exception ex)
            {
                return PipeResponse.Fail(PipeStatusCode.BadRequest, "请求帧解析失败：" + ex.Message);
            }

            var request = new PipeRequest
            {
                ContentType = header.ContentType,
                Sender = header.Sender,
                MethodName = header.MethodName,
                TraceId = string.IsNullOrEmpty(header.TraceId) ? Guid.NewGuid().ToString("N") : header.TraceId,
                Args = body.Args,
                Data = body.Data,
                MessageHeader = header.MessageHeader,
                Metadata = header.Metadata
            };

            // ★ 构造上下文：传入 conn（Handler 可主动推）；TraceId 贯穿链路
            var ctx = new PipeContext(conn.ConnectionId, conn, request, _serviceFactory, _logger, request.TraceId);

            // 走缓存的管道
            var response = await _pipeline(ctx).ConfigureAwait(false);

            // TimingMiddleware 写入的耗时，转存到响应
            object dur;
            if (ctx.Items.TryGetValue("__duration_ms", out dur) && dur is long)
                response.DurationMs = (long)dur;

            return response;
        }

        /// <summary>构建中间件管道一次（构造时调用）。</summary>
        private PipeRequestDelegate BuildPipelineOnce()
        {
            // 终点：按 ContentType 路由到对应 Handler
            PipeRequestDelegate terminal = async ctx =>
            {
                var handler = _handlers.Resolve(ctx.Request.ContentType);
                if (handler == null)
                    return PipeResponse.Fail(PipeStatusCode.UnsupportedContent,
                        "不支持的 ContentType：" + ctx.Request.ContentType
                        + "，支持：" + string.Join(",", _handlers.SupportedTypes));

                return await handler.HandleAsync(ctx.Request, ctx, CancellationToken.None).ConfigureAwait(false);
            };

            // 逆序包裹中间件
            var next = terminal;
            for (int i = _middlewares.Length - 1; i >= 0; i--)
            {
                var mw = _middlewares[i];
                var inner = next;
                next = ctx => mw.InvokeAsync(ctx, inner);
            }
            return next;
        }

        /// <summary>写响应；若响应带 StreamFactory，则启动后台线程推送 chunk。</summary>
        private async Task WriteResponseAsync(IPipeConnection conn, long seqId, PipeResponse response, CancellationToken ct)
        {
            // ── 1. 写 Response 帧 ──
            var header = new PipeResponseHeader
            {
                Code = response.Code,
                Message = response.Message,
                DurationMs = response.DurationMs,
                TraceId = ""
            };
            var body = new PipeResponseBody { Data = response.Data };
            var frame = new PipeFrame
            {
                MessageType = PipeMessageType.Response,
                SequenceId = seqId,
                Header = _serializer.Serialize(header, typeof(PipeResponseHeader)),
                Body = _serializer.Serialize(body, typeof(PipeResponseBody))
            };
            await conn.WriteFrameAsync(frame, ct).ConfigureAwait(false);

            // ── 2. 流式响应：后台线程推 chunk ──
            var streamFactory = response.StreamFactory;
            if (streamFactory != null)
            {
                Task.Run(async () =>
                {
                    // push 回调：把每个 chunk 包装成 ResponseChunk 帧
                    var push = new Action<byte[]>(chunk =>
                    {
                        try
                        {
                            var cf = new PipeFrame
                            {
                                MessageType = PipeMessageType.ResponseChunk,
                                SequenceId = seqId,
                                Body = chunk ?? new byte[0]
                            };
                            conn.WriteFrameAsync(cf, ct).GetAwaiter().GetResult();
                        }
                        catch (Exception ex) { _logger.Warn("[Stream] push 失败", ex); }
                    });

                    // complete 回调：推 ResponseEnd 结束标记
                    var complete = new Action(() =>
                    {
                        try
                        {
                            var ef = new PipeFrame
                            {
                                MessageType = PipeMessageType.ResponseEnd,
                                SequenceId = seqId
                            };
                            conn.WriteFrameAsync(ef, ct).GetAwaiter().GetResult();
                        }
                        catch (Exception ex) { _logger.Warn("[Stream] end 失败", ex); }
                    });

                    try { await streamFactory(push, complete).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        _logger.Error("[Stream] 流式响应异常", ex);
                        complete();   // 出错也要发结束帧，避免客户端一直等
                    }
                });
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            try { _transport.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}