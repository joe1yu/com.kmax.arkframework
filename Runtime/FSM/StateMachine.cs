using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class StateMachine<TContext> : IStateMachine<TContext>
    {
        private static AsyncLocal<CallbackFrame> CallbackOwner =
            new AsyncLocal<CallbackFrame>();

        static StateMachine()
        {
            FrameworkStaticReset.Register(ResetStatics);
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, IState<TContext>> _states =
            new Dictionary<string, IState<TContext>>(StringComparer.Ordinal);
        private readonly Dictionary<TransitionKey, List<StateTransition<TContext>>>
            _transitions =
                new Dictionary<TransitionKey, List<StateTransition<TContext>>>();
        private readonly List<StateTransition<TContext>> _registeredTransitions =
            new List<StateTransition<TContext>>();
        private readonly Queue<FireRequest> _requests = new Queue<FireRequest>();
        private readonly List<StateHistoryEntry> _history =
            new List<StateHistoryEntry>();
        private readonly int _historyCapacity;
        private readonly CancellationTokenSource _lifetime =
            new CancellationTokenSource();

        private TContext _context;
        private string _currentStateId;
        private string _previousStateId;
        private Exception _lastException;
        private Exception _faultException;
        private bool _starting;
        private bool _started;
        private bool _consumerRunning;
        private bool _transitioning;
        private bool _faulted;
        private bool _disposing;
        private bool _disposed;
        private Task _startTask = Task.CompletedTask;
        private Task _consumerTask = Task.CompletedTask;
        private Task _disposeTask;

        public StateMachine(
            string id,
            TContext context,
            int historyCapacity = 32)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A state machine ID is required.",
                    nameof(id));
            }

            if (historyCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(historyCapacity),
                    historyCapacity,
                    "History capacity must be positive.");
            }

            Id = id;
            _context = context;
            _historyCapacity = historyCapacity;
        }

        private static void ResetStatics()
        {
            Interlocked.Exchange(
                ref CallbackOwner,
                new AsyncLocal<CallbackFrame>());
        }

        public string Id { get; }
        internal bool IsExecutingCallback
        {
            get
            {
                var frame = Volatile.Read(ref CallbackOwner).Value;
                return frame != null &&
                       frame.IsActive &&
                       ReferenceEquals(frame.Machine, this);
            }
        }

        public string CurrentStateId
        {
            get
            {
                lock (_sync)
                {
                    return _currentStateId;
                }
            }
        }

        public string PreviousStateId
        {
            get
            {
                lock (_sync)
                {
                    return _previousStateId;
                }
            }
        }

        public bool IsFaulted
        {
            get
            {
                lock (_sync)
                {
                    return _faulted;
                }
            }
        }

        public IReadOnlyList<StateHistoryEntry> History
        {
            get
            {
                lock (_sync)
                {
                    return Array.AsReadOnly(_history.ToArray());
                }
            }
        }

        public void RegisterState(string stateId, IState<TContext> state)
        {
            ValidateId(stateId, nameof(stateId), "state");
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            lock (_sync)
            {
                EnsureRegistrationAllowed();
                if (_states.ContainsKey(stateId))
                {
                    throw new InvalidOperationException(
                        $"State machine '{Id}' already contains state '{stateId}'.");
                }

                _states.Add(stateId, state);
            }
        }

        public void RegisterTransition(StateTransition<TContext> transition)
        {
            if (transition == null)
            {
                throw new ArgumentNullException(nameof(transition));
            }

            lock (_sync)
            {
                EnsureRegistrationAllowed();
                if (!_states.ContainsKey(transition.From))
                {
                    throw new InvalidOperationException(
                        $"State machine '{Id}' does not contain source state " +
                        $"'{transition.From}'.");
                }

                if (!_states.ContainsKey(transition.To))
                {
                    throw new InvalidOperationException(
                        $"State machine '{Id}' does not contain target state " +
                        $"'{transition.To}'.");
                }

                var key = new TransitionKey(
                    transition.From,
                    transition.Trigger);
                if (!_transitions.TryGetValue(key, out var candidates))
                {
                    candidates = new List<StateTransition<TContext>>();
                    _transitions.Add(key, candidates);
                }

                candidates.Add(transition);
                _registeredTransitions.Add(transition);
            }
        }

        public ValueTask StartAsync(
            string stateId,
            CancellationToken token = default)
        {
            ValidateId(stateId, nameof(stateId), "start state");
            ThrowIfCallbackReentry(nameof(StartAsync));
            lock (_sync)
            {
                EnsureNotDisposed();
                EnsureNotFaulted();
                if (token.IsCancellationRequested)
                {
                    return new ValueTask(Task.FromCanceled(token));
                }

                if (_starting || _started)
                {
                    throw new InvalidOperationException(
                        $"State machine '{Id}' can only be started successfully once.");
                }

                if (!_states.TryGetValue(stateId, out var state))
                {
                    throw new InvalidOperationException(
                        $"State machine '{Id}' does not contain start state " +
                        $"'{stateId}'.");
                }

                _starting = true;
                _startTask = StartCoreAsync(stateId, state, token);
                return new ValueTask(_startTask);
            }
        }

        public ValueTask FireAsync(
            string trigger,
            CancellationToken token = default)
        {
            ValidateId(trigger, nameof(trigger), "trigger");
            ThrowIfCallbackReentry(nameof(FireAsync));
            lock (_sync)
            {
                EnsureNotDisposed();
                EnsureNotFaulted();
                if (token.IsCancellationRequested)
                {
                    return new ValueTask(Task.FromCanceled(token));
                }

                if (!_started)
                {
                    throw new InvalidOperationException(
                        $"State machine '{Id}' must be started before firing " +
                        $"trigger '{trigger}'.");
                }

                var request = new FireRequest(trigger, token);
                _requests.Enqueue(request);
                if (!_consumerRunning)
                {
                    _consumerRunning = true;
                    _consumerTask = ConsumeAsync();
                }

                return new ValueTask(request.Completion.Task);
            }
        }

        public void Update(float deltaTime)
        {
            lock (_sync)
            {
                if (!_started ||
                    _faulted ||
                    _transitioning ||
                    _disposing ||
                    _disposed)
                {
                    return;
                }

                try
                {
                    InvokeUpdate(
                        _states[_currentStateId],
                        _context,
                        deltaTime);
                }
                catch (Exception exception)
                {
                    MarkFaulted(exception);
                    throw;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            ThrowIfCallbackReentry(nameof(DisposeAsync));
            lock (_sync)
            {
                if (_disposeTask != null)
                {
                    return new ValueTask(_disposeTask);
                }

                if (_disposed)
                {
                    return default;
                }

                _disposing = true;
                _lifetime.Cancel();
                CancelQueuedRequests();
                _disposeTask = DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        public FsmDiagnostics GetDiagnostics()
        {
            lock (_sync)
            {
                var available =
                    !_started ||
                    _faulted ||
                    _disposing ||
                    _disposed ||
                    _currentStateId == null
                        ? Array.Empty<FsmTransitionDiagnostics>()
                        : SnapshotAvailableTransitionsNoLock();
                return new FsmDiagnostics(
                    Id,
                    _currentStateId,
                    _previousStateId,
                    _faulted,
                    _transitioning,
                    _requests.Count,
                    Array.AsReadOnly(_history.ToArray()),
                    _lastException,
                    available);
            }
        }

        private FsmTransitionDiagnostics[] SnapshotAvailableTransitionsNoLock()
        {
            var values = new List<FsmTransitionDiagnostics>();
            for (var index = 0; index < _registeredTransitions.Count; index++)
            {
                var transition = _registeredTransitions[index];
                if (string.Equals(
                        transition.From,
                        _currentStateId,
                        StringComparison.Ordinal))
                {
                    values.Add(
                        new FsmTransitionDiagnostics(
                            transition.Trigger,
                            transition.To,
                            transition.Guard != null));
                }
            }

            return values.ToArray();
        }

        private async Task StartCoreAsync(
            string stateId,
            IState<TContext> state,
            CancellationToken requestToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       requestToken,
                       _lifetime.Token))
            {
                try
                {
                    await InvokeEnterAsync(state, _context, linked.Token);
                    lock (_sync)
                    {
                        _currentStateId = stateId;
                        _previousStateId = null;
                        _started = true;
                        _starting = false;
                        AddHistory(null, stateId, "Start");
                    }
                }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        _starting = false;
                    }

                    RecordException(exception);
                    throw;
                }
            }
        }

        private async Task ConsumeAsync()
        {
            while (true)
            {
                FireRequest request;
                lock (_sync)
                {
                    if (_faulted || _disposing || _disposed)
                    {
                        CancelOrFaultQueuedRequests();
                        _consumerRunning = false;
                        return;
                    }

                    if (_requests.Count == 0)
                    {
                        _consumerRunning = false;
                        return;
                    }

                    request = _requests.Dequeue();
                }

                if (request.Token.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.Token);
                    continue;
                }

                try
                {
                    await ProcessRequestAsync(request);
                    request.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException exception)
                {
                    request.Completion.TrySetCanceled(
                        exception.CancellationToken.CanBeCanceled
                            ? exception.CancellationToken
                            : new CancellationToken(canceled: true));
                }
                catch (Exception exception)
                {
                    RecordException(exception);
                    request.Completion.TrySetException(exception);
                }
            }
        }

        private async Task ProcessRequestAsync(FireRequest request)
        {
            StateTransition<TContext> selected = null;
            IState<TContext> oldState;
            IState<TContext> newState;
            string oldStateId;
            TContext context;
            lock (_sync)
            {
                EnsureNotFaulted();
                oldStateId = _currentStateId;
                context = _context;
                var key = new TransitionKey(oldStateId, request.Trigger);
                if (!_transitions.TryGetValue(key, out var candidates))
                {
                    throw new InvalidOperationException(
                        $"State machine '{Id}' in state '{oldStateId}' has no " +
                        $"transition for trigger '{request.Trigger}'.");
                }

                for (var index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    if (candidate.Guard == null ||
                        InvokeGuard(candidate.Guard, context))
                    {
                        selected = candidate;
                        break;
                    }
                }

                if (selected == null)
                {
                    return;
                }

                oldState = _states[oldStateId];
                newState = _states[selected.To];
                _transitioning = true;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       request.Token,
                       _lifetime.Token))
            {
                try
                {
                    try
                    {
                        await InvokeExitAsync(oldState, context, linked.Token);
                    }
                    catch (Exception exception)
                    {
                        MarkFaulted(exception);
                        throw;
                    }

                    try
                    {
                        await InvokeEnterAsync(newState, context, linked.Token);
                    }
                    catch (Exception enterException)
                    {
                        try
                        {
                            await InvokeEnterAsync(
                                oldState,
                                context,
                                CancellationToken.None);
                        }
                        catch (Exception rollbackException)
                        {
                            var combined = new AggregateException(
                                $"State machine '{Id}' failed to enter state " +
                                $"'{selected.To}' and failed to roll back to " +
                                $"'{oldStateId}'.",
                                enterException,
                                rollbackException);
                            MarkFaulted(combined);
                            throw combined;
                        }

                        ExceptionDispatchInfo.Capture(enterException).Throw();
                        throw;
                    }

                    lock (_sync)
                    {
                        _previousStateId = oldStateId;
                        _currentStateId = selected.To;
                        AddHistory(oldStateId, selected.To, request.Trigger);
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        _transitioning = false;
                    }
                }
            }
        }

        private async Task DisposeCoreAsync()
        {
            ExceptionDispatchInfo failure = null;
            try
            {
                try
                {
                    await _startTask;
                }
                catch
                {
                    // The caller of StartAsync observes its own failure.
                }

                await _consumerTask;

                IState<TContext> currentState = null;
                TContext context = default;
                lock (_sync)
                {
                    if (_started &&
                        _currentStateId != null &&
                        _states.TryGetValue(_currentStateId, out var state))
                    {
                        currentState = state;
                        context = _context;
                    }
                }

                if (currentState != null)
                {
                    try
                    {
                        await InvokeExitAsync(
                            currentState,
                            context,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        RecordException(exception);
                        failure = ExceptionDispatchInfo.Capture(exception);
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    CancelQueuedRequests();
                    _states.Clear();
                    _transitions.Clear();
                    _history.Clear();
                    _context = default;
                    _currentStateId = null;
                    _previousStateId = null;
                    _started = false;
                    _starting = false;
                    _consumerRunning = false;
                    _transitioning = false;
                    _disposing = false;
                    _disposed = true;
                }

                _lifetime.Dispose();
            }

            failure?.Throw();
        }

        private async Task InvokeEnterAsync(
            IState<TContext> state,
            TContext context,
            CancellationToken token)
        {
            var holder = Volatile.Read(ref CallbackOwner);
            var previous = holder.Value;
            var frame = new CallbackFrame(this);
            holder.Value = frame;
            try
            {
                await state.EnterAsync(context, token);
            }
            finally
            {
                frame.Deactivate();
                holder.Value = previous;
            }
        }

        private async Task InvokeExitAsync(
            IState<TContext> state,
            TContext context,
            CancellationToken token)
        {
            var holder = Volatile.Read(ref CallbackOwner);
            var previous = holder.Value;
            var frame = new CallbackFrame(this);
            holder.Value = frame;
            try
            {
                await state.ExitAsync(context, token);
            }
            finally
            {
                frame.Deactivate();
                holder.Value = previous;
            }
        }

        private bool InvokeGuard(
            Func<TContext, bool> guard,
            TContext context)
        {
            var holder = Volatile.Read(ref CallbackOwner);
            var previous = holder.Value;
            var frame = new CallbackFrame(this);
            holder.Value = frame;
            try
            {
                return guard(context);
            }
            finally
            {
                frame.Deactivate();
                holder.Value = previous;
            }
        }

        private void InvokeUpdate(
            IState<TContext> state,
            TContext context,
            float deltaTime)
        {
            var holder = Volatile.Read(ref CallbackOwner);
            var previous = holder.Value;
            var frame = new CallbackFrame(this);
            holder.Value = frame;
            try
            {
                state.Update(context, deltaTime);
            }
            finally
            {
                frame.Deactivate();
                holder.Value = previous;
            }
        }

        private void AddHistory(string from, string to, string trigger)
        {
            if (_history.Count == _historyCapacity)
            {
                _history.RemoveAt(0);
            }

            _history.Add(
                new StateHistoryEntry(from, to, trigger, DateTime.UtcNow));
        }

        private void MarkFaulted(Exception exception)
        {
            lock (_sync)
            {
                if (!_faulted)
                {
                    _faulted = true;
                    _faultException = exception;
                }

                RecordExceptionLocked(exception);
            }
        }

        private void RecordException(Exception exception)
        {
            lock (_sync)
            {
                RecordExceptionLocked(exception);
            }
        }

        private void RecordExceptionLocked(Exception exception)
        {
            if (!(exception is OperationCanceledException))
            {
                _lastException = exception;
            }
        }

        private void CancelOrFaultQueuedRequests()
        {
            while (_requests.Count != 0)
            {
                var request = _requests.Dequeue();
                if (_faulted)
                {
                    request.Completion.TrySetException(CreateFaultedException());
                }
                else
                {
                    request.Completion.TrySetCanceled(
                        new CancellationToken(canceled: true));
                }
            }
        }

        private void CancelQueuedRequests()
        {
            while (_requests.Count != 0)
            {
                _requests.Dequeue().Completion.TrySetCanceled(
                    new CancellationToken(canceled: true));
            }
        }

        private void EnsureRegistrationAllowed()
        {
            EnsureNotDisposed();
            if (_starting || _started)
            {
                throw new InvalidOperationException(
                    $"State machine '{Id}' cannot be modified after start begins.");
            }
        }

        private void EnsureNotFaulted()
        {
            if (_faulted)
            {
                throw CreateFaultedException();
            }
        }

        private InvalidOperationException CreateFaultedException()
        {
            return new InvalidOperationException(
                $"State machine '{Id}' is faulted and cannot continue.",
                _faultException);
        }

        private void EnsureNotDisposed()
        {
            if (_disposing || _disposed)
            {
                throw new ObjectDisposedException(
                    nameof(StateMachine<TContext>),
                    $"State machine '{Id}' is disposing or disposed.");
            }
        }

        private void ThrowIfCallbackReentry(string operation)
        {
            if (IsExecutingCallback)
            {
                throw new InvalidOperationException(
                    $"State machine '{Id}' cannot synchronously wait for its own " +
                    $"{operation} operation from a state callback.");
            }
        }

        private static void ValidateId(
            string value,
            string parameterName,
            string kind)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"A {kind} ID is required.",
                    parameterName);
            }
        }

        private readonly struct TransitionKey : IEquatable<TransitionKey>
        {
            public TransitionKey(string stateId, string trigger)
            {
                StateId = stateId;
                Trigger = trigger;
            }

            private string StateId { get; }
            private string Trigger { get; }

            public bool Equals(TransitionKey other)
            {
                return string.Equals(
                           StateId,
                           other.StateId,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           Trigger,
                           other.Trigger,
                           StringComparison.Ordinal);
            }

            public override bool Equals(object value)
            {
                return value is TransitionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((StateId != null
                                ? StringComparer.Ordinal.GetHashCode(StateId)
                                : 0) *
                            397) ^
                           (Trigger != null
                               ? StringComparer.Ordinal.GetHashCode(Trigger)
                               : 0);
                }
            }
        }

        private sealed class FireRequest
        {
            public FireRequest(string trigger, CancellationToken token)
            {
                Trigger = trigger;
                Token = token;
                Completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string Trigger { get; }
            public CancellationToken Token { get; }
            public TaskCompletionSource<bool> Completion { get; }
        }

        private sealed class CallbackFrame
        {
            private int _active = 1;

            public CallbackFrame(StateMachine<TContext> machine)
            {
                Machine = machine;
            }

            public StateMachine<TContext> Machine { get; }

            public bool IsActive => Volatile.Read(ref _active) != 0;

            public void Deactivate()
            {
                Interlocked.Exchange(ref _active, 0);
            }
        }
    }
}
