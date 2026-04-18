using R3;
using UnityEngine;

namespace GameCult.Logging.Unity
{
    /// <summary>
    /// Adapts <see cref="ILogger"/> calls to Unity's logging facilities.
    /// </summary>
    public class UnityLogger : ILogger
    {
        private readonly bool _isDebugBuild = Debug.isDebugBuild;  // Cache Unity-specific

        /// <inheritdoc />
        public void LogInfo(string message)
        {
            Observable.NextFrame().Subscribe(_ => Debug.Log($"[INFO] {message}"));
        }

        /// <inheritdoc />
        public void LogWarning(string message)
        {
            Observable.NextFrame().Subscribe(_ => Debug.LogWarning($"[WARN] {message}"));
        }

        /// <inheritdoc />
        public void LogError(string message)
        {
            Observable.NextFrame().Subscribe(_ => Debug.LogError($"[ERROR] {message}"));
        }

        /// <inheritdoc />
        public void LogDebug(string message)
        {
            if (_isDebugBuild)
                Observable.NextFrame().Subscribe(_ => Debug.Log($"[DEBUG] {message}"));
        }
    }
}
