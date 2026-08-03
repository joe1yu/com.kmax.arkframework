using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class FrameworkRuntime : IAsyncDisposable
    {
        private const string LifecycleCategory = "Lifecycle";
        private const string FrameCategory = "Frame";

        private readonly List<ModuleRecord> _modules = new List<ModuleRecord>();
        private readonly IReadOnlyList<ModuleRecord> _moduleView;
        private readonly IFrameworkLogger _logger;
        private readonly SerializedOperationQueue _operations =
            new SerializedOperationQueue();
        private bool _startRequested;
        private bool _runtimeRunning;
        private bool _cleanupCompleted;
        private bool _disposed;
        private Task _startTask;
        private Task _stopTask;
        private Task _disposeTask;

        public FrameworkRuntime(IFrameworkLogger logger = null)
        {
            _logger = logger ?? new UnityFrameworkLogger();
            Services = new ServiceContainer();
            _moduleView = _modules.AsReadOnly();
        }

        public IReadOnlyList<ModuleRecord> Modules => _moduleView;

        public ServiceContainer Services { get; }

        public ValueTask StartAsync(
            IReadOnlyList<ModuleDescriptor> descriptors,
            CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FrameworkRuntime));
            }

            if (_startRequested)
            {
                throw new InvalidOperationException(
                    "FrameworkRuntime.StartAsync can only be called once.");
            }

            _startRequested = true;
            _startTask = _operations.Enqueue(
                () => StartCoreAsync(descriptors, token));
            return new ValueTask(_startTask);
        }

        private async Task StartCoreAsync(
            IReadOnlyList<ModuleDescriptor> descriptors,
            CancellationToken token)
        {
            var sortedDescriptors = ModuleGraph.Sort(descriptors);
            ModuleRecord currentRecord = null;
            try
            {
                for (var index = 0; index < sortedDescriptors.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var descriptor = sortedDescriptors[index];
                    currentRecord = new ModuleRecord(descriptor);
                    _modules.Add(currentRecord);

                    var module = descriptor.Factory();
                    currentRecord.Module = module;
                    ValidateModuleContract(descriptor, module);

                    var scope = Services.CreateScope(descriptor.Id);
                    currentRecord.Scope = scope;
                    var context = new ModuleContext(
                        Services,
                        scope,
                        _logger,
                        descriptor.Id);

                    await InitializeAsync(currentRecord, context, token);
                    await StartModuleAsync(currentRecord, token);
                    currentRecord = null;
                }

                _runtimeRunning = true;
            }
            catch (Exception exception)
            {
                _runtimeRunning = false;
                if (currentRecord != null)
                {
                    Transition(currentRecord, ModuleState.Faulted, exception);
                }

                SafeLogError(
                    currentRecord?.Descriptor.Id ?? nameof(FrameworkRuntime),
                    LifecycleCategory,
                    "Framework startup failed.",
                    exception);
                await CleanupAsync(
                    CancellationToken.None,
                    preserveExistingFailures: true,
                    propagateFailure: false);
                _cleanupCompleted = true;
                throw;
            }
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            if (!_startRequested || _cleanupCompleted)
            {
                return default;
            }

            if (_stopTask == null)
            {
                _stopTask = _operations.Enqueue(() => StopIfNeededAsync(token));
            }

            return new ValueTask(_stopTask);
        }

        public ValueTask InstallAsync(
            ModuleDescriptor descriptor,
            CancellationToken token = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FrameworkRuntime));
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            return new ValueTask(
                _operations.Enqueue(() => InstallCoreAsync(descriptor, token)));
        }

        public ValueTask<ModuleUnloadResult> UnloadAsync(
            string moduleId,
            ModuleUnloadMode mode = ModuleUnloadMode.RequireNoDependents,
            CancellationToken token = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FrameworkRuntime));
            }

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                throw new ArgumentException(
                    "A module ID cannot be null, empty, or whitespace.",
                    nameof(moduleId));
            }

            if (mode != ModuleUnloadMode.RequireNoDependents &&
                mode != ModuleUnloadMode.Cascade)
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }

            return new ValueTask<ModuleUnloadResult>(
                _operations.Enqueue(() => UnloadCoreAsync(moduleId, mode, token)));
        }

        public void Update(float deltaTime)
        {
            DispatchFrame<IUpdateModule>(
                module => module.Update(deltaTime),
                nameof(Update));
        }

        public void LateUpdate(float deltaTime)
        {
            DispatchFrame<ILateUpdateModule>(
                module => module.LateUpdate(deltaTime),
                nameof(LateUpdate));
        }

        public void FixedUpdate(float fixedDeltaTime)
        {
            DispatchFrame<IFixedUpdateModule>(
                module => module.FixedUpdate(fixedDeltaTime),
                nameof(FixedUpdate));
        }

        public ValueTask DisposeAsync()
        {
            if (_disposeTask == null)
            {
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }

        private async Task StopCoreAsync(CancellationToken token)
        {
            _runtimeRunning = false;
            ExceptionDispatchInfo failure = null;
            try
            {
                var exception = await CleanupAsync(
                    token,
                    preserveExistingFailures: false,
                    propagateFailure: true);
                if (exception != null)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            }
            finally
            {
                _cleanupCompleted = true;
            }

            failure?.Throw();
        }

        private async Task StopIfNeededAsync(CancellationToken token)
        {
            if (!_cleanupCompleted)
            {
                await StopCoreAsync(token);
            }
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                await StopAsync(CancellationToken.None);
            }
            finally
            {
                _disposed = true;
            }
        }

        private async Task<ModuleUnloadResult> UnloadCoreAsync(
            string moduleId,
            ModuleUnloadMode mode,
            CancellationToken token)
        {
            EnsureRuntimeAcceptsGraphMutation();

            var descriptors = SnapshotDescriptors();
            var targetFound = false;
            for (var index = 0; index < descriptors.Count; index++)
            {
                if (string.Equals(
                        descriptors[index].Id,
                        moduleId,
                        StringComparison.Ordinal))
                {
                    targetFound = true;
                    break;
                }
            }

            if (!targetFound)
            {
                throw new InvalidOperationException(
                    $"Cannot unload unknown module '{moduleId}'.");
            }

            var closureIds = FindDependentClosure(moduleId, descriptors);
            if (mode == ModuleUnloadMode.RequireNoDependents &&
                closureIds.Count != 1)
            {
                var dependentIds = new List<string>(closureIds.Count - 1);
                for (var index = 0; index < descriptors.Count; index++)
                {
                    var descriptorId = descriptors[index].Id;
                    if (!string.Equals(
                            descriptorId,
                            moduleId,
                            StringComparison.Ordinal) &&
                        closureIds.Contains(descriptorId))
                    {
                        dependentIds.Add(descriptorId);
                    }
                }

                throw new InvalidOperationException(
                    $"Cannot unload module '{moduleId}' because dependent modules " +
                    $"are still installed: {string.Join(", ", dependentIds)}.");
            }

            token.ThrowIfCancellationRequested();
            var selectedIds = mode == ModuleUnloadMode.Cascade
                ? closureIds
                : new HashSet<string>(StringComparer.Ordinal) { moduleId };
            var sortedDescriptors = ModuleGraph.Sort(descriptors);
            var selectedRecords = SelectRecords(sortedDescriptors, selectedIds);
            var unloadedIds = new string[selectedRecords.Count];

            // 在第一次异步清理前一次性关闭全部目标的帧回调入口。
            for (var index = 0; index < selectedRecords.Count; index++)
            {
                var record = selectedRecords[index];
                if (record.State == ModuleState.Running)
                {
                    Transition(record, ModuleState.Stopping);
                }

                unloadedIds[index] =
                    selectedRecords[selectedRecords.Count - index - 1].Descriptor.Id;
            }

            Exception cleanupException;
            try
            {
                cleanupException = await CleanupRecordsAsync(
                    selectedRecords,
                    CancellationToken.None,
                    "module unloading");
            }
            finally
            {
                RemoveRecords(selectedIds);
            }

            if (cleanupException != null)
            {
                throw cleanupException is AggregateException
                    ? cleanupException
                    : new AggregateException(cleanupException);
            }

            return new ModuleUnloadResult(unloadedIds);
        }

        private IReadOnlyList<ModuleDescriptor> SnapshotDescriptors()
        {
            var descriptors = new ModuleDescriptor[_modules.Count];
            for (var index = 0; index < _modules.Count; index++)
            {
                descriptors[index] = _modules[index].Descriptor;
            }

            return Array.AsReadOnly(descriptors);
        }

        private void RemoveRecords(ISet<string> selectedIds)
        {
            for (var index = _modules.Count - 1; index >= 0; index--)
            {
                if (selectedIds.Contains(_modules[index].Descriptor.Id))
                {
                    _modules.RemoveAt(index);
                }
            }
        }

        private async Task InstallCoreAsync(
            ModuleDescriptor descriptor,
            CancellationToken token)
        {
            var sortedDescriptors = ValidateInstall(descriptor);
            token.ThrowIfCancellationRequested();

            var selectedIds = new HashSet<string>(StringComparer.Ordinal)
            {
                descriptor.Id
            };
            var candidateRecords = new List<ModuleRecord>(1);
            try
            {
                await InstallRecordsAsync(
                    sortedDescriptors,
                    selectedIds,
                    candidateRecords,
                    token);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                // 候选记录尚未提交，只清理本次安装创建的模块和作用域。
                var cleanupException = await CleanupRecordsAsync(
                    candidateRecords,
                    CancellationToken.None,
                    "module installation");
                if (cleanupException != null)
                {
                    throw new AggregateException(exception, cleanupException);
                }

                throw;
            }

            CommitRecords(sortedDescriptors, selectedIds, candidateRecords);
        }

        private IReadOnlyList<ModuleDescriptor> ValidateInstall(
            ModuleDescriptor descriptor)
        {
            EnsureRuntimeAcceptsGraphMutation();

            var descriptors = new ModuleDescriptor[_modules.Count + 1];
            for (var index = 0; index < _modules.Count; index++)
            {
                var record = _modules[index];
                if (string.Equals(
                    record.Descriptor.Id,
                    descriptor.Id,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Cannot install duplicate module ID '{descriptor.Id}'.");
                }

                descriptors[index] = record.Descriptor;
            }

            foreach (var dependencyId in descriptor.Dependencies)
            {
                var dependencyFound = false;
                for (var index = 0; index < _modules.Count; index++)
                {
                    var record = _modules[index];
                    if (!string.Equals(
                            record.Descriptor.Id,
                            dependencyId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    dependencyFound = true;
                    if (record.State != ModuleState.Running)
                    {
                        throw new InvalidOperationException(
                            $"Cannot install module '{descriptor.Id}' because dependency " +
                            $"'{dependencyId}' is not running.");
                    }

                    break;
                }

                if (!dependencyFound)
                {
                    throw new InvalidOperationException(
                        $"Cannot install module '{descriptor.Id}' because dependency " +
                        $"'{dependencyId}' is missing.");
                }
            }

            descriptors[descriptors.Length - 1] = descriptor;
            return ModuleGraph.Sort(descriptors);
        }

        private void EnsureRuntimeAcceptsGraphMutation()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FrameworkRuntime));
            }

            if (!_startRequested || !_runtimeRunning || _cleanupCompleted)
            {
                throw new InvalidOperationException(
                    "FrameworkRuntime must be running before changing modules.");
            }
        }

        private static HashSet<string> FindDependentClosure(
            string moduleId,
            IReadOnlyList<ModuleDescriptor> descriptors)
        {
            var closure = new HashSet<string>(StringComparer.Ordinal)
            {
                moduleId
            };
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var index = 0; index < descriptors.Count; index++)
                {
                    var descriptor = descriptors[index];
                    if (closure.Contains(descriptor.Id))
                    {
                        continue;
                    }

                    foreach (var dependencyId in descriptor.Dependencies)
                    {
                        if (closure.Contains(dependencyId))
                        {
                            closure.Add(descriptor.Id);
                            changed = true;
                            break;
                        }
                    }
                }
            }

            return closure;
        }

        private List<ModuleRecord> SelectRecords(
            IReadOnlyList<ModuleDescriptor> sortedDescriptors,
            ISet<string> selectedIds)
        {
            var recordsById = new Dictionary<string, ModuleRecord>(
                _modules.Count,
                StringComparer.Ordinal);
            for (var index = 0; index < _modules.Count; index++)
            {
                recordsById.Add(_modules[index].Descriptor.Id, _modules[index]);
            }

            var selected = new List<ModuleRecord>(selectedIds.Count);
            for (var index = 0; index < sortedDescriptors.Count; index++)
            {
                var id = sortedDescriptors[index].Id;
                if (selectedIds.Contains(id))
                {
                    selected.Add(recordsById[id]);
                }
            }

            return selected;
        }

        private async Task InstallRecordsAsync(
            IReadOnlyList<ModuleDescriptor> sortedDescriptors,
            ISet<string> selectedIds,
            ICollection<ModuleRecord> records,
            CancellationToken token)
        {
            for (var index = 0; index < sortedDescriptors.Count; index++)
            {
                var descriptor = sortedDescriptors[index];
                if (!selectedIds.Contains(descriptor.Id))
                {
                    continue;
                }

                token.ThrowIfCancellationRequested();
                var record = new ModuleRecord(descriptor);
                records.Add(record);
                try
                {
                    var module = descriptor.Factory();
                    record.Module = module;
                    ValidateModuleContract(descriptor, module);

                    var scope = Services.CreateScope(descriptor.Id);
                    record.Scope = scope;
                    var context = new ModuleContext(
                        Services,
                        scope,
                        _logger,
                        descriptor.Id);
                    await InitializeAsync(record, context, token);
                    await StartModuleAsync(record, token);
                }
                catch (Exception exception)
                {
                    Transition(record, ModuleState.Faulted, exception);
                    throw;
                }
            }
        }

        private async Task<Exception> CleanupRecordsAsync(
            IReadOnlyList<ModuleRecord> records,
            CancellationToken token,
            string operationDescription)
        {
            Exception failures = null;
            for (var index = records.Count - 1; index >= 0; index--)
            {
                var record = records[index];
                var recordFailed = record.State == ModuleState.Faulted;

                if (record.State == ModuleState.Running ||
                    record.State == ModuleState.Stopping)
                {
                    if (record.State == ModuleState.Running)
                    {
                        Transition(record, ModuleState.Stopping);
                    }

                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await record.Module.StopAsync(token);
                        Transition(record, ModuleState.Stopped);
                    }
                    catch (Exception exception)
                    {
                        recordFailed = true;
                        Transition(record, ModuleState.Faulted, exception);
                        failures = RecordCleanupOperationFailure(
                            record,
                            $"Module stop failed during {operationDescription}.",
                            exception,
                            failures);
                    }
                    finally
                    {
                        stopwatch.Stop();
                        record.StopDuration = stopwatch.Elapsed;
                    }
                }

                if (record.Module != null)
                {
                    if (!recordFailed)
                    {
                        Transition(record, ModuleState.Disposing);
                    }

                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await record.Module.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        if (!recordFailed)
                        {
                            Transition(record, ModuleState.Faulted, exception);
                        }

                        recordFailed = true;
                        failures = RecordCleanupOperationFailure(
                            record,
                            $"Module disposal failed during {operationDescription}.",
                            exception,
                            failures);
                    }
                    finally
                    {
                        stopwatch.Stop();
                        record.DisposeDuration = stopwatch.Elapsed;
                        record.Module = null;
                    }
                }

                if (record.Scope != null)
                {
                    try
                    {
                        await record.Scope.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        if (!recordFailed)
                        {
                            Transition(record, ModuleState.Faulted, exception);
                        }

                        recordFailed = true;
                        failures = RecordCleanupOperationFailure(
                            record,
                            $"Module scope disposal failed during {operationDescription}.",
                            exception,
                            failures);
                    }
                    finally
                    {
                        record.Scope = null;
                    }
                }

                if (!recordFailed)
                {
                    Transition(record, ModuleState.Unloaded);
                }
            }

            return failures;
        }

        private Exception RecordCleanupOperationFailure(
            ModuleRecord record,
            string message,
            Exception exception,
            Exception failures)
        {
            SafeLogError(
                record.Descriptor.Id,
                LifecycleCategory,
                message,
                exception);
            return CombineExceptions(failures, exception);
        }

        private static Exception CombineExceptions(
            Exception first,
            Exception second)
        {
            if (first == null)
            {
                return second;
            }

            if (second == null)
            {
                return first;
            }

            return new AggregateException(first, second);
        }

        private void CommitRecords(
            IReadOnlyList<ModuleDescriptor> sortedDescriptors,
            ISet<string> selectedIds,
            IReadOnlyList<ModuleRecord> installedRecords)
        {
            var existingById = new Dictionary<string, ModuleRecord>(
                _modules.Count,
                StringComparer.Ordinal);
            for (var index = 0; index < _modules.Count; index++)
            {
                existingById[_modules[index].Descriptor.Id] = _modules[index];
            }

            var installedById = new Dictionary<string, ModuleRecord>(
                installedRecords.Count,
                StringComparer.Ordinal);
            for (var index = 0; index < installedRecords.Count; index++)
            {
                installedById[installedRecords[index].Descriptor.Id] =
                    installedRecords[index];
            }

            _modules.Clear();
            for (var index = 0; index < sortedDescriptors.Count; index++)
            {
                var descriptor = sortedDescriptors[index];
                _modules.Add(
                    selectedIds.Contains(descriptor.Id)
                        ? installedById[descriptor.Id]
                        : existingById[descriptor.Id]);
            }
        }

        private async ValueTask<Exception> CleanupAsync(
            CancellationToken token,
            bool preserveExistingFailures,
            bool propagateFailure)
        {
            Exception firstFailure = null;
            for (var index = _modules.Count - 1; index >= 0; index--)
            {
                var record = _modules[index];
                var preserveRecordFailure =
                    preserveExistingFailures && record.State == ModuleState.Faulted;

                if (record.State == ModuleState.Running)
                {
                    Transition(record, ModuleState.Stopping);
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await record.Module.StopAsync(token);
                        Transition(record, ModuleState.Stopped);
                    }
                    catch (Exception exception)
                    {
                        firstFailure = RecordCleanupFailure(
                            record,
                            "Module stop failed.",
                            exception,
                            preserveRecordFailure,
                            firstFailure);
                        preserveRecordFailure = true;
                    }
                    finally
                    {
                        stopwatch.Stop();
                        record.StopDuration = stopwatch.Elapsed;
                    }
                }

                if (record.Module != null)
                {
                    if (!preserveRecordFailure)
                    {
                        Transition(record, ModuleState.Disposing);
                    }

                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await record.Module.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        firstFailure = RecordCleanupFailure(
                            record,
                            "Module disposal failed.",
                            exception,
                            preserveRecordFailure,
                            firstFailure);
                        preserveRecordFailure = true;
                    }
                    finally
                    {
                        stopwatch.Stop();
                        record.DisposeDuration = stopwatch.Elapsed;
                    }
                }

                if (record.Scope != null)
                {
                    try
                    {
                        await record.Scope.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        firstFailure = RecordCleanupFailure(
                            record,
                            "Module scope disposal failed.",
                            exception,
                            preserveRecordFailure,
                            firstFailure);
                        preserveRecordFailure = true;
                    }
                }

                if (!preserveRecordFailure)
                {
                    Transition(record, ModuleState.Unloaded);
                }
            }

            return propagateFailure ? firstFailure : null;
        }

        private Exception RecordCleanupFailure(
            ModuleRecord record,
            string message,
            Exception exception,
            bool preserveRecordFailure,
            Exception firstFailure)
        {
            if (!preserveRecordFailure)
            {
                Transition(record, ModuleState.Faulted, exception);
            }

            SafeLogError(
                record.Descriptor.Id,
                LifecycleCategory,
                message,
                exception);
            return firstFailure ?? exception;
        }

        private async ValueTask InitializeAsync(
            ModuleRecord record,
            ModuleContext context,
            CancellationToken token)
        {
            Transition(record, ModuleState.Initializing);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await record.Module.InitializeAsync(context, token);
            }
            finally
            {
                stopwatch.Stop();
                record.InitializeDuration = stopwatch.Elapsed;
            }

            Transition(record, ModuleState.Initialized);
        }

        private async ValueTask StartModuleAsync(
            ModuleRecord record,
            CancellationToken token)
        {
            Transition(record, ModuleState.Starting);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await record.Module.StartAsync(token);
            }
            finally
            {
                stopwatch.Stop();
                record.StartDuration = stopwatch.Elapsed;
            }

            Transition(record, ModuleState.Running);
        }

        private void DispatchFrame<TFrameModule>(
            Action<TFrameModule> callback,
            string callbackName)
            where TFrameModule : class
        {
            for (var index = 0; index < _modules.Count; index++)
            {
                var record = _modules[index];
                if (record.State != ModuleState.Running ||
                    !(record.Module is TFrameModule frameModule))
                {
                    continue;
                }

                try
                {
                    callback(frameModule);
                }
                catch (Exception exception)
                {
                    Transition(record, ModuleState.Faulted, exception);
                    SafeLogError(
                        record.Descriptor.Id,
                        FrameCategory,
                        $"{callbackName} callback failed.",
                        exception);
                }
            }
        }

        private static void ValidateModuleContract(
            ModuleDescriptor descriptor,
            IFrameworkModule module)
        {
            if (module == null)
            {
                throw new InvalidOperationException(
                    $"Factory for module '{descriptor.Id}' returned null.");
            }

            if (!string.Equals(module.Id, descriptor.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Module factory for descriptor '{descriptor.Id}' returned module " +
                    $"'{module.Id ?? "<null>"}'.");
            }

            if (module.Dependencies == null)
            {
                throw new InvalidOperationException(
                    $"Module '{descriptor.Id}' returned null dependencies.");
            }

            var expectedDependencies = new HashSet<string>(
                descriptor.Dependencies,
                StringComparer.Ordinal);
            var actualDependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in module.Dependencies)
            {
                if (!actualDependencies.Add(dependency))
                {
                    throw new InvalidOperationException(
                        $"Module '{descriptor.Id}' contains duplicate dependency " +
                        $"'{dependency ?? "<null>"}'.");
                }
            }

            if (!expectedDependencies.SetEquals(actualDependencies))
            {
                throw new InvalidOperationException(
                    $"Module '{descriptor.Id}' dependencies do not match its descriptor.");
            }
        }

        private static void Transition(
            ModuleRecord record,
            ModuleState state,
            Exception exception = null)
        {
            record.State = state;
            record.LastStateChangedUtc = DateTime.UtcNow;
            if (exception != null)
            {
                record.LastException = exception;
            }
        }

        private void SafeLogError(
            string moduleId,
            string category,
            string message,
            Exception exception)
        {
            try
            {
                _logger.Error(moduleId, category, message, exception);
            }
            catch
            {
                // Logging must not replace lifecycle or frame callback failures.
            }
        }
    }
}
