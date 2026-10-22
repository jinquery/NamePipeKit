using System;

namespace NamePipeKit.Abstractions.Attributes
{
    /// <summary>
    /// 标记一个类型为"管道服务"，框架会自动扫描其标注了 <see cref="PipeMethodAttribute"/> 的方法。
    /// </summary>
    /// <remarks>
    /// 约定：服务类型需具备公共无参构造函数，或通过 <c>PipeServerBuilder.UseServiceFactory</c> 注入 DI。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class PipeServiceAttribute : Attribute
    {
        /// <summary>可选的服务别名，用于将多个实现映射到同一个客户端 Key。</summary>
        public string Alias { get; private set; }

        /// <summary>构造函数。</summary>
        /// <param name="alias">可选的服务别名。</param>
        public PipeServiceAttribute(string alias = null)
        {
            Alias = alias;
        }
    }

    /// <summary>
    /// 标记一个方法为"管道方法"，客户端可通过 <see cref="Key"/> 远程调用。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class PipeMethodAttribute : Attribute
    {
        /// <summary>客户端调用时使用的全局唯一 Key。缺省时使用 "类名.方法名"。</summary>
        public string Key { get; set; }

        /// <summary>方法说明（可选）。</summary>
        public string Description { get; set; }

        public PipeMethodAttribute(string key)
        {
            Key = key;
        }
        public PipeMethodAttribute(string key, string description)
        {
            Key = key;
            Description = description;
        }
    }

    /// <summary>程序集级特性，配合扫描器可按程序集过滤。</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class PipeAssemblyAttribute : Attribute
    {
    }
}

