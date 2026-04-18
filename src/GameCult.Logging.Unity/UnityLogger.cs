using R3;
using UnityEngine;

namespace GameCult.Logging.Unity
{
    public class UnityLogger : ILogger
    {
        private readonly bool _isDebugBuild = Debug.isDebugBuild;  // Cache Unity-specific

        public void LogInfo(string message)
        {
            Observable.NextFrame().Subscribe(_ => Debug.Log($"[INFO] {message}"));
        }

        public void LogWarning(string message)
        {
            Observable.NextFrame().Subscribe(_ => Debug.LogWarning($"[WARN] {message}"));
        }

        public void LogError(string message)
        {
            Observable.NextFrame().Subscribe(_ => Debug.LogError($"[ERROR] {message}"));
        }

        public void LogDebug(string message)
        {
            if (_isDebugBuild)
                Observable.NextFrame().Subscribe(_ => Debug.Log($"[DEBUG] {message}"));
        }
    }
}