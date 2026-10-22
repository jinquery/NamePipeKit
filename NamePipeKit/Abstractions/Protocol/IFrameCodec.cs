// ════════════════════════════════════════════════════════════════════════════
//  文件：IFrameCodec.cs
//  作用：帧编解码抽象接口。V1/V2/未来版本各自实现。
//  设计原则：
//    - 传输层只依赖 IFrameCodec，不依赖具体版本实现；
//    - 支持运行时切换（SwitchCodec），为协议协商留路。
// ════════════════════════════════════════════════════════════════════════════
using System.IO;

namespace NamePipeKit.Abstractions.Protocol
{
    /// <summary>
    /// 帧编解码器接口。
    /// <para>
    /// <b>约定</b>：同一连接上，客户端和服务的 codec.Version 必须一致。
    /// 若要跨版本通信，需要走 Handshake（未来扩展）协商后 SwitchCodec。
    /// </para>
    /// </summary>
    public interface IFrameCodec
    {
        /// <summary>本 codec 支持的协议版本（对应帧头第 3 字节）。</summary>
        byte Version { get; }

        /// <summary>
        /// 同步写出一个完整帧到流。
        /// <para><b>注意</b>：实现方不得调用 Stream.Flush——命名管道上 Flush 会阻塞等待对端读取。</para>
        /// </summary>
        void Write(Stream stream, PipeFrame frame);

        /// <summary>
        /// 同步从流读取一个完整帧。
        /// <para>对端关闭时抛 <see cref="EndOfStreamException"/>；协议错误抛 <see cref="InvalidDataException"/>。</para>
        /// </summary>
        PipeFrame Read(Stream stream);
    }
}