// SilentPipeLogger.cs —— 不变
using System;
using NamePipeKit.Abstractions.Logging;

namespace NamePipeKit.StressTest
{
    internal sealed class SilentPipeLogger : IPipeLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message, Exception ex = null) { }
        public void Error(string message, Exception ex = null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[服务端错误] " + message + (ex != null ? " | " + ex.Message : ""));
            Console.ResetColor();
        }
    }
}