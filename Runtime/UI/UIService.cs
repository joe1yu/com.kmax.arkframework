using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace ArkFramework
{
    public sealed class UIService : IUIService
    {
        private static AsyncLocal<CallbackFrame> CurrentCallback =
            new AsyncLocal<CallbackFrame>();

        static UIService()
        {
            FrameworkStaticReset.Register(ResetStatics);
        }

        private readonly IResourceService _resources;
        private readonly IGameObjectPool _pool;
        private readonly IEventBus _events;
        private readonly UIRoot _root;
        private readonly int _mainThreadId;
        private readonly IReadOnlyList<UILayerDiagnostics> _layerDiagnostics;
        private readonly CancellationTokenSource _lifetime =
            new CancellationTokenSource();
        private readonly Dictionary<Type, Registration> _registrations =
            new Dictionary<Type, Registration>();
        private readonly HashSet<string> _descriptorIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Entry> _entries =
            new Dictionary<Guid, Entry>();
        private readonly Dictionary<Type, Entry> _singleEntries =
            new Dictionary<Type, Entry>();
        private readonly List<Entry> _normalNavigation = new List<Entry>();
        private readonly List<Entry> _popupNavigation = new List<Entry>();
        private long _nextSequence;
        private Exception _recentException;
        private bool _registrationFrozen;
        private bool _stopped;
        private int _disposed;
        private Task _stopTask;

        public UIService(
            IResourceService resources,
            IGameObjectPool pool,
            IEventBus events,
            UIRoot root)
        {
            _resources =
                resources ?? throw new ArgumentNullException(nameof(resources));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            if (_mainThreadId != _root.OwnerThreadId)
            {
                throw new InvalidOperationException(
                    "UIService must be created on the UIRoot Unity main thread.");
            }
            var layers = new UILayerDiagnostics[_root.Layers.Count];
            for (var index = 0; index < layers.Length; index++)
            {
                var layer = _root.Layers[index];
                layers[index] = new UILayerDiagnostics(
                    layer.Layer,
                    layer.Root.name,
                    layer.SortingOrder);
            }

            _layerDiagnostics = UIDiagnostics.ReadOnly(layers);
            _root.Mask.onClick.AddListener(HandleMaskClick);
        }

        private static void ResetStatics()
        {
            Interlocked.Exchange(
                ref CurrentCallback,
                new AsyncLocal<CallbackFrame>());
        }

        public UIDiagnostics Diagnostics
        {
            get
            {
                EnsureMainThread();
                PruneCachedRecords();
                var entryValues =
                    new UIWindowDiagnostics[_entries.Count];
                var entryIndex = 0;
                foreach (var entry in _entries.Values)
                {
                    entryValues[entryIndex++] = new UIWindowDiagnostics(
                        entry.Descriptor.Id,
                        entry.InstanceId,
                        entry.Descriptor.Layer,
                        entry.State);
                }

                Array.Sort(
                    entryValues,
                    (left, right) =>
                        left.InstanceId.CompareTo(right.InstanceId));
                return new UIDiagnostics(
                    _layerDiagnostics,
                    UIDiagnostics.ReadOnly(entryValues),
                    SnapshotNavigation(_normalNavigation),
                    SnapshotNavigation(_popupNavigation),
                    FindMaskedPopup()?.InstanceId,
                    _recentException);
            }
        }

        public void Register<TWindow>(UIWindowDescriptor descriptor)
            where TWindow : UIWindow
        {
            EnsureMainThread();
            EnsureExternalOperationAllowed();
            if (_registrationFrozen)
            {
                throw new InvalidOperationException(
                    "UI window registration is frozen after the first open or stop.");
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var windowType = typeof(TWindow);
            if (windowType.IsAbstract ||
                !typeof(UIWindow).IsAssignableFrom(windowType))
            {
                throw new ArgumentException(
                    "A concrete UIWindow type is required.",
                    nameof(TWindow));
            }

            if (_registrations.ContainsKey(windowType))
            {
                throw new InvalidOperationException(
                    "Window type '" + windowType.FullName +
                    "' is already registered.");
            }

            if (!_descriptorIds.Add(descriptor.Id))
            {
                throw new InvalidOperationException(
                    "Window descriptor ID '" + descriptor.Id +
                    "' is already registered.");
            }

            _registrations.Add(
                windowType,
                new Registration(windowType, descriptor));
        }

        public ValueTask<IWindowHandle> OpenAsync<TWindow>(
            object parameter = null,
            CancellationToken token = default)
            where TWindow : UIWindow
        {
            // 先调用默认实现，保留参数、取消和主线程校验的同步异常语义。
            var operation = OpenDefaultAsync<TWindow>(parameter, token);
            return new ValueTask<IWindowHandle>(
                AsWindowHandleAsync(operation));
        }

        private static async Task<IWindowHandle> AsWindowHandleAsync(
            ValueTask<WindowHandle> operation)
        {
            return await operation;
        }

        private ValueTask<WindowHandle> OpenDefaultAsync<TWindow>(
            object parameter = null,
            CancellationToken token = default)
            where TWindow : UIWindow
        {
            EnsureMainThread();
            RejectCallbackReentry("Open");
            token.ThrowIfCancellationRequested();
            EnsureRunning();
            _registrationFrozen = true;
            PruneCachedRecords();

            var windowType = typeof(TWindow);
            if (!_registrations.TryGetValue(
                    windowType,
                    out var registration))
            {
                throw new InvalidOperationException(
                    "Window type '" + windowType.FullName +
                    "' is not registered.");
            }

            Task<WindowHandle> openTask;
            if (registration.Descriptor.Mode ==
                UIWindowMode.SingleInstance)
            {
                if (_singleEntries.TryGetValue(
                        windowType,
                        out var existing))
                {
                    if (existing.State == UIWindowState.Opening ||
                        existing.State == UIWindowState.Open)
                    {
                        openTask = existing.OpenTask;
                        return new ValueTask<WindowHandle>(
                            AwaitCallerAsync(openTask, token));
                    }

                    throw new InvalidOperationException(
                        "Window '" + registration.Descriptor.Id +
                        "' is currently closing.");
                }
            }

            var entry = new Entry(
                registration,
                Guid.NewGuid(),
                ++_nextSequence,
                parameter);
            var completion = new TaskCompletionSource<WindowHandle>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            entry.OpenTask = completion.Task;
            _entries.Add(entry.InstanceId, entry);
            if (registration.Descriptor.Mode ==
                UIWindowMode.SingleInstance)
            {
                _singleEntries.Add(windowType, entry);
            }

            _ = OpenAndCompleteAsync(entry, completion);
            return new ValueTask<WindowHandle>(
                AwaitCallerAsync(entry.OpenTask, token));
        }

        public ValueTask CloseAsync(
            IWindowHandle handle,
            CancellationToken token = default)
        {
            EnsureMainThread();
            RejectCallbackReentry("Close");
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            if (!(handle is WindowHandle defaultHandle))
            {
                throw new ArgumentException(
                    "The window handle was not created by the default UIService.",
                    nameof(handle));
            }

            if (!defaultHandle.IsOwnedBy(this))
            {
                throw new InvalidOperationException(
                    "The window handle belongs to a different UI service.");
            }

            EnsureRunning();
            if (!_entries.TryGetValue(
                    defaultHandle.InstanceId,
                    out var entry) ||
                !ReferenceEquals(entry.Handle, defaultHandle))
            {
                return new ValueTask(
                    AwaitCallerAsync(
                        Task.CompletedTask,
                        token));
            }

            var closeTask = BeginClose(entry);
            return new ValueTask(AwaitCallerAsync(closeTask, token));
        }

        public ValueTask<bool> BackAsync(CancellationToken token = default)
        {
            EnsureMainThread();
            RejectCallbackReentry("Back");
            token.ThrowIfCancellationRequested();
            EnsureRunning();

            var entry = FindBackEntry(_popupNavigation) ??
                FindBackEntry(_normalNavigation);
            if (entry == null)
            {
                return new ValueTask<bool>(false);
            }

            var closeTask = BeginClose(entry);
            return new ValueTask<bool>(
                AwaitBackAsync(closeTask, token));
        }

        public bool TryGetWindow(
            IWindowHandle handle,
            out UIWindow window)
        {
            EnsureMainThread();
            window = null;
            if (handle == null)
            {
                return false;
            }

            if (!(handle is WindowHandle defaultHandle))
            {
                throw new ArgumentException(
                    "The window handle was not created by the default UIService.",
                    nameof(handle));
            }

            if (!defaultHandle.IsOwnedBy(this) ||
                !_entries.TryGetValue(defaultHandle.InstanceId, out var entry) ||
                !ReferenceEquals(entry.Handle, defaultHandle) ||
                (entry.State != UIWindowState.Opening &&
                 entry.State != UIWindowState.Open))
            {
                return false;
            }

            window = entry.Window;
            return window != null;
        }

        public UIWindow GetWindow(IWindowHandle handle)
        {
            EnsureMainThread();
            if (!TryGetWindow(handle, out var window))
            {
                throw new InvalidOperationException(
                    "The window handle is stale or no longer open.");
            }

            return window;
        }

        public ValueTask StopAsync(CancellationToken token = default)
        {
            EnsureMainThread();
            RejectCallbackReentry("Stop");
            var stopTask = EnsureStopStarted();
            return new ValueTask(AwaitCallerAsync(stopTask, token));
        }

        public ValueTask DisposeAsync()
        {
            EnsureMainThread();
            RejectCallbackReentry("Dispose");
            return new ValueTask(DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                await EnsureStopStarted();
            }
            finally
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _lifetime.Dispose();
                }
            }
        }

        internal bool IsHandleValid(WindowHandle handle)
        {
            EnsureMainThread();
            return handle != null &&
                   handle.IsOwnedBy(this) &&
                   _entries.TryGetValue(
                       handle.InstanceId,
                       out var entry) &&
                   ReferenceEquals(entry.Handle, handle) &&
                   (entry.State == UIWindowState.Opening ||
                    entry.State == UIWindowState.Open);
        }

        private async Task OpenAndCompleteAsync(
            Entry entry,
            TaskCompletionSource<WindowHandle> completion)
        {
            try
            {
                await OpenCoreAsync(entry);
                completion.TrySetResult(entry.Handle);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(
                    SelectCanceledToken(entry, exception));
            }
            catch (Exception exception)
            {
                RecordException(exception);
                completion.TrySetException(exception);
            }
        }

        private async Task OpenCoreAsync(Entry entry)
        {
            Exception primary = null;
            try
            {
                entry.Lifetime = CancellationTokenSource
                    .CreateLinkedTokenSource(_lifetime.Token);
                GameObject instance;
                var parent = _root.GetWindowRoot(entry.Descriptor);
                var stagingParent = _root.StagingRoot;
                if (entry.Descriptor.CacheOnClose)
                {
                    entry.PoolHandle = await _pool.RentAsync(
                        entry.Descriptor.ResourceKey,
                        stagingParent,
                        CancellationToken.None);
                    instance = entry.PoolHandle.Instance;
                }
                else
                {
                    entry.InstanceLease =
                        await _resources.InstantiateAsync(
                            entry.Descriptor.ResourceKey,
                            stagingParent,
                            CancellationToken.None);
                    instance = entry.InstanceLease.Instance;
                }

                entry.Lifetime.Token.ThrowIfCancellationRequested();
                if (instance == null)
                {
                    throw ComponentFailure(
                        entry,
                        "the acquired prefab instance was null");
                }

                instance.SetActive(false);
                instance.transform.SetParent(parent, false);
                var components = instance.GetComponentsInChildren(
                    entry.WindowType,
                    true);
                if (components.Length != 1 ||
                    !(components[0] is UIWindow window))
                {
                    throw ComponentFailure(
                        entry,
                        components.Length == 0
                            ? "the expected component is missing"
                            : "the expected component is duplicated");
                }

                entry.Window = window;
                RemoveCachedRecord(window, entry.WindowType);
                entry.Handle = new WindowHandle(
                    this,
                    entry.Descriptor.Id,
                    entry.InstanceId,
                    entry.WindowType);
                entry.Window.BeginLifetime(
                    _events,
                    entry.Lifetime.Token);
                instance.transform.SetAsLastSibling();
                instance.SetActive(true);
                entry.OpenCallbackStarted = true;
                await InvokeOpenCallbackAsync(entry);
                entry.Lifetime.Token.ThrowIfCancellationRequested();
                EnsureRunning();

                entry.State = UIWindowState.Open;
                AddNavigation(entry);
                UpdateMask();
            }
            catch (Exception exception)
            {
                primary = exception;
            }

            if (primary == null)
            {
                return;
            }

            var failure = await CleanupFailedOpenAsync(entry, primary);
            RemoveEntry(entry);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private async Task<Exception> CleanupFailedOpenAsync(
            Entry entry,
            Exception primary)
        {
            var cleanup = CancelLifetime(entry);

            if (!ReferenceEquals(entry.Window, null))
            {
                cleanup = Combine(
                    cleanup,
                    entry.Window.EndSubscriptions());
            }
            if (entry.OpenCallbackStarted && entry.Window != null)
            {
                try
                {
                    await InvokeCloseCallbackAsync(entry);
                }
                catch (Exception exception)
                {
                    cleanup = Combine(cleanup, exception);
                }
            }

            try
            {
                ReleaseOwnership(entry);
            }
            catch (Exception exception)
            {
                cleanup = Combine(cleanup, exception);
            }

            entry.Lifetime?.Dispose();
            entry.Lifetime = null;
            return cleanup == null
                ? primary
                : new AggregateException(primary, cleanup);
        }

        private Task BeginClose(Entry entry)
        {
            if (entry.State == UIWindowState.Closing)
            {
                return entry.CloseTask;
            }

            if (entry.State == UIWindowState.Cached)
            {
                return Task.CompletedTask;
            }

            if (entry.State == UIWindowState.Opening)
            {
                if (entry.CloseTask != null)
                {
                    return entry.CloseTask;
                }

                var openingCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                entry.CloseTask = openingCompletion.Task;
                _ = CompleteOpeningCloseAsync(
                    entry,
                    openingCompletion,
                    CancelLifetime(entry));
                return entry.CloseTask;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            entry.State = UIWindowState.Closing;
            entry.CloseTask = completion.Task;
            RemoveNavigation(entry);
            UpdateMask();
            var preCallbackFailure = CancelLifetime(entry);

            if (!ReferenceEquals(entry.Window, null))
            {
                preCallbackFailure = Combine(
                    preCallbackFailure,
                    entry.Window.EndSubscriptions());
            }
            _ = CloseAndCompleteAsync(
                entry,
                completion,
                preCallbackFailure);
            return entry.CloseTask;
        }

        private async Task CompleteOpeningCloseAsync(
            Entry entry,
            TaskCompletionSource<bool> completion,
            Exception failure)
        {
            try
            {
                await entry.OpenTask;
            }
            catch (OperationCanceledException)
            {
                // Closing an opening window intentionally cancels Open.
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            if (failure != null)
            {
                RecordException(failure);
                completion.TrySetException(failure);
            }
            else
            {
                completion.TrySetResult(true);
            }
        }

        private async Task CloseAndCompleteAsync(
            Entry entry,
            TaskCompletionSource<bool> completion,
            Exception failure)
        {
            try
            {
                if (entry.Window != null)
                {
                    try
                    {
                        await InvokeCloseCallbackAsync(entry);
                    }
                    catch (Exception exception)
                    {
                        failure = Combine(failure, exception);
                    }
                }

                try
                {
                    ReleaseOwnership(entry);
                }
                catch (Exception exception)
                {
                    failure = Combine(failure, exception);
                }

                entry.Lifetime?.Dispose();
                entry.Lifetime = null;
                if (entry.Descriptor.CacheOnClose)
                {
                    entry.State = UIWindowState.Cached;
                    RemoveSingleEntry(entry);
                }
                else
                {
                    RemoveEntry(entry);
                }

                if (failure != null)
                {
                    RecordException(failure);
                    completion.TrySetException(failure);
                }
                else
                {
                    completion.TrySetResult(true);
                }
            }
            catch (Exception exception)
            {
                RecordException(exception);
                completion.TrySetException(exception);
            }
        }

        private void ReleaseOwnership(Entry entry)
        {
            var poolHandle = entry.PoolHandle;
            entry.PoolHandle = null;
            var instanceLease = entry.InstanceLease;
            entry.InstanceLease = null;
            try
            {
                poolHandle?.Dispose();
            }
            finally
            {
                instanceLease?.Dispose();
            }
        }

        private async Task InvokeOpenCallbackAsync(Entry entry)
        {
            var frame = EnterCallback(entry);
            try
            {
                await entry.Window.OnOpenAsync(
                    entry.Parameter,
                    entry.Lifetime.Token);
            }
            finally
            {
                ExitCallback(frame);
            }
        }

        private async Task InvokeCloseCallbackAsync(Entry entry)
        {
            var frame = EnterCallback(entry);
            try
            {
                await entry.Window.OnCloseAsync(CancellationToken.None);
            }
            finally
            {
                ExitCallback(frame);
            }
        }

        private CallbackFrame EnterCallback(Entry entry)
        {
            var holder = Volatile.Read(ref CurrentCallback);
            var frame = new CallbackFrame(
                this,
                entry,
                holder,
                holder.Value);
            holder.Value = frame;
            return frame;
        }

        private static void ExitCallback(CallbackFrame frame)
        {
            frame.Active = false;
            if (ReferenceEquals(frame.Holder.Value, frame))
            {
                frame.Holder.Value = frame.Previous;
            }
        }

        private void RejectCallbackReentry(string operation)
        {
            var frame = Volatile.Read(ref CurrentCallback).Value;
            if (frame != null &&
                frame.Active &&
                ReferenceEquals(frame.Owner, this))
            {
                throw new InvalidOperationException(
                    operation +
                    " cannot be called synchronously from a UIWindow callback.");
            }
        }

        private Task EnsureStopStarted()
        {
            if (_stopTask != null)
            {
                return _stopTask;
            }

            _registrationFrozen = true;
            _stopped = true;
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            Exception cancellationFailure = null;
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }

            var entries = new Entry[_entries.Count];
            _entries.Values.CopyTo(entries, 0);
            Array.Sort(
                entries,
                (left, right) =>
                {
                    var layer = right.Descriptor.Layer.CompareTo(
                        left.Descriptor.Layer);
                    return layer != 0
                        ? layer
                        : right.Sequence.CompareTo(left.Sequence);
                });
            _ = StopCoreAsync(
                entries,
                completion,
                cancellationFailure);
            return _stopTask;
        }

        private async Task StopCoreAsync(
            Entry[] entries,
            TaskCompletionSource<bool> completion,
            Exception failure)
        {
            try
            {
                failure = await ExecuteStopCleanupAsync(
                    entries,
                    failure);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            if (failure != null)
            {
                RecordException(failure);
                completion.TrySetException(failure);
            }
            else
            {
                completion.TrySetResult(true);
            }
        }

        private async Task<Exception> ExecuteStopCleanupAsync(
            Entry[] entries,
            Exception failure)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                Task cleanup = null;
                var expectedOpeningCancellation = false;
                try
                {
                    switch (entry.State)
                    {
                        case UIWindowState.Opening:
                            if (entry.CloseTask != null)
                            {
                                cleanup = entry.CloseTask;
                                break;
                            }

                            failure = Combine(
                                failure,
                                CancelLifetime(entry));
                            cleanup = entry.OpenTask;
                            expectedOpeningCancellation = true;
                            break;
                        case UIWindowState.Open:
                            cleanup = BeginClose(entry);
                            break;
                        case UIWindowState.Closing:
                            cleanup = entry.CloseTask;
                            break;
                        default:
                            RemoveEntry(entry);
                            continue;
                    }

                    await cleanup;
                }
                catch (OperationCanceledException)
                    when (expectedOpeningCancellation)
                {
                    // Stopping an opening window is expected cancellation.
                }
                catch (Exception exception)
                {
                    failure = Combine(failure, exception);
                    if (cleanup == null)
                    {
                        failure = Combine(
                            failure,
                            await EmergencyStopEntryAsync(entry));
                    }
                }
            }

            try
            {
                if (_root != null && _root.Mask != null)
                {
                    _root.Mask.onClick.RemoveListener(HandleMaskClick);
                }
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            try
            {
                await _root.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            return failure;
        }

        private async Task<Exception> EmergencyStopEntryAsync(Entry entry)
        {
            var failure = CancelLifetime(entry);
            if (!ReferenceEquals(entry.Window, null))
            {
                failure = Combine(
                    failure,
                    entry.Window.EndSubscriptions());
                if (entry.Window != null &&
                    entry.OpenCallbackStarted)
                {
                    try
                    {
                        await InvokeCloseCallbackAsync(entry);
                    }
                    catch (Exception exception)
                    {
                        failure = Combine(failure, exception);
                    }
                }
            }

            try
            {
                ReleaseOwnership(entry);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            try
            {
                entry.Lifetime?.Dispose();
                entry.Lifetime = null;
                RemoveEntry(entry);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            return failure;
        }

        private void HandleMaskClick()
        {
            var entry = FindMaskedPopup();
            if (entry == null || !entry.Descriptor.CloseOnMaskClick)
            {
                return;
            }

            _ = ObserveMaskCloseAsync(BeginClose(entry));
        }

        private async Task ObserveMaskCloseAsync(Task closeTask)
        {
            try
            {
                await closeTask;
            }
            catch (Exception exception)
            {
                RecordException(exception);
            }
        }

        private void AddNavigation(Entry entry)
        {
            if (entry.Descriptor.Layer == UILayer.Normal)
            {
                _normalNavigation.Add(entry);
            }
            else if (entry.Descriptor.Layer == UILayer.Popup)
            {
                _popupNavigation.Add(entry);
            }
        }

        private void RemoveNavigation(Entry entry)
        {
            _normalNavigation.Remove(entry);
            _popupNavigation.Remove(entry);
        }

        private static Entry FindBackEntry(List<Entry> navigation)
        {
            for (var index = navigation.Count - 1; index >= 0; index--)
            {
                var entry = navigation[index];
                if (entry.State == UIWindowState.Open &&
                    entry.Descriptor.AllowBack)
                {
                    return entry;
                }
            }

            return null;
        }

        private Entry FindMaskedPopup()
        {
            for (var index = _popupNavigation.Count - 1;
                 index >= 0;
                 index--)
            {
                var entry = _popupNavigation[index];
                if (entry.State == UIWindowState.Open &&
                    entry.Descriptor.RequiresMask)
                {
                    return entry;
                }
            }

            return null;
        }

        private void UpdateMask()
        {
            if (_root == null || _root.Mask == null)
            {
                return;
            }

            var mask = _root.Mask;
            var popup = FindMaskedPopup();
            if (popup == null || popup.Window == null)
            {
                mask.gameObject.SetActive(false);
                mask.GetComponent<Image>().raycastTarget = false;
                return;
            }

            mask.gameObject.SetActive(true);
            var popupRoot = _root.GetWindowRoot(popup.Descriptor);
            _root.PlaceMask(popupRoot);
            mask.GetComponent<Image>().raycastTarget =
                popup.Descriptor.BlocksInput ||
                popup.Descriptor.CloseOnMaskClick;
            var desiredOrder = new List<Transform>(
                _popupNavigation.Count + 1);
            for (var index = 0;
                 index < _popupNavigation.Count;
                 index++)
            {
                var entry = _popupNavigation[index];
                if (entry.State != UIWindowState.Open ||
                    entry.Window == null ||
                    entry.Window.transform.parent != popupRoot)
                {
                    continue;
                }

                if (ReferenceEquals(entry, popup))
                {
                    desiredOrder.Add(mask.transform);
                }

                desiredOrder.Add(entry.Window.transform);
            }

            for (var index = 0; index < desiredOrder.Count; index++)
            {
                desiredOrder[index].SetSiblingIndex(index);
            }
        }

        private void RemoveEntry(Entry entry)
        {
            RemoveNavigation(entry);
            RemoveSingleEntry(entry);
            _entries.Remove(entry.InstanceId);
            UpdateMask();
        }

        private void PruneCachedRecords()
        {
            List<Guid> stale = null;
            foreach (var candidate in _entries.Values)
            {
                if (candidate.State != UIWindowState.Cached)
                {
                    continue;
                }

                if (candidate.Window != null &&
                    candidate.Window.gameObject != null &&
                    !candidate.Window.gameObject.activeSelf)
                {
                    continue;
                }

                if (stale == null)
                {
                    stale = new List<Guid>();
                }

                stale.Add(candidate.InstanceId);
            }

            if (stale == null)
            {
                return;
            }

            for (var index = 0; index < stale.Count; index++)
            {
                _entries.Remove(stale[index]);
            }
        }

        private void RemoveCachedRecord(
            UIWindow window,
            Type windowType)
        {
            Entry cached = null;
            foreach (var candidate in _entries.Values)
            {
                if (candidate.State == UIWindowState.Cached &&
                    candidate.Window != null &&
                    ReferenceEquals(
                        candidate.Window.gameObject,
                        window.gameObject))
                {
                    cached = candidate;
                    break;
                }
            }

            if (cached == null)
            {
                foreach (var candidate in _entries.Values)
                {
                    if (candidate.State == UIWindowState.Cached &&
                        candidate.WindowType == windowType)
                    {
                        cached = candidate;
                        break;
                    }
                }
            }

            if (cached != null)
            {
                _entries.Remove(cached.InstanceId);
            }
        }

        private void RemoveSingleEntry(Entry entry)
        {
            if (_singleEntries.TryGetValue(
                    entry.WindowType,
                    out var existing) &&
                ReferenceEquals(existing, entry))
            {
                _singleEntries.Remove(entry.WindowType);
            }
        }

        private void RecordException(Exception exception)
        {
            if (!(exception is OperationCanceledException))
            {
                _recentException = exception;
            }
        }

        private static Exception CancelLifetime(Entry entry)
        {
            try
            {
                entry.Lifetime?.Cancel();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private void EnsureExternalOperationAllowed()
        {
            RejectCallbackReentry("Register");
            EnsureRunning();
        }

        private void EnsureRunning()
        {
            if (_stopped || Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(UIService));
            }
        }

        private void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "UIService operations must run on the Unity main thread.");
            }
        }

        private static InvalidOperationException ComponentFailure(
            Entry entry,
            string reason)
        {
            return new InvalidOperationException(
                "Window descriptor '" + entry.Descriptor.Id +
                "' at key '" + entry.Descriptor.ResourceKey.Value +
                "' expected exactly one '" +
                entry.WindowType.FullName + "' component, but " +
                reason + ".");
        }

        private static IReadOnlyList<Guid> SnapshotNavigation(
            List<Entry> entries)
        {
            var values = new Guid[entries.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = entries[index].InstanceId;
            }

            return UIDiagnostics.ReadOnly(values);
        }

        private static async Task<T> AwaitCallerAsync<T>(
            Task<T> task,
            CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                ObserveAbandonedTask(task);
                throw new OperationCanceledException(token);
            }

            if (!token.CanBeCanceled || task.IsCompleted)
            {
                return await task;
            }

            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(
                       state =>
                           ((TaskCompletionSource<bool>)state)
                           .TrySetResult(true),
                       canceled))
            {
                if (task != await Task.WhenAny(task, canceled.Task))
                {
                    ObserveAbandonedTask(task);
                    throw new OperationCanceledException(token);
                }
            }

            return await task;
        }

        private static async Task AwaitCallerAsync(
            Task task,
            CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                ObserveAbandonedTask(task);
                throw new OperationCanceledException(token);
            }

            if (!token.CanBeCanceled || task.IsCompleted)
            {
                await task;
                return;
            }

            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(
                       state =>
                           ((TaskCompletionSource<bool>)state)
                           .TrySetResult(true),
                       canceled))
            {
                if (task != await Task.WhenAny(task, canceled.Task))
                {
                    ObserveAbandonedTask(task);
                    throw new OperationCanceledException(token);
                }
            }

            await task;
        }

        private static void ObserveAbandonedTask(Task task)
        {
            _ = ObserveFaultAsync(task);
        }

        private static async Task ObserveFaultAsync(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // Caller cancellation abandons only the wait. Cleanup faults
                // are recorded by the canonical operation and observed here.
            }
        }

        private static async Task<bool> AwaitBackAsync(
            Task closeTask,
            CancellationToken token)
        {
            await AwaitCallerAsync(closeTask, token);
            return true;
        }

        private static Exception Combine(
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

        private CancellationToken SelectCanceledToken(
            Entry entry,
            OperationCanceledException exception)
        {
            if (exception.CancellationToken.IsCancellationRequested)
            {
                return exception.CancellationToken;
            }

            if (entry.Lifetime != null &&
                entry.Lifetime.Token.IsCancellationRequested)
            {
                return entry.Lifetime.Token;
            }

            if (_lifetime.Token.IsCancellationRequested)
            {
                return _lifetime.Token;
            }

            return new CancellationToken(canceled: true);
        }

        private sealed class Registration
        {
            public Registration(
                Type windowType,
                UIWindowDescriptor descriptor)
            {
                WindowType = windowType;
                Descriptor = descriptor;
            }

            public Type WindowType { get; }

            public UIWindowDescriptor Descriptor { get; }
        }

        private sealed class Entry
        {
            public Entry(
                Registration registration,
                Guid instanceId,
                long sequence,
                object parameter)
            {
                Registration = registration;
                InstanceId = instanceId;
                Sequence = sequence;
                Parameter = parameter;
                State = UIWindowState.Opening;
            }

            public Registration Registration { get; }

            public Type WindowType => Registration.WindowType;

            public UIWindowDescriptor Descriptor =>
                Registration.Descriptor;

            public Guid InstanceId { get; }

            public long Sequence { get; }

            public object Parameter { get; }

            public UIWindowState State { get; set; }

            public CancellationTokenSource Lifetime { get; set; }

            public UIWindow Window { get; set; }

            public WindowHandle Handle { get; set; }

            public IInstanceLease InstanceLease { get; set; }

            public IPooledGameObjectHandle PoolHandle { get; set; }

            public Task<WindowHandle> OpenTask { get; set; }

            public Task CloseTask { get; set; }

            public bool OpenCallbackStarted { get; set; }
        }

        private sealed class CallbackFrame
        {
            public CallbackFrame(
                UIService owner,
                Entry entry,
                AsyncLocal<CallbackFrame> holder,
                CallbackFrame previous)
            {
                Owner = owner;
                Entry = entry;
                Holder = holder;
                Previous = previous;
                Active = true;
            }

            public UIService Owner { get; }

            public Entry Entry { get; }

            public AsyncLocal<CallbackFrame> Holder { get; }

            public CallbackFrame Previous { get; }

            public bool Active { get; set; }
        }
    }
}
