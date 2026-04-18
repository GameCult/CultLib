namespace GameCult.Logging
{
    /// <summary>
    /// Defines the logging contract used across CultLib components.
    /// </summary>
    public interface ILogger
    {
        /// <summary>
        /// Writes an informational message.
        /// </summary>
        /// <param name="message">The message to record.</param>
        void LogInfo(string message);

        /// <summary>
        /// Writes a warning message.
        /// </summary>
        /// <param name="message">The message to record.</param>
        void LogWarning(string message);

        /// <summary>
        /// Writes an error message.
        /// </summary>
        /// <param name="message">The message to record.</param>
        void LogError(string message);

        /// <summary>
        /// Writes a debug message.
        /// </summary>
        /// <param name="message">The message to record.</param>
        void LogDebug(string message);  // Optional; skip in prod
    }

    /// <summary>
    /// Provides a no-op logger implementation.
    /// </summary>
    public class NullLogger : ILogger
    {
        /// <inheritdoc />
        public void LogInfo(string message) { }

        /// <inheritdoc />
        public void LogWarning(string message) { }

        /// <inheritdoc />
        public void LogError(string message) { }

        /// <inheritdoc />
        public void LogDebug(string message) { }
    }
}
