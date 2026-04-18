using System;
using System.IO;

namespace GameCult.Logging
{
    public class FileLogger : ILogger
    {
        private readonly string _logPath;

        public FileLogger(string logPath = "server.log")
        {
            _logPath = logPath;
        }

        private void Append(string level, string message)
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}\n");
        }

        public void LogInfo(string message) => Append("INFO", message);
        public void LogWarning(string message) => Append("WARN", message);
        public void LogError(string message) => Append("ERROR", message);
        public void LogDebug(string message) => Append("DEBUG", message);
    }
}