// ════════════════════════════════════════════════════════════════════════════
//  文件：PipeServerBuilder.cs
//  作用：链式构建 PipeServer。
//  所有能力都通过注册注入，新增能力不需要改本类（开闭原则）。
//  典型用法见文件末尾 XML 注释。
// ════════════════════════════════════════════════════════════════════════════
using NamePipeKit.Abstractions.Handling;
using NamePipeKit.Abstractions.Logging;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Abstractions.Serialization;
using NamePipeKit.Abstractions.Transport;
using NamePipeKit.Discovery;
using NamePipeKit.Handling;
using NamePipeKit.Logging;
using NamePipeKit.Middleware;
using NamePipeKit.Protocol;
using NamePipeKit.Serialization;
using NamePipeKit.Transport;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace NamePipeKit.Server
{
    /// <summary>
    /// 服务端构建器。
    /// <para>
    /// 链式使用示例：
    /// <code>
    /// using (var server = new PipeServerBuilder("MyPipe")
    ///     .UseLogger(new ConsolePipeLogger())
    ///     .UseMaxInstances(100)
    ///     .ScanAssemblies(typeof(Program).Assembly)
    ///     .AddHandler(new SubscribeHandler(eventBus))
    ///     .OnMessageReceived((s, h, d) =&gt; Console.WriteLine(d))
    ///     .Build())
    /// {
    ///     server.StartAsync();
    ///     Console.ReadLine();
    ///     server.Stop();
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public sealed class PipeServerBuilder
    {
        private readonly string _pipeName;
        private int _maxInstances = 10;
        private bool _allowNetwork = false;
        private int _maxFrameSize = PipeProtocol.MaxFrameSize;
        //private IFrameCodec _frameCodec;
        private FrameCodecV1 _frameCodec;
        private IPipeSerializer _serializer;
        private IPipeLogger _logger;
        private Func<Type, object> _serviceFactory;
        private Func<string, IPipeLogger, IPipeServerTransport> _transportFactory;

        private readonly List<IPipeMiddleware> _middlewares = new List<IPipeMiddleware>();
        private readonly HandlerRegistry _handlers = new HandlerRegistry();
        private readonly List<Assembly> _scanAssemblies = new List<Assembly>();
        private readonly List<Action<string, string, string>> _messageHandlers =
            new List<Action<string, string, string>>();

        /// <summary>构造函数。</summary>
        public PipeServerBuilder(string pipeName)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("pipeName 不能为空");
            _pipeName = pipeName;
            _serializer = new JsonPipeSerializer();
            _logger = new ConsolePipeLogger();
            _serviceFactory = t => Activator.CreateInstance(t);

            // 默认三件套中间件
            _middlewares.Add(new ExceptionMiddleware());
            _middlewares.Add(new LoggingMiddleware());
            _middlewares.Add(new TimingMiddleware());
        }

        // ── 组件替换 ──
        public PipeServerBuilder UseSerializer(IPipeSerializer s) { _serializer = s; return this; }
        public PipeServerBuilder UseLogger(IPipeLogger l) { _logger = l; return this; }
        public PipeServerBuilder UseMaxInstances(int n) { _maxInstances = n; return this; }
        public PipeServerBuilder AllowNetwork(bool allow = true) { _allowNetwork = allow; return this; }
        public PipeServerBuilder UseMaxFrameSize(int size) { _maxFrameSize = size; return this; }
        public PipeServerBuilder UseServiceFactory(Func<Type, object> f) { _serviceFactory = f; return this; }

        /// <summary>指定帧编解码器（默认 FrameCodecV1）。可传入 V2 实现做协议升级。</summary>
        public PipeServerBuilder UseFrameCodec(FrameCodecV1 codec) { _frameCodec = codec; return this; }

        /// <summary>自定义传输层（例如 TCP）。</summary>
        public PipeServerBuilder UseTransport(Func<string, IPipeLogger, IPipeServerTransport> f)
        { _transportFactory = f; return this; }

        // ── 注册 ──
        public PipeServerBuilder AddMiddleware(IPipeMiddleware mw) { _middlewares.Add(mw); return this; }
        public PipeServerBuilder AddHandler(IPipeRequestHandler h) { _handlers.Register(h); return this; }
        public PipeServerBuilder ScanAssemblies(params Assembly[] asms)
        {
            if (asms != null) foreach (var a in asms) if (a != null) _scanAssemblies.Add(a);
            return this;
        }
        public PipeServerBuilder ScanEntryAssembly()
        {
            var asm = Assembly.GetEntryAssembly();
            if (asm != null) _scanAssemblies.Add(asm);
            return this;
        }

        /// <summary>订阅 MESSAGE 消息。</summary>
        public PipeServerBuilder OnMessageReceived(Action<string, string, string> handler)
        {
            if (handler != null) _messageHandlers.Add(handler);
            return this;
        }

        // ── 构建 ──
        /// <summary>构建服务端实例。</summary>
        public PipeServer Build()
        {
            // 1. 传输层
            IPipeServerTransport transport;
            if (_transportFactory != null) transport = _transportFactory(_pipeName, _logger);
            else transport = new NamedPipeServerTransport(
                _pipeName, _logger, _maxInstances, _allowNetwork, _frameCodec);

            // 2. 扫描方法
            var registry = new PipeMethodRegistry(_logger);
            registry.ScanAssemblies(_scanAssemblies);

            // 3. 默认 Handler
            _handlers.Register(new CallMethodHandler(registry, _logger));

            var msgHandler = new MessageHandler();
            foreach (var h in _messageHandlers) msgHandler.MessageReceived += h;
            _handlers.Register(msgHandler);

            // 4. 组装
            return new PipeServer(transport, _serializer, _logger, _handlers,
                _middlewares.ToArray(), _serviceFactory);
        }
    }
}