using System;
using System.Collections.Generic;

namespace ArkFramework
{
    public interface IActionContainer
    {
        void Add(IAction action);
    }

    public sealed class SequenceAction : ActionNode, IActionContainer
    {
        private readonly List<IAction> _actions = new List<IAction>();
        private int _index;

        public int Count => _actions.Count;

        public void Add(IAction action)
        {
            EnsureConfigurable();
            _actions.Add(action ?? throw new ArgumentNullException(nameof(action)));
        }

        protected override void OnBegin()
        {
            StartAndDrainCompletedActions();
        }

        protected override void OnTick(float deltaTime)
        {
            if (_index >= _actions.Count)
            {
                Complete();
                return;
            }

            var current = _actions[_index];
            current.Tick(deltaTime);
            StartAndDrainCompletedActions();
        }

        protected override void OnCancel()
        {
            CancelCurrent();
        }

        protected override void OnFaulted()
        {
            CancelCurrent();
        }

        protected override void OnReset()
        {
            _index = 0;
            for (var index = 0; index < _actions.Count; index++)
            {
                if (_actions[index].Status != ActionStatus.Idle)
                {
                    _actions[index].Reset();
                }
            }
        }

        private void StartAndDrainCompletedActions()
        {
            // 同步动作在同一帧连续排空，Delay 等运行中动作则留给后续 Tick。
            while (_index < _actions.Count)
            {
                var current = _actions[_index];
                if (current.Status == ActionStatus.Idle)
                {
                    current.Begin();
                }

                if (current.Status == ActionStatus.Completed)
                {
                    _index++;
                    continue;
                }

                ThrowIfChildCannotContinue(current);
                return;
            }

            Complete();
        }

        private void CancelCurrent()
        {
            if (_index < _actions.Count &&
                _actions[_index].Status == ActionStatus.Running)
            {
                _actions[_index].Cancel();
            }
        }

        private void EnsureConfigurable()
        {
            if (Status != ActionStatus.Idle)
            {
                throw new InvalidOperationException(
                    "A sequence cannot be changed after it begins.");
            }
        }

        private static void ThrowIfChildCannotContinue(IAction action)
        {
            if (action.Status == ActionStatus.Faulted)
            {
                throw new InvalidOperationException(
                    "A child action faulted.",
                    action.Exception);
            }

            if (action.Status == ActionStatus.Canceled)
            {
                throw new InvalidOperationException(
                    "A child action was canceled outside its parent sequence.");
            }
        }
    }

    public sealed class ParallelAction : ActionNode, IActionContainer
    {
        private readonly List<IAction> _actions = new List<IAction>();

        public int Count => _actions.Count;

        public void Add(IAction action)
        {
            EnsureConfigurable();
            _actions.Add(action ?? throw new ArgumentNullException(nameof(action)));
        }

        protected override void OnBegin()
        {
            for (var index = 0; index < _actions.Count; index++)
            {
                _actions[index].Begin();
            }

            CompleteWhenAllChildrenFinish();
        }

        protected override void OnTick(float deltaTime)
        {
            for (var index = 0; index < _actions.Count; index++)
            {
                if (_actions[index].Status == ActionStatus.Running)
                {
                    _actions[index].Tick(deltaTime);
                }
            }

            CompleteWhenAllChildrenFinish();
        }

        protected override void OnCancel()
        {
            CancelRunningChildren();
        }

        protected override void OnFaulted()
        {
            CancelRunningChildren();
        }

        protected override void OnReset()
        {
            for (var index = 0; index < _actions.Count; index++)
            {
                if (_actions[index].Status != ActionStatus.Idle)
                {
                    _actions[index].Reset();
                }
            }
        }

        private void CompleteWhenAllChildrenFinish()
        {
            for (var index = 0; index < _actions.Count; index++)
            {
                var child = _actions[index];
                if (child.Status == ActionStatus.Faulted)
                {
                    throw new InvalidOperationException(
                        "A parallel child action faulted.",
                        child.Exception);
                }

                if (child.Status == ActionStatus.Canceled)
                {
                    throw new InvalidOperationException(
                        "A child action was canceled outside its parent parallel action.");
                }

                if (child.Status != ActionStatus.Completed)
                {
                    return;
                }
            }

            Complete();
        }

        private void CancelRunningChildren()
        {
            for (var index = 0; index < _actions.Count; index++)
            {
                if (_actions[index].Status == ActionStatus.Running)
                {
                    _actions[index].Cancel();
                }
            }
        }

        private void EnsureConfigurable()
        {
            if (Status != ActionStatus.Idle)
            {
                throw new InvalidOperationException(
                    "A parallel action cannot be changed after it begins.");
            }
        }
    }

    public sealed class RepeatAction : ActionNode, IActionContainer
    {
        private readonly SequenceAction _body = new SequenceAction();
        private readonly int _repeatCount;
        private int _completedIterations;
        private bool _restartPending;

        public RepeatAction(int repeatCount = -1)
        {
            if (repeatCount < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(repeatCount),
                    repeatCount,
                    "Repeat count must be -1 for infinite or non-negative.");
            }

            _repeatCount = repeatCount;
        }

        public int RepeatCount => _repeatCount;

        public int CompletedIterations => _completedIterations;

        public void Add(IAction action)
        {
            if (Status != ActionStatus.Idle)
            {
                throw new InvalidOperationException(
                    "A repeat action cannot be changed after it begins.");
            }

            _body.Add(action);
        }

        protected override void OnBegin()
        {
            if (_repeatCount == 0)
            {
                Complete();
                return;
            }

            _body.Begin();
            ProcessCompletedIteration();
        }

        protected override void OnTick(float deltaTime)
        {
            if (_restartPending)
            {
                _restartPending = false;
                _body.Begin();
                if (_body.Status == ActionStatus.Completed)
                {
                    ProcessCompletedIteration();
                    return;
                }
            }

            if (_body.Status == ActionStatus.Running)
            {
                _body.Tick(deltaTime);
            }

            ProcessCompletedIteration();
        }

        protected override void OnCancel()
        {
            CancelBody();
        }

        protected override void OnFaulted()
        {
            CancelBody();
        }

        protected override void OnReset()
        {
            _completedIterations = 0;
            _restartPending = false;
            if (_body.Status != ActionStatus.Idle)
            {
                _body.Reset();
            }
        }

        private void ProcessCompletedIteration()
        {
            if (_body.Status == ActionStatus.Faulted)
            {
                throw new InvalidOperationException(
                    "The repeated action body faulted.",
                    _body.Exception);
            }

            if (_body.Status == ActionStatus.Canceled)
            {
                throw new InvalidOperationException(
                    "The repeated action body was canceled outside its parent.");
            }

            if (_body.Status != ActionStatus.Completed)
            {
                return;
            }

            _completedIterations++;
            if (_repeatCount >= 0 && _completedIterations >= _repeatCount)
            {
                Complete();
                return;
            }

            // 每帧最多开始一次同步重复，避免无限 Repeat 在单帧内死循环。
            _body.Reset();
            _restartPending = true;
        }

        private void CancelBody()
        {
            if (_body.Status == ActionStatus.Running)
            {
                _body.Cancel();
            }
        }
    }
}
