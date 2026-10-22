// ════════════════════════════════════════════════════════════════════════════
//  文件：PipeClient.cs
//  作用：客户端封装。三种 API：
//    - InvokeAsync        ：普通调用
//    - SubscribeAsync     ：订阅推送
//    - StreamWithCallbackAsync：流式（回调式，C# 7.3 兼容）
//  无 Channel / IAsyncEnumerable / IAsyncEnumerable 依赖。
// ════════════════════════════════════════════════════════════════════════════
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Abstractions.Serialization;
using NamePipeKit.Logging;
using NamePipeKit.Protocol;
using NamePipeKit.Serialization;
using NamePipeKit.Transport;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NamePipeKit.Client
{
    public sealed class PipeClient : IDisposable
    {
        private readonly NamedPipeClientTransport _transport;
        private readonly IPipeSerializer _serializer;
        private readonly IPipeLogger _logger;
        private readonly string _sender;
        private long _seq = DateTime.UtcNow.Ticks;
        private bool _disposed;

        public event Action<byte[]> PushReceived;
        public IDictionary<string, string> DefaultMetadata { get; private set; }
            = new Dictionary<string, string>();

        public PipeClient(string pipeName, string serverName = ".", string sender = "client",
            IPipeLogger logger = null, IPipeSerializer serializer = null, IFrameCodec codec = null)
        {
            _logger = logger ?? new ConsolePipeLogger();
            _serializer = serializer ?? new JsonPipeSerializer();
            _sender = sender;
            _transport = new NamedPipeClientTransport(pipeName, serverName, _logger, codec);
            _transport.PushReceived = b =>
            {
                var h = PushReceived;
                if (h != null) try { h(b); } catch { }
            };
        }

        public async Task InvokeAsync(string methodName, params object[] args)
        {
            var resp = await InvokeCoreAsync(methodName, args, CancellationToken.None).ConfigureAwait(false);
            EnsureSuccess(resp);
        }

        public async Task<T> InvokeAsync<T>(string methodName, params object[] args)
        {
            var resp = await InvokeCoreAsync(methodName, args, CancellationToken.None).ConfigureAwait(false);
            EnsureSuccess(resp);
            if (resp.Data == null) return default(T);
            var json = JsonSerializer.Serialize(resp.Data);
            return _serializer.Deserialize<T>(Encoding.UTF8.GetBytes(json));
        }

        public async Task SendMessageAsync(string header, string data, CancellationToken ct = default(CancellationToken))
        {
            var resp = await SendRawAsync(PipeContentTypes.Message, null, null, header, data, ct).ConfigureAwait(false);
            EnsureSuccess(resp);
        }

        public async Task<PipeResponse> SendRawAsync(string contentType, string methodName,
            object[] args, string messageHeader, string data, CancellationToken ct = default(CancellationToken))
        {
            var reqHeader = NewHeader(contentType, methodName, messageHeader);
            var reqBody = new PipeRequestBody { Args = args, Data = data };
            var frame = BuildFrame(reqHeader, reqBody, NextSeq());
            var respFrame = await _transport.SendAndReceiveAsync(frame, ct).ConfigureAwait(false);
            return ParseResponse(respFrame);
        }

        public async Task SubscribeAsync(string topic, CancellationToken ct = default(CancellationToken))
        {
            var resp = await SendRawAsync(PipeContentTypes.Subscribe, null, null, null, topic, ct).ConfigureAwait(false);
            EnsureSuccess(resp);
        }

        public async Task UnsubscribeAsync(string topic, CancellationToken ct = default(CancellationToken))
        {
            var resp = await SendRawAsync(PipeContentTypes.Unsubscribe, null, null, null, topic, ct).ConfigureAwait(false);
            EnsureSuccess(resp);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ★ 流式 API：回调式（C# 7.3，零依赖）
        //  直接调 transport.SendStreamAsync，onChunk 在 Worker 线程上执行。
        // ══════════════════════════════════════════════════════════════════════
        public Task StreamWithCallbackAsync(
            string methodName, object[] args,
            Action<byte[]> onChunk,
            CancellationToken ct = default(CancellationToken))
        {
            if (onChunk == null) throw new ArgumentNullException("onChunk");

            var reqHeader = NewHeader(PipeContentTypes.Stream, methodName, null);
            var reqBody = new PipeRequestBody { Args = args };
            var frame = BuildFrame(reqHeader, reqBody, NextSeq());

            return _transport.SendStreamAsync(frame, onChunk, ct);
        }

        private PipeRequestHeader NewHeader(string contentType, string methodName, string messageHeader)
        {
            return new PipeRequestHeader
            {
                ContentType = contentType,
                Sender = _sender,
                MethodName = methodName,
                TraceId = Guid.NewGuid().ToString("N"),
                MessageHeader = messageHeader,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Metadata = DefaultMetadata.Count == 0
                    ? null : new Dictionary<string, string>(DefaultMetadata)
            };
        }

        private PipeFrame BuildFrame(PipeRequestHeader header, PipeRequestBody body, long seq)
        {
            return new PipeFrame
            {
                MessageType = PipeMessageType.Request,
                SequenceId = seq,
                Header = _serializer.Serialize(header, typeof(PipeRequestHeader)),
                Body = _serializer.Serialize(body, typeof(PipeRequestBody))
            };
        }

        private async Task<PipeResponse> InvokeCoreAsync(string methodName, object[] args, CancellationToken ct)
        {
            return await SendRawAsync(PipeContentTypes.CallMethod, methodName, args, null, null, ct)
                .ConfigureAwait(false);
        }

        private PipeResponse ParseResponse(PipeFrame respFrame)
        {
            var h = _serializer.Deserialize<PipeResponseHeader>(respFrame.Header) ?? new PipeResponseHeader();
            var b = _serializer.Deserialize<PipeResponseBody>(respFrame.Body) ?? new PipeResponseBody();
            return new PipeResponse { Code = h.Code, Message = h.Message, DurationMs = h.DurationMs, Data = b.Data };
        }

        private static void EnsureSuccess(PipeResponse resp)
        {
            if (resp.Code != PipeStatusCode.Success)
                throw new InvalidOperationException("管道调用失败：[" + resp.Code + "] " + resp.Message);
        }

        private long NextSeq() { return Interlocked.Increment(ref _seq); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport.Dispose();
        }
    }
}