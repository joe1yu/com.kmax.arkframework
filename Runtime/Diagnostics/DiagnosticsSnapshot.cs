using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace ArkFramework
{
    public enum DiagnosticsPageKind
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

    public sealed class DiagnosticsFieldSnapshot
    {
        public DiagnosticsFieldSnapshot(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "A diagnostics field name is required.",
                    nameof(name));
            }

            Name = name;
            Value = value ?? string.Empty;
        }

        public string Name { get; }
        public string Value { get; }
    }

    public sealed class DiagnosticsEntrySnapshot
    {
        public DiagnosticsEntrySnapshot(
            string id,
            IReadOnlyList<DiagnosticsFieldSnapshot> fields)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A diagnostics entry ID is required.",
                    nameof(id));
            }

            if (fields == null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            var copy = new DiagnosticsFieldSnapshot[fields.Count];
            for (var index = 0; index < copy.Length; index++)
            {
                copy[index] = fields[index] ??
                    throw new ArgumentException(
                        "Diagnostics fields cannot contain null.",
                        nameof(fields));
            }

            Array.Sort(
                copy,
                (left, right) => string.CompareOrdinal(
                    left.Name,
                    right.Name));
            Id = id;
            Fields = new ReadOnlyCollection<DiagnosticsFieldSnapshot>(copy);
        }

        public string Id { get; }
        public IReadOnlyList<DiagnosticsFieldSnapshot> Fields { get; }
    }

    public sealed class DiagnosticsPageSnapshot
    {
        public DiagnosticsPageSnapshot(
            DiagnosticsPageKind kind,
            bool isAvailable,
            string error,
            IReadOnlyList<DiagnosticsEntrySnapshot> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var copy = new DiagnosticsEntrySnapshot[entries.Count];
            for (var index = 0; index < copy.Length; index++)
            {
                copy[index] = entries[index] ??
                    throw new ArgumentException(
                        "Diagnostics entries cannot contain null.",
                        nameof(entries));
            }

            Kind = kind;
            IsAvailable = isAvailable;
            Error = error;
            Entries = new ReadOnlyCollection<DiagnosticsEntrySnapshot>(copy);
        }

        public DiagnosticsPageKind Kind { get; }
        public bool IsAvailable { get; }
        public string Error { get; }
        public IReadOnlyList<DiagnosticsEntrySnapshot> Entries { get; }
    }

    public sealed class DiagnosticsSnapshot
    {
        private static readonly IReadOnlyList<DiagnosticsEntrySnapshot>
            EmptyEntries =
                new ReadOnlyCollection<DiagnosticsEntrySnapshot>(
                    Array.Empty<DiagnosticsEntrySnapshot>());

        private DiagnosticsSnapshot(
            IReadOnlyList<DiagnosticsPageSnapshot> pages)
        {
            Pages = new ReadOnlyCollection<DiagnosticsPageSnapshot>(
                pages.ToArray());
        }

        public IReadOnlyList<DiagnosticsPageSnapshot> Pages { get; }

        public static DiagnosticsSnapshot Capture(FrameworkRuntime runtime)
        {
            var pages = new DiagnosticsPageSnapshot[10];
            pages[0] = CaptureModules(runtime);
            pages[1] = CaptureEvents(runtime);
            pages[2] = CaptureResources(runtime);
            pages[3] = CapturePools(runtime);
            pages[4] = CaptureUI(runtime);
            pages[5] = CaptureAudio(runtime);
            pages[6] = CaptureScene(runtime);
            pages[7] = CaptureConfig(runtime);
            pages[8] = CaptureFsm(runtime);
            pages[9] = CaptureProcedure(runtime);
            return new DiagnosticsSnapshot(pages);
        }

        private static DiagnosticsPageSnapshot CaptureModules(
            FrameworkRuntime runtime)
        {
            if (runtime == null)
            {
                return Unavailable(DiagnosticsPageKind.Modules);
            }

            try
            {
                var records = runtime.Modules.ToArray();
                Array.Sort(
                    records,
                    (left, right) =>
                    {
                        var order = left.Descriptor.StableOrder.CompareTo(
                            right.Descriptor.StableOrder);
                        return order != 0
                            ? order
                            : string.CompareOrdinal(
                                left.Descriptor.Id,
                                right.Descriptor.Id);
                    });
                var entries =
                    new DiagnosticsEntrySnapshot[records.Length];
                for (var index = 0; index < records.Length; index++)
                {
                    var record = records[index];
                    var descriptor = record.Descriptor;
                    entries[index] = Entry(
                        descriptor.Id,
                        Field(
                            "Dependencies",
                            string.Join(",", descriptor.Dependencies)),
                        Field("DisposeMs", Milliseconds(record.DisposeDuration)),
                        Field("Exception", ExceptionText(record.LastException)),
                        Field("InitializeMs", Milliseconds(record.InitializeDuration)),
                        Field(
                            "LastStateChangedUtc",
                            Timestamp(record.LastStateChangedUtc)),
                        Field("StableOrder", Number(descriptor.StableOrder)),
                        Field("StartMs", Milliseconds(record.StartDuration)),
                        Field("State", record.State.ToString()),
                        Field("StopMs", Milliseconds(record.StopDuration)));
                }

                return Available(DiagnosticsPageKind.Modules, entries);
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Modules,
                    true,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureEvents(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IEventBus service))
                {
                    return Unavailable(DiagnosticsPageKind.Events);
                }

                available = true;
                var diagnostics = service.Diagnostics;
                var pairs = diagnostics.Entries.ToArray();
                Array.Sort(
                    pairs,
                    (left, right) => string.CompareOrdinal(
                        TypeName(left.Key),
                        TypeName(right.Key)));
                var entries =
                    new DiagnosticsEntrySnapshot[pairs.Length];
                for (var index = 0; index < pairs.Length; index++)
                {
                    var pair = pairs[index];
                    entries[index] = Entry(
                        TypeName(pair.Key),
                        Field("DispatchCount", Number(pair.Value.DispatchCount)),
                        Field("ExceptionCount", Number(pair.Value.ExceptionCount)),
                        Field(
                            "LastDispatchUtc",
                            Timestamp(pair.Value.LastDispatchUtc)),
                        Field("ListenerCount", Number(pair.Value.ListenerCount)));
                }

                return Available(DiagnosticsPageKind.Events, entries);
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Events,
                    available,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureResources(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IResourceService service))
                {
                    return Unavailable(DiagnosticsPageKind.Resources);
                }

                available = true;
                var diagnostics = service.Diagnostics;
                var leases = diagnostics.OutstandingLeases.ToArray();
                Array.Sort(
                    leases,
                    (left, right) =>
                    {
                        var key = string.CompareOrdinal(
                            left.KeyOrLabel,
                            right.KeyOrLabel);
                        if (key != 0)
                        {
                            return key;
                        }

                        var type = string.CompareOrdinal(
                            TypeName(left.AssetType),
                            TypeName(right.AssetType));
                        return type != 0
                            ? type
                            : left.LeaseId.CompareTo(right.LeaseId);
                    });
                var entries =
                    new DiagnosticsEntrySnapshot[leases.Length + 1];
                entries[0] = Entry(
                    "Summary",
                    Field(
                        "InflightOperationCount",
                        Number(diagnostics.InflightOperationCount)),
                    Field(
                        "OutstandingLeaseCount",
                        Number(leases.Length)));
                for (var index = 0; index < leases.Length; index++)
                {
                    var lease = leases[index];
                    entries[index + 1] = Entry(
                        $"{lease.KeyOrLabel}#{lease.LeaseId}",
                        Field("AssetType", TypeName(lease.AssetType)),
                        Field("CreatedUtc", Timestamp(lease.CreatedUtc)),
                        Field("Kind", lease.Kind.ToString()),
                        Field("LeaseId", Number(lease.LeaseId)));
                }

                return Available(DiagnosticsPageKind.Resources, entries);
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Resources,
                    available,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CapturePools(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IGameObjectPool service))
                {
                    return Unavailable(DiagnosticsPageKind.Pools);
                }

                available = true;
                var pairs = service.Diagnostics.ToArray();
                Array.Sort(
                    pairs,
                    (left, right) => string.CompareOrdinal(
                        left.Key.Value,
                        right.Key.Value));
                var entries =
                    new DiagnosticsEntrySnapshot[pairs.Length];
                for (var index = 0; index < pairs.Length; index++)
                {
                    entries[index] = PoolEntry(
                        pairs[index].Key.Value,
                        pairs[index].Value);
                }

                return Available(DiagnosticsPageKind.Pools, entries);
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Pools,
                    available,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureUI(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IUIService service))
                {
                    return Unavailable(DiagnosticsPageKind.UI);
                }

                available = true;
                var diagnostics = service.Diagnostics;
                var entries = new List<DiagnosticsEntrySnapshot>();
                var layers = diagnostics.Layers
                    .OrderBy(value => value.Layer)
                    .ToArray();
                for (var index = 0; index < layers.Length; index++)
                {
                    entries.Add(
                        Entry(
                            $"Layer:{layers[index].Layer}",
                            Field("RootName", layers[index].RootName),
                            Field(
                                "SortingOrder",
                                Number(layers[index].SortingOrder))));
                }

                var windows = diagnostics.Windows
                    .OrderBy(value => value.DescriptorId, StringComparer.Ordinal)
                    .ThenBy(value => value.InstanceId)
                    .ToArray();
                for (var index = 0; index < windows.Length; index++)
                {
                    var window = windows[index];
                    entries.Add(
                        Entry(
                            $"Window:{window.DescriptorId}:{window.InstanceId:D}",
                            Field("Layer", window.Layer.ToString()),
                            Field("State", window.State.ToString())));
                }

                entries.Add(
                    Entry(
                        "Summary",
                        Field("CachedCount", Number(diagnostics.CachedCount)),
                        Field("ClosingCount", Number(diagnostics.ClosingCount)),
                        Field("Exception", ExceptionText(diagnostics.RecentException)),
                        Field(
                            "MaskPopupInstanceId",
                            diagnostics.MaskPopupInstanceId?.ToString("D") ??
                            string.Empty),
                        Field(
                            "NormalNavigation",
                            string.Join(",", diagnostics.NormalNavigation)),
                        Field("OpenCount", Number(diagnostics.OpenCount)),
                        Field("OpeningCount", Number(diagnostics.OpeningCount)),
                        Field(
                            "PopupNavigation",
                            string.Join(",", diagnostics.PopupNavigation))));
                return Available(DiagnosticsPageKind.UI, entries);
            }
            catch (Exception exception)
            {
                return Faulted(DiagnosticsPageKind.UI, available, exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureAudio(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IAudioService service))
                {
                    return Unavailable(DiagnosticsPageKind.Audio);
                }

                available = true;
                var diagnostics = service.Diagnostics;
                var entries = new List<DiagnosticsEntrySnapshot>();
                var channels = diagnostics.Channels
                    .OrderBy(value => value.Channel)
                    .ToArray();
                for (var index = 0; index < channels.Length; index++)
                {
                    var channel = channels[index];
                    entries.Add(
                        Entry(
                            $"Channel:{channel.Channel}",
                            Field("MixerGroup", channel.MixerGroupName),
                            Field("Muted", Boolean(channel.Muted)),
                            Field("Paused", Boolean(channel.Paused)),
                            Field("Volume", Decimal(channel.Volume))));
                }

                var playing = diagnostics.Entries
                    .OrderBy(value => value.ResourceKey.Value, StringComparer.Ordinal)
                    .ThenBy(value => value.InstanceId)
                    .ToArray();
                for (var index = 0; index < playing.Length; index++)
                {
                    var item = playing[index];
                    entries.Add(
                        Entry(
                            $"Entry:{item.ResourceKey.Value}:{item.InstanceId:D}",
                            Field("Channel", item.Channel.ToString()),
                            Field("EffectiveVolume", Decimal(item.EffectiveVolume)),
                            Field("Loop", Boolean(item.Loop)),
                            Field("PlayVolume", Decimal(item.PlayVolume)),
                            Field("State", item.State.ToString())));
                }

                entries.Add(
                    Entry(
                        "Summary",
                        Field(
                            "CurrentMusicKey",
                            diagnostics.CurrentMusicKey?.Value ?? string.Empty),
                        Field("Exception", ExceptionText(diagnostics.RecentException)),
                        Field(
                            "PendingLoadCount",
                            Number(diagnostics.PendingLoadCount))));
                entries.Add(
                    PoolEntry("OneShotPool", diagnostics.OneShotPool));
                return Available(DiagnosticsPageKind.Audio, entries);
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Audio,
                    available,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureScene(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out ISceneService service))
                {
                    return Unavailable(DiagnosticsPageKind.Scene);
                }

                available = true;
                var diagnostics = service.Diagnostics;
                var owned = diagnostics.OwnedSceneKeys
                    .Select(value => value.Value)
                    .OrderBy(value => value, StringComparer.Ordinal);
                return Available(
                    DiagnosticsPageKind.Scene,
                    new[]
                    {
                        Entry(
                            "Scene",
                            Field(
                                "ActiveSceneKey",
                                diagnostics.ActiveSceneKey.Value),
                            Field(
                                "ActiveSceneName",
                                diagnostics.ActiveSceneName),
                            Field(
                                "CurrentStage",
                                diagnostics.CurrentStage?.ToString() ??
                                string.Empty),
                            Field(
                                "Exception",
                                ExceptionText(diagnostics.LastException)),
                            Field(
                                "IsTransitioning",
                                Boolean(diagnostics.IsTransitioning)),
                            Field(
                                "OwnedSceneKeys",
                                string.Join(",", owned)),
                            Field(
                                "QueueLength",
                                Number(diagnostics.QueueLength)))
                    });
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Scene,
                    available,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureConfig(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IConfigService service))
                {
                    return Unavailable(DiagnosticsPageKind.Config);
                }

                available = true;
                var diagnostics = service.Diagnostics;
                var pairs = diagnostics.Entries.ToArray();
                Array.Sort(
                    pairs,
                    (left, right) =>
                    {
                        var type = string.CompareOrdinal(
                            TypeName(left.Key.Type),
                            TypeName(right.Key.Type));
                        return type != 0
                            ? type
                            : string.CompareOrdinal(
                                left.Key.Key,
                                right.Key.Key);
                    });
                var entries =
                    new DiagnosticsEntrySnapshot[pairs.Length + 1];
                entries[0] = Entry(
                    "Validation",
                    Field(
                        "LastSuccessfulReloadUtc",
                        Timestamp(diagnostics.LastSuccessfulReloadUtc)),
                    Field(
                        "LastValidationError",
                        diagnostics.LastValidationError),
                    Field(
                        "LastValidationSucceeded",
                        diagnostics.LastValidationSucceeded?.ToString() ??
                        string.Empty));
                for (var index = 0; index < pairs.Length; index++)
                {
                    var pair = pairs[index];
                    entries[index + 1] = Entry(
                        $"{TypeName(pair.Key.Type)}:{pair.Key.Key}",
                        Field("Source", pair.Value.Source),
                        Field("Version", pair.Value.Version));
                }

                return Available(DiagnosticsPageKind.Config, entries);
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Config,
                    available,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureFsm(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IFsmService service))
                {
                    return Unavailable(DiagnosticsPageKind.FSM);
                }

                available = true;
                var machines = service.Diagnostics
                    .OrderBy(value => value.MachineId, StringComparer.Ordinal)
                    .ToArray();
                var entries =
                    new DiagnosticsEntrySnapshot[machines.Length];
                for (var index = 0; index < machines.Length; index++)
                {
                    var machine = machines[index];
                    var transitions = machine.AvailableTransitions.Select(
                        value =>
                            $"{value.Trigger}->{value.TargetStateId}" +
                            (value.HasGuard ? "[guard]" : string.Empty));
                    var history = machine.History.Select(
                        value =>
                            $"{value.From}->{value.To}({value.Trigger})");
                    entries[index] = Entry(
                        machine.MachineId,
                        Field("AvailableTransitions", string.Join(",", transitions)),
                        Field("CurrentStateId", machine.CurrentStateId),
                        Field("Exception", ExceptionText(machine.LastException)),
                        Field("History", string.Join(",", history)),
                        Field("IsFaulted", Boolean(machine.IsFaulted)),
                        Field("IsTransitioning", Boolean(machine.IsTransitioning)),
                        Field("PreviousStateId", machine.PreviousStateId),
                        Field(
                            "QueuedRequestCount",
                            Number(machine.QueuedRequestCount)));
                }

                return Available(DiagnosticsPageKind.FSM, entries);
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.FSM,
                    available,
                    exception);
            }
        }

        private static DiagnosticsPageSnapshot CaptureProcedure(
            FrameworkRuntime runtime)
        {
            var available = false;
            try
            {
                if (!TryResolve(runtime, out IProcedureService service))
                {
                    return Unavailable(DiagnosticsPageKind.Procedure);
                }

                available = true;
                var diagnostics = service.Diagnostics;
                var history = diagnostics.History.Select(
                    value =>
                        $"{value.From}->{value.To}({value.Trigger})");
                return Available(
                    DiagnosticsPageKind.Procedure,
                    new[]
                    {
                        Entry(
                            diagnostics.MachineId,
                            Field(
                                "AvailableTargets",
                                string.Join(
                                    ",",
                                    diagnostics.AvailableTargetProcedureIds)),
                            Field(
                                "CurrentProcedureId",
                                diagnostics.CurrentProcedureId),
                            Field(
                                "Exception",
                                ExceptionText(diagnostics.LastException)),
                            Field("History", string.Join(",", history)),
                            Field("IsFaulted", Boolean(diagnostics.IsFaulted)),
                            Field("IsStarted", Boolean(diagnostics.IsStarted)),
                            Field(
                                "PreviousProcedureId",
                                diagnostics.PreviousProcedureId),
                            Field(
                                "RegisteredProcedureIds",
                                string.Join(
                                    ",",
                                    diagnostics.RegisteredProcedureIds)))
                    });
            }
            catch (Exception exception)
            {
                return Faulted(
                    DiagnosticsPageKind.Procedure,
                    available,
                    exception);
            }
        }

        private static bool TryResolve<T>(
            FrameworkRuntime runtime,
            out T service)
        {
            if (runtime == null)
            {
                service = default;
                return false;
            }

            return runtime.Services.TryResolve(out service);
        }

        private static DiagnosticsPageSnapshot Available(
            DiagnosticsPageKind kind,
            IReadOnlyList<DiagnosticsEntrySnapshot> entries)
        {
            return new DiagnosticsPageSnapshot(
                kind,
                true,
                null,
                entries);
        }

        private static DiagnosticsPageSnapshot Unavailable(
            DiagnosticsPageKind kind)
        {
            return new DiagnosticsPageSnapshot(
                kind,
                false,
                null,
                EmptyEntries);
        }

        private static DiagnosticsPageSnapshot Faulted(
            DiagnosticsPageKind kind,
            bool available,
            Exception exception)
        {
            return new DiagnosticsPageSnapshot(
                kind,
                available,
                ExceptionText(exception),
                EmptyEntries);
        }

        private static DiagnosticsEntrySnapshot Entry(
            string id,
            params DiagnosticsFieldSnapshot[] fields)
        {
            return new DiagnosticsEntrySnapshot(id, fields);
        }

        private static DiagnosticsEntrySnapshot PoolEntry(
            string id,
            PoolDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                return Entry(id, Field("Unavailable", Boolean(true)));
            }

            return Entry(
                id,
                Field("ActiveCount", Number(diagnostics.ActiveCount)),
                Field("HitRate", Decimal(diagnostics.HitRate)),
                Field("IdleCount", Number(diagnostics.IdleCount)),
                Field("PeakActiveCount", Number(diagnostics.PeakActiveCount)),
                Field(
                    "TotalCreatedCount",
                    Number(diagnostics.TotalCreatedCount)));
        }

        private static DiagnosticsFieldSnapshot Field(
            string name,
            string value)
        {
            return new DiagnosticsFieldSnapshot(name, value);
        }

        private static string TypeName(Type type)
        {
            return type?.FullName ?? string.Empty;
        }

        private static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Decimal(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Boolean(bool value)
        {
            return value ? "True" : "False";
        }

        private static string Milliseconds(TimeSpan value)
        {
            return value.TotalMilliseconds.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
        }

        private static string Timestamp(DateTime value)
        {
            return value.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture);
        }

        private static string Timestamp(DateTime? value)
        {
            return value.HasValue
                ? Timestamp(value.Value)
                : string.Empty;
        }

        private static string ExceptionText(Exception exception)
        {
            if (exception == null)
            {
                return null;
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
                // A diagnostics formatter must never suppress the other pages.
            }

            try
            {
                return $"{exception.GetType().FullName}: " +
                       "exception text unavailable.";
            }
            catch
            {
                return "Diagnostics exception text unavailable.";
            }
        }
    }
}
