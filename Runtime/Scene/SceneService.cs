using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class SceneService : ISceneService
    {
        private readonly object _sync = new object();
        private readonly ISceneBackend _backend;
        private readonly IEventBus _events;
        private readonly TableData<SceneTableRow> _catalog;
        private readonly Action _afterQueuedStopCancellation;
        private readonly Queue<QueuedRequest> _queue =
            new Queue<QueuedRequest>();
        private readonly List<ISceneBackendScene> _ownedScenes =
            new List<ISceneBackendScene>();
        private readonly CancellationTokenSource _lifetime =
            new CancellationTokenSource();
        private readonly AsyncLocal<CallbackFrame> _callbackFrame =
            new AsyncLocal<CallbackFrame>();
        private ISceneBackendScene _activeScene;
        private string _activeSceneId;
        private ResourceKey _activeSceneKey;
        private string _activeSceneName;
        private SceneTransitionStage? _currentStage;
        private Exception _lastException;
        private Task _consumerTask = Task.CompletedTask;
        private Task _stopTask;
        private long _nextRequestId;
        private bool _consumerRunning;
        private bool _isTransitioning;
        private bool _stopping;

        internal SceneService(
            ISceneBackend backend,
            IEventBus events,
            Action afterQueuedStopCancellation = null)
            : this(
                backend,
                events,
                null,
                afterQueuedStopCancellation)
        {
        }

        private SceneService(
            ISceneBackend backend,
            IEventBus events,
            TableData<SceneTableRow> catalog,
            Action afterQueuedStopCancellation)
        {
            _backend = backend ??
                throw new ArgumentNullException(nameof(backend));
            _events = events ??
                throw new ArgumentNullException(nameof(events));
            _catalog = catalog;
            _afterQueuedStopCancellation = afterQueuedStopCancellation;
            _activeScene = _backend.CaptureActiveScene();
            _activeSceneKey = _activeScene.Key;
            _activeSceneName = _activeScene.Name;
        }

        internal static SceneService CreateWithCatalog(
            ISceneBackend backend,
            IEventBus events,
            TableData<SceneTableRow> catalog)
        {
            ValidateCatalog(catalog);
            return new SceneService(
                backend,
                events,
                catalog,
                null);
        }

        private static void ValidateCatalog(TableData<SceneTableRow> catalog)
        {
            if (catalog == null)
            {
                return;
            }

            if (!catalog.HasKey ||
                !string.Equals(
                    catalog.Schema.KeyColumnName,
                    nameof(SceneTableRow.Id),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The scene table must declare '#key,Id'.");
            }

            for (var index = 0; index < catalog.Rows.Count; index++)
            {
                catalog.Rows[index].CreateRequest();
            }
        }

        public string ActiveSceneId
        {
            get
            {
                lock (_sync)
                {
                    return _activeSceneId ?? string.Empty;
                }
            }
        }

        public ResourceKey ActiveSceneKey
        {
            get
            {
                lock (_sync)
                {
                    return _activeSceneKey;
                }
            }
        }

        public string ActiveSceneName
        {
            get
            {
                lock (_sync)
                {
                    return _activeSceneName;
                }
            }
        }

        public bool IsTransitioning
        {
            get
            {
                lock (_sync)
                {
                    return _isTransitioning;
                }
            }
        }

        public int QueueLength
        {
            get
            {
                lock (_sync)
                {
                    return CountLiveQueuedRequestsNoLock();
                }
            }
        }

        public SceneDiagnostics Diagnostics
        {
            get
            {
                lock (_sync)
                {
                    var owned = new ResourceKey[_ownedScenes.Count];
                    for (var index = 0; index < owned.Length; index++)
                    {
                        owned[index] = _ownedScenes[index].Key;
                    }

                    return new SceneDiagnostics(
                        _activeSceneKey,
                        _activeSceneName,
                        _isTransitioning,
                        CountLiveQueuedRequestsNoLock(),
                        _currentStage,
                        Array.AsReadOnly(owned),
                        _lastException);
                }
            }
        }

        public ValueTask LoadAsync(
            SceneRequest request,
            CancellationToken token = default)
        {
            ValidateRequest(request);
            token.ThrowIfCancellationRequested();

            QueuedRequest queued;
            TaskCompletionSource<bool> consumerCompletion = null;
            lock (_sync)
            {
                if (_stopping)
                {
                    throw new ObjectDisposedException(nameof(SceneService));
                }

                queued = new QueuedRequest(
                    ++_nextRequestId,
                    request,
                    token);
                queued.RegisterCancellation(_lifetime.Token);
                _queue.Enqueue(queued);
                if (!_consumerRunning)
                {
                    _consumerRunning = true;
                    consumerCompletion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _consumerTask = consumerCompletion.Task;
                }
            }

            if (consumerCompletion != null)
            {
                _ = RunConsumerAsync(consumerCompletion);
            }

            return new ValueTask(queued.Completion.Task);
        }

        public ValueTask LoadByIdAsync(
            string id,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A scene table ID is required.",
                    nameof(id));
            }

            if (_catalog == null)
            {
                throw new InvalidOperationException(
                    "SceneModuleInstaller does not define a scene table path.");
            }

            return LoadAsync(_catalog.Get(id.Trim()).CreateRequest(), token);
        }

        public bool TryGetDefinition(
            string id,
            out SceneTableRow definition)
        {
            if (_catalog == null || string.IsNullOrWhiteSpace(id))
            {
                definition = null;
                return false;
            }

            return _catalog.TryGet(id.Trim(), out definition);
        }

        public ValueTask StopAsync(CancellationToken token = default)
        {
            var callbackFrame = _callbackFrame.Value;
            if (callbackFrame != null && callbackFrame.IsActive)
            {
                throw new InvalidOperationException(
                    "SceneService.StopAsync cannot be called from a " +
                    "scene event callback.");
            }

            var stopTask = EnsureStopStarted();
            return token.CanBeCanceled
                ? new ValueTask(ApplyCancellationAsync(stopTask, token))
                : new ValueTask(stopTask);
        }

        public ValueTask DisposeAsync()
        {
            var callbackFrame = _callbackFrame.Value;
            if (callbackFrame != null && callbackFrame.IsActive)
            {
                throw new InvalidOperationException(
                    "SceneService.DisposeAsync cannot be called from a " +
                    "scene event callback.");
            }

            return new ValueTask(EnsureStopStarted());
        }

        private Task EnsureStopStarted()
        {
            Task stopTask;
            TaskCompletionSource<bool> completion = null;
            QueuedRequest[] queued = null;
            lock (_sync)
            {
                if (_stopTask == null)
                {
                    _stopping = true;
                    completion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _stopTask = completion.Task;
                    queued = _queue.ToArray();
                }

                stopTask = _stopTask;
            }

            if (completion != null)
            {
                _lifetime.Cancel();
                for (var index = 0; index < queued.Length; index++)
                {
                    queued[index].CancelForStop(_lifetime.Token);
                }

                _afterQueuedStopCancellation?.Invoke();
                _ = StopCoreAsync(completion);
            }

            return stopTask;
        }

        private async Task RunConsumerAsync(
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await ConsumeAsync();
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task ConsumeAsync()
        {
            while (true)
            {
                QueuedRequest queued;
                lock (_sync)
                {
                    if (_queue.Count == 0)
                    {
                        _consumerRunning = false;
                        return;
                    }

                    queued = _queue.Dequeue();
                    if (!queued.TryMarkStarted())
                    {
                        queued.DisposeCancellation();
                        continue;
                    }

                    _isTransitioning = true;
                }

                try
                {
                    await ExecuteAsync(queued);
                }
                finally
                {
                    queued.DisposeCancellation();
                    lock (_sync)
                    {
                        _isTransitioning = false;
                        _currentStage = null;
                    }
                }
            }
        }

        private async Task ExecuteAsync(QueuedRequest queued)
        {
            ISceneBackendScene target = null;
            ISceneBackendScene previous;
            var loadingShown = false;
            var irreversible = false;
            var setActiveSucceeded = false;
            var stage = SceneTransitionStage.Started;

            lock (_sync)
            {
                previous = _activeScene;
                _lastException = null;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       queued.CallerToken,
                       _lifetime.Token))
            {
                var preActivationToken = linked.Token;
                try
                {
                    Publish(queued, SceneTransitionStage.Started, 0f);
                    Publish(queued, SceneTransitionStage.ShowLoading, 0f);
                    loadingShown = true;
                    stage = SceneTransitionStage.Loading;
                    Publish(queued, stage, 0f);
                    target = await _backend.LoadAsync(
                        queued.Request.Key,
                        progress => PublishProgress(queued, progress),
                        preActivationToken);
                    preActivationToken.ThrowIfCancellationRequested();

                    if (queued.Request.ActivateOnLoad)
                    {
                        stage = SceneTransitionStage.Activating;
                        if (!queued.TryCrossIrreversibleBoundary())
                        {
                            throw new OperationCanceledException(
                                preActivationToken);
                        }

                        irreversible = true;
                        Publish(queued, stage, 1f);
                        await _backend.ActivateAsync(
                            target,
                            CancellationToken.None);
                        Publish(
                            queued,
                            SceneTransitionStage.Activated,
                            1f);
                        stage = SceneTransitionStage.SettingActive;
                        Publish(queued, stage, 1f);
                        _backend.SetActiveScene(target);
                        setActiveSucceeded = true;
                        lock (_sync)
                        {
                            _activeScene = target;
                            _activeSceneId = queued.Request.Id;
                            _activeSceneKey = target.Key;
                            _activeSceneName = target.Name;
                        }
                    }

                    AddOwned(target);
                    if (queued.Request.Mode == SceneLoadMode.Single)
                    {
                        stage = SceneTransitionStage.UnloadingPrevious;
                        Publish(queued, stage, 1f);
                        await _backend.UnloadAsync(
                            previous,
                            CancellationToken.None);
                        RemoveOwned(previous);
                    }

                    Publish(
                        queued,
                        SceneTransitionStage.HideLoading,
                        1f);
                    loadingShown = false;
                    if (!irreversible &&
                        !queued.TryCrossIrreversibleBoundary())
                    {
                        throw new OperationCanceledException(
                            preActivationToken);
                    }

                    Publish(
                        queued,
                        SceneTransitionStage.Completed,
                        1f);
                    queued.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException exception)
                    when (!irreversible)
                {
                    var failure = await CleanupTargetAsync(
                        target,
                        exception);
                    if (loadingShown)
                    {
                        Publish(
                            queued,
                            SceneTransitionStage.HideLoading,
                            1f);
                    }

                    Publish(
                        queued,
                        SceneTransitionStage.Canceled,
                        1f);
                    lock (_sync)
                    {
                        _lastException = failure == exception
                            ? null
                            : failure;
                    }

                    queued.Completion.TrySetCanceled(
                        SelectCancellationToken(
                            queued.CallerToken,
                            _lifetime.Token,
                            exception.CancellationToken));
                }
                catch (Exception exception)
                {
                    var failure = exception;
                    if (!setActiveSucceeded)
                    {
                        failure = await CleanupTargetAsync(
                            target,
                            exception);
                    }

                    if (loadingShown)
                    {
                        Publish(
                            queued,
                            SceneTransitionStage.HideLoading,
                            1f);
                    }

                    lock (_sync)
                    {
                        _lastException = failure;
                    }

                    Publish(
                        queued,
                        SceneTransitionStage.Failed,
                        1f,
                        stage,
                        failure);
                    queued.Completion.TrySetException(failure);
                }
            }
        }

        private async Task<Exception> CleanupTargetAsync(
            ISceneBackendScene target,
            Exception primary)
        {
            if (target == null)
            {
                return primary;
            }

            try
            {
                await _backend.UnloadAsync(
                    target,
                    CancellationToken.None);
                RemoveOwned(target);
                return primary;
            }
            catch (Exception cleanup)
            {
                AddOwned(target);
                return new AggregateException(primary, cleanup);
            }
        }

        private void PublishProgress(
            QueuedRequest queued,
            float progress)
        {
            if (float.IsNaN(progress) || float.IsInfinity(progress))
            {
                progress = 0f;
            }

            progress = Math.Max(0f, Math.Min(1f, progress));
            if (progress < queued.LastProgress)
            {
                progress = queued.LastProgress;
            }

            queued.LastProgress = progress;
            Publish(
                queued,
                SceneTransitionStage.Progress,
                progress);
        }

        private void Publish(
            QueuedRequest queued,
            SceneTransitionStage stage,
            float progress,
            SceneTransitionStage? failureStage = null,
            Exception exception = null)
        {
            lock (_sync)
            {
                _currentStage = stage;
            }

            var previousFrame = _callbackFrame.Value;
            var frame = new CallbackFrame();
            _callbackFrame.Value = frame;
            try
            {
                _events.Publish(
                    new SceneTransitionEvent(
                        queued.Id,
                        queued.Request,
                        stage,
                        progress,
                        failureStage,
                        exception));
            }
            finally
            {
                frame.Deactivate();
                _callbackFrame.Value = previousFrame;
            }
        }

        private void AddOwned(ISceneBackendScene scene)
        {
            if (scene == null || !scene.IsOwned)
            {
                return;
            }

            lock (_sync)
            {
                if (!_ownedScenes.Contains(scene))
                {
                    _ownedScenes.Add(scene);
                }
            }
        }

        private void RemoveOwned(ISceneBackendScene scene)
        {
            if (scene == null)
            {
                return;
            }

            lock (_sync)
            {
                _ownedScenes.Remove(scene);
            }
        }

        private async Task StopCoreAsync(
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await _consumerTask;
                ISceneBackendScene[] owned;
                lock (_sync)
                {
                    owned = _ownedScenes.ToArray();
                }

                Exception firstFailure = null;
                for (var index = owned.Length - 1; index >= 0; index--)
                {
                    try
                    {
                        await _backend.UnloadAsync(
                            owned[index],
                            CancellationToken.None);
                        RemoveOwned(owned[index]);
                    }
                    catch (Exception exception)
                    {
                        if (firstFailure == null)
                        {
                            firstFailure = exception;
                        }
                    }
                }

                if (firstFailure != null)
                {
                    throw firstFailure;
                }

                _lifetime.Dispose();
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                _lifetime.Dispose();
                completion.TrySetException(exception);
            }
        }

        private int CountLiveQueuedRequestsNoLock()
        {
            var count = 0;
            foreach (var queued in _queue)
            {
                if (!queued.Completion.Task.IsCompleted)
                {
                    count++;
                }
            }

            return count;
        }

        private static void ValidateRequest(SceneRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Key.Value))
            {
                throw new ArgumentException(
                    "A scene request must contain a resource key.",
                    nameof(request));
            }

            if (!Enum.IsDefined(typeof(SceneLoadMode), request.Mode))
            {
                throw new ArgumentOutOfRangeException(nameof(request));
            }

            if (request.Mode == SceneLoadMode.Single &&
                !request.ActivateOnLoad)
            {
                throw new ArgumentException(
                    "Single scene transactions must activate the target.",
                    nameof(request));
            }
        }

        private static async Task ApplyCancellationAsync(
            Task task,
            CancellationToken token)
        {
            if (!token.CanBeCanceled || task.IsCompleted)
            {
                await task;
                token.ThrowIfCancellationRequested();
                return;
            }

            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(
                       () => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, canceled.Task) != task)
                {
                    throw new OperationCanceledException(token);
                }
            }

            await task;
        }

        private static CancellationToken SelectCancellationToken(
            CancellationToken callerToken,
            CancellationToken lifetimeToken,
            CancellationToken exceptionToken)
        {
            if (callerToken.IsCancellationRequested)
            {
                return callerToken;
            }

            if (lifetimeToken.IsCancellationRequested)
            {
                return lifetimeToken;
            }

            return exceptionToken.IsCancellationRequested
                ? exceptionToken
                : new CancellationToken(true);
        }

        private sealed class QueuedRequest
        {
            private readonly SceneRequestCancellationArbiter _arbiter =
                new SceneRequestCancellationArbiter();
            private CancellationTokenRegistration _callerRegistration;
            private CancellationTokenRegistration _lifetimeRegistration;

            public QueuedRequest(
                long id,
                SceneRequest request,
                CancellationToken callerToken)
            {
                Id = id;
                Request = request;
                CallerToken = callerToken;
                Completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public long Id { get; }

            public SceneRequest Request { get; }

            public CancellationToken CallerToken { get; }

            public TaskCompletionSource<bool> Completion { get; }

            public float LastProgress { get; set; }

            public void RegisterCancellation(
                CancellationToken lifetimeToken)
            {
                if (CallerToken.CanBeCanceled)
                {
                    _callerRegistration = CallerToken.Register(
                        CompleteCallerCancellationIfSafe);
                }

                _lifetimeRegistration =
                    _arbiter.RegisterLifetimeCancellation(lifetimeToken);
            }

            public bool TryMarkStarted()
            {
                return _arbiter.TryStart(
                    Completion.Task.IsCompleted);
            }

            public bool TryCrossIrreversibleBoundary()
            {
                return _arbiter.TryCrossIrreversibleBoundary();
            }

            public void CancelForStop(CancellationToken token)
            {
                if (_arbiter.TryCancelQueuedForStop())
                {
                    Completion.TrySetCanceled(token);
                }
            }

            public void DisposeCancellation()
            {
                _callerRegistration.Dispose();
                _lifetimeRegistration.Dispose();
            }

            private void CompleteCallerCancellationIfSafe()
            {
                if (_arbiter.RecordCallerCancellation())
                {
                    Completion.TrySetCanceled(CallerToken);
                }
            }
        }

        private sealed class CallbackFrame
        {
            private int _active = 1;

            public bool IsActive => Volatile.Read(ref _active) != 0;

            public void Deactivate()
            {
                Volatile.Write(ref _active, 0);
            }
        }
    }
}
