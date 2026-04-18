using System;

namespace GameCult.Logging
{
    /// <summary>
    /// Writes log messages to the process console.
    /// </summary>
    public class ConsoleLogger : ILogger
    {
        /// <inheritdoc />
        public void LogInfo(string message) => Console.WriteLine($"[INFO] {message}");

        /// <inheritdoc />
        public void LogWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARN] {message}"); Console.ResetColor();
        }

        /// <inheritdoc />
        public void LogError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] {message}"); Console.ResetColor();
        }

        /// <inheritdoc />
        public void LogDebug(string message)
        {
            if(Environment.GetEnvironmentVariable("NODEV") is not null) Console.WriteLine($"[DEBUG] {message}");
        }
    }
}
