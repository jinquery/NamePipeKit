// ════════════════════════════════════════════════════════════════════════════
//  文件：NamedPipeServerTransport.cs
//  作用：命名管道服务端传输层。
//
//  【核心设计 1】Accept 用专用线程 + 同步 WaitForConnection
//    WaitForConnectionAsync 是伪异步，会抢 ThreadPool。
//    用专用 Thread 同步等待，ThreadPool 完全空闲给 TCS continuation。
//
//  【核心设计 2】每个连接一个专用线程
//    连接建立后整个"读-处理-写"循环在该专用线程同步执行，
//    依赖 NamedPipeConnection 返回已完成的 Task（ReadFrameAsync 内部同步）。
//
//  【核心设计 3】支持运行时 SwitchCodec
//    _codec 用 volatile 修饰，写锁保护切换。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Abstractions.Transport;
using NamePipeKit.Protocol;

namespace NamePipeKit.Transport
{
    /// <summary>命名管道服务端传输层（专用线程 + 同步 IO）。</summary>
    public sealed class NamedPipeServerTransport : IPipeServerTransport
    {
        /// <summary>等待槽位上限。32 个足以服务上千并发。</summary>
        private const int DefaultWaitSlotCap = 32;

        private readonly string _pipeName;
        private readonly int _maxInstances;
        private readonly int _waitSlots;
        private readonly bool _allowNetwork;
        private readonly IPipeLogger _logger;
        private readonly IFrameCodec _codec;

        /// <summary>构造函数。</summary>
        /// <param name="pipeName">管道名（客户端必须一致）。</param>
        /// <param name="logger">日志器。</param>
        /// <param name="maxInstances">Windows 层允许同时存在的最大连接数（≥ 并发峰值）。</param>
        /// <param name="allowNetwork">是否允许跨机器访问。</param>
        /// <param name="codec">帧编解码器（默认 FrameCodecV1）。</param>
        public NamedPipeServerTransport(string pipeName, IPipeLogger logger,
            int maxInstances = 10, bool allowNetwork = false, IFrameCodec codec = null)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("pipeName 不能为空");
            if (maxInstances <= 0) throw new ArgumentOutOfRangeException("maxInstances");

            _pipeName = pipeName;
            _logger = logger;
            _maxInstances = maxInstances;
            // 等待槽位数与实例数解耦：32 个专用线程已足够"随时接下一个"
            _waitSlots = Math.Min(maxInstances, DefaultWaitSlotCap);
            if (_waitSlots < 1) _waitSlots = 1;
            _allowNetwork = allowNetwork;
            _codec = codec ?? new FrameCodecV1(logger);
        }

        /// <inheritdoc />
        public async Task ListenAsync(
            Func<IPipeConnection, CancellationToken, Task> connectionHandler,
            CancellationToken cancellationToken)
        {
            _logger.Info("[Server] 监听 " + _pipeName
                + "，maxInstances=" + _maxInstances
                + "，等待槽位=" + _waitSlots
                + "，codec=V" + _codec.Version);

            // 启动 _waitSlots 个专用接受线程
            for (int i = 0; i < _waitSlots; i++)
            {
                var slotIndex = i;
                var t = new Thread(() => SlotLoop(slotIndex, connectionHandler, cancellationToken))
                {
                    IsBackground = true,
                    Name = "PipeAccept-" + slotIndex
                };
                t.Start();
            }

            // 主流程只负责挂起，不参与实际工作
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { _logger.Info("[Server] 监听已停止"); }
        }

        /// <summary>接受槽位循环：同步等待连接 → 每连接开新线程处理 → 继续等待。</summary>
        private void SlotLoop(int slotIndex,
            Func<IPipeConnection, CancellationToken, Task> connectionHandler,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream stream = null;
                try
                {
                    stream = CreateServerStream();
                    stream.WaitForConnection();   // ★ 专用线程同步等待，不占 ThreadPool

                    if (ct.IsCancellationRequested) { try { stream.Dispose(); } catch { } break; }

                    var connId = Guid.NewGuid().ToString("N");
                    var connection = new NamedPipeConnection(connId, stream, _codec, _logger);
                    stream = null; // 所有权转交 connection

                    // 每个连接一个专用线程
                    var connThread = new Thread(() =>
                    {
                        try
                        {
                            // handler 内部所有 await 都是已完成的 Task，因此实际同步执行
                            connectionHandler(connection, ct).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) { /* 正常关闭 */ }
                        catch (Exception ex) { _logger.Error("[" + connId + "] 连接异常", ex); }
                        finally { connection.Dispose(); }
                    })
                    {
                        IsBackground = true,
                        Name = "PipeConn-" + connId.Substring(0, 8)
                    };
                    connThread.Start();
                }
                catch (OperationCanceledException)
                {
                    if (stream != null) { try { stream.Dispose(); } catch { } }
                    break;
                }
                catch (Exception ex)
                {
                    if (stream != null) { try { stream.Dispose(); } catch { } }
                    if (ct.IsCancellationRequested) break;
                    _logger.Warn("[Accept-" + slotIndex + "] " + ex.Message);
                    try { Thread.Sleep(100); } catch { break; }
                }
            }
        }

        /// <summary>创建服务端管道流。</summary>
        private NamedPipeServerStream CreateServerStream()
        {
            const PipeOptions options = PipeOptions.Asynchronous;
            if (!_allowNetwork)
            {
                return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, _maxInstances,
                    PipeTransmissionMode.Byte, options);
            }

            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, _maxInstances,
                PipeTransmissionMode.Byte, options, 0, 0, security);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// 命名管道连接实现。
    /// <para>
    /// <b>核心</b>：ReadFrameAsync / WriteFrameAsync 内部同步 IO，
    /// 返回已完成的 Task，上层 await 不会切换线程。
    /// </para>
    /// </summary>
    internal sealed class NamedPipeConnection : IPipeConnection
    {
        private readonly IPipeLogger _logger;
        private readonly NamedPipeServerStream _stream;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private volatile IFrameCodec _codec;      // ★ volatile 保证 SwitchCodec 后的可见性
        private bool _disposed;

        public string ConnectionId { get; private set; }

        public NamedPipeConnection(string id, NamedPipeServerStream stream,
            IFrameCodec codec, IPipeLogger logger)
        {
            ConnectionId = id;
            _stream = stream;
            _codec = codec;
            _logger = logger;
        }

        /// <summary>同步读 → 返回已完成 Task。</summary>
        public Task<PipeFrame> ReadFrameAsync(CancellationToken ct)
        {
            return Task.FromResult(_codec.Read(_stream));
        }

        /// <summary>同步写 → 返回已完成 Task。用同步 Wait 拿锁，不占 ThreadPool。</summary>
        public Task WriteFrameAsync(PipeFrame frame, CancellationToken ct)
        {
            _writeLock.Wait();
            try { _codec.Write(_stream, frame); }
            finally { _writeLock.Release(); }
            return Task.CompletedTask;
        }

        /// <summary>协议协商后切换 codec。</summary>
        public void SwitchCodec(IFrameCodec codec)
        {
            _writeLock.Wait();
            try { _codec = codec; }
            finally { _writeLock.Release(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_stream.IsConnected) _stream.Disconnect(); } catch { }
            try { _stream.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
        }
    }
}