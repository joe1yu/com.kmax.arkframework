using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class ActionService : IActionService, IDisposable
    {
        private const string ExecutionCategory = "Execution";

        private readonly List<ActionEntry> _active = new List<ActionEntry>();
        private readonly List<ActionEntry> _pending = new List<ActionEntry>();
        private readonly IFrameworkLogger _logger;
        private bool _updating;
        private bool _cancelingAll;
        private bool _disposed;

        public ActionService(IFrameworkLogger logger = null)
        {
            _logger = logger ?? new UnityFrameworkLogger();
        }

        public int RunningCount
        {
            get
            {
                var count = CountRunning(_active);
                return count + CountRunning(_pending);
            }
        }

        public IActionHandle Start(IAction action, Action onCompleted = null)
        {
            return StartCore(action, null, false, onCompleted);
        }

        public IActionHandle Start(
            IAction action,
            Object owner,
            Action onCompleted = null)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            return StartCore(action, owner, true, onCompleted);
        }

        public void CancelAll()
        {
            EnsureNotDisposed();
            if (_cancelingAll)
            {
                return;
            }

            _cancelingAll = true;
            try
            {
                CancelEntries(_active);
                CancelEntries(_pending);
                if (!_updating)
                {
                    _active.Clear();
                    _pending.Clear();
                }
            }
            finally
            {
                _cancelingAll = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelEntries(_active);
            CancelEntries(_pending);
            if (!_updating)
            {
                _active.Clear();
                _pending.Clear();
            }
        }

        internal void Update(float deltaTime)
        {
            EnsureNotDisposed();
            if (float.IsNaN(deltaTime) ||
                float.IsInfinity(deltaTime) ||
                deltaTime < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deltaTime),
                    deltaTime,
                    "Action delta time must be finite and non-negative.");
            }

            _updating = true;
            try
            {
                for (var index = _active.Count - 1; index >= 0; index--)
                {
                    UpdateEntry(index, deltaTime);
                }
            }
            finally
            {
                _updating = false;
                FlushPendingEntries();
            }
        }

        private IActionHandle StartCore(
            IAction action,
            Object owner,
            bool hasOwner,
            Action onCompleted)
        {
            EnsureNotDisposed();
            if (_cancelingAll)
            {
                throw new InvalidOperationException(
                    "An action cannot start while CancelAll is canceling actions.");
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (action.Status != ActionStatus.Idle)
            {
                throw new InvalidOperationException(
                    "Only an idle action can be started.");
            }

            var entry = new ActionEntry(action, owner, hasOwner, onCompleted);
            var handle = new ActionHandle(action, () => CancelEntry(entry));
            try
            {
                action.Begin();
            }
            catch (Exception exception)
            {
                TryLog("An action threw while starting.", exception);
                throw;
            }

            if (action.Status == ActionStatus.Completed)
            {
                InvokeCompletion(entry);
            }
            else if (action.Status == ActionStatus.Running)
            {
                (_updating ? _pending : _active).Add(entry);
            }

            return handle;
        }

        private void UpdateEntry(int index, float deltaTime)
        {
            var entry = _active[index];
            if (entry.HasOwner && entry.Owner == null)
            {
                CancelEntry(entry);
                _active.RemoveAt(index);
                return;
            }

            if (entry.Action.Status != ActionStatus.Running)
            {
                _active.RemoveAt(index);
                return;
            }

            try
            {
                entry.Action.Tick(deltaTime);
            }
            catch (Exception exception)
            {
                _active.RemoveAt(index);
                TryLog("An action threw while executing.", exception);
                return;
            }

            if (entry.Action.Status == ActionStatus.Completed)
            {
                _active.RemoveAt(index);
                InvokeCompletion(entry);
            }
            else if (entry.Action.Status != ActionStatus.Running)
            {
                _active.RemoveAt(index);
            }
        }

        private void CancelEntry(ActionEntry entry)
        {
            if (entry.Action.Status == ActionStatus.Idle ||
                entry.Action.Status == ActionStatus.Running)
            {
                try
                {
                    entry.Action.Cancel();
                }
                catch (Exception exception)
                {
                    TryLog("An action threw while being canceled.", exception);
                }
            }

            if (!_updating)
            {
                _active.Remove(entry);
                _pending.Remove(entry);
            }
        }

        private void FlushPendingEntries()
        {
            for (var index = 0; index < _pending.Count; index++)
            {
                if (_pending[index].Action.Status == ActionStatus.Running)
                {
                    _active.Add(_pending[index]);
                }
            }

            _pending.Clear();
        }

        private void InvokeCompletion(ActionEntry entry)
        {
            if (entry.OnCompleted == null)
            {
                return;
            }

            try
            {
                entry.OnCompleted();
            }
            catch (Exception exception)
            {
                // 完成回调与动作结果隔离，避免一个观察者破坏调度循环。
                TryLog("An action completion callback threw.", exception);
            }
        }

        private void CancelEntries(IReadOnlyList<ActionEntry> entries)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var action = entries[index].Action;
                if (action.Status != ActionStatus.Running &&
                    action.Status != ActionStatus.Idle)
                {
                    continue;
                }

                try
                {
                    action.Cancel();
                }
                catch (Exception exception)
                {
                    TryLog("An action threw while being canceled.", exception);
                }
            }
        }

        private void TryLog(string message, Exception exception)
        {
            try
            {
                _logger.Error(
                    BuiltInModuleIds.ActionKit,
                    ExecutionCategory,
                    message,
                    exception);
            }
            catch
            {
                // 日志实现异常不得中断其余动作。
            }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ActionService));
            }
        }

        private static int CountRunning(IReadOnlyList<ActionEntry> entries)
        {
            var count = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Action.Status == ActionStatus.Running)
                {
                    count++;
                }
            }

            return count;
        }

        private sealed class ActionEntry
        {
            public ActionEntry(
                IAction action,
                Object owner,
                bool hasOwner,
                Action onCompleted)
            {
                Action = action;
                Owner = owner;
                HasOwner = hasOwner;
                OnCompleted = onCompleted;
            }

            public IAction Action { get; }

            public Object Owner { get; }

            public bool HasOwner { get; }

            public Action OnCompleted { get; }

        }

        private sealed class ActionHandle : IActionHandle
        {
            private readonly IAction _action;
            private Action _cancel;

            public ActionHandle(IAction action, Action cancel)
            {
                _action = action;
                _cancel = cancel;
            }

            public ActionStatus Status => _action.Status;

            public Exception Exception => _action.Exception;

            public bool IsRunning => Status == ActionStatus.Running;

            public void Cancel()
            {
                _cancel?.Invoke();
                _cancel = null;
            }

            public void Dispose()
            {
                Cancel();
            }
        }
    }
}
