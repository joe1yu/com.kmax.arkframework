using System;
using System.Collections.Generic;

namespace ArkFramework
{
    public sealed class ModuleUnloadResult
    {
        public ModuleUnloadResult(IReadOnlyList<string> unloadedModuleIds)
        {
            if (unloadedModuleIds == null)
            {
                throw new ArgumentNullException(nameof(unloadedModuleIds));
            }

            var snapshot = new string[unloadedModuleIds.Count];
            var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < unloadedModuleIds.Count; index++)
            {
                var moduleId = unloadedModuleIds[index];
                if (string.IsNullOrWhiteSpace(moduleId))
                {
                    throw new ArgumentException(
                        "An unloaded module ID cannot be null, empty, or whitespace.",
                        nameof(unloadedModuleIds));
                }

                if (!uniqueIds.Add(moduleId))
                {
                    throw new ArgumentException(
                        $"Duplicate unloaded module ID '{moduleId}'.",
                        nameof(unloadedModuleIds));
                }

                snapshot[index] = moduleId;
            }

            UnloadedModuleIds = Array.AsReadOnly(snapshot);
        }

        public IReadOnlyList<string> UnloadedModuleIds { get; }
    }
}
