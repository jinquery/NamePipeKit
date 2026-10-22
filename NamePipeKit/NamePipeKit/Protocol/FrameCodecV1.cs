// ════════════════════════════════════════════════════════════════════════════
//  文件：FrameCodecV1.cs
//  作用：协议 V1 的帧编解码实现。
//
//  【核心设计 1】全部用同步 Stream.Read/Write
//    NamedPipeStream.ReadAsync 在 Windows 上是伪异步，会占用 ThreadPool 线程，
//    高并发下抢空线程池导致饥饿。改用同步 API + 专用线程反而更高效。
//
//  【核心设计 2】绝不调用 Flush
//    PipeStream.Flush 等价于 FlushFileBuffers，会阻塞等待对端读取，
//    每写一帧多等 30~40ms，是 QPS 上不去的头号杀手。
//
//  【核心设计 3】大端序 + CRC32 校验
//    网络字节序统一，CRC32 保证帧完整性。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.IO;
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.Protocol
{
    /// <summary>协议 V1 帧编解码器。</summary>
    public sealed class FrameCodecV1 : IFrameCodec
    {
        private readonly IPipeLogger _logger;
        private readonly int _maxFrameSize;

        /// <summary>协议版本号（对应帧头第 3 字节）。</summary>
        public byte Version { get { return PipeProtocol.Version; } }

        /// <summary>构造函数。</summary>
        /// <param name="logger">日志器。</param>
        /// <param name="maxFrameSize">单帧最大字节数，默认 16MB。</param>
        public FrameCodecV1(IPipeLogger logger, int maxFrameSize = PipeProtocol.MaxFrameSize)
        {
            _logger = logger;
            _maxFrameSize = maxFrameSize;
        }

        /// <summary>同步写出一个完整帧。写完后不 Flush。</summary>
        public void Write(Stream stream, PipeFrame frame)
        {
            var buffer = BuildBuffer(frame);
            stream.Write(buffer, 0, buffer.Length);
            // ★★★ 绝不调用 stream.Flush() ★★★
            // 数据写入内核缓冲区后，对端会立即被唤醒，无需应用层刷新。
        }

        /// <summary>同步读取一个完整帧。</summary>
        public PipeFrame Read(Stream stream)
        {
            // ── 1. 读 21 字节固定头 ──
            var head = new byte[PipeProtocol.FixedHeaderSize];
            ReadExact(stream, head, 0, head.Length);

            // ── 2. 校验魔数和版本 ──
            if (head[0] != PipeProtocol.Magic[0] || head[1] != PipeProtocol.Magic[1])
                throw new InvalidDataException("协议魔数不匹配。");
            if (head[2] != PipeProtocol.Version)
                throw new InvalidDataException("协议版本不兼容：" + head[2]);

            // ── 3. 解析长度字段 ──
            var seqId = ReadInt64BigEndian(head, 5);
            var headerLen = ReadInt32BigEndian(head, 13);
            var bodyLen = ReadInt32BigEndian(head, 17);
            if (headerLen < 0 || bodyLen < 0 || headerLen + bodyLen > _maxFrameSize)
                throw new InvalidDataException("帧长度非法。");

            // ── 4. 读载荷（Header + Body + CRC） ──
            var payload = new byte[headerLen + bodyLen + PipeProtocol.CrcSize];
            ReadExact(stream, payload, 0, payload.Length);

            // ── 5. 校验 CRC32 ──
            var expectedCrc = ReadUInt32BigEndian(payload, headerLen + bodyLen);
            var actualCrc = Crc32.Compute(payload, 0, headerLen + bodyLen);
            if (expectedCrc != actualCrc)
                throw new InvalidDataException("CRC32 校验失败。");

            // ── 6. 组装 PipeFrame ──
            var frame = new PipeFrame
            {
                MessageType = (PipeMessageType)head[4],
                Flags = head[3],
                SequenceId = seqId,
                Header = new byte[headerLen],
                Body = new byte[bodyLen]
            };
            Buffer.BlockCopy(payload, 0, frame.Header, 0, headerLen);
            Buffer.BlockCopy(payload, headerLen, frame.Body, 0, bodyLen);
            return frame;
        }

        /// <summary>把 PipeFrame 组装为可写出的字节缓冲区。</summary>
        private byte[] BuildBuffer(PipeFrame frame)
        {
            var totalLen = PipeProtocol.FixedHeaderSize
                         + frame.Header.Length
                         + frame.Body.Length
                         + PipeProtocol.CrcSize;
            if (totalLen > _maxFrameSize)
                throw new InvalidOperationException("帧超出大小限制：" + _maxFrameSize);

            var buffer = new byte[totalLen];

            // 固定头（21B，大端）
            buffer[0] = PipeProtocol.Magic[0];
            buffer[1] = PipeProtocol.Magic[1];
            buffer[2] = PipeProtocol.Version;
            buffer[3] = frame.Flags;
            buffer[4] = (byte)frame.MessageType;
            WriteInt64BigEndian(buffer, 5, frame.SequenceId);
            WriteInt32BigEndian(buffer, 13, frame.Header.Length);
            WriteInt32BigEndian(buffer, 17, frame.Body.Length);

            // Header + Body
            Buffer.BlockCopy(frame.Header, 0, buffer, PipeProtocol.FixedHeaderSize, frame.Header.Length);
            Buffer.BlockCopy(frame.Body, 0, buffer,
                PipeProtocol.FixedHeaderSize + frame.Header.Length, frame.Body.Length);

            // CRC32（对 Header+Body 计算）
            var crcOffset = PipeProtocol.FixedHeaderSize + frame.Header.Length + frame.Body.Length;
            var crc = Crc32.Compute(buffer, PipeProtocol.FixedHeaderSize,
                frame.Header.Length + frame.Body.Length);
            WriteUInt32BigEndian(buffer, crcOffset, crc);

            return buffer;
        }

        /// <summary>读满 count 字节（同步 API 不保证一次读满）。</summary>
        private static void ReadExact(Stream s, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, offset + read, count - read);
                if (n == 0) throw new EndOfStreamException("对端已关闭连接。");
                read += n;
            }
        }

        #region 大端读写（避免依赖 BinaryPrimitives，兼容 .NET Framework）
        private static void WriteInt32BigEndian(byte[] b, int o, int v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        private static int ReadInt32BigEndian(byte[] b, int o)
        { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }
        private static void WriteUInt32BigEndian(byte[] b, int o, uint v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        private static uint ReadUInt32BigEndian(byte[] b, int o)
        { return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3]; }
        private static void WriteInt64BigEndian(byte[] b, int o, long v)
        {
            b[o] = (byte)(v >> 56); b[o + 1] = (byte)(v >> 48); b[o + 2] = (byte)(v >> 40); b[o + 3] = (byte)(v >> 32);
            b[o + 4] = (byte)(v >> 24); b[o + 5] = (byte)(v >> 16); b[o + 6] = (byte)(v >> 8); b[o + 7] = (byte)v;
        }
        private static long ReadInt64BigEndian(byte[] b, int o)
        {
            return ((long)b[o] << 56) | ((long)b[o + 1] << 48) | ((long)b[o + 2] << 40) | ((long)b[o + 3] << 32)
                 | ((long)b[o + 4] << 24) | ((long)b[o + 5] << 16) | ((long)b[o + 6] << 8) | (long)b[o + 7];
        }
        #endregion
    }

   
}