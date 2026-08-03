using System;
using System.Collections.Generic;

namespace ArkFramework.Editor
{
    public enum FrameworkEditorIssueSeverity
    {
        Warning,
        Error
    }

    public static class FrameworkEditorIssueCodes
    {
        public const string NullInstaller = nameof(NullInstaller);
        public const string InvalidModuleId = nameof(InvalidModuleId);
        public const string DuplicateModuleId = nameof(DuplicateModuleId);
        public const string NullDependencies = nameof(NullDependencies);
        public const string InvalidDependencyId = nameof(InvalidDependencyId);
        public const string SelfDependency = nameof(SelfDependency);
        public const string DuplicateDependency = nameof(DuplicateDependency);
        public const string MissingDependency = nameof(MissingDependency);
        public const string DependencyCycle = nameof(DependencyCycle);
        public const string NullServiceTypes = nameof(NullServiceTypes);
        public const string NullServiceType = nameof(NullServiceType);
        public const string DuplicateServiceDeclaration =
            nameof(DuplicateServiceDeclaration);
        public const string MetadataAccessFailed = nameof(MetadataAccessFailed);
    }

    public sealed class FrameworkEditorIssue
    {
        internal FrameworkEditorIssue(
            string code,
            FrameworkEditorIssueSeverity severity,
            string message,
            int? installerIndex,
            string moduleId)
        {
            Code = code;
            Severity = severity;
            Message = message;
            InstallerIndex = installerIndex;
            ModuleId = moduleId;
        }

        public string Code { get; }

        public FrameworkEditorIssueSeverity Severity { get; }

        public string Message { get; }

        public int? InstallerIndex { get; }

        public string ModuleId { get; }
    }

    public sealed class FrameworkEditorValidationResult
    {
        internal FrameworkEditorValidationResult(
            FrameworkEditorIssue[] issues,
            string[] startupOrder)
        {
            Issues = Array.AsReadOnly(issues);
            StartupOrder = Array.AsReadOnly(startupOrder);
        }

        public IReadOnlyList<FrameworkEditorIssue> Issues { get; }

        public IReadOnlyList<string> StartupOrder { get; }

        public bool IsValid => Issues.Count == 0;
    }

    public static class FrameworkEditorValidation
    {
        private static readonly string[] EmptyStartupOrder = Array.Empty<string>();

        public static FrameworkEditorValidationResult Validate(
            FrameworkProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            var issues = new List<FrameworkEditorIssue>();
            var installers = profile.Installers;
            var snapshots = new InstallerSnapshot[installers.Count];
            for (var index = 0; index < installers.Count; index++)
            {
                snapshots[index] = CaptureInstaller(
                    installers[index],
                    index,
                    issues);
            }

            var idCounts = ValidateModuleIds(snapshots, issues);
            ValidateDependencies(snapshots, idCounts, issues);
            ValidateServiceDeclarations(snapshots, issues);
            ValidateCycles(snapshots, idCounts, issues);

            var startupOrder = issues.Count == 0
                ? ComputeStartupOrder(snapshots)
                : EmptyStartupOrder;
            return new FrameworkEditorValidationResult(
                issues.ToArray(),
                startupOrder);
        }

        private static InstallerSnapshot CaptureInstaller(
            ModuleInstaller installer,
            int index,
            ICollection<FrameworkEditorIssue> issues)
        {
            var snapshot = new InstallerSnapshot(index, installer);
            if (installer == null)
            {
                AddError(
                    issues,
                    FrameworkEditorIssueCodes.NullInstaller,
                    $"Installer at index {index} is null.",
                    index,
                    null);
                return snapshot;
            }

            try
            {
                snapshot.ModuleId = installer.ModuleId;
                snapshot.HasModuleId = true;
            }
            catch (UnityEngine.ExitGUIException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddMetadataFailure(
                    issues,
                    index,
                    null,
                    nameof(ModuleInstaller.ModuleId),
                    exception);
            }

            try
            {
                var dependencies = installer.Dependencies;
                if (dependencies == null)
                {
                    AddError(
                        issues,
                        FrameworkEditorIssueCodes.NullDependencies,
                        $"Module '{DisplayId(snapshot.ModuleId)}' has a null " +
                        "dependencies collection.",
                        index,
                        snapshot.ModuleId);
                }
                else
                {
                    snapshot.Dependencies = CopyCollection(dependencies);
                    snapshot.HasDependencies = true;
                }
            }
            catch (UnityEngine.ExitGUIException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddMetadataFailure(
                    issues,
                    index,
                    snapshot.ModuleId,
                    nameof(ModuleInstaller.Dependencies),
                    exception);
            }

            try
            {
                var serviceTypes = installer.ServiceTypes;
                if (serviceTypes == null)
                {
                    AddError(
                        issues,
                        FrameworkEditorIssueCodes.NullServiceTypes,
                        $"Module '{DisplayId(snapshot.ModuleId)}' has a null " +
                        "service types collection.",
                        index,
                        snapshot.ModuleId);
                }
                else
                {
                    snapshot.ServiceTypes = CopyCollection(serviceTypes);
                    snapshot.HasServiceTypes = true;
                }
            }
            catch (UnityEngine.ExitGUIException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddMetadataFailure(
                    issues,
                    index,
                    snapshot.ModuleId,
                    nameof(ModuleInstaller.ServiceTypes),
                    exception);
            }

            return snapshot;
        }

        private static Dictionary<string, int> ValidateModuleIds(
            IReadOnlyList<InstallerSnapshot> snapshots,
            ICollection<FrameworkEditorIssue> issues)
        {
            var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var firstIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                if (snapshot.Installer == null || !snapshot.HasModuleId)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(snapshot.ModuleId))
                {
                    AddError(
                        issues,
                        FrameworkEditorIssueCodes.InvalidModuleId,
                        $"Installer at index {snapshot.Index} has a null, empty, " +
                        "or whitespace module ID.",
                        snapshot.Index,
                        snapshot.ModuleId);
                    continue;
                }

                if (!idCounts.TryGetValue(snapshot.ModuleId, out var count))
                {
                    idCounts.Add(snapshot.ModuleId, 1);
                    firstIndices.Add(snapshot.ModuleId, snapshot.Index);
                    continue;
                }

                idCounts[snapshot.ModuleId] = count + 1;
                AddError(
                    issues,
                    FrameworkEditorIssueCodes.DuplicateModuleId,
                    $"Module ID '{snapshot.ModuleId}' at installer index " +
                    $"{snapshot.Index} duplicates index " +
                    $"{firstIndices[snapshot.ModuleId]}.",
                    snapshot.Index,
                    snapshot.ModuleId);
            }

            return idCounts;
        }

        private static void ValidateDependencies(
            IReadOnlyList<InstallerSnapshot> snapshots,
            IReadOnlyDictionary<string, int> idCounts,
            ICollection<FrameworkEditorIssue> issues)
        {
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                if (!snapshot.HasDependencies)
                {
                    continue;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var dependencyIndex = 0;
                    dependencyIndex < snapshot.Dependencies.Length;
                    dependencyIndex++)
                {
                    var dependencyId = snapshot.Dependencies[dependencyIndex];
                    if (string.IsNullOrWhiteSpace(dependencyId))
                    {
                        AddError(
                            issues,
                            FrameworkEditorIssueCodes.InvalidDependencyId,
                            $"Module '{DisplayId(snapshot.ModuleId)}' has a null, " +
                            "empty, or whitespace dependency ID at position " +
                            $"{dependencyIndex}.",
                            snapshot.Index,
                            snapshot.ModuleId);
                        continue;
                    }

                    if (snapshot.HasModuleId &&
                        string.Equals(
                            snapshot.ModuleId,
                            dependencyId,
                            StringComparison.Ordinal))
                    {
                        AddError(
                            issues,
                            FrameworkEditorIssueCodes.SelfDependency,
                            $"Module '{snapshot.ModuleId}' cannot depend on itself.",
                            snapshot.Index,
                            snapshot.ModuleId);
                    }

                    if (!seen.Add(dependencyId))
                    {
                        AddError(
                            issues,
                            FrameworkEditorIssueCodes.DuplicateDependency,
                            $"Module '{DisplayId(snapshot.ModuleId)}' declares " +
                            $"dependency '{dependencyId}' more than once.",
                            snapshot.Index,
                            snapshot.ModuleId);
                        continue;
                    }

                    if (!idCounts.ContainsKey(dependencyId))
                    {
                        AddError(
                            issues,
                            FrameworkEditorIssueCodes.MissingDependency,
                            $"Module '{DisplayId(snapshot.ModuleId)}' depends on " +
                            $"missing module '{dependencyId}'.",
                            snapshot.Index,
                            snapshot.ModuleId);
                    }
                }
            }
        }

        private static void ValidateServiceDeclarations(
            IReadOnlyList<InstallerSnapshot> snapshots,
            ICollection<FrameworkEditorIssue> issues)
        {
            var owners = new Dictionary<Type, InstallerSnapshot>();
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                if (!snapshot.HasServiceTypes)
                {
                    continue;
                }

                for (var serviceIndex = 0;
                    serviceIndex < snapshot.ServiceTypes.Length;
                    serviceIndex++)
                {
                    var serviceType = snapshot.ServiceTypes[serviceIndex];
                    if (serviceType == null)
                    {
                        AddError(
                            issues,
                            FrameworkEditorIssueCodes.NullServiceType,
                            $"Module '{DisplayId(snapshot.ModuleId)}' declares a " +
                            $"null service type at position {serviceIndex}.",
                            snapshot.Index,
                            snapshot.ModuleId);
                        continue;
                    }

                    if (!owners.TryGetValue(serviceType, out var owner))
                    {
                        owners.Add(serviceType, snapshot);
                        continue;
                    }

                    AddError(
                        issues,
                        FrameworkEditorIssueCodes.DuplicateServiceDeclaration,
                        $"Service '{serviceType.FullName}' declared by module " +
                        $"'{DisplayId(snapshot.ModuleId)}' at index {snapshot.Index} " +
                        $"duplicates module '{DisplayId(owner.ModuleId)}' at index " +
                        $"{owner.Index}.",
                        snapshot.Index,
                        snapshot.ModuleId);
                }
            }
        }

        private static void ValidateCycles(
            IReadOnlyList<InstallerSnapshot> snapshots,
            IReadOnlyDictionary<string, int> idCounts,
            ICollection<FrameworkEditorIssue> issues)
        {
            var candidates =
                new Dictionary<string, InstallerSnapshot>(StringComparer.Ordinal);
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                if (!snapshot.HasModuleId ||
                    !snapshot.HasDependencies ||
                    string.IsNullOrWhiteSpace(snapshot.ModuleId) ||
                    !idCounts.TryGetValue(snapshot.ModuleId, out var count) ||
                    count != 1)
                {
                    continue;
                }

                candidates.Add(snapshot.ModuleId, snapshot);
            }

            var visitStates = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new List<string>();
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                if (string.IsNullOrWhiteSpace(snapshot.ModuleId) ||
                    !candidates.ContainsKey(snapshot.ModuleId) ||
                    visitStates.TryGetValue(snapshot.ModuleId, out var state) &&
                    state != 0)
                {
                    continue;
                }

                if (!TryFindCycle(
                        snapshot.ModuleId,
                        candidates,
                        visitStates,
                        stack,
                        out var path))
                {
                    continue;
                }

                AddError(
                    issues,
                    FrameworkEditorIssueCodes.DependencyCycle,
                    $"Circular module dependency detected: " +
                    $"{string.Join(" -> ", path)}.",
                    snapshot.Index,
                    snapshot.ModuleId);
                return;
            }
        }

        private static bool TryFindCycle(
            string moduleId,
            IReadOnlyDictionary<string, InstallerSnapshot> candidates,
            IDictionary<string, int> visitStates,
            IList<string> stack,
            out string[] path)
        {
            visitStates[moduleId] = 1;
            stack.Add(moduleId);
            var snapshot = candidates[moduleId];
            for (var index = 0; index < snapshot.Dependencies.Length; index++)
            {
                var dependencyId = snapshot.Dependencies[index];
                if (string.IsNullOrWhiteSpace(dependencyId) ||
                    !candidates.ContainsKey(dependencyId) ||
                    string.Equals(moduleId, dependencyId, StringComparison.Ordinal))
                {
                    continue;
                }

                visitStates.TryGetValue(dependencyId, out var state);
                if (state == 0)
                {
                    if (TryFindCycle(
                            dependencyId,
                            candidates,
                            visitStates,
                            stack,
                            out path))
                    {
                        return true;
                    }
                }
                else if (state == 1)
                {
                    var cycleStart = stack.IndexOf(dependencyId);
                    path = new string[stack.Count - cycleStart + 1];
                    for (var pathIndex = cycleStart;
                        pathIndex < stack.Count;
                        pathIndex++)
                    {
                        path[pathIndex - cycleStart] = stack[pathIndex];
                    }

                    path[path.Length - 1] = dependencyId;
                    return true;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visitStates[moduleId] = 2;
            path = null;
            return false;
        }

        private static string[] ComputeStartupOrder(
            IReadOnlyList<InstallerSnapshot> snapshots)
        {
            var descriptors = new ModuleDescriptor[snapshots.Count];
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                descriptors[index] = new ModuleDescriptor(
                    snapshot.ModuleId,
                    snapshot.Dependencies,
                    snapshot.Index,
                    () => null);
            }

            var sorted = ModuleGraph.Sort(descriptors);
            var startupOrder = new string[sorted.Count];
            for (var index = 0; index < sorted.Count; index++)
            {
                startupOrder[index] = sorted[index].Id;
            }

            return startupOrder;
        }

        private static T[] CopyCollection<T>(IReadOnlyCollection<T> source)
        {
            var values = new List<T>(source.Count);
            foreach (var value in source)
            {
                values.Add(value);
            }

            return values.ToArray();
        }

        private static void AddMetadataFailure(
            ICollection<FrameworkEditorIssue> issues,
            int installerIndex,
            string moduleId,
            string metadataName,
            Exception exception)
        {
            AddError(
                issues,
                FrameworkEditorIssueCodes.MetadataAccessFailed,
                $"{metadataName} getter failed for installer at index " +
                $"{installerIndex}: {exception.GetType().Name}: " +
                $"{exception.Message}",
                installerIndex,
                moduleId);
        }

        private static void AddError(
            ICollection<FrameworkEditorIssue> issues,
            string code,
            string message,
            int? installerIndex,
            string moduleId)
        {
            issues.Add(
                new FrameworkEditorIssue(
                    code,
                    FrameworkEditorIssueSeverity.Error,
                    message,
                    installerIndex,
                    moduleId));
        }

        private static string DisplayId(string moduleId)
        {
            return string.IsNullOrWhiteSpace(moduleId) ? "<invalid>" : moduleId;
        }

        private sealed class InstallerSnapshot
        {
            public InstallerSnapshot(int index, ModuleInstaller installer)
            {
                Index = index;
                Installer = installer;
                Dependencies = Array.Empty<string>();
                ServiceTypes = Array.Empty<Type>();
            }

            public int Index { get; }

            public ModuleInstaller Installer { get; }

            public string ModuleId { get; set; }

            public bool HasModuleId { get; set; }

            public string[] Dependencies { get; set; }

            public bool HasDependencies { get; set; }

            public Type[] ServiceTypes { get; set; }

            public bool HasServiceTypes { get; set; }
        }
    }
}
