// ════════════════════════════════════════════════════════════════════════════
//  文件：PipeProtocol.cs
//  作用：协议数据模型。无 Channel / IAsyncEnumerable / StreamHandle 依赖。
//  C# 7.3 兼容。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;

namespace NamePipeKit.Abstractions.Protocol
{
    /// <summary>帧消息类型。</summary>
    public enum PipeMessageType : byte
    {
        Request = 1,
        Response = 2,
        Heartbeat = 3,
        Handshake = 4,
        Error = 5,
        Cancel = 6,
        ServerPush = 7,
        ResponseChunk = 8,
        ResponseEnd = 9,
    }

    public static class PipeProtocol
    {
        public static readonly byte[] Magic = new byte[] { 0x50, 0x50 };
        public const byte Version = 1;
        public const int FixedHeaderSize = 21;
        public const int CrcSize = 4;
        public const int MaxFrameSize = 16 * 1024 * 1024;
    }

    public static class PipeStatusCode
    {
        public const string Success = "0000";
        public const string BadRequest = "1000";
        public const string EmptyPayload = "1001";
        public const string UnsupportedContent = "1002";
        public const string MethodNotFound = "2000";
        public const string ArgBindFailed = "3000";
        public const string InvokeFailed = "3001";
        public const string ServerError = "5000";
        public const string TransportError = "5001";
    }

    public static class PipeContentTypes
    {
        public const string CallMethod = "CALLMETHOD";
        public const string Message = "MESSAGE";
        public const string Subscribe = "SUBSCRIBE";
        public const string Unsubscribe = "UNSUBSCRIBE";
        public const string Event = "EVENT";
        public const string Stream = "STREAM";
    }

    public sealed class PipeFrame
    {
        public PipeMessageType MessageType { get; set; }
        public byte Flags { get; set; }
        public long SequenceId { get; set; }
        public byte[] Header { get; set; }
        public byte[] Body { get; set; }
        public PipeFrame() { Header = new byte[0]; Body = new byte[0]; }
    }

    public sealed class PipeRequestHeader
    {
        public string ContentType { get; set; }
        public string Sender { get; set; }
        public string MethodName { get; set; }
        public string TraceId { get; set; }
        public string MessageHeader { get; set; }
        public long Timestamp { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
        public PipeRequestHeader() { ContentType = ""; Sender = ""; TraceId = ""; }
    }

    public sealed class PipeRequestBody
    {
        public object[] Args { get; set; }
        public string Data { get; set; }
    }

    public sealed class PipeResponseHeader
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public long DurationMs { get; set; }
        public string TraceId { get; set; }
        public PipeResponseHeader() { Code = PipeStatusCode.Success; Message = "OK"; TraceId = ""; }
    }

    public sealed class PipeResponseBody { public object Data { get; set; } }

    public sealed class PipeRequest
    {
        public string ContentType { get; set; }
        public string Sender { get; set; }
        public string MethodName { get; set; }
        public string TraceId { get; set; }
        public object[] Args { get; set; }
        public string Data { get; set; }
        public string MessageHeader { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
        public PipeRequest() { ContentType = ""; Sender = ""; TraceId = ""; }
    }

    public sealed class PipeResponse
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
        public long DurationMs { get; set; }
        public Func<Action<byte[]>, Action, System.Threading.Tasks.Task> StreamFactory { get; set; }
        public PipeResponse() { Code = PipeStatusCode.Success; Message = "OK"; }

        public static PipeResponse Ok(object data = null) =>
            new PipeResponse { Code = PipeStatusCode.Success, Message = "OK", Data = data };

        public static PipeResponse Fail(string code, string message) =>
            new PipeResponse { Code = code, Message = message };

        public static PipeResponse Stream(object headerData,
            Func<Action<byte[]>, Action, System.Threading.Tasks.Task> streamFactory) =>
            new PipeResponse
            {
                Code = PipeStatusCode.Success,
                Message = "OK",
                Data = headerData,
                StreamFactory = streamFactory
            };
    }
}