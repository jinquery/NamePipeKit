using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;

namespace NamePipeKit.Protocol
{
    /// <summary>
    /// 帧编解码器（大端序）。
    ///
    /// <para><b>【关键设计 1】绝不调用 Flush / FlushAsync</b></para>
    /// <para>
    /// <c>PipeStream.Flush</c> 在命名管道上等价于 <c>FlushFileBuffers</c>，
    /// 它的语义是"阻塞等待对端把数据全部读走"。
    /// 每写一帧就 Flush 一次，就意味着每帧多等 30~40ms（Windows 定时器精度），
    /// 这是压测 QPS 上不去的头号元凶。数据只要 Write 出去就已经在内核缓冲区里，
    /// 对端会立即被唤醒，不需要应用层显式 Flush。
    /// </para>
    ///
    /// <para><b>【关键设计 2】使用同步 Read / Write，而非 ReadAsync / WriteAsync</b></para>
    /// <para>
    /// <c>NamedPipeStream.ReadAsync</c> 在 Windows 上是"伪异步"——
    /// 底层是同步阻塞包装，调用后实际会占用一个 ThreadPool 线程直到读取返回。
    /// 高并发下 N 个连接同时在 await ReadAsync，会把 ThreadPool 抢空，
    /// 导致真正的回调（比如 TCS continuation）无法被调度，形成"饥饿"。
    /// 同步 API 虽然会阻塞当前线程，但如果我们把当前线程换成"专用线程"，
    /// 就不占用 ThreadPool，反而更高效。
    /// </para>
    /// </summary>
    public sealed class FrameCodec
    {
        private readonly IPipeLogger _logger;
        private readonly int _maxFrameSize;

        /// <summary>构造函数。</summary>
        /// <param name="logger">日志器。</param>
        /// <param name="maxFrameSize">单帧最大字节数（防止恶意构造超大帧）。</param>
        public FrameCodec(IPipeLogger logger, int maxFrameSize = PipeProtocol.MaxFrameSize)
        {
            _logger = logger;
            _maxFrameSize = maxFrameSize;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  同步 API（核心，高性能路径）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 同步写出一个完整帧。
        /// <para>
        /// 步骤：组装缓冲区（固定头 + Header + Body + CRC32）→ 一次 <c>Write</c> 写出去。
        /// <b>写完之后绝对不调用 Flush</b>。
        /// </para>
        /// </summary>
        /// <param name="stream">目标流。</param>
        /// <param name="frame">要写的帧。</param>
        public void Write(Stream stream, PipeFrame frame)
        {
            var buffer = BuildBuffer(frame);

            // 一次 Write 写完，数据进入内核缓冲区。
            // 注意：这里绝不调用 stream.Flush()！
            // 原因见类顶部注释【关键设计 1】。
            stream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// 同步读取一个完整帧。
        /// <para>
        /// 步骤：读 21 字节固定头 → 校验魔数/版本 → 按 headerLen / bodyLen 读完整载荷 → 校验 CRC32。
        /// </para>
        /// </summary>
        /// <param name="stream">来源流。</param>
        /// <returns>读取到的帧。</returns>
        /// <exception cref="InvalidDataException">协议错误（魔数/版本/CRC/长度非法）。</exception>
        /// <exception cref="EndOfStreamException">对端关闭连接。</exception>
        public PipeFrame Read(Stream stream)
        {
            // ── 步骤 1：读固定头 21 字节 ──
            var head = new byte[PipeProtocol.FixedHeaderSize];
            ReadExact(stream, head, 0, head.Length);

            // ── 步骤 2：校验魔数和版本 ──
            if (head[0] != PipeProtocol.Magic[0] || head[1] != PipeProtocol.Magic[1])
                throw new InvalidDataException("协议魔数不匹配。");
            if (head[2] != PipeProtocol.Version)
                throw new InvalidDataException("协议版本不兼容：" + head[2]);

            // ── 步骤 3：解析出长度字段 ──
            var seqId = ReadInt64BigEndian(head, 5);
            var headerLen = ReadInt32BigEndian(head, 13);
            var bodyLen = ReadInt32BigEndian(head, 17);

            // 防御性校验：长度必须在合法范围内，否则可能是协议污染
            if (headerLen < 0 || bodyLen < 0 || headerLen + bodyLen > _maxFrameSize)
                throw new InvalidDataException("帧长度非法。");

            // ── 步骤 4：读 Header + Body + CRC 载荷 ──
            var payload = new byte[headerLen + bodyLen + PipeProtocol.CrcSize];
            ReadExact(stream, payload, 0, payload.Length);

            // ── 步骤 5：校验 CRC32 ──
            var expectedCrc = ReadUInt32BigEndian(payload, headerLen + bodyLen);
            var actualCrc = Crc32.Compute(payload, 0, headerLen + bodyLen);
            if (expectedCrc != actualCrc)
                throw new InvalidDataException("CRC32 校验失败。");

            // ── 步骤 6：组装 PipeFrame ──
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

        // ═══════════════════════════════════════════════════════════════════
        //  异步包装（仅供向后兼容，内部仍是同步 IO）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 异步包装（内部同步写）。
        /// <para>
        /// <b>返回的是已完成的 Task</b>，调用方 <c>await</c> 时会同步继续执行，
        /// 不会发生线程切换，也不会占 ThreadPool。
        /// </para>
        /// </summary>
        public Task WriteAsync(Stream stream, PipeFrame frame, CancellationToken ct)
        {
            Write(stream, frame);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 异步包装（内部同步读）。
        /// <para>
        /// <b>返回的是已完成的 Task</b>，调用方 <c>await</c> 时同步拿到结果。
        /// 这样上层代码仍然是 async 风格（易于阅读），实际执行完全是同步的。
        /// </para>
        /// </summary>
        public Task<PipeFrame> ReadAsync(Stream stream, CancellationToken ct)
        {
            return Task.FromResult(Read(stream));
        }

        // ═══════════════════════════════════════════════════════════════════
        //  内部辅助方法
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 将帧序列化为二进制缓冲区（固定头 21B + Header + Body + CRC 4B）。
        /// </summary>
        private byte[] BuildBuffer(PipeFrame frame)
        {
            var totalLen = PipeProtocol.FixedHeaderSize
                           + frame.Header.Length
                           + frame.Body.Length
                           + PipeProtocol.CrcSize;
            if (totalLen > _maxFrameSize)
                throw new InvalidOperationException("帧超出大小限制：" + _maxFrameSize);

            var buffer = new byte[totalLen];

            // ── 固定头（21 字节，大端） ──
            buffer[0] = PipeProtocol.Magic[0];                 // 魔数高字节
            buffer[1] = PipeProtocol.Magic[1];                 // 魔数低字节
            buffer[2] = PipeProtocol.Version;                  // 版本
            buffer[3] = frame.Flags;                           // 标志位
            buffer[4] = (byte)frame.MessageType;               // 消息类型
            WriteInt64BigEndian(buffer, 5, frame.SequenceId);  // 序列号
            WriteInt32BigEndian(buffer, 13, frame.Header.Length);  // Header 长度
            WriteInt32BigEndian(buffer, 17, frame.Body.Length);    // Body 长度

            // ── Header + Body ──
            Buffer.BlockCopy(frame.Header, 0, buffer,
                PipeProtocol.FixedHeaderSize, frame.Header.Length);
            Buffer.BlockCopy(frame.Body, 0, buffer,
                PipeProtocol.FixedHeaderSize + frame.Header.Length, frame.Body.Length);

            // ── CRC32（对 Header + Body 计算） ──
            var crcOffset = PipeProtocol.FixedHeaderSize + frame.Header.Length + frame.Body.Length;
            var crc = Crc32.Compute(buffer, PipeProtocol.FixedHeaderSize,
                frame.Header.Length + frame.Body.Length);
            WriteUInt32BigEndian(buffer, crcOffset, crc);

            return buffer;
        }

        /// <summary>
        /// 从流中读满 <paramref name="count"/> 字节。
        /// <para>同步 API 不保证一次读满，必须循环。</para>
        /// </summary>
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

        #region 大端读写（不依赖 BinaryPrimitives，兼容 .NET Framework）

        private static void WriteInt32BigEndian(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static int ReadInt32BigEndian(byte[] buf, int offset)
        {
            return (buf[offset] << 24) | (buf[offset + 1] << 16)
                 | (buf[offset + 2] << 8) | buf[offset + 3];
        }

        private static void WriteUInt32BigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static uint ReadUInt32BigEndian(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16)
                 | ((uint)buf[offset + 2] << 8) | buf[offset + 3];
        }

        private static void WriteInt64BigEndian(byte[] buf, int offset, long value)
        {
            buf[offset] = (byte)(value >> 56);
            buf[offset + 1] = (byte)(value >> 48);
            buf[offset + 2] = (byte)(value >> 40);
            buf[offset + 3] = (byte)(value >> 32);
            buf[offset + 4] = (byte)(value >> 24);
            buf[offset + 5] = (byte)(value >> 16);
            buf[offset + 6] = (byte)(value >> 8);
            buf[offset + 7] = (byte)value;
        }

        private static long ReadInt64BigEndian(byte[] buf, int offset)
        {
            return ((long)buf[offset] << 56) |
                   ((long)buf[offset + 1] << 48) |
                   ((long)buf[offset + 2] << 40) |
                   ((long)buf[offset + 3] << 32) |
                   ((long)buf[offset + 4] << 24) |
                   ((long)buf[offset + 5] << 16) |
                   ((long)buf[offset + 6] << 8) |
                   (long)buf[offset + 7];
        }
        #endregion
    }

    /// <summary>
    /// 标准 CRC32（多项式 0xEDB88320）。
    /// <para>查表法，一次 8 位，比逐位计算快 8 倍。</para>
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        /// <summary>对数组指定区间计算 CRC32。</summary>
        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return ~crc;
        }
    }
}