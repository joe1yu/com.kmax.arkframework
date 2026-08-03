using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class CallbackAction : ActionNode
    {
        private readonly Action _callback;

        public CallbackAction(Action callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        protected override void OnBegin()
        {
            _callback();
            Complete();
        }
    }

    public sealed class DelayAction : ActionNode
    {
        private readonly float _duration;
        private float _elapsed;

        public DelayAction(float duration)
        {
            if (float.IsNaN(duration) ||
                float.IsInfinity(duration) ||
                duration < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    duration,
                    "Delay duration must be finite and non-negative.");
            }

            _duration = duration;
        }

        public float Duration => _duration;

        public float Elapsed => _elapsed;

        protected override void OnBegin()
        {
            if (_duration == 0f)
            {
                Complete();
            }
        }

        protected override void OnTick(float deltaTime)
        {
            _elapsed += deltaTime;
            if (_elapsed >= _duration)
            {
                Complete();
            }
        }

        protected override void OnReset()
        {
            _elapsed = 0f;
        }
    }

    public sealed class FrameDelayAction : ActionNode
    {
        private readonly int _frameCount;
        private int _elapsedFrames;

        public FrameDelayAction(int frameCount)
        {
            if (frameCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameCount),
                    frameCount,
                    "Frame delay cannot be negative.");
            }

            _frameCount = frameCount;
        }

        public int FrameCount => _frameCount;

        public int ElapsedFrames => _elapsedFrames;

        protected override void OnBegin()
        {
            if (_frameCount == 0)
            {
                Complete();
            }
        }

        protected override void OnTick(float deltaTime)
        {
            _elapsedFrames++;
            if (_elapsedFrames >= _frameCount)
            {
                Complete();
            }
        }

        protected override void OnReset()
        {
            _elapsedFrames = 0;
        }
    }

    public sealed class ConditionAction : ActionNode
    {
        private readonly Func<bool> _predicate;

        public ConditionAction(Func<bool> predicate)
        {
            _predicate =
                predicate ?? throw new ArgumentNullException(nameof(predicate));
        }

        protected override void OnBegin()
        {
            Evaluate();
        }

        protected override void OnTick(float deltaTime)
        {
            Evaluate();
        }

        private void Evaluate()
        {
            if (_predicate())
            {
                Complete();
            }
        }
    }

    public sealed class AsyncAction : ActionNode
    {
        private readonly Func<CancellationToken, Task> _operation;
        private CancellationTokenSource _cancellation;
        private Task _task;

        public AsyncAction(Func<CancellationToken, Task> operation)
        {
            _operation =
                operation ?? throw new ArgumentNullException(nameof(operation));
        }

        protected override void OnBegin()
        {
            _cancellation = new CancellationTokenSource();
            _task = _operation(_cancellation.Token) ??
                throw new InvalidOperationException(
                    "An asynchronous action operation returned a null task.");
            ObserveCompletion();
        }

        protected override void OnTick(float deltaTime)
        {
            ObserveCompletion();
        }

        protected override void OnCancel()
        {
            _cancellation?.Cancel();
        }

        protected override void OnFaulted()
        {
            _cancellation?.Cancel();
        }

        protected override void OnReset()
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _task = null;
        }

        private void ObserveCompletion()
        {
            if (_task == null || !_task.IsCompleted)
            {
                return;
            }

            // GetResult 会解包 AggregateException，保留异步操作的原始异常。
            _task.GetAwaiter().GetResult();
            Complete();
        }
    }

    public sealed class CustomAction : ActionNode
    {
        private Action _begin;
        private Action<float> _execute;
        private Action _finish;
        private Action _cancel;

        public CustomAction OnStart(Action callback)
        {
            EnsureConfigurable();
            _begin = callback ?? throw new ArgumentNullException(nameof(callback));
            return this;
        }

        public CustomAction OnExecute(Action<float> callback)
        {
            EnsureConfigurable();
            _execute = callback ?? throw new ArgumentNullException(nameof(callback));
            return this;
        }

        public CustomAction OnFinish(Action callback)
        {
            EnsureConfigurable();
            _finish = callback ?? throw new ArgumentNullException(nameof(callback));
            return this;
        }

        public CustomAction OnCanceled(Action callback)
        {
            EnsureConfigurable();
            _cancel = callback ?? throw new ArgumentNullException(nameof(callback));
            return this;
        }

        public void Finish()
        {
            Complete();
        }

        protected override void OnBegin()
        {
            _begin?.Invoke();
        }

        protected override void OnTick(float deltaTime)
        {
            _execute?.Invoke(deltaTime);
        }

        protected override void OnCompleted()
        {
            _finish?.Invoke();
        }

        protected override void OnCancel()
        {
            _cancel?.Invoke();
        }

        private void EnsureConfigurable()
        {
            if (Status != ActionStatus.Idle)
            {
                throw new InvalidOperationException(
                    "A custom action cannot be configured after it begins.");
            }
        }
    }
}
