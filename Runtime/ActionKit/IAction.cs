using System;

namespace ArkFramework
{
    public interface IAction
    {
        ActionStatus Status { get; }

        Exception Exception { get; }

        void Begin();

        void Tick(float deltaTime);

        void Cancel();

        void Reset();
    }
}
