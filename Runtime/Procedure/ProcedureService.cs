using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class ProcedureService : IProcedureService
    {
        private static AsyncLocal<CallbackFrame> CallbackOwner =
            new AsyncLocal<CallbackFrame>();

        static ProcedureService()
        {
            FrameworkStaticReset.Register(ResetStatics);
        }

        private readonly object _sync = new object();
        private readonly IFsmService _fsmService;
        private readonly IStateMachine<ProcedureContext> _machine;
        private readonly Dictionary<string, ProcedureStateAdapter> _procedures =
            new Dictionary<string, ProcedureStateAdapter>(
                StringComparer.Ordinal);
        private readonly List<string> _registeredIds = new List<string>();
        private bool _registrationClosed;
        private bool _starting;
        private bool _started;
        private bool _stopping;
        private bool _stopped;
        private Task _stopTask;
        private Exception _lastException;

        public const string MainMachineId = "MainProcedure";

        public ProcedureService(
            IFsmService fsmService,
            ServiceContainer services,
            int historyCapacity = 32)
        {
            _fsmService = fsmService ??
                          throw new ArgumentNullException(nameof(fsmService));
            var context = new ProcedureContext(services);
            _machine = fsmService.Create(
                MainMachineId,
                context,
                historyCapacity);
        }

        private static void ResetStatics()
        {
            Interlocked.Exchange(
                ref CallbackOwner,
                new AsyncLocal<CallbackFrame>());
        }

        public string CurrentProcedureId => _machine.CurrentStateId;
        public string PreviousProcedureId => _machine.PreviousStateId;

        public bool IsStarted
        {
            get
            {
                lock (_sync)
                {
                    return _started && !_stopping && !_stopped;
                }
            }
        }

        public bool IsFaulted => _machine.IsFaulted;

        public ProcedureDiagnostics Diagnostics
        {
            get
            {
                string[] registeredIds;
                string[] availableTargets;
                bool isStarted;
                Exception lastException;
                var currentProcedureId = _machine.CurrentStateId;
                lock (_sync)
                {
                    registeredIds = _registeredIds.ToArray();
                    isStarted = _started && !_stopping && !_stopped;
                    lastException = _lastException;
                    if (!isStarted || _machine.IsFaulted)
                    {
                        availableTargets = Array.Empty<string>();
                    }
                    else
                    {
                        var targets = new List<string>(
                            Math.Max(0, _registeredIds.Count - 1));
                        for (var index = 0;
                             index < _registeredIds.Count;
                             index++)
                        {
                            if (!string.Equals(
                                    _registeredIds[index],
                                    currentProcedureId,
                                    StringComparison.Ordinal))
                            {
                                targets.Add(_registeredIds[index]);
                            }
                        }

                        availableTargets = targets.ToArray();
                    }
                }

                var machineDiagnostics = FindMachineDiagnostics();
                if (machineDiagnostics?.LastException != null)
                {
                    lastException = machineDiagnostics.LastException;
                    lock (_sync)
                    {
                        _lastException = lastException;
                    }
                }

                return new ProcedureDiagnostics(
                    MainMachineId,
                    currentProcedureId,
                    _machine.PreviousStateId,
                    isStarted,
                    _machine.IsFaulted,
                    Array.AsReadOnly(registeredIds),
                    _machine.History,
                    lastException,
                    Array.AsReadOnly(availableTargets));
            }
        }

        public void Register(ProcedureBase procedure)
        {
            if (procedure == null)
            {
                throw new ArgumentNullException(nameof(procedure));
            }

            ValidateId(procedure.Id, nameof(procedure));
            lock (_sync)
            {
                EnsureActive();
                if (_registrationClosed)
                {
                    throw new InvalidOperationException(
                        "Procedures cannot be registered after start begins.");
                }

                if (_procedures.ContainsKey(procedure.Id))
                {
                    throw new InvalidOperationException(
                        $"Procedure '{procedure.Id}' is already registered.");
                }

                var adapter = new ProcedureStateAdapter(this, procedure);
                _machine.RegisterState(procedure.Id, adapter);
                for (var index = 0; index < _registeredIds.Count; index++)
                {
                    var existingId = _registeredIds[index];
                    _machine.RegisterTransition(
                        new StateTransition<ProcedureContext>(
                            existingId,
                            TriggerFor(procedure.Id),
                            procedure.Id));
                    _machine.RegisterTransition(
                        new StateTransition<ProcedureContext>(
                            procedure.Id,
                            TriggerFor(existingId),
                            existingId));
                }

                _procedures.Add(procedure.Id, adapter);
                _registeredIds.Add(procedure.Id);
            }
        }

        public ValueTask StartAsync(
            string initialId,
            CancellationToken token = default)
        {
            ValidateId(initialId, nameof(initialId));
            ThrowIfCallbackReentry(nameof(StartAsync));
            ValueTask start;
            lock (_sync)
            {
                EnsureActive();
                if (_starting || _started)
                {
                    throw new InvalidOperationException(
                        "The main Procedure is already starting or started.");
                }

                if (!_procedures.ContainsKey(initialId))
                {
                    throw new KeyNotFoundException(
                        $"Procedure '{initialId}' is not registered.");
                }

                _registrationClosed = true;
                _starting = true;
                try
                {
                    start = _machine.StartAsync(initialId, token);
                }
                catch
                {
                    _starting = false;
                    throw;
                }
            }

            return new ValueTask(CompleteStartAsync(start));
        }

        public ValueTask ChangeAsync(
            string targetId,
            CancellationToken token = default)
        {
            ValidateId(targetId, nameof(targetId));
            ThrowIfCallbackReentry(nameof(ChangeAsync));
            ValueTask change;
            lock (_sync)
            {
                EnsureActive();
                if (_starting)
                {
                    throw new InvalidOperationException(
                        "The initial Procedure is still entering.");
                }

                if (!_started)
                {
                    throw new InvalidOperationException(
                        "The main Procedure must be started before changing.");
                }

                if (_machine.IsFaulted)
                {
                    // FireAsync supplies the canonical FSM fault and original
                    // failure as its inner exception.
                    return _machine.FireAsync(TriggerFor(targetId), token);
                }

                if (!_procedures.ContainsKey(targetId))
                {
                    throw new KeyNotFoundException(
                        $"Procedure '{targetId}' is not registered.");
                }

                if (string.Equals(
                        targetId,
                        _machine.CurrentStateId,
                        StringComparison.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    return default;
                }

                change = _machine.FireAsync(TriggerFor(targetId), token);
            }

            return new ValueTask(CompleteOperationAsync(change));
        }

        public ValueTask StopAsync(CancellationToken token = default)
        {
            ThrowIfCallbackReentry(nameof(StopAsync));
            Task canonical;
            TaskCompletionSource<bool> completion = null;
            lock (_sync)
            {
                if (_stopTask != null)
                {
                    canonical = _stopTask;
                }
                else if (_stopped)
                {
                    canonical = Task.CompletedTask;
                }
                else
                {
                    _stopping = true;
                    _started = false;
                    completion = NewCompletion();
                    canonical = completion.Task;
                    _stopTask = canonical;
                }
            }

            if (completion != null)
            {
                _ = CompleteStopAsync(completion);
            }

            return token.IsCancellationRequested
                ? new ValueTask(CompleteCanceledStopAsync(canonical, token))
                : new ValueTask(canonical);
        }

        public ValueTask DisposeAsync()
        {
            ThrowIfCallbackReentry(nameof(DisposeAsync));
            return StopAsync();
        }

        private async Task CompleteStartAsync(ValueTask start)
        {
            try
            {
                await start;
                lock (_sync)
                {
                    _started = !_stopping && !_stopped;
                }
            }
            catch (Exception exception)
            {
                RecordException(exception);
                throw;
            }
            finally
            {
                lock (_sync)
                {
                    _starting = false;
                }
            }
        }

        private async Task CompleteOperationAsync(ValueTask operation)
        {
            try
            {
                await operation;
            }
            catch (Exception exception)
            {
                RecordException(exception);
                throw;
            }
        }

        private async Task CompleteStopAsync(
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await _fsmService.RemoveAsync(MainMachineId);
                lock (_sync)
                {
                    _stopping = false;
                    _stopped = true;
                }

                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                RecordException(exception);
                completion.TrySetException(exception);
            }
        }

        private static async Task CompleteCanceledStopAsync(
            Task cleanup,
            CancellationToken token)
        {
            await cleanup;
            token.ThrowIfCancellationRequested();
        }

        private FsmDiagnostics FindMachineDiagnostics()
        {
            var diagnostics = _fsmService.Diagnostics;
            for (var index = 0; index < diagnostics.Count; index++)
            {
                if (string.Equals(
                        diagnostics[index].MachineId,
                        MainMachineId,
                        StringComparison.Ordinal))
                {
                    return diagnostics[index];
                }
            }

            return null;
        }

        private void RecordException(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return;
            }

            lock (_sync)
            {
                _lastException = exception;
            }
        }

        private void EnsureActive()
        {
            if (_stopping || _stopped)
            {
                throw new ObjectDisposedException(
                    nameof(ProcedureService),
                    "The main Procedure is stopping or stopped.");
            }
        }

        private void ThrowIfCallbackReentry(string operation)
        {
            var frame = Volatile.Read(ref CallbackOwner).Value;
            if (frame != null &&
                frame.IsActive &&
                ReferenceEquals(frame.Service, this))
            {
                throw new InvalidOperationException(
                    $"Procedure service cannot start {operation} from one of " +
                    "its Procedure callbacks.");
            }
        }

        private static string TriggerFor(string targetId)
        {
            return "Procedure.ChangeTo:" + targetId;
        }

        private static void ValidateId(string id, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A Procedure ID is required.",
                    parameterName);
            }
        }

        private static TaskCompletionSource<bool> NewCompletion()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class ProcedureStateAdapter :
            IState<ProcedureContext>
        {
            private readonly ProcedureService _service;
            private readonly ProcedureBase _procedure;

            public ProcedureStateAdapter(
                ProcedureService service,
                ProcedureBase procedure)
            {
                _service = service;
                _procedure = procedure;
            }

            public async ValueTask EnterAsync(
                ProcedureContext context,
                CancellationToken token)
            {
                var holder = Volatile.Read(ref CallbackOwner);
                var previous = holder.Value;
                var frame = new CallbackFrame(_service);
                holder.Value = frame;
                try
                {
                    await _procedure.EnterAsync(context, token);
                }
                finally
                {
                    frame.Deactivate();
                    holder.Value = previous;
                }
            }

            public void Update(ProcedureContext context, float deltaTime)
            {
                var holder = Volatile.Read(ref CallbackOwner);
                var previous = holder.Value;
                var frame = new CallbackFrame(_service);
                holder.Value = frame;
                try
                {
                    _procedure.Update(context, deltaTime);
                }
                finally
                {
                    frame.Deactivate();
                    holder.Value = previous;
                }
            }

            public async ValueTask ExitAsync(
                ProcedureContext context,
                CancellationToken token)
            {
                var holder = Volatile.Read(ref CallbackOwner);
                var previous = holder.Value;
                var frame = new CallbackFrame(_service);
                holder.Value = frame;
                try
                {
                    await _procedure.ExitAsync(context, token);
                }
                finally
                {
                    frame.Deactivate();
                    holder.Value = previous;
                }
            }
        }

        private sealed class CallbackFrame
        {
            private int _active = 1;

            public CallbackFrame(ProcedureService service)
            {
                Service = service;
            }

            public ProcedureService Service { get; }
            public bool IsActive => Volatile.Read(ref _active) != 0;

            public void Deactivate()
            {
                Interlocked.Exchange(ref _active, 0);
            }
        }
    }
}
