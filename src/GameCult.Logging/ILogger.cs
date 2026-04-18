namespace GameCult.Logging
{
    public interface ILogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogDebug(string message);  // Optional; skip in prod
    }

    // Null impl for no-op logging (e.g., perf tests)
    public class NullLogger : ILogger
    {
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogDebug(string message) { }
    }
}