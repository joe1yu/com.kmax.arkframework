using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class FsmService : IFsmService
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, IMachineEntry> _machines =
            new Dictionary<string, IMachineEntry>(StringComparer.Ordinal);
        private bool _disposing;
        private bool _disposed;
        private Task _disposeTask;

        public IReadOnlyList<FsmDiagnostics> Diagnostics
        {
            get
            {
                IMachineEntry[] entries;
                lock (_sync)
                {
                    entries = SnapshotEntries();
                }

                var diagnostics = new FsmDiagnostics[entries.Length];
                for (var index = 0; index < entries.Length; index++)
                {
                    diagnostics[index] = entries[index].Diagnostics;
                }

                return Array.AsReadOnly(diagnostics);
            }
        }

        public IStateMachine<TContext> Create<TContext>(
            string id,
            TContext context,
            int historyCapacity = 32)
        {
            ValidateId(id, nameof(id));
            lock (_sync)
            {
                EnsureActive();
                if (_machines.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        $"FSM service already contains machine '{id}'.");
                }

                var machine = new StateMachine<TContext>(
                    id,
                    context,
                    historyCapacity);
                _machines.Add(id, new MachineEntry<TContext>(machine));
                return machine;
            }
        }

        public IStateMachine<TContext> Get<TContext>(string id)
        {
            ValidateId(id, nameof(id));
            lock (_sync)
            {
                EnsureActive();
                if (!_machines.TryGetValue(id, out var entry))
                {
                    throw new KeyNotFoundException(
                        $"FSM service does not contain machine '{id}'.");
                }

                if (!(entry is MachineEntry<TContext> typed))
                {
                    throw new InvalidOperationException(
                        $"FSM machine '{id}' uses context type " +
                        $"'{entry.ContextType.FullName}', not " +
                        $"'{typeof(TContext).FullName}'.");
                }

                return typed.Machine;
            }
        }

        public bool TryGet<TContext>(
            string id,
            out IStateMachine<TContext> machine)
        {
            ValidateId(id, nameof(id));
            lock (_sync)
            {
                EnsureActive();
                if (!_machines.TryGetValue(id, out var entry))
                {
                    machine = null;
                    return false;
                }

                if (!(entry is MachineEntry<TContext> typed))
                {
                    machine = null;
                    return false;
                }

                machine = typed.Machine;
                return true;
            }
        }

        public ValueTask RemoveAsync(string id)
        {
            ValidateId(id, nameof(id));
            IMachineEntry entry;
            TaskCompletionSource<bool> completion;
            lock (_sync)
            {
                EnsureActive();
                if (!_machines.TryGetValue(id, out entry))
                {
                    throw new KeyNotFoundException(
                        $"FSM service does not contain machine '{id}'.");
                }

                ThrowIfCallbackReentry(entry, nameof(RemoveAsync));
                if (entry.RemovalTask != null)
                {
                    return new ValueTask(entry.RemovalTask);
                }

                completion = NewCompletion();
                entry.RemovalTask = completion.Task;
            }

            _ = CompleteRemoveAsync(id, entry, completion);
            return new ValueTask(completion.Task);
        }

        public void Update(float deltaTime)
        {
            IMachineEntry[] entries;
            lock (_sync)
            {
                EnsureActive();
                entries = SnapshotEntries();
            }

            for (var index = 0; index < entries.Length; index++)
            {
                try
                {
                    entries[index].Update(deltaTime);
                }
                catch
                {
                    // The machine records its own failure. A single bad machine
                    // must not fault the FSM module or stop sibling updates.
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            IMachineEntry[] entries;
            TaskCompletionSource<bool> completion;
            lock (_sync)
            {
                ThrowIfCallbackReentry(nameof(DisposeAsync));
                if (_disposeTask != null)
                {
                    return new ValueTask(_disposeTask);
                }

                if (_disposed)
                {
                    return default;
                }

                _disposing = true;
                entries = SnapshotEntries();
                completion = NewCompletion();
                _disposeTask = completion.Task;
            }

            _ = CompleteDisposeAsync(entries, completion);
            return new ValueTask(completion.Task);
        }

        private async Task CompleteRemoveAsync(
            string id,
            IMachineEntry entry,
            TaskCompletionSource<bool> completion)
        {
            Exception failure = null;
            try
            {
                await entry.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            lock (_sync)
            {
                if (_machines.TryGetValue(id, out var registered) &&
                    ReferenceEquals(registered, entry))
                {
                    if (failure == null)
                    {
                        _machines.Remove(id);
                    }
                    else
                    {
                        entry.RemovalTask = null;
                    }
                }
            }

            if (failure == null)
            {
                completion.TrySetResult(true);
            }
            else
            {
                completion.TrySetException(failure);
            }
        }

        private async Task CompleteDisposeAsync(
            IMachineEntry[] entries,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await DisposeCoreAsync(entries);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task DisposeCoreAsync(IMachineEntry[] entries)
        {
            var failures = new List<Exception>();
            try
            {
                for (var index = 0; index < entries.Length; index++)
                {
                    try
                    {
                        await entries[index].DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _machines.Clear();
                    _disposing = false;
                    _disposed = true;
                }
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "One or more FSM machines failed during disposal.",
                    failures);
            }
        }

        private IMachineEntry[] SnapshotEntries()
        {
            var entries = new IMachineEntry[_machines.Count];
            var index = 0;
            foreach (var entry in _machines.Values)
            {
                entries[index++] = entry;
            }

            return entries;
        }

        private void ThrowIfCallbackReentry(string operation)
        {
            foreach (var entry in _machines.Values)
            {
                if (entry.IsExecutingCallback)
                {
                    ThrowCallbackReentry(operation, entry);
                }
            }
        }

        private static void ThrowIfCallbackReentry(
            IMachineEntry entry,
            string operation)
        {
            if (entry.IsExecutingCallback)
            {
                ThrowCallbackReentry(operation, entry);
            }
        }

        private static void ThrowCallbackReentry(
            string operation,
            IMachineEntry entry)
        {
            throw new InvalidOperationException(
                $"FSM service cannot start {operation} for machine " +
                $"'{entry.Id}' from that machine's state callback.");
        }

        private static TaskCompletionSource<bool> NewCompletion()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void EnsureActive()
        {
            if (_disposing || _disposed)
            {
                throw new ObjectDisposedException(
                    nameof(FsmService),
                    "The FSM service is disposing or disposed.");
            }
        }

        private static void ValidateId(string id, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A state machine ID is required.",
                    parameterName);
            }
        }

        private interface IMachineEntry
        {
            string Id { get; }
            Type ContextType { get; }
            FsmDiagnostics Diagnostics { get; }
            bool IsExecutingCallback { get; }
            Task RemovalTask { get; set; }
            void Update(float deltaTime);
            ValueTask DisposeAsync();
        }

        private sealed class MachineEntry<TContext> : IMachineEntry
        {
            public MachineEntry(StateMachine<TContext> machine)
            {
                Machine = machine;
            }

            public StateMachine<TContext> Machine { get; }
            public string Id => Machine.Id;
            public Type ContextType => typeof(TContext);
            public FsmDiagnostics Diagnostics => Machine.GetDiagnostics();
            public bool IsExecutingCallback => Machine.IsExecutingCallback;
            public Task RemovalTask { get; set; }

            public void Update(float deltaTime)
            {
                Machine.Update(deltaTime);
            }

            public ValueTask DisposeAsync()
            {
                return Machine.DisposeAsync();
            }
        }
    }
}
