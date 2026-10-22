// ════════════════════════════════════════════════════════════════════════════
//  文件：ConsolePipeLogger.cs
//  作用：控制台日志实现。生产环境可替换为 Serilog / Microsoft.Extensions.Logging。
// ════════════════════════════════════════════════════════════════════════════
using System;
using NamePipeKit.Abstractions.Logging;

namespace NamePipeKit.Logging
{
    /// <summary>控制台彩色日志。</summary>
    public sealed class ConsolePipeLogger : IPipeLogger
    {
        public void Debug(string message) { Write("DBG", message, ConsoleColor.Gray); }
        public void Info(string message) { Write("INF", message, ConsoleColor.Green); }
        public void Warn(string message, Exception ex = null)
        { Write("WRN", message + (ex != null ? " | " + ex.Message : ""), ConsoleColor.Yellow); }
        public void Error(string message, Exception ex = null)
        { Write("ERR", message + (ex != null ? " | " + ex : ""), ConsoleColor.Red); }

        private static void Write(string level, string msg, ConsoleColor color)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg);
            Console.ForegroundColor = old;
        }
    }
}