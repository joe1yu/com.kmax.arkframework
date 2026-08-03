using System;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public interface IActionHandle : IDisposable
    {
        ActionStatus Status { get; }

        Exception Exception { get; }

        bool IsRunning { get; }

        void Cancel();
    }

    public interface IActionService
    {
        int RunningCount { get; }

        IActionHandle Start(IAction action, Action onCompleted = null);

        IActionHandle Start(
            IAction action,
            Object owner,
            Action onCompleted = null);

        void CancelAll();
    }
}
