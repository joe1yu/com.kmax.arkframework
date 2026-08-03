using System;
using System.Collections.Generic;

namespace ArkFramework
{
    public sealed class ModuleDescriptor
    {
        public ModuleDescriptor(
            string id,
            IReadOnlyCollection<string> dependencies,
            int stableOrder,
            Func<IFrameworkModule> factory)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A module ID cannot be null, empty, or whitespace.",
                    nameof(id));
            }

            if (dependencies == null)
            {
                throw new ArgumentNullException(nameof(dependencies));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            var dependencySnapshot = new string[dependencies.Count];
            var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var dependency in dependencies)
            {
                if (string.IsNullOrWhiteSpace(dependency))
                {
                    throw new ArgumentException(
                        $"Module '{id}' has a null, empty, or whitespace dependency ID.",
                        nameof(dependencies));
                }

                if (string.Equals(id, dependency, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Module '{id}' cannot depend on itself.",
                        nameof(dependencies));
                }

                if (!dependencyIds.Add(dependency))
                {
                    throw new ArgumentException(
                        $"Module '{id}' contains duplicate dependency '{dependency}'.",
                        nameof(dependencies));
                }

                dependencySnapshot[index++] = dependency;
            }

            Id = id;
            Dependencies = Array.AsReadOnly(dependencySnapshot);
            StableOrder = stableOrder;
            Factory = factory;
        }

        public string Id { get; }

        public IReadOnlyCollection<string> Dependencies { get; }

        public int StableOrder { get; }

        public Func<IFrameworkModule> Factory { get; }
    }
}
