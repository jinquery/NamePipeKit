// ════════════════════════════════════════════════════════════════════════════
//  文件：IPipeLogger.cs
//  作用：日志抽象，避免框架强依赖 Microsoft.Extensions.Logging。
//  使用：默认 ConsolePipeLogger；可接入 Serilog / NLog / ILogger。
// ════════════════════════════════════════════════════════════════════════════
using System;

namespace NamePipeKit.Abstractions.Logging
{
    /// <summary>轻量日志接口。</summary>
    public interface IPipeLogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message, Exception ex = null);
        void Error(string message, Exception ex = null);
    }
}