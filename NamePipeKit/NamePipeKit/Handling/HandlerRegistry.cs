// ════════════════════════════════════════════════════════════════════════════
//  文件：HandlerRegistry.cs
//  作用：ContentType → Handler 的路由表。
//  开闭原则核心：新增 ContentType 只需 Register 一个新 Handler，不改任何现有代码。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NamePipeKit.Abstractions.Handling;

namespace NamePipeKit.Handling
{
    /// <summary>处理器注册表。</summary>
    public sealed class HandlerRegistry
    {
        private readonly ConcurrentDictionary<string, IPipeRequestHandler> _handlers =
            new ConcurrentDictionary<string, IPipeRequestHandler>(StringComparer.OrdinalIgnoreCase);

        /// <summary>注册一个 Handler。</summary>
        public HandlerRegistry Register(IPipeRequestHandler handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (string.IsNullOrEmpty(handler.ContentType))
                throw new ArgumentException("ContentType 不能为空");
            _handlers[handler.ContentType] = handler;
            return this;
        }

        /// <summary>按 ContentType 查找。</summary>
        public IPipeRequestHandler Resolve(string contentType)
        {
            IPipeRequestHandler h;
            return contentType != null && _handlers.TryGetValue(contentType, out h) ? h : null;
        }

        /// <summary>已注册的所有 ContentType。</summary>
        public IEnumerable<string> SupportedTypes { get { return _handlers.Keys; } }
    }
}