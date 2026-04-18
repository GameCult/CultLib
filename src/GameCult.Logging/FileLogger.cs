using System;
using System.IO;

namespace GameCult.Logging
{
    /// <summary>
    /// Appends timestamped log entries to a file on disk.
    /// </summary>
    public class FileLogger : ILogger
    {
        private readonly string _logPath;

        /// <summary>
        /// Initializes a new file-backed logger.
        /// </summary>
        /// <param name="logPath">The path to the log file to append to.</param>
        public FileLogger(string logPath = "server.log")
        {
            _logPath = logPath;
        }

        private void Append(string level, string message)
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}\n");
        }

        /// <inheritdoc />
        public void LogInfo(string message) => Append("INFO", message);

        /// <inheritdoc />
        public void LogWarning(string message) => Append("WARN", message);

        /// <inheritdoc />
        public void LogError(string message) => Append("ERROR", message);

        /// <inheritdoc />
        public void LogDebug(string message) => Append("DEBUG", message);
    }
}
