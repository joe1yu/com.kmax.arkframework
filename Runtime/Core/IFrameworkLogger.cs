using System;

namespace ArkFramework
{
    public interface IFrameworkLogger
    {
        void Debug(string moduleId, string category, string message);
        void Info(string moduleId, string category, string message);
        void Warning(string moduleId, string category, string message);
        void Error(
            string moduleId,
            string category,
            string message,
            Exception exception);
    }
}
