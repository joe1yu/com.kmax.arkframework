using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using static ArkFramework.Editor.FrameworkDiagnosticsDrawer;

namespace ArkFramework.Editor
{
    public sealed class FrameworkDebugWindow : EditorWindow
    {
        private static readonly string[] PageNames =
        {
            "Modules",
            "Events",
            "Resources",
            "Pools",
            "UI",
            "Audio",
            "Scene",
            "Config",
            "FSM",
            "Procedure"
        };

        private DebugPage _page;
        private Vector2 _scroll;
        private ModuleInstaller _moduleInstaller;
        private Task<OperationOutcome> _pendingOperation;
        private string _operationStatus;
        private MessageType _operationStatusType = MessageType.Info;
        private double _nextRefreshTime;
        private bool _subscribed;

        [MenuItem("ArkFramework/Debug Window")]
        public static void Open()
        {
            var window = GetWindow<FrameworkDebugWindow>();
            window.titleContent = new GUIContent("ArkFramework Debug");
            window.minSize = new Vector2(640f, 360f);
            window.Show();
        }

        private void OnEnable()
        {
            if (_subscribed)
            {
                return;
            }

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            _subscribed = true;
        }

        private void OnDisable()
        {
            if (!_subscribed)
            {
                return;
            }

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            _subscribed = false;
        }

        private void OnEditorUpdate()
        {
            ObserveCompletedOperation();
            if (EditorApplication.timeSinceStartup < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = EditorApplication.timeSinceStartup + 0.25d;
            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            _operationStatus = $"Play Mode state changed: {state}.";
            _operationStatusType = MessageType.Info;
            Repaint();
        }

        private void OnGUI()
        {
            _page = (DebugPage)GUILayout.Toolbar((int)_page, PageNames);
            EditorGUILayout.Space();

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to inspect a live FrameworkRuntime.",
                    MessageType.Info);
                return;
            }

            var host = FrameworkHost.Current;
            if (host == null)
            {
                EditorGUILayout.HelpBox(
                    "No active FrameworkHost was found.",
                    MessageType.Warning);
                return;
            }

            var runtime = host.Runtime;
            if (runtime == null)
            {
                EditorGUILayout.HelpBox(
                    "FrameworkHost exists, but its runtime has not been created.",
                    MessageType.Info);
                return;
            }

            if (!string.IsNullOrEmpty(_operationStatus))
            {
                EditorGUILayout.HelpBox(
                    _operationStatus,
                    _operationStatusType);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            try
            {
                DrawPage(runtime);
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox(
                    $"{PageNames[(int)_page]} diagnostics failed: " +
                    $"{exception.GetType().Name}: {exception.Message}",
                    MessageType.Error);
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawPage(FrameworkRuntime runtime)
        {
            switch (_page)
            {
                case DebugPage.Modules:
                    DrawModules(runtime);
                    break;
                case DebugPage.Events:
                    DrawEvents(runtime);
                    break;
                case DebugPage.Resources:
                    DrawResources(runtime);
                    break;
                case DebugPage.Pools:
                    DrawPools(runtime);
                    break;
                case DebugPage.UI:
                    DrawUI(runtime);
                    break;
                case DebugPage.Audio:
                    DrawAudio(runtime);
                    break;
                case DebugPage.Scene:
                    DrawScene(runtime);
                    break;
                case DebugPage.Config:
                    DrawConfig(runtime);
                    break;
                case DebugPage.FSM:
                    DrawFsm(runtime);
                    break;
                case DebugPage.Procedure:
                    DrawProcedure(runtime);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void DrawModules(FrameworkRuntime runtime)
        {
            EditorGUILayout.LabelField("Runtime Modules", EditorStyles.boldLabel);
            _moduleInstaller = (ModuleInstaller)EditorGUILayout.ObjectField(
                "Module Installer",
                _moduleInstaller,
                typeof(ModuleInstaller),
                allowSceneObjects: false);

            using (new EditorGUI.DisabledScope(_pendingOperation != null))
            {
                if (GUILayout.Button("Install Selected Module"))
                {
                    BeginOperation(
                        "Installing module...",
                        () => InstallModuleAsync(
                            runtime,
                            CreateInstallDescriptor(runtime)));
                }

                if (GUILayout.Button("Stop Runtime"))
                {
                    BeginOperation(
                        "Stopping runtime...",
                        () => StopRuntimeAsync(runtime));
                }
            }

            var modules = new List<ModuleRecord>(runtime.Modules);
            if (modules.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "The runtime has no module records.",
                    MessageType.Info);
                return;
            }

            for (var index = 0; index < modules.Count; index++)
            {
                DrawModule(runtime, modules[index]);
            }
        }

        private void DrawModule(
            FrameworkRuntime runtime,
            ModuleRecord record)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                try
                {
                    EditorGUILayout.LabelField(
                        record.Descriptor.Id,
                        EditorStyles.boldLabel);
                    DrawValue("State", record.State);
                    DrawValue(
                        "Dependencies",
                        record.Descriptor.Dependencies.Count == 0
                            ? "(none)"
                            : string.Join(", ", record.Descriptor.Dependencies));
                    DrawValue("Stable Order", record.Descriptor.StableOrder);
                    DrawValue(
                        "Last State Change (UTC)",
                        record.LastStateChangedUtc.ToString("O"));
                    DrawDuration("Initialize", record.InitializeDuration);
                    DrawDuration("Start", record.StartDuration);
                    DrawDuration("Stop", record.StopDuration);
                    DrawDuration("Dispose", record.DisposeDuration);
                    DrawException("Last Exception", record.LastException);

                    using (new EditorGUI.DisabledScope(
                        _pendingOperation != null ||
                        record.State != ModuleState.Running &&
                        record.State != ModuleState.Faulted))
                    {
                        if (GUILayout.Button("Unload"))
                        {
                            BeginUnloadOperation(
                                runtime,
                                record.Descriptor.Id);
                        }
                    }
                }
                catch (ExitGUIException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    EditorGUILayout.HelpBox(
                        $"Module diagnostics failed: " +
                        $"{exception.GetType().Name}: {exception.Message}",
                        MessageType.Error);
                }
            }
        }

        private ModuleDescriptor CreateInstallDescriptor(FrameworkRuntime runtime)
        {
            var installer = _moduleInstaller;
            if (installer == null)
            {
                throw new InvalidOperationException(
                    "Select a ModuleInstaller first.");
            }

            var modules = runtime.Modules;
            var stableOrder = 0;
            if (modules.Count != 0)
            {
                var maximumOrder = modules[0].Descriptor.StableOrder;
                for (var index = 1; index < modules.Count; index++)
                {
                    maximumOrder = Math.Max(
                        maximumOrder,
                        modules[index].Descriptor.StableOrder);
                }

                if (maximumOrder == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Cannot install another module because the stable " +
                        "order range is exhausted.");
                }

                stableOrder = maximumOrder + 1;
            }

            return new ModuleDescriptor(
                installer.ModuleId,
                installer.Dependencies,
                stableOrder,
                () => installer.CreateModule());
        }

        private static void DrawEvents(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.EventBus,
                    out IEventBus eventBus))
            {
                return;
            }

            var diagnostics = eventBus.Diagnostics;
            var entries =
                new List<KeyValuePair<Type, EventTypeDiagnostics>>(
                    diagnostics.Entries);
            DrawValue("Event Types", entries.Count);
            foreach (var pair in entries)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        pair.Key.FullName ?? pair.Key.Name,
                        EditorStyles.boldLabel);
                    DrawValue("Listeners", pair.Value.ListenerCount);
                    DrawValue("Dispatches", pair.Value.DispatchCount);
                    DrawValue("Exceptions", pair.Value.ExceptionCount);
                    DrawValue(
                        "Last Dispatch (UTC)",
                        pair.Value.LastDispatchUtc?.ToString("O") ?? "(never)");
                }
            }
        }

        private static void DrawResources(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.Resource,
                    out IResourceService service))
            {
                return;
            }

            var diagnostics = service.Diagnostics;
            var leases = new List<ResourceLeaseDiagnostics>(
                diagnostics.OutstandingLeases);
            DrawValue("Outstanding Leases", leases.Count);
            DrawValue("In-flight Operations", diagnostics.InflightOperationCount);
            for (var index = 0; index < leases.Count; index++)
            {
                var lease = leases[index];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    DrawValue("Lease ID", lease.LeaseId);
                    DrawValue("Kind", lease.Kind);
                    DrawValue("Key / Label", lease.KeyOrLabel);
                    DrawValue(
                        "Asset Type",
                        lease.AssetType?.FullName ?? "(none)");
                    DrawValue("Created (UTC)", lease.CreatedUtc.ToString("O"));
                }
            }
        }

        private static void DrawPools(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.Pool,
                    out IGameObjectPool service))
            {
                return;
            }

            var pools =
                new List<KeyValuePair<ResourceKey, PoolDiagnostics>>(
                    service.Diagnostics);
            pools.Sort(
                (left, right) => string.Compare(
                    left.Key.Value,
                    right.Key.Value,
                    StringComparison.Ordinal));
            DrawValue("Pools", pools.Count);
            for (var index = 0; index < pools.Count; index++)
            {
                var pair = pools[index];
                using (new EditorGUILayout.VerticalScope(
                    EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        pair.Key.ToString(),
                        EditorStyles.boldLabel);
                    DrawValue("Total Created", pair.Value.TotalCreatedCount);
                    DrawValue("Active", pair.Value.ActiveCount);
                    DrawValue("Idle", pair.Value.IdleCount);
                    DrawValue("Peak Active", pair.Value.PeakActiveCount);
                    DrawValue("Hit Rate", pair.Value.HitRate.ToString("P1"));
                }
            }
        }

        private static void DrawUI(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.UI,
                    out IUIService service))
            {
                return;
            }

            var diagnostics = service.Diagnostics;
            var layers = new List<UILayerDiagnostics>(diagnostics.Layers);
            var windows = new List<UIWindowDiagnostics>(diagnostics.Windows);
            var normalNavigation = new List<Guid>(
                diagnostics.NormalNavigation);
            var popupNavigation = new List<Guid>(
                diagnostics.PopupNavigation);
            DrawValue("Layers", layers.Count);
            DrawValue("Windows", windows.Count);
            DrawValue("Opening", diagnostics.OpeningCount);
            DrawValue("Open", diagnostics.OpenCount);
            DrawValue("Closing", diagnostics.ClosingCount);
            DrawValue("Cached", diagnostics.CachedCount);
            DrawValue("Normal Navigation", normalNavigation.Count);
            DrawValue("Popup Navigation", popupNavigation.Count);
            DrawGuidSequence(
                "Normal Navigation (bottom to top)",
                normalNavigation);
            DrawGuidSequence(
                "Popup Navigation (bottom to top)",
                popupNavigation);
            DrawValue(
                "Mask Popup",
                diagnostics.MaskPopupInstanceId?.ToString() ?? "(none)");
            DrawException("Recent Exception", diagnostics.RecentException);
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        window.DescriptorId,
                        EditorStyles.boldLabel);
                    DrawValue("Instance", window.InstanceId);
                    DrawValue("Layer", window.Layer);
                    DrawValue("State", window.State);
                }
            }
        }

        private static void DrawAudio(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.Audio,
                    out IAudioService service))
            {
                return;
            }

            var diagnostics = service.Diagnostics;
            var channels = new List<AudioChannelDiagnostics>(
                diagnostics.Channels);
            var entries = new List<AudioEntryDiagnostics>(
                diagnostics.Entries);
            DrawValue("Active Entries", entries.Count);
            DrawValue("Pending Loads", diagnostics.PendingLoadCount);
            DrawValue(
                "Current Music",
                diagnostics.CurrentMusicKey?.ToString() ?? "(none)");
            DrawException("Recent Exception", diagnostics.RecentException);
            if (diagnostics.OneShotPool != null)
            {
                DrawValue(
                    "One-shot Pool Active",
                    diagnostics.OneShotPool.ActiveCount);
                DrawValue(
                    "One-shot Pool Idle",
                    diagnostics.OneShotPool.IdleCount);
                DrawValue(
                    "One-shot Pool Peak",
                    diagnostics.OneShotPool.PeakActiveCount);
                DrawValue(
                    "One-shot Pool Hit Rate",
                    diagnostics.OneShotPool.HitRate.ToString("P1"));
            }

            for (var index = 0; index < channels.Count; index++)
            {
                var channel = channels[index];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        channel.Channel.ToString(),
                        EditorStyles.boldLabel);
                    DrawValue("Volume", channel.Volume);
                    DrawValue("Muted", channel.Muted);
                    DrawValue("Paused", channel.Paused);
                    DrawValue(
                        "Mixer Group",
                        string.IsNullOrEmpty(channel.MixerGroupName)
                            ? "(none)"
                            : channel.MixerGroupName);
                }
            }

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        entry.ResourceKey.ToString(),
                        EditorStyles.boldLabel);
                    DrawValue("Instance", entry.InstanceId);
                    DrawValue("Channel", entry.Channel);
                    DrawValue("State", entry.State);
                    DrawValue("Loop", entry.Loop);
                    DrawValue("Play Volume", entry.PlayVolume);
                    DrawValue("Effective Volume", entry.EffectiveVolume);
                }
            }
        }

        private static void DrawScene(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.Scene,
                    out ISceneService service))
            {
                return;
            }

            var diagnostics = service.Diagnostics;
            var ownedSceneKeys = new List<ResourceKey>(
                diagnostics.OwnedSceneKeys);
            DrawValue("Active Scene Key", diagnostics.ActiveSceneKey);
            DrawValue("Active Scene Name", diagnostics.ActiveSceneName);
            DrawValue("Transitioning", diagnostics.IsTransitioning);
            DrawValue("Queue Length", diagnostics.QueueLength);
            DrawValue(
                "Current Stage",
                diagnostics.CurrentStage?.ToString() ?? "(none)");
            DrawValue("Owned Scenes", ownedSceneKeys.Count);
            for (var index = 0; index < ownedSceneKeys.Count; index++)
            {
                DrawValue(
                    $"Owned Scene {index + 1}",
                    ownedSceneKeys[index]);
            }

            DrawException("Last Exception", diagnostics.LastException);
        }

        private static void DrawConfig(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.Config,
                    out IConfigService service))
            {
                return;
            }

            var diagnostics = service.Diagnostics;
            var entries =
                new List<KeyValuePair<ConfigKey, ConfigEntryDiagnostics>>(
                    diagnostics.Entries);
            DrawValue("Entries", entries.Count);
            DrawValue(
                "Last Successful Reload (UTC)",
                diagnostics.LastSuccessfulReloadUtc?.ToString("O") ?? "(never)");
            DrawValue(
                "Validation",
                FormatConfigValidation(
                    diagnostics.LastValidationSucceeded,
                    diagnostics.LastValidationError));
            foreach (var pair in entries)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        pair.Key.ToString(),
                        EditorStyles.boldLabel);
                    DrawValue("Source", pair.Value.Source);
                    DrawValue("Version", pair.Value.Version);
                }
            }
        }

        private static void DrawFsm(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.Fsm,
                    out IFsmService service))
            {
                return;
            }

            var diagnostics = new List<FsmDiagnostics>(service.Diagnostics);
            DrawValue("State Machines", diagnostics.Count);
            for (var index = 0; index < diagnostics.Count; index++)
            {
                var machine = diagnostics[index];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        machine.MachineId,
                        EditorStyles.boldLabel);
                    DrawValue("Current", machine.CurrentStateId ?? "(none)");
                    DrawValue("Previous", machine.PreviousStateId ?? "(none)");
                    DrawValue("Transitioning", machine.IsTransitioning);
                    DrawValue("Faulted", machine.IsFaulted);
                    DrawValue("Queued Requests", machine.QueuedRequestCount);
                    var transitions = FormatFsmTransitions(
                        machine.AvailableTransitions);
                    DrawValue("Available Transitions", transitions.Count);
                    for (var transitionIndex = 0;
                        transitionIndex < transitions.Count;
                        transitionIndex++)
                    {
                        DrawValue(
                            $"Transition {transitionIndex + 1}",
                            transitions[transitionIndex]);
                    }

                    DrawHistory(machine.History);
                    DrawException("Last Exception", machine.LastException);
                }
            }
        }

        private static void DrawProcedure(FrameworkRuntime runtime)
        {
            if (!TryResolve(
                    runtime,
                    BuiltInModuleIds.Procedure,
                    out IProcedureService service))
            {
                return;
            }

            var diagnostics = service.Diagnostics;
            var registeredProcedureIds = diagnostics.RegisteredProcedureIds == null
                ? new List<string>()
                : new List<string>(diagnostics.RegisteredProcedureIds);
            var availableTargets = SortProcedureTargets(
                diagnostics.AvailableTargetProcedureIds);
            DrawValue("Machine", diagnostics.MachineId);
            DrawValue(
                "Current Procedure",
                diagnostics.CurrentProcedureId ?? "(none)");
            DrawValue(
                "Previous Procedure",
                diagnostics.PreviousProcedureId ?? "(none)");
            DrawValue("Started", diagnostics.IsStarted);
            DrawValue("Faulted", diagnostics.IsFaulted);
            DrawValue(
                "Registered Procedures",
                registeredProcedureIds.Count);
            for (var index = 0;
                index < registeredProcedureIds.Count;
                index++)
            {
                DrawValue(
                    $"Procedure {index + 1}",
                    registeredProcedureIds[index]);
            }

            DrawValue("Available Targets", availableTargets.Count);
            for (var index = 0; index < availableTargets.Count; index++)
            {
                DrawValue(
                    $"Target {index + 1}",
                    availableTargets[index]);
            }

            DrawHistory(diagnostics.History);
            DrawException("Last Exception", diagnostics.LastException);
        }

        private static void DrawGuidSequence(
            string label,
            IReadOnlyList<Guid> values)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            if (values == null)
            {
                EditorGUILayout.LabelField("(empty)");
                return;
            }

            var snapshot = new List<Guid>(values);
            if (snapshot.Count == 0)
            {
                EditorGUILayout.LabelField("(empty)");
                return;
            }

            for (var index = 0; index < snapshot.Count; index++)
            {
                var position = snapshot.Count == 1
                    ? "bottom / top"
                    : index == 0
                    ? "bottom"
                    : index == snapshot.Count - 1
                        ? "top"
                        : "middle";
                DrawValue(
                    $"[{index}] ({position})",
                    snapshot[index]);
            }
        }

        private static void DrawHistory(
            IReadOnlyList<StateHistoryEntry> history)
        {
            if (history == null)
            {
                DrawValue("History Entries", 0);
                return;
            }

            var snapshot = new List<StateHistoryEntry>(history);
            DrawValue("History Entries", snapshot.Count);
            for (var index = 0; index < snapshot.Count; index++)
            {
                var entry = snapshot[index];
                DrawValue(
                    $"History {index + 1}",
                    $"{entry.From ?? "(none)"} -> {entry.To} " +
                    $"[{entry.Trigger}] {entry.TimestampUtc:O}");
            }
        }

        private static bool TryResolve<T>(
            FrameworkRuntime runtime,
            string displayName,
            out T service)
        {
            if (runtime.Services.TryResolve(out service))
            {
                return true;
            }

            EditorGUILayout.HelpBox(
                $"{displayName} service is unavailable or not registered.",
                MessageType.Info);
            return false;
        }

        private void BeginUnloadOperation(
            FrameworkRuntime runtime,
            string moduleId)
        {
            var dependents = FindDirectDependents(runtime, moduleId);
            var mode = ModuleUnloadMode.RequireNoDependents;
            if (dependents.Count != 0)
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Cascade Unload Module",
                    $"Module '{moduleId}' is required by: " +
                    $"{string.Join(", ", dependents)}.\n\n" +
                    "Unload it and all downstream modules?",
                    "Cascade Unload",
                    "Cancel");
                if (!confirmed)
                {
                    return;
                }

                mode = ModuleUnloadMode.Cascade;
            }

            BeginOperation(
                mode == ModuleUnloadMode.Cascade
                    ? $"Cascade unloading '{moduleId}'..."
                    : $"Unloading '{moduleId}'...",
                () => UnloadModuleAsync(runtime, moduleId, mode));
        }

        private static IReadOnlyList<string> FindDirectDependents(
            FrameworkRuntime runtime,
            string moduleId)
        {
            var dependents = new List<string>();
            var modules = runtime.Modules;
            for (var moduleIndex = 0;
                moduleIndex < modules.Count;
                moduleIndex++)
            {
                var descriptor = modules[moduleIndex].Descriptor;
                foreach (var dependencyId in descriptor.Dependencies)
                {
                    if (!string.Equals(
                            dependencyId,
                            moduleId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    dependents.Add(descriptor.Id);
                    break;
                }
            }

            dependents.Sort(StringComparer.Ordinal);
            return dependents;
        }

        private void BeginOperation(
            string pendingMessage,
            Func<Task<string>> operationFactory)
        {
            if (_pendingOperation != null)
            {
                return;
            }

            try
            {
                var operation = operationFactory();
                if (operation == null)
                {
                    throw new InvalidOperationException(
                        "The runtime operation returned no task.");
                }

                _pendingOperation = ObserveOperationAsync(operation);
                _operationStatus = pendingMessage;
                _operationStatusType = MessageType.Info;
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _operationStatus =
                    $"{exception.GetType().Name}: {exception.Message}";
                _operationStatusType = MessageType.Error;
            }
        }

        private void ObserveCompletedOperation()
        {
            if (_pendingOperation == null || !_pendingOperation.IsCompleted)
            {
                return;
            }

            var outcome = _pendingOperation.GetAwaiter().GetResult();
            _pendingOperation = null;
            _operationStatus = outcome.Message;
            _operationStatusType = outcome.Succeeded
                ? MessageType.Info
                : MessageType.Error;
        }

        private static async Task<OperationOutcome> ObserveOperationAsync(
            Task<string> operation)
        {
            try
            {
                return OperationOutcome.Success(await operation);
            }
            catch (OperationCanceledException exception)
            {
                return OperationOutcome.Failure(
                    $"Operation canceled: {exception.Message}");
            }
            catch (Exception exception)
            {
                return OperationOutcome.Failure(
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        private static async Task<string> StopRuntimeAsync(
            FrameworkRuntime runtime)
        {
            await runtime.StopAsync(CancellationToken.None);
            return "Runtime stopped.";
        }

        private static async Task<string> InstallModuleAsync(
            FrameworkRuntime runtime,
            ModuleDescriptor descriptor)
        {
            await runtime.InstallAsync(descriptor, CancellationToken.None);
            return $"Installed module '{descriptor.Id}'.";
        }

        private static async Task<string> UnloadModuleAsync(
            FrameworkRuntime runtime,
            string moduleId,
            ModuleUnloadMode mode)
        {
            var result = await runtime.UnloadAsync(
                moduleId,
                mode,
                CancellationToken.None);
            return $"Unloaded modules: {string.Join(", ", result.UnloadedModuleIds)}.";
        }

        private enum DebugPage
        {
            Modules,
            Events,
            Resources,
            Pools,
            UI,
            Audio,
            Scene,
            Config,
            FSM,
            Procedure
        }

        private sealed class OperationOutcome
        {
            private OperationOutcome(bool succeeded, string message)
            {
                Succeeded = succeeded;
                Message = message;
            }

            public bool Succeeded { get; }

            public string Message { get; }

            public static OperationOutcome Success(string message)
            {
                return new OperationOutcome(true, message);
            }

            public static OperationOutcome Failure(string message)
            {
                return new OperationOutcome(false, message);
            }
        }
    }
}
