using System;

namespace NamePipeKit.Abstractions.Serialization
{
    /// <summary>序列化器接口。默认 JsonPipeSerializer，可替换为 MessagePack。</summary>
    public interface IPipeSerializer
    {
        byte[] Serialize(object value, Type type);
        object Deserialize(byte[] data, Type type);
        T Deserialize<T>(byte[] data);
    }
}