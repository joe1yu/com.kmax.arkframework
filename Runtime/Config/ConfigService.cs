using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class ConfigService : IConfigService, IAsyncDisposable
    {
        private const string ModuleId = BuiltInModuleIds.Config;
        private const string CleanupCategory = "Cleanup";
        private const string EventCategory = "Event";

        private readonly object _lifecycleSync = new object();
        private readonly object _validatorSync = new object();
        private readonly IConfigProvider[] _providers;
        private readonly IEventBus _eventBus;
        private readonly IFrameworkLogger _logger;
        private readonly SemaphoreSlim _reloadGate = new SemaphoreSlim(1, 1);
        private readonly AsyncLocal<int> _operationExecutionDepth =
            new AsyncLocal<int>();
        private readonly Dictionary<Type, List<IValidatorAdapter>> _validators =
            new Dictionary<Type, List<IValidatorAdapter>>();
        private ActiveSnapshot _active = ActiveSnapshot.Empty;
        private ValidationDiagnosticState _validationDiagnostic =
            ValidationDiagnosticState.None;
        private TaskCompletionSource<bool> _reloadsDrained;
        private int _activeReloadCount;
        private bool _stopped;
        private Task _stopTask;
        private Task _disposeTask;
        private int _disposed;

        public ConfigService(
            IEnumerable<IConfigProvider> providers,
            IEventBus eventBus,
            IFrameworkLogger logger = null)
        {
            if (providers == null)
            {
                throw new ArgumentNullException(nameof(providers));
            }

            _providers = providers.ToArray();
            for (var index = 0; index < _providers.Length; index++)
            {
                if (_providers[index] == null)
                {
                    throw new ArgumentException(
                        "The config provider list cannot contain null.",
                        nameof(providers));
                }
            }

            _eventBus =
                eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger ?? new UnityFrameworkLogger();
        }

        public ConfigDiagnostics Diagnostics
        {
            get
            {
                var active = Volatile.Read(ref _active).Diagnostics;
                var validation = Volatile.Read(ref _validationDiagnostic);
                return new ConfigDiagnostics(
                    active.Entries,
                    active.LastSuccessfulReloadUtc,
                    validation.Succeeded,
                    validation.Error);
            }
        }

        public T Get<T>(string key)
        {
            if (TryGet(key, out T value))
            {
                return value;
            }

            throw new KeyNotFoundException(
                $"Config '{typeof(T).FullName}:{key}' is not loaded or does " +
                "not match the requested type.");
        }

        public bool TryGet<T>(string key, out T value)
        {
            var configKey = new ConfigKey(typeof(T), key);
            var snapshot = Volatile.Read(ref _active);
            if (snapshot.Entries.TryGetValue(
                    configKey,
                    out var entry) &&
                entry.Value is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public void RegisterValidator<T>(IConfigValidator<T> validator)
        {
            if (validator == null)
            {
                throw new ArgumentNullException(nameof(validator));
            }

            lock (_lifecycleSync)
            {
                ThrowIfStoppedNoLock();
                lock (_validatorSync)
                {
                    var type = typeof(T);
                    if (!_validators.TryGetValue(
                            type,
                            out var validators))
                    {
                        validators = new List<IValidatorAdapter>();
                        _validators.Add(type, validators);
                    }

                    validators.Add(new ValidatorAdapter<T>(validator));
                }
            }
        }

        public ValueTask ReloadAsync(CancellationToken token = default)
        {
            lock (_lifecycleSync)
            {
                ThrowIfStoppedNoLock();
                _activeReloadCount++;
                return new ValueTask(ReloadTrackedAsync(token));
            }
        }

        public ValueTask StopAsync(CancellationToken token = default)
        {
            var reentrant = _operationExecutionDepth.Value != 0;
            var stopTask = EnsureStopStarted();
            if (reentrant)
            {
                return default;
            }

            return token.CanBeCanceled
                ? new ValueTask(ApplyStopCancellationAsync(stopTask, token))
                : new ValueTask(stopTask);
        }

        public ValueTask DisposeAsync()
        {
            var reentrant = _operationExecutionDepth.Value != 0;
            var stopTask = EnsureStopStarted();
            Task disposeTask;
            TaskCompletionSource<bool> disposeCompletion = null;
            lock (_lifecycleSync)
            {
                if (_disposeTask == null)
                {
                    disposeCompletion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _disposeTask = disposeCompletion.Task;
                }

                disposeTask = _disposeTask;
            }

            if (disposeCompletion != null)
            {
                _ = CompleteDisposeAsync(stopTask, disposeCompletion);
            }

            return reentrant ? default : new ValueTask(disposeTask);
        }

        private Task EnsureStopStarted()
        {
            Task stopTask;
            Task reloadsDrained = null;
            TaskCompletionSource<bool> stopCompletion = null;
            lock (_lifecycleSync)
            {
                _stopped = true;
                if (_stopTask == null)
                {
                    stopCompletion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _stopTask = stopCompletion.Task;
                    reloadsDrained = GetReloadDrainTaskNoLock();
                }

                stopTask = _stopTask;
            }

            if (stopCompletion != null)
            {
                _ = CompleteStopAsync(reloadsDrained, stopCompletion);
            }

            return stopTask;
        }

        private async Task CompleteDisposeAsync(
            Task stopTask,
            TaskCompletionSource<bool> completion)
        {
            Exception failure = null;
            try
            {
                await stopTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    {
                        _reloadGate.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    failure = failure == null
                        ? exception
                        : new AggregateException(failure, exception);
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

        private async Task ReloadTrackedAsync(CancellationToken token)
        {
            _operationExecutionDepth.Value++;
            try
            {
                var changes = await CommitReloadAsync(token);
                PublishChanges(changes);
            }
            finally
            {
                try
                {
                    CompleteReload();
                }
                finally
                {
                    _operationExecutionDepth.Value--;
                }
            }
        }

        private async Task<ConfigChanged[]> CommitReloadAsync(
            CancellationToken token)
        {
            await _reloadGate.WaitAsync(token);
            var candidates = new List<ConfigProviderSnapshot>(
                _providers.Length);
            var ownershipTransferred = false;
            try
            {
                token.ThrowIfCancellationRequested();
                for (var index = 0; index < _providers.Length; index++)
                {
                    var snapshot =
                        await _providers[index].LoadAsync(token);
                    if (snapshot == null)
                    {
                        throw new InvalidOperationException(
                            $"Config provider '{_providers[index].Name}' " +
                            "returned a null snapshot.");
                    }

                    candidates.Add(snapshot);
                }

                var merged = Merge(candidates);
                try
                {
                    Validate(merged);
                }
                catch (Exception validationFailure)
                {
                    Volatile.Write(
                        ref _validationDiagnostic,
                        ValidationDiagnosticState.Failed(
                            validationFailure));
                    throw;
                }
                Volatile.Write(
                    ref _validationDiagnostic,
                    ValidationDiagnosticState.Success);
                var previous = Volatile.Read(ref _active);
                var changes = CreateChanges(previous.Entries, merged);
                var next = ActiveSnapshot.Create(
                    merged,
                    candidates.ToArray(),
                    DateTime.UtcNow);

                Volatile.Write(ref _active, next);
                ownershipTransferred = true;

                TryDisposeCommittedSnapshot(previous);
                return changes;
            }
            catch (Exception primary)
            {
                if (!ownershipTransferred)
                {
                    var owners = new IDisposable[candidates.Count];
                    for (var index = 0; index < candidates.Count; index++)
                    {
                        owners[index] = candidates[index];
                    }

                    var cleanup = ConfigCleanup.DisposeAll(owners);
                    ConfigCleanup.ThrowPrimaryWithCleanup(primary, cleanup);
                }

                throw;
            }
            finally
            {
                _reloadGate.Release();
            }
        }

        private Task GetReloadDrainTaskNoLock()
        {
            if (_activeReloadCount == 0)
            {
                return Task.CompletedTask;
            }

            if (_reloadsDrained == null)
            {
                _reloadsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return _reloadsDrained.Task;
        }

        private void CompleteReload()
        {
            TaskCompletionSource<bool> reloadsDrained = null;
            lock (_lifecycleSync)
            {
                _activeReloadCount--;
                if (_activeReloadCount == 0)
                {
                    reloadsDrained = _reloadsDrained;
                    _reloadsDrained = null;
                }
            }

            reloadsDrained?.TrySetResult(true);
        }

        private async Task CompleteStopAsync(
            Task reloadsDrained,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await StopCoreAsync(reloadsDrained).ConfigureAwait(false);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task StopCoreAsync(Task reloadsDrained)
        {
            _operationExecutionDepth.Value++;
            try
            {
                await reloadsDrained;
                await _reloadGate.WaitAsync();
                try
                {
                    var previous = Interlocked.Exchange(
                        ref _active,
                        ActiveSnapshot.Empty);
                    var cleanup = previous.DisposeResources();
                    if (cleanup != null)
                    {
                        SafeLogError(
                            CleanupCategory,
                            "Failed to release one or more active config resources.",
                            cleanup);
                        throw cleanup;
                    }
                }
                finally
                {
                    _reloadGate.Release();
                }
            }
            finally
            {
                _operationExecutionDepth.Value--;
            }
        }

        private static async Task ApplyStopCancellationAsync(
            Task stopTask,
            CancellationToken token)
        {
            await stopTask.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }

        private Dictionary<ConfigKey, ConfigEntry> Merge(
            IReadOnlyList<ConfigProviderSnapshot> candidates)
        {
            var merged = new Dictionary<ConfigKey, ConfigEntry>();
            for (var providerIndex = 0;
                 providerIndex < candidates.Count;
                 providerIndex++)
            {
                var entries = candidates[providerIndex].Entries;
                for (var entryIndex = 0;
                     entryIndex < entries.Count;
                     entryIndex++)
                {
                    var entry = entries[entryIndex];
                    if (entry == null)
                    {
                        throw new InvalidOperationException(
                            "A provider snapshot contains a null config entry.");
                    }

                    merged[entry.Key] = entry;
                }
            }

            return merged;
        }

        private void Validate(IReadOnlyDictionary<ConfigKey, ConfigEntry> entries)
        {
            Dictionary<Type, IValidatorAdapter[]> validators;
            lock (_validatorSync)
            {
                validators =
                    new Dictionary<Type, IValidatorAdapter[]>(
                        _validators.Count);
                foreach (var pair in _validators)
                {
                    validators.Add(pair.Key, pair.Value.ToArray());
                }
            }

            foreach (var pair in entries)
            {
                if (!validators.TryGetValue(
                        pair.Key.Type,
                        out var matching))
                {
                    continue;
                }

                for (var index = 0; index < matching.Length; index++)
                {
                    matching[index].Validate(
                        pair.Key.Key,
                        pair.Value.Value);
                }
            }
        }

        private static ConfigChanged[] CreateChanges(
            IReadOnlyDictionary<ConfigKey, ConfigEntry> previous,
            IReadOnlyDictionary<ConfigKey, ConfigEntry> next)
        {
            var keys = new HashSet<ConfigKey>(previous.Keys);
            keys.UnionWith(next.Keys);
            var orderedKeys = keys.ToArray();
            Array.Sort(
                orderedKeys,
                (left, right) =>
                {
                    var typeComparison = string.CompareOrdinal(
                        left.Type.AssemblyQualifiedName,
                        right.Type.AssemblyQualifiedName);
                    return typeComparison != 0
                        ? typeComparison
                        : string.CompareOrdinal(left.Key, right.Key);
                });

            var changes = new List<ConfigChanged>(orderedKeys.Length);
            for (var index = 0; index < orderedKeys.Length; index++)
            {
                var key = orderedKeys[index];
                previous.TryGetValue(key, out var oldEntry);
                next.TryGetValue(key, out var newEntry);
                if (!HasChanged(oldEntry, newEntry))
                {
                    continue;
                }

                changes.Add(
                    new ConfigChanged(
                        key.Type,
                        key.Key,
                        oldEntry?.Source,
                        newEntry?.Source,
                        oldEntry?.Version,
                        newEntry?.Version));
            }

            return changes.ToArray();
        }

        private static bool HasChanged(
            ConfigEntry previous,
            ConfigEntry next)
        {
            if (previous == null || next == null)
            {
                return previous != next;
            }

            return !string.Equals(
                       previous.Source,
                       next.Source,
                       StringComparison.Ordinal) ||
                !string.Equals(
                    previous.Version,
                    next.Version,
                    StringComparison.Ordinal) ||
                !object.Equals(previous.Value, next.Value);
        }

        private void TryDisposeCommittedSnapshot(ActiveSnapshot snapshot)
        {
            var cleanup = snapshot.DisposeResources();
            if (cleanup != null)
            {
                SafeLogError(
                    CleanupCategory,
                    "Failed to release one or more previous config resources.",
                    cleanup);
            }
        }

        private void PublishChanges(IReadOnlyList<ConfigChanged> changes)
        {
            for (var index = 0; index < changes.Count; index++)
            {
                try
                {
                    _eventBus.Publish(changes[index]);
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        EventCategory,
                        $"Failed to publish config change " +
                        $"'{changes[index].Type.FullName}:{changes[index].Key}'.",
                        exception);
                }
            }
        }

        private void SafeLogError(
            string category,
            string message,
            Exception exception)
        {
            try
            {
                _logger.Error(ModuleId, category, message, exception);
            }
            catch
            {
                // Logging cannot change committed config state or failures.
            }
        }

        private void ThrowIfStoppedNoLock()
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(ConfigService));
            }
        }

        private interface IValidatorAdapter
        {
            void Validate(string key, object value);
        }

        private sealed class ValidatorAdapter<T> : IValidatorAdapter
        {
            private readonly IConfigValidator<T> _validator;

            public ValidatorAdapter(IConfigValidator<T> validator)
            {
                _validator = validator;
            }

            public void Validate(string key, object value)
            {
                _validator.Validate(key, (T)value);
            }
        }

        private sealed class ActiveSnapshot
        {
            private static readonly IReadOnlyDictionary<ConfigKey, ConfigEntry>
                EmptyEntries =
                    new ReadOnlyDictionary<ConfigKey, ConfigEntry>(
                        new Dictionary<ConfigKey, ConfigEntry>());
            private static readonly ConfigDiagnostics EmptyDiagnostics =
                new ConfigDiagnostics(
                    new Dictionary<ConfigKey, ConfigEntryDiagnostics>(),
                    null);

            private ConfigProviderSnapshot[] _providers;

            private ActiveSnapshot(
                IReadOnlyDictionary<ConfigKey, ConfigEntry> entries,
                ConfigProviderSnapshot[] providers,
                ConfigDiagnostics diagnostics)
            {
                Entries = entries;
                _providers = providers;
                Diagnostics = diagnostics;
            }

            public static ActiveSnapshot Empty =>
                new ActiveSnapshot(
                    EmptyEntries,
                    Array.Empty<ConfigProviderSnapshot>(),
                    EmptyDiagnostics);

            public IReadOnlyDictionary<ConfigKey, ConfigEntry> Entries { get; }

            public ConfigDiagnostics Diagnostics { get; }

            public static ActiveSnapshot Create(
                IDictionary<ConfigKey, ConfigEntry> entries,
                ConfigProviderSnapshot[] providers,
                DateTime successfulReloadUtc)
            {
                var entryCopy =
                    new Dictionary<ConfigKey, ConfigEntry>(entries);
                var diagnostics =
                    new Dictionary<ConfigKey, ConfigEntryDiagnostics>(
                        entryCopy.Count);
                foreach (var pair in entryCopy)
                {
                    diagnostics.Add(
                        pair.Key,
                        new ConfigEntryDiagnostics(
                            pair.Value.Source,
                            pair.Value.Version));
                }

                return new ActiveSnapshot(
                    new ReadOnlyDictionary<ConfigKey, ConfigEntry>(entryCopy),
                    providers,
                    new ConfigDiagnostics(
                        diagnostics,
                        successfulReloadUtc));
            }

            public Exception DisposeResources()
            {
                var providers = Interlocked.Exchange(ref _providers, null);
                if (providers == null)
                {
                    return null;
                }

                var owners = new IDisposable[providers.Length];
                for (var index = 0; index < providers.Length; index++)
                {
                    owners[index] = providers[index];
                }

                return ConfigCleanup.DisposeAll(owners);
            }
        }

        private sealed class ValidationDiagnosticState
        {
            public static readonly ValidationDiagnosticState None =
                new ValidationDiagnosticState(null, null);
            public static readonly ValidationDiagnosticState Success =
                new ValidationDiagnosticState(true, null);

            private ValidationDiagnosticState(bool? succeeded, string error)
            {
                Succeeded = succeeded;
                Error = error;
            }

            public bool? Succeeded { get; }

            public string Error { get; }

            public static ValidationDiagnosticState Failed(Exception exception)
            {
                return new ValidationDiagnosticState(
                    false,
                    SafeExceptionText(exception));
            }

            private static string SafeExceptionText(Exception exception)
            {
                if (exception == null)
                {
                    return "Config validation failed.";
                }

                try
                {
                    var text = exception.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
                catch
                {
                    // Diagnostics formatting must never replace the validator failure.
                }

                try
                {
                    return $"{exception.GetType().FullName}: " +
                           "exception text unavailable.";
                }
                catch
                {
                    return "Config validation exception text unavailable.";
                }
            }
        }
    }
}
