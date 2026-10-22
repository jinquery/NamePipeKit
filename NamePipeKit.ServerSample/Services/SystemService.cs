using System;
using NamePipeKit.Abstractions.Attributes;

namespace NamePipeKit.ServerSample.Services
{
    /// <summary>
    /// 系统信息服务。
    /// <para>演示点：无参方法、DateTime 值类型返回、健康检查。</para>
    /// </summary>
    [PipeService]
    public sealed class SystemService
    {
        /// <summary>健康检查。</summary>
        [PipeMethod("system.ping", Description = "健康检查")]
        public string Ping(string msg)
        {
            return "PONG: " + msg;
        }

        /// <summary>服务器当前时间。</summary>
        [PipeMethod("system.now", Description = "服务器当前时间")]
        public DateTime Now()
        {
            return DateTime.Now;
        }

        /// <summary>服务器信息。</summary>
        [PipeMethod("system.info", Description = "服务器信息")]
        public string Info()
        {
            return "NamePipeKit Server / " + Environment.MachineName;
        }
    }
}