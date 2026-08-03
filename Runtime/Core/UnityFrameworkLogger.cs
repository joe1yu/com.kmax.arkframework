using System;

namespace ArkFramework
{
    public sealed class UnityFrameworkLogger : IFrameworkLogger
    {
        public void Debug(string moduleId, string category, string message)
        {
            UnityEngine.Debug.Log(Format(moduleId, category, message));
        }

        public void Info(string moduleId, string category, string message)
        {
            UnityEngine.Debug.Log(Format(moduleId, category, message));
        }

        public void Warning(string moduleId, string category, string message)
        {
            UnityEngine.Debug.LogWarning(Format(moduleId, category, message));
        }

        public void Error(
            string moduleId,
            string category,
            string message,
            Exception exception)
        {
            UnityEngine.Debug.LogError(
                $"{Format(moduleId, category, message)}{Environment.NewLine}{exception}");
        }

        private static string Format(
            string moduleId,
            string category,
            string message)
        {
            return $"[{category}] [{moduleId}] {message}";
        }
    }
}
