// ════════════════════════════════════════════════════════════════════════════
//  文件：JsonPipeSerializer.cs
//  作用：默认 JSON 序列化实现（System.Text.Json）。
//  设计原则：
//    - 序列化器可替换（换 MessagePack 时只写一个新实现）；
//    - 支持值类型（int/decimal/DateTime），不用 where T : class。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Text.Json;
using NamePipeKit.Abstractions.Serialization;

namespace NamePipeKit.Serialization
{
    /// <summary>基于 System.Text.Json 的序列化器（比 DataContractJsonSerializer 快 3~5 倍）。</summary>
    public sealed class JsonPipeSerializer : IPipeSerializer
    {
        /// <summary>全局 Options：camelCase、大小写不敏感、不缩进。</summary>
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }

        /// <inheritdoc />
        public byte[] Serialize(object value, Type type)
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, type, Options);
        }

        /// <inheritdoc />
        public object Deserialize(byte[] data, Type type)
        {
            return JsonSerializer.Deserialize(data, type, Options);
        }

        /// <inheritdoc />
        public T Deserialize<T>(byte[] data)
        {
            // System.Text.Json 对值类型也支持，null 时返回 default(T)
            return JsonSerializer.Deserialize<T>(data, Options);
        }
    }
}