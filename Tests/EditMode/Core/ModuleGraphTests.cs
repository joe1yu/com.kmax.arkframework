using System;
using System.Linq;
using NUnit.Framework;

namespace ArkFramework.Tests
{
    public sealed class ModuleGraphTests
    {
        [Test]
        public void Sort_ReturnsStableTopologicalOrder()
        {
            var descriptors = new[]
            {
                Descriptor("Resource", 20, "Core"),
                Descriptor("UI", 30, "Resource"),
                Descriptor("Core", 10),
                Descriptor("Pool", 20, "Core")
            };

            CollectionAssert.AreEqual(
                new[] { "Core", "Resource", "Pool", "UI" },
                ModuleGraph.Sort(descriptors).Select(descriptor => descriptor.Id));
        }

        [Test]
        public void Reverse_ReturnsStableReverseTopologicalOrder()
        {
            var descriptors = new[]
            {
                Descriptor("Resource", 20, "Core"),
                Descriptor("UI", 30, "Resource"),
                Descriptor("Core", 10),
                Descriptor("Pool", 20, "Core")
            };

            CollectionAssert.AreEqual(
                new[] { "UI", "Pool", "Resource", "Core" },
                ModuleGraph.Reverse(descriptors).Select(descriptor => descriptor.Id));
        }

        [Test]
        public void Sort_RejectsMissingDependencyWithIds()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => ModuleGraph.Sort(new[]
                {
                    Descriptor("UI", 0, "Resource")
                }));

            StringAssert.Contains("UI", exception.Message);
            StringAssert.Contains("Resource", exception.Message);
        }

        [Test]
        public void Sort_RejectsDuplicateModuleId()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => ModuleGraph.Sort(new[]
                {
                    Descriptor("Core", 0),
                    Descriptor("Core", 1)
                }));

            StringAssert.Contains("Core", exception.Message);
        }

        [Test]
        public void Sort_RejectsCycleWithReadablePath()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => ModuleGraph.Sort(new[]
                {
                    Descriptor("A", 0, "B"),
                    Descriptor("B", 0, "C"),
                    Descriptor("C", 0, "A")
                }));

            StringAssert.Contains("A -> B -> C -> A", exception.Message);
        }

        [Test]
        public void Descriptor_RejectsDuplicateDependency()
        {
            var exception = Assert.Throws<ArgumentException>(
                () => Descriptor("UI", 0, "Core", "Core"));

            StringAssert.Contains("UI", exception.Message);
            StringAssert.Contains("Core", exception.Message);
        }

        private static ModuleDescriptor Descriptor(
            string id,
            int stableOrder,
            params string[] dependencies)
        {
            return new ModuleDescriptor(id, dependencies, stableOrder, () => null);
        }
    }
}
