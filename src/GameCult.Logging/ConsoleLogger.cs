using System;

namespace GameCult.Logging
{
    public class ConsoleLogger : ILogger
    {
        public void LogInfo(string message) => Console.WriteLine($"[INFO] {message}");
        public void LogWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARN] {message}"); Console.ResetColor();
        }

        public void LogError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] {message}"); Console.ResetColor();
        }

        public void LogDebug(string message)
        {
            if(Environment.GetEnvironmentVariable("NODEV") is not null) Console.WriteLine($"[DEBUG] {message}");
        }

    }
}