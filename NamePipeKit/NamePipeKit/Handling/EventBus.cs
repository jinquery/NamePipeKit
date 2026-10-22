// ════════════════════════════════════════════════════════════════════════════
//  文件：EventBus.cs
//  作用：事件总线。管理 topic → 连接列表，业务侧 Publish 时向所有订阅者推 ServerPush 帧。
//  线程安全：ConcurrentDictionary；推送时对每个连接独立 try。
//  扩展：换 Redis Pub/Sub 时只需替换此实现，Handler 不变。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using NamePipeKit.Abstractions.Protocol;
using NamePipeKit.Abstractions.Transport;

namespace NamePipeKit.Handling
{
    /// <summary>事件总线。</summary>
    public sealed class EventBus
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IPipeConnection>> _topics =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, IPipeConnection>>();

        /// <summary>订阅 topic。</summary>
        public void Subscribe(string topic, IPipeConnection conn)
        {
            var subs = _topics.GetOrAdd(topic, _ => new ConcurrentDictionary<string, IPipeConnection>());
            subs[conn.ConnectionId] = conn;
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string topic, IPipeConnection conn)
        {
            ConcurrentDictionary<string, IPipeConnection> subs;
            if (_topics.TryGetValue(topic, out subs))
                subs.TryRemove(conn.ConnectionId, out _);
        }

        /// <summary>向 topic 的所有订阅者推送 ServerPush 帧。</summary>
        public void Publish(string topic, object payload)
        {
            ConcurrentDictionary<string, IPipeConnection> subs;
            if (!_topics.TryGetValue(topic, out subs)) return;

            // 统一序列化一次，多连接复用
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                topic = topic,
                payload = payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            foreach (var kv in subs)
            {
                try
                {
                    var frame = new PipeFrame
                    {
                        MessageType = PipeMessageType.ServerPush,
                        SequenceId = 0,          // 推送不带 SequenceId
                        Body = json
                    };
                    kv.Value.WriteFrameAsync(frame, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // 连接已断，从订阅表清理
                    subs.TryRemove(kv.Key, out _);
                }
            }
        }
    }
}