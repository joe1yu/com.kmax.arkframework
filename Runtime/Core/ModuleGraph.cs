using System;
using System.Collections.Generic;

namespace ArkFramework
{
    public static class ModuleGraph
    {
        public static IReadOnlyList<ModuleDescriptor> Sort(
            IReadOnlyList<ModuleDescriptor> descriptors)
        {
            if (descriptors == null)
            {
                throw new ArgumentNullException(nameof(descriptors));
            }

            var count = descriptors.Count;
            var indicesById = new Dictionary<string, int>(count, StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var descriptor = descriptors[index];
                if (descriptor == null)
                {
                    throw new ArgumentException(
                        $"Module descriptor at index {index} is null.",
                        nameof(descriptors));
                }

                if (indicesById.ContainsKey(descriptor.Id))
                {
                    throw new InvalidOperationException(
                        $"Duplicate module ID '{descriptor.Id}'.");
                }

                indicesById.Add(descriptor.Id, index);
            }

            var incomingCounts = new int[count];
            var dependents = new List<int>[count];
            for (var index = 0; index < count; index++)
            {
                dependents[index] = new List<int>();
            }

            for (var moduleIndex = 0; moduleIndex < count; moduleIndex++)
            {
                foreach (var dependencyId in descriptors[moduleIndex].Dependencies)
                {
                    if (!indicesById.TryGetValue(dependencyId, out var dependencyIndex))
                    {
                        throw new InvalidOperationException(
                            $"Module '{descriptors[moduleIndex].Id}' depends on missing module " +
                            $"'{dependencyId}'.");
                    }

                    incomingCounts[moduleIndex]++;
                    dependents[dependencyIndex].Add(moduleIndex);
                }
            }

            var available = new List<int>();
            for (var index = 0; index < count; index++)
            {
                if (incomingCounts[index] == 0)
                {
                    available.Add(index);
                }
            }

            var sorted = new List<ModuleDescriptor>(count);
            while (available.Count != 0)
            {
                var selectedPosition = FindStableMinimum(available, descriptors);
                var selectedIndex = available[selectedPosition];
                available.RemoveAt(selectedPosition);
                sorted.Add(descriptors[selectedIndex]);

                var selectedDependents = dependents[selectedIndex];
                for (var index = 0; index < selectedDependents.Count; index++)
                {
                    var dependentIndex = selectedDependents[index];
                    incomingCounts[dependentIndex]--;
                    if (incomingCounts[dependentIndex] == 0)
                    {
                        available.Add(dependentIndex);
                    }
                }
            }

            if (sorted.Count != count)
            {
                var path = FindCyclePath(descriptors, indicesById, incomingCounts);
                throw new InvalidOperationException(
                    $"Circular module dependency detected: {string.Join(" -> ", path)}.");
            }

            return sorted.AsReadOnly();
        }

        public static IReadOnlyList<ModuleDescriptor> Reverse(
            IReadOnlyList<ModuleDescriptor> descriptors)
        {
            var sorted = Sort(descriptors);
            var reversed = new ModuleDescriptor[sorted.Count];
            for (var index = 0; index < sorted.Count; index++)
            {
                reversed[index] = sorted[sorted.Count - index - 1];
            }

            return Array.AsReadOnly(reversed);
        }

        private static int FindStableMinimum(
            IReadOnlyList<int> available,
            IReadOnlyList<ModuleDescriptor> descriptors)
        {
            var selectedPosition = 0;
            for (var position = 1; position < available.Count; position++)
            {
                var candidateIndex = available[position];
                var selectedIndex = available[selectedPosition];
                if (descriptors[candidateIndex].StableOrder <
                    descriptors[selectedIndex].StableOrder ||
                    descriptors[candidateIndex].StableOrder ==
                    descriptors[selectedIndex].StableOrder &&
                    candidateIndex < selectedIndex)
                {
                    selectedPosition = position;
                }
            }

            return selectedPosition;
        }

        private static IReadOnlyList<string> FindCyclePath(
            IReadOnlyList<ModuleDescriptor> descriptors,
            IReadOnlyDictionary<string, int> indicesById,
            IReadOnlyList<int> incomingCounts)
        {
            var visitStates = new int[descriptors.Count];
            var stack = new List<int>();
            for (var index = 0; index < descriptors.Count; index++)
            {
                if (incomingCounts[index] != 0 &&
                    visitStates[index] == 0 &&
                    TryFindCycle(
                        index,
                        descriptors,
                        indicesById,
                        incomingCounts,
                        visitStates,
                        stack,
                        out var cycle))
                {
                    return cycle;
                }
            }

            throw new InvalidOperationException(
                "A module dependency cycle exists, but its path could not be determined.");
        }

        private static bool TryFindCycle(
            int moduleIndex,
            IReadOnlyList<ModuleDescriptor> descriptors,
            IReadOnlyDictionary<string, int> indicesById,
            IReadOnlyList<int> incomingCounts,
            IList<int> visitStates,
            IList<int> stack,
            out IReadOnlyList<string> cycle)
        {
            visitStates[moduleIndex] = 1;
            stack.Add(moduleIndex);

            foreach (var dependencyId in descriptors[moduleIndex].Dependencies)
            {
                var dependencyIndex = indicesById[dependencyId];
                if (incomingCounts[dependencyIndex] == 0)
                {
                    continue;
                }

                if (visitStates[dependencyIndex] == 0)
                {
                    if (TryFindCycle(
                            dependencyIndex,
                            descriptors,
                            indicesById,
                            incomingCounts,
                            visitStates,
                            stack,
                            out cycle))
                    {
                        return true;
                    }
                }
                else if (visitStates[dependencyIndex] == 1)
                {
                    var cycleStart = stack.IndexOf(dependencyIndex);
                    var path = new string[stack.Count - cycleStart + 1];
                    for (var index = cycleStart; index < stack.Count; index++)
                    {
                        path[index - cycleStart] = descriptors[stack[index]].Id;
                    }

                    path[path.Length - 1] = descriptors[dependencyIndex].Id;
                    cycle = path;
                    return true;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visitStates[moduleIndex] = 2;
            cycle = null;
            return false;
        }
    }
}
