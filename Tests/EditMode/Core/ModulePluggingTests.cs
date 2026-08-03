using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class ModulePluggingTests
    {
        [UnityTest]
        public IEnumerator UnloadThenInstall_ConsumerResolvesCurrentProviderService()
        {
            var runtime = new FrameworkRuntime();
            var originalService = new PlugService("original");
            var originalProvider = new ProviderModule(originalService);
            var originalConsumer = new ConsumerModule();
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(originalConsumer, 1),
                        Descriptor(originalProvider, 0)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            Assert.That(originalConsumer.ResolvedService, Is.SameAs(originalService));

            var unloadTask = runtime
                .UnloadAsync(
                    originalProvider.Id,
                    ModuleUnloadMode.Cascade,
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(unloadTask);
            var unloadResult = unloadTask.GetAwaiter().GetResult();

            CollectionAssert.AreEqual(
                new[] { originalConsumer.Id, originalProvider.Id },
                unloadResult.UnloadedModuleIds);
            Assert.That(originalService.DisposeCount, Is.EqualTo(1));
            Assert.That(runtime.Modules, Is.Empty);

            var currentService = new PlugService("current");
            var currentProvider = new ProviderModule(currentService);
            var currentConsumer = new ConsumerModule();
            var installProviderTask = runtime
                .InstallAsync(
                    Descriptor(currentProvider, 0),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installProviderTask);
            installProviderTask.GetAwaiter().GetResult();
            var installConsumerTask = runtime
                .InstallAsync(
                    Descriptor(currentConsumer, 1),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installConsumerTask);
            installConsumerTask.GetAwaiter().GetResult();

            Assert.That(currentConsumer.ResolvedService, Is.SameAs(currentService));
            Assert.That(currentConsumer.ResolvedService, Is.Not.SameAs(originalService));
            CollectionAssert.AreEqual(
                new[] { currentProvider.Id, currentConsumer.Id },
                ModuleIds(runtime.Modules));

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();
            Assert.That(currentService.DisposeCount, Is.EqualTo(1));
        }

        private static ModuleDescriptor Descriptor(
            IFrameworkModule module,
            int stableOrder)
        {
            return new ModuleDescriptor(
                module.Id,
                module.Dependencies,
                stableOrder,
                () => module);
        }

        private static IReadOnlyList<string> ModuleIds(
            IReadOnlyList<ModuleRecord> records)
        {
            var ids = new string[records.Count];
            for (var index = 0; index < records.Count; index++)
            {
                ids[index] = records[index].Descriptor.Id;
            }

            return Array.AsReadOnly(ids);
        }

        private static IEnumerator WaitFor(Task task)
        {
            for (var frame = 0; frame < 120 && !task.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, "Async operation timed out.");
        }

        private interface IPlugService
        {
            string Generation { get; }
        }

        private sealed class PlugService : IPlugService, IDisposable
        {
            public PlugService(string generation)
            {
                Generation = generation;
            }

            public string Generation { get; }

            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
            }
        }

        private sealed class ProviderModule : IFrameworkModule
        {
            private readonly IPlugService _service;

            public ProviderModule(IPlugService service)
            {
                _service = service;
            }

            public string Id => "Provider";

            public IReadOnlyCollection<string> Dependencies =>
                Array.Empty<string>();

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                context.ModuleScope.RegisterInstance(_service);
                return default;
            }

            public ValueTask StartAsync(CancellationToken token)
            {
                return default;
            }

            public ValueTask StopAsync(CancellationToken token)
            {
                return default;
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class ConsumerModule : IFrameworkModule
        {
            public string Id => "Consumer";

            public IReadOnlyCollection<string> Dependencies =>
                new[] { "Provider" };

            public IPlugService ResolvedService { get; private set; }

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                ResolvedService = context.Services.Resolve<IPlugService>();
                return default;
            }

            public ValueTask StartAsync(CancellationToken token)
            {
                return default;
            }

            public ValueTask StopAsync(CancellationToken token)
            {
                return default;
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }
    }
}
