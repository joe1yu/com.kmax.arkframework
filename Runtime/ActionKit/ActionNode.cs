using System;

namespace ArkFramework
{
    public abstract class ActionNode : IAction
    {
        public ActionStatus Status { get; private set; }

        public Exception Exception { get; private set; }

        public void Begin()
        {
            if (Status != ActionStatus.Idle)
            {
                throw new InvalidOperationException(
                    $"Action '{GetType().Name}' must be idle before it begins.");
            }

            Status = ActionStatus.Running;
            try
            {
                OnBegin();
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }

        public void Tick(float deltaTime)
        {
            if (Status != ActionStatus.Running)
            {
                return;
            }

            if (float.IsNaN(deltaTime) ||
                float.IsInfinity(deltaTime) ||
                deltaTime < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deltaTime),
                    deltaTime,
                    "Action delta time must be finite and non-negative.");
            }

            try
            {
                OnTick(deltaTime);
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }

        public void Cancel()
        {
            if (Status == ActionStatus.Completed ||
                Status == ActionStatus.Canceled ||
                Status == ActionStatus.Faulted)
            {
                return;
            }

            var wasRunning = Status == ActionStatus.Running;
            Status = ActionStatus.Canceled;
            if (!wasRunning)
            {
                return;
            }

            try
            {
                OnCancel();
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }

        public void Reset()
        {
            if (Status == ActionStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Running action '{GetType().Name}' cannot be reset.");
            }

            try
            {
                OnReset();
                Exception = null;
                Status = ActionStatus.Idle;
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }

        protected void Complete()
        {
            if (Status != ActionStatus.Running)
            {
                return;
            }

            Status = ActionStatus.Completed;
            OnCompleted();
        }

        protected virtual void OnBegin()
        {
        }

        protected virtual void OnTick(float deltaTime)
        {
        }

        protected virtual void OnCancel()
        {
        }

        protected virtual void OnCompleted()
        {
        }

        protected virtual void OnFaulted()
        {
        }

        protected virtual void OnReset()
        {
        }

        private void Fail(Exception exception)
        {
            Exception = exception;
            Status = ActionStatus.Faulted;
            try
            {
                OnFaulted();
            }
            catch
            {
                // 清理异常不得覆盖触发故障的原始异常。
            }
        }
    }
}
