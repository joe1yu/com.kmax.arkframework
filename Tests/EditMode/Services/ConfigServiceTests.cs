using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class ConfigServiceTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private readonly List<ResourceService> _resources =
            new List<ResourceService>();
        private readonly List<ConfigService> _services =
            new List<ConfigService>();

        [TearDown]
        public void TearDown()
        {
            for (var index = _services.Count - 1; index >= 0; index--)
            {
                Await(_services[index].StopAsync());
                Await(_services[index].DisposeAsync());
            }

            for (var index = _resources.Count - 1; index >= 0; index--)
            {
                Await(_resources[index].StopAsync());
                Await(_resources[index].DisposeAsync());
            }

            for (var index = _objects.Count - 1; index >= 0; index--)
            {
                if (_objects[index] != null)
                {
                    Object.DestroyImmediate(_objects[index]);
                }
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void ConfigKey_RejectsNullEmptyOrWhitespaceKey(string key)
        {
            Assert.Throws<ArgumentException>(
                () => new ConfigKey(typeof(GameplayConfig), key));
        }

        [Test]
        public void ConfigKey_RejectsNullTypeAndUsesOrdinalEquality()
        {
            Assert.Throws<ArgumentNullException>(
                () => new ConfigKey(null, "default"));

            var first = new ConfigKey(typeof(GameplayConfig), "default");
            var same = new ConfigKey(typeof(GameplayConfig), "default");
            var differentCase =
                new ConfigKey(typeof(GameplayConfig), "Default");

            Assert.That(first, Is.EqualTo(same));
            Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(first == same, Is.True);
            Assert.That(first != differentCase, Is.True);
        }

        [Test]
        public void ConfigEntry_RejectsInvalidValueSourceAndVersion()
        {
            var key = new ConfigKey(typeof(GameplayConfig), "default");

            Assert.Throws<ArgumentNullException>(
                () => new ConfigEntry(key, null, "Source", "1"));
            Assert.Throws<ArgumentException>(
                () => new ConfigEntry(key, "wrong", "Source", "1"));
            Assert.Throws<ArgumentException>(
                () => new ConfigEntry(
                    key,
                    new GameplayConfig(),
                    " ",
                    "1"));
            Assert.Throws<ArgumentNullException>(
                () => new ConfigEntry(
                    key,
                    new GameplayConfig(),
                    "Source",
                    null));
        }

        [Test]
        public void ProviderSnapshot_RejectsDuplicateKeysAndDisposeIsIdempotent()
        {
            var firstOwner = new TrackingOwner();
            var failingOwner = new TrackingOwner
            {
                DisposeException = new TestCleanupException()
            };
            var duplicateEntries = new[]
            {
                Entry("same", 1, "Source", "1", firstOwner),
                Entry("same", 2, "Source", "2", failingOwner)
            };

            Assert.Throws<ArgumentException>(
                () => new ConfigProviderSnapshot(
                    duplicateEntries,
                    new IDisposable[] { firstOwner, failingOwner }));
            firstOwner.Dispose();
            Assert.Throws<TestCleanupException>(() => failingOwner.Dispose());

            var validOwner = new TrackingOwner();
            var cleanupFailure = new TrackingOwner
            {
                DisposeException = new TestCleanupException()
            };
            var snapshot = new ConfigProviderSnapshot(
                new[] { Entry("valid", 1, "Source", "1", validOwner) },
                new IDisposable[] { validOwner, cleanupFailure });

            Assert.Throws<AggregateException>(() => snapshot.Dispose());
            snapshot.Dispose();

            Assert.That(cleanupFailure.DisposeCount, Is.EqualTo(1));
            Assert.That(validOwner.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Reload_CombinesRealProvidersWithJsonPriorityAndNestedData()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var scriptable = Track(CreateAsset(
                "default",
                "so-1",
                new GameplayConfig
                {
                    Damage = 3,
                    Multipliers = new Dictionary<string, int>
                    {
                        { "normal", 1 }
                    },
                    Nested = new NestedConfig { Label = "scriptable" }
                }));
            var scriptOperation =
                backend.EnqueueCompletedLabel<ScriptableObjectConfigAsset>(
                    scriptable);
            var manifestOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset(CreateManifest(
                    typeof(GameplayConfig),
                    "default",
                    "json-2",
                    "config/gameplay/default"))));
            var jsonOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset(
                    "{\"Damage\":9,\"Multipliers\":{\"normal\":2,\"boss\":5}," +
                    "\"Nested\":{\"Label\":\"json\"}}")));
            var events = new RecordingEventBus();
            var service = Track(new ConfigService(
                new IConfigProvider[]
                {
                    new ScriptableObjectConfigProvider(
                        resources,
                        "config-scriptable"),
                    new JsonConfigProvider(
                        resources,
                        new ResourceKey("config/manifest"))
                },
                events,
                new RecordingLogger()));

            Await(service.ReloadAsync());

            var value = service.Get<GameplayConfig>("default");
            Assert.That(value.Damage, Is.EqualTo(9));
            Assert.That(value.Multipliers["normal"], Is.EqualTo(2));
            Assert.That(value.Multipliers["boss"], Is.EqualTo(5));
            Assert.That(value.Nested.Label, Is.EqualTo("json"));
            var key = new ConfigKey(typeof(GameplayConfig), "default");
            Assert.That(
                service.Diagnostics.Entries[key].Source,
                Is.EqualTo(JsonConfigProvider.DefaultName));
            Assert.That(
                service.Diagnostics.Entries[key].Version,
                Is.EqualTo("json-2"));
            Assert.That(service.Diagnostics.LastSuccessfulReloadUtc, Is.Not.Null);
            Assert.That(resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(3));
            Assert.That(events.Changes.Count, Is.EqualTo(1));
            Assert.That(events.Changes[0].OldSource, Is.Null);
            Assert.That(events.Changes[0].NewSource, Is.EqualTo("Json"));

            Await(service.StopAsync());

            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(scriptOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(manifestOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(jsonOperation.UnderlyingReleaseCount, Is.EqualTo(1));
        }

        [Test]
        public void Reload_ValidationFailureKeepsOldSnapshotAndDiagnostics()
        {
            var oldOwner = new TrackingOwner();
            var candidateOwner = new TrackingOwner();
            var provider = new SequenceProvider("Provider");
            provider.Enqueue(Snapshot(
                Entry("default", 4, "Old", "1", oldOwner),
                oldOwner));
            provider.Enqueue(Snapshot(
                Entry("default", -1, "New", "2", candidateOwner),
                candidateOwner));
            var service = Track(CreateService(provider));

            Await(service.ReloadAsync());
            var previousDiagnostics = service.Diagnostics;
            service.RegisterValidator(
                new DelegateValidator<GameplayConfig>(
                    (_, value) =>
                    {
                        if (value.Damage < 0)
                        {
                            throw new TestValidationException();
                        }
                    }));

            Assert.Throws<TestValidationException>(
                () => Await(service.ReloadAsync()));

            Assert.That(
                service.Get<GameplayConfig>("default").Damage,
                Is.EqualTo(4));
            var key = new ConfigKey(typeof(GameplayConfig), "default");
            Assert.That(service.Diagnostics.Entries[key].Source, Is.EqualTo("Old"));
            Assert.That(
                service.Diagnostics.LastSuccessfulReloadUtc,
                Is.EqualTo(previousDiagnostics.LastSuccessfulReloadUtc));
            Assert.That(oldOwner.DisposeCount, Is.Zero);
            Assert.That(candidateOwner.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Reload_RunsMatchingValidatorsInRegistrationOrder()
        {
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(Entry("default", 4, "Json", "1")));
            var service = Track(CreateService(provider));
            var calls = new List<string>();
            service.RegisterValidator(
                new DelegateValidator<object>(
                    (_, __) => calls.Add("wrong-type")));
            service.RegisterValidator(
                new DelegateValidator<GameplayConfig>(
                    (_, __) => calls.Add("first")));
            service.RegisterValidator(
                new DelegateValidator<GameplayConfig>(
                    (_, __) => calls.Add("second")));

            Await(service.ReloadAsync());

            CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
        }

        [Test]
        public void Reload_ProviderFailureKeepsOldAndCleansCandidateSnapshots()
        {
            var first = new SequenceProvider("First");
            var second = new SequenceProvider("Second");
            var oldFirstOwner = new TrackingOwner();
            var oldSecondOwner = new TrackingOwner();
            var candidateOwner = new TrackingOwner();
            first.Enqueue(Snapshot(
                Entry("default", 1, "First", "1", oldFirstOwner),
                oldFirstOwner));
            second.Enqueue(Snapshot(
                Entry("other", 2, "Second", "1", oldSecondOwner),
                oldSecondOwner));
            first.Enqueue(Snapshot(
                Entry("default", 9, "First", "2", candidateOwner),
                candidateOwner));
            var primary = new TestLoadException();
            second.Enqueue(primary);
            var service = Track(CreateService(first, second));
            Await(service.ReloadAsync());

            var thrown = Assert.Throws<TestLoadException>(
                () => Await(service.ReloadAsync()));

            Assert.That(thrown, Is.SameAs(primary));
            Assert.That(
                service.Get<GameplayConfig>("default").Damage,
                Is.EqualTo(1));
            Assert.That(oldFirstOwner.DisposeCount, Is.Zero);
            Assert.That(oldSecondOwner.DisposeCount, Is.Zero);
            Assert.That(candidateOwner.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Reload_CandidateCleanupFailurePreservesPrimaryFailure()
        {
            var first = new SequenceProvider("First");
            var second = new SequenceProvider("Second");
            var cleanupFailure = new TestCleanupException();
            var owner = new TrackingOwner
            {
                DisposeException = cleanupFailure
            };
            first.Enqueue(Snapshot(
                Entry("candidate", 1, "First", "1", owner),
                owner));
            var primary = new TestLoadException();
            second.Enqueue(primary);
            var service = Track(CreateService(first, second));

            var aggregate = Assert.Throws<AggregateException>(
                () => Await(service.ReloadAsync()));

            Assert.That(aggregate.InnerExceptions[0], Is.SameAs(primary));
            Assert.That(aggregate.InnerExceptions, Does.Contain(cleanupFailure));
            Assert.That(owner.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void JsonProvider_InvalidTypeReleasesManifestLease()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var manifestOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset(
                    "{\"entries\":[{\"type\":\"Missing.Type, Missing\"," +
                    "\"key\":\"default\",\"version\":\"1\"," +
                    "\"address\":\"config/value\"}]}")));
            var provider = new JsonConfigProvider(
                resources,
                new ResourceKey("config/manifest"));

            Assert.Throws<InvalidOperationException>(
                () => Await(provider.LoadAsync()));

            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(manifestOperation.UnderlyingReleaseCount, Is.EqualTo(1));
        }

        [Test]
        public void JsonProvider_DuplicateKeyReleasesManifestAndPayloadLeases()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var typeName = typeof(GameplayConfig).AssemblyQualifiedName;
            var manifest = "{\"entries\":[" +
                "{\"type\":\"" + typeName + "\",\"key\":\"same\"," +
                "\"version\":\"1\",\"address\":\"config/one\"}," +
                "{\"type\":\"" + typeName + "\",\"key\":\"same\"," +
                "\"version\":\"2\",\"address\":\"config/two\"}]}";
            var manifestOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset(manifest)));
            var firstPayload = backend.EnqueueCompletedAsset(
                Track(new TextAsset("{\"Damage\":1}")));
            var secondPayload = backend.EnqueueCompletedAsset(
                Track(new TextAsset("{\"Damage\":2}")));
            var provider = new JsonConfigProvider(
                resources,
                new ResourceKey("config/manifest"));

            Assert.Throws<ArgumentException>(
                () => Await(provider.LoadAsync()));

            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(manifestOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(firstPayload.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(secondPayload.UnderlyingReleaseCount, Is.EqualTo(1));
        }

        [TestCase("")]
        [TestCase(" ")]
        public void JsonProvider_InvalidAddressReleasesManifestLease(string address)
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var manifestOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset(CreateManifest(
                    typeof(GameplayConfig),
                    "default",
                    "1",
                    address))));
            var provider = new JsonConfigProvider(
                resources,
                new ResourceKey("config/manifest"));

            Assert.Throws<ArgumentException>(
                () => Await(provider.LoadAsync()));

            Assert.That(manifestOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [Test]
        public void JsonProvider_NullPayloadReleasesEveryLease()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var manifestOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset(CreateManifest(
                    typeof(GameplayConfig),
                    "default",
                    "1",
                    "config/value"))));
            var payloadOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset("null")));
            var provider = new JsonConfigProvider(
                resources,
                new ResourceKey("config/manifest"));

            Assert.Throws<InvalidOperationException>(
                () => Await(provider.LoadAsync()));

            Assert.That(manifestOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(payloadOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [UnityTest]
        public IEnumerator JsonProvider_CancellationReleasesAcquiredLeases()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var typeName = typeof(GameplayConfig).AssemblyQualifiedName;
            var manifest = "{\"entries\":[" +
                "{\"type\":\"" + typeName + "\",\"key\":\"first\"," +
                "\"version\":\"1\",\"address\":\"config/first\"}," +
                "{\"type\":\"" + typeName + "\",\"key\":\"second\"," +
                "\"version\":\"1\",\"address\":\"config/second\"}]}";
            var manifestOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset(manifest)));
            var firstPayload = backend.EnqueueCompletedAsset(
                Track(new TextAsset("{\"Damage\":1}")));
            var secondPayload = backend.EnqueueAsset<TextAsset>();
            var provider = new JsonConfigProvider(
                resources,
                new ResourceKey("config/manifest"));
            var cancellation = new CancellationTokenSource();
            var load = provider.LoadAsync(cancellation.Token).AsTask();

            cancellation.Cancel();
            yield return WaitForTask(load);
            secondPayload.Succeed(Track(new TextAsset("{\"Damage\":2}")));
            yield return WaitUntilAsync(
                () => secondPayload.UnderlyingReleaseCount == 1);

            Assert.Throws<OperationCanceledException>(
                () => load.GetAwaiter().GetResult());
            Assert.That(manifestOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(firstPayload.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(secondPayload.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(
                resources.Diagnostics.InflightOperationCount,
                Is.Zero);
            cancellation.Dispose();
        }

        [Test]
        public void ScriptableProvider_InvalidAssetReleasesLabelLease()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var invalid = Track(CreateAsset(
                " ",
                "1",
                new GameplayConfig { Damage = 1 }));
            var valid = Track(CreateAsset(
                "valid",
                "1",
                new GameplayConfig { Damage = 2 }));
            var labelOperation =
                backend.EnqueueCompletedLabel<ScriptableObjectConfigAsset>(
                    invalid,
                    valid);
            var provider = new ScriptableObjectConfigProvider(
                resources,
                "config-scriptable");

            Assert.Throws<ArgumentException>(
                () => Await(provider.LoadAsync()));

            Assert.That(labelOperation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [Test]
        public void Reload_SuccessReleasesOldExactlyOnceAndKeepsNewActive()
        {
            var oldOwner = new TrackingOwner();
            var newOwner = new TrackingOwner();
            var provider = new SequenceProvider("Provider");
            provider.Enqueue(Snapshot(
                Entry("default", 1, "Old", "1", oldOwner),
                oldOwner));
            provider.Enqueue(Snapshot(
                Entry("default", 2, "New", "2", newOwner),
                newOwner));
            var service = Track(CreateService(provider));

            Await(service.ReloadAsync());
            Await(service.ReloadAsync());

            Assert.That(oldOwner.DisposeCount, Is.EqualTo(1));
            Assert.That(newOwner.DisposeCount, Is.Zero);
            Assert.That(
                service.Get<GameplayConfig>("default").Damage,
                Is.EqualTo(2));

            Await(service.StopAsync());
            Await(service.StopAsync());
            Assert.That(oldOwner.DisposeCount, Is.EqualTo(1));
            Assert.That(newOwner.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Reload_OldCleanupFailureDoesNotRollbackCommittedSnapshot()
        {
            var logger = new RecordingLogger();
            var oldOwner = new TrackingOwner
            {
                DisposeException = new TestCleanupException()
            };
            var newOwner = new TrackingOwner();
            var provider = new SequenceProvider("Provider");
            provider.Enqueue(Snapshot(
                Entry("default", 1, "Old", "1", oldOwner),
                oldOwner));
            provider.Enqueue(Snapshot(
                Entry("default", 2, "New", "2", newOwner),
                newOwner));
            var service = Track(new ConfigService(
                new[] { provider },
                new RecordingEventBus(),
                logger));

            Await(service.ReloadAsync());
            Assert.DoesNotThrow(() => Await(service.ReloadAsync()));

            Assert.That(
                service.Get<GameplayConfig>("default").Damage,
                Is.EqualTo(2));
            Assert.That(oldOwner.DisposeCount, Is.EqualTo(1));
            Assert.That(logger.Errors, Is.Not.Empty);
        }

        [Test]
        public void Reload_PublishesAddedDeletedAndOverriddenEntries()
        {
            var provider = new SequenceProvider("Provider");
            provider.Enqueue(Snapshot(
                Entry("kept", 1, "ScriptableObject", "1"),
                Entry("removed", 2, "ScriptableObject", "1")));
            provider.Enqueue(Snapshot(
                Entry("kept", 1, "Json", "2"),
                Entry("added", 3, "Json", "1")));
            var events = new RecordingEventBus();
            var service = Track(new ConfigService(
                new[] { provider },
                events,
                new RecordingLogger()));
            Await(service.ReloadAsync());
            events.Changes.Clear();

            Await(service.ReloadAsync());

            Assert.That(events.Changes.Count, Is.EqualTo(3));
            var kept = events.Changes.Single(value => value.Key == "kept");
            var removed =
                events.Changes.Single(value => value.Key == "removed");
            var added = events.Changes.Single(value => value.Key == "added");
            Assert.That(kept.OldSource, Is.EqualTo("ScriptableObject"));
            Assert.That(kept.NewSource, Is.EqualTo("Json"));
            Assert.That(kept.OldVersion, Is.EqualTo("1"));
            Assert.That(kept.NewVersion, Is.EqualTo("2"));
            Assert.That(removed.OldSource, Is.EqualTo("ScriptableObject"));
            Assert.That(removed.NewSource, Is.Null);
            Assert.That(added.OldSource, Is.Null);
            Assert.That(added.NewSource, Is.EqualTo("Json"));
        }

        [Test]
        public void Reload_EventBusFailureIsLoggedAndDoesNotFailCommit()
        {
            var eventBus = new RecordingEventBus
            {
                PublishException = new TestPublishException()
            };
            var logger = new RecordingLogger();
            var owner = new TrackingOwner();
            var service = Track(new ConfigService(
                new[]
                {
                    new SequenceProvider(
                        "Provider",
                        Snapshot(
                            Entry("default", 7, "Json", "1", owner),
                            owner))
                },
                eventBus,
                logger));

            Assert.DoesNotThrow(() => Await(service.ReloadAsync()));

            Assert.That(
                service.Get<GameplayConfig>("default").Damage,
                Is.EqualTo(7));
            Assert.That(owner.DisposeCount, Is.Zero);
            Assert.That(logger.Errors, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator Reload_EventHandlerCanSynchronouslyReload()
        {
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(Entry("default", 1, "First", "1")),
                Snapshot(Entry("default", 2, "Second", "2")));
            var eventBus = new RecordingEventBus();
            ConfigService service = null;
            var publishCount = 0;
            eventBus.OnPublish =
                _ =>
                {
                    if (Interlocked.Increment(ref publishCount) == 1)
                    {
                        Await(service.ReloadAsync());
                    }
                };
            service = new ConfigService(
                new[] { provider },
                eventBus,
                new RecordingLogger());
            var outerReload = Task.Run(() => Await(service.ReloadAsync()));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!outerReload.IsCompleted && DateTime.UtcNow < deadline)
            {
                yield return null;
            }

            Assert.That(
                outerReload.IsCompleted,
                Is.True,
                "Synchronous event-handler reload deadlocked.");
            outerReload.GetAwaiter().GetResult();
            Assert.That(
                service.Get<GameplayConfig>("default").Damage,
                Is.EqualTo(2));
            Await(service.StopAsync());
            Await(service.DisposeAsync());
        }

        [UnityTest]
        public IEnumerator Reload_EventHandlerCanSynchronouslyStop()
        {
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(Entry("default", 1, "Json", "1")));
            var eventBus = new RecordingEventBus();
            ConfigService service = null;
            eventBus.OnPublish = _ => Await(service.StopAsync());
            service = new ConfigService(
                new[] { provider },
                eventBus,
                new RecordingLogger());

            var reload = Task.Run(() => Await(service.ReloadAsync()));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!reload.IsCompleted && DateTime.UtcNow < deadline)
            {
                yield return null;
            }

            Assert.That(
                reload.IsCompleted,
                Is.True,
                "Synchronous event-handler Stop deadlocked with Reload.");
            reload.GetAwaiter().GetResult();
            Await(service.StopAsync());
            Assert.That(
                service.TryGet<GameplayConfig>("default", out _),
                Is.False);
            Await(service.DisposeAsync());
        }

        [UnityTest]
        public IEnumerator Reload_OldOwnerCanSynchronouslyStopDuringCleanup()
        {
            var oldOwner = new TrackingOwner();
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(
                    Entry("default", 1, "Old", "1", oldOwner),
                    oldOwner),
                Snapshot(Entry("default", 2, "New", "2")));
            var service = CreateService(provider);
            Await(service.ReloadAsync());
            oldOwner.OnDispose = () => Await(service.StopAsync());

            var reload = Task.Run(() => Await(service.ReloadAsync()));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!reload.IsCompleted && DateTime.UtcNow < deadline)
            {
                yield return null;
            }

            Assert.That(
                reload.IsCompleted,
                Is.True,
                "Old-owner cleanup Stop deadlocked with Reload.");
            reload.GetAwaiter().GetResult();
            Await(service.StopAsync());
            Assert.That(oldOwner.DisposeCount, Is.EqualTo(1));
            Assert.That(
                service.TryGet<GameplayConfig>("default", out _),
                Is.False);
            Await(service.DisposeAsync());
        }

        [Test]
        public void GetAndTryGet_ReportMissingAndMismatchedType()
        {
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(Entry("default", 1, "Json", "1")));
            var service = Track(CreateService(provider));

            Assert.That(
                service.TryGet<GameplayConfig>("default", out _),
                Is.False);
            var unloaded = Assert.Throws<KeyNotFoundException>(
                () => service.Get<GameplayConfig>("default"));
            Assert.That(
                unloaded.Message,
                Does.Contain(typeof(GameplayConfig).FullName));
            Assert.That(unloaded.Message, Does.Contain("default"));

            Await(service.ReloadAsync());

            Assert.That(
                service.TryGet<GameplayConfig>(
                    "default",
                    out var gameplay),
                Is.True);
            Assert.That(gameplay.Damage, Is.EqualTo(1));
            Assert.That(service.TryGet<string>("default", out _), Is.False);
            var mismatch = Assert.Throws<KeyNotFoundException>(
                () => service.Get<string>("default"));
            Assert.That(mismatch.Message, Does.Contain(typeof(string).FullName));
            Assert.That(mismatch.Message, Does.Contain("default"));
        }

        [UnityTest]
        public IEnumerator Reload_ConcurrentCallsAreSerialized()
        {
            var provider = new ControlledProvider();
            var firstOwner = new TrackingOwner();
            var secondOwner = new TrackingOwner();
            var service = Track(CreateService(provider));

            var firstReload = service.ReloadAsync().AsTask();
            Assert.That(provider.LoadCallCount, Is.EqualTo(1));
            var secondReload = service.ReloadAsync().AsTask();
            yield return null;
            Assert.That(provider.LoadCallCount, Is.EqualTo(1));

            provider.Complete(
                0,
                Snapshot(
                    Entry("default", 1, "First", "1", firstOwner),
                    firstOwner));
            yield return WaitUntilAsync(() => provider.LoadCallCount == 2);
            Assert.That(firstReload.IsCompleted, Is.True);
            Assert.That(secondReload.IsCompleted, Is.False);

            provider.Complete(
                1,
                Snapshot(
                    Entry("default", 2, "Second", "2", secondOwner),
                    secondOwner));
            yield return WaitForTask(secondReload);
            secondReload.GetAwaiter().GetResult();

            Assert.That(
                service.Get<GameplayConfig>("default").Damage,
                Is.EqualTo(2));
            Assert.That(firstOwner.DisposeCount, Is.EqualTo(1));
            Assert.That(secondOwner.DisposeCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator Stop_WaitsForReloadReleasesActiveAndRejectsFutureReload()
        {
            var provider = new ControlledProvider();
            var owner = new TrackingOwner();
            var service = Track(CreateService(provider));
            var reload = service.ReloadAsync().AsTask();

            var stop = service.StopAsync().AsTask();
            yield return null;
            Assert.That(stop.IsCompleted, Is.False);
            provider.Complete(
                0,
                Snapshot(
                    Entry("default", 1, "Json", "1", owner),
                    owner));
            yield return WaitForTask(reload);
            reload.GetAwaiter().GetResult();
            yield return WaitForTask(stop);
            stop.GetAwaiter().GetResult();

            Assert.That(owner.DisposeCount, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(
                () => Await(service.ReloadAsync()));
            Assert.That(
                service.TryGet<GameplayConfig>("default", out _),
                Is.False);
        }

        [UnityTest]
        public IEnumerator Stop_WaitsForEveryReloadAcceptedBeforeStop()
        {
            var provider = new ControlledProvider();
            var firstOwner = new TrackingOwner();
            var secondOwner = new TrackingOwner();
            var service = Track(CreateService(provider));
            var firstReload = service.ReloadAsync().AsTask();
            var secondReload = service.ReloadAsync().AsTask();
            var stop = service.StopAsync().AsTask();

            provider.Complete(
                0,
                Snapshot(
                    Entry("default", 1, "First", "1", firstOwner),
                    firstOwner));
            yield return WaitUntilAsync(() => provider.LoadCallCount == 2);
            Assert.That(stop.IsCompleted, Is.False);

            provider.Complete(
                1,
                Snapshot(
                    Entry("default", 2, "Second", "2", secondOwner),
                    secondOwner));
            yield return WaitForTask(firstReload);
            yield return WaitForTask(secondReload);
            yield return WaitForTask(stop);
            firstReload.GetAwaiter().GetResult();
            secondReload.GetAwaiter().GetResult();
            stop.GetAwaiter().GetResult();

            Assert.That(firstOwner.DisposeCount, Is.EqualTo(1));
            Assert.That(secondOwner.DisposeCount, Is.EqualTo(1));
            Assert.That(
                service.TryGet<GameplayConfig>("default", out _),
                Is.False);
        }

        [UnityTest]
        public IEnumerator Stop_ActiveOwnerCanSynchronouslyCallStop()
        {
            var owner = new TrackingOwner();
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(
                    Entry("default", 1, "Json", "1", owner),
                    owner));
            var service = CreateService(provider);
            Await(service.ReloadAsync());
            owner.OnDispose = () => Await(service.StopAsync());

            var stop = Task.Run(() => Await(service.StopAsync()));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!stop.IsCompleted && DateTime.UtcNow < deadline)
            {
                yield return null;
            }

            Assert.That(
                stop.IsCompleted,
                Is.True,
                "Owner cleanup synchronously reentering Stop deadlocked.");
            stop.GetAwaiter().GetResult();
            Assert.That(owner.DisposeCount, Is.EqualTo(1));
            Await(service.DisposeAsync());
        }

        [UnityTest]
        public IEnumerator Dispose_ActiveOwnerCanSynchronouslyCallDispose()
        {
            var owner = new TrackingOwner();
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(
                    Entry("default", 1, "Json", "1", owner),
                    owner));
            var service = CreateService(provider);
            Await(service.ReloadAsync());
            owner.OnDispose = () => Await(service.DisposeAsync());

            var dispose = Task.Run(() => Await(service.DisposeAsync()));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!dispose.IsCompleted && DateTime.UtcNow < deadline)
            {
                yield return null;
            }

            Assert.That(
                dispose.IsCompleted,
                Is.True,
                "Owner cleanup synchronously reentering Dispose deadlocked.");
            dispose.GetAwaiter().GetResult();
            Assert.That(owner.DisposeCount, Is.EqualTo(1));
            Await(service.DisposeAsync());
        }

        [Test]
        public void Dispose_NonPumpingContextOwnerCanSynchronouslyDispose()
        {
            var owner = new TrackingOwner();
            var provider = new SequenceProvider(
                "Provider",
                Snapshot(
                    Entry("default", 1, "Json", "1", owner),
                    owner));
            var service = CreateService(provider);
            Await(service.ReloadAsync());
            owner.OnDispose = () => Await(service.DisposeAsync());
            Exception failure = null;
            var completed = new ManualResetEventSlim();
            var thread = new Thread(
                () =>
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new NonPumpingSynchronizationContext());
                    try
                    {
                        Await(service.DisposeAsync());
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        completed.Set();
                    }
                })
            {
                IsBackground = true
            };

            thread.Start();

            Assert.That(
                completed.Wait(TimeSpan.FromSeconds(5)),
                Is.True,
                "Dispose captured a non-pumping synchronization context.");
            Assert.That(failure, Is.Null);
            Assert.That(owner.DisposeCount, Is.EqualTo(1));
            Await(service.DisposeAsync());
            completed.Dispose();
        }

        [UnityTest]
        public IEnumerator ConcurrentStopAndDisposeReleaseActiveExactlyOnce()
        {
            var owner = new TrackingOwner();
            var service = CreateService(
                new SequenceProvider(
                    "Provider",
                    Snapshot(
                        Entry("default", 1, "Json", "1", owner),
                        owner)));
            Await(service.ReloadAsync());
            var start = new ManualResetEventSlim();
            var stop = Task.Run(
                () =>
                {
                    start.Wait();
                    Await(service.StopAsync());
                });
            var firstDispose = Task.Run(
                () =>
                {
                    start.Wait();
                    Await(service.DisposeAsync());
                });
            var secondDispose = Task.Run(
                () =>
                {
                    start.Wait();
                    Await(service.DisposeAsync());
                });

            start.Set();
            var all = Task.WhenAll(stop, firstDispose, secondDispose);
            yield return WaitForTask(all);
            all.GetAwaiter().GetResult();

            Assert.That(owner.DisposeCount, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(
                () => Await(service.ReloadAsync()));
            Await(service.DisposeAsync());
            start.Dispose();
        }

        [Test]
        public void ConfigModule_DeclaresDependenciesRegistersServiceAndDoesNotReload()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var logger = new RecordingLogger();
            var runtime = new FrameworkRuntime(logger);
            var module = new ConfigModule(
                "custom-label",
                new ResourceKey("custom-manifest"));
            try
            {
                CollectionAssert.AreEquivalent(
                    new[] { "Resource", "EventBus" },
                    module.Dependencies);
                Assert.That(module.Id, Is.EqualTo("Config"));
                Await(runtime.StartAsync(
                    new[]
                    {
                        new ModuleDescriptor(
                            "Resource",
                            Array.Empty<string>(),
                            0,
                            () => new ProvidedResourceModule(resources)),
                        new ModuleDescriptor(
                            "EventBus",
                            Array.Empty<string>(),
                            1,
                            () => new EventBusModule()),
                        new ModuleDescriptor(
                            "Config",
                            new[] { "Resource", "EventBus" },
                            2,
                            () => module)
                    },
                    CancellationToken.None));

                Assert.That(runtime.Services.Resolve<IConfigService>(), Is.Not.Null);
                Assert.That(backend.StartCount, Is.Zero);
            }
            finally
            {
                Await(runtime.StopAsync(CancellationToken.None));
                Await(runtime.DisposeAsync());
            }
        }

        [Test]
        public void ConfigModule_RuntimeStopAndScopeDisposeReleaseActiveLeasesOnce()
        {
            var backend = new FakeResourceBackend();
            var resources = Track(new ResourceService(backend, new RecordingLogger()));
            var asset = Track(CreateAsset(
                "default",
                "1",
                new GameplayConfig { Damage = 3 }));
            var labelOperation =
                backend.EnqueueCompletedLabel<ScriptableObjectConfigAsset>(
                    asset);
            var manifestOperation = backend.EnqueueCompletedAsset(
                Track(new TextAsset("{\"entries\":[]}")));
            var runtime = new FrameworkRuntime(new RecordingLogger());
            try
            {
                Await(runtime.StartAsync(
                    new[]
                    {
                        new ModuleDescriptor(
                            "Resource",
                            Array.Empty<string>(),
                            0,
                            () => new ProvidedResourceModule(resources)),
                        new ModuleDescriptor(
                            "EventBus",
                            Array.Empty<string>(),
                            1,
                            () => new EventBusModule()),
                        new ModuleDescriptor(
                            "Config",
                            new[] { "Resource", "EventBus" },
                            2,
                            () => new ConfigModule())
                    },
                    CancellationToken.None));
                var service = runtime.Services.Resolve<IConfigService>();
                Await(service.ReloadAsync());
                Assert.That(
                    resources.Diagnostics.OutstandingLeases.Count,
                    Is.EqualTo(2));

                Await(runtime.StopAsync(CancellationToken.None));
                Await(runtime.DisposeAsync());

                Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
                Assert.That(
                    labelOperation.UnderlyingReleaseCount,
                    Is.EqualTo(1));
                Assert.That(
                    manifestOperation.UnderlyingReleaseCount,
                    Is.EqualTo(1));
            }
            finally
            {
                Await(runtime.StopAsync(CancellationToken.None));
                Await(runtime.DisposeAsync());
            }
        }

        private ConfigService CreateService(params IConfigProvider[] providers)
        {
            return new ConfigService(
                providers,
                new RecordingEventBus(),
                new RecordingLogger());
        }

        private static ConfigEntry Entry(
            string key,
            int damage,
            string source,
            string version,
            IDisposable ownership = null)
        {
            return new ConfigEntry(
                new ConfigKey(typeof(GameplayConfig), key),
                new GameplayConfig { Damage = damage },
                source,
                version,
                ownership);
        }

        private static ConfigProviderSnapshot Snapshot(
            params ConfigEntry[] entries)
        {
            return new ConfigProviderSnapshot(entries, Array.Empty<IDisposable>());
        }

        private static ConfigProviderSnapshot Snapshot(
            ConfigEntry entry,
            IDisposable owner)
        {
            return new ConfigProviderSnapshot(
                new[] { entry },
                new[] { owner });
        }

        private static string CreateManifest(
            Type type,
            string key,
            string version,
            string address)
        {
            return "{\"entries\":[{\"type\":\"" +
                type.AssemblyQualifiedName +
                "\",\"key\":\"" + key +
                "\",\"version\":\"" + version +
                "\",\"address\":\"" + address + "\"}]}";
        }

        private TestConfigAsset CreateAsset(
            string key,
            string version,
            GameplayConfig payload)
        {
            var asset = ScriptableObject.CreateInstance<TestConfigAsset>();
            SetPrivateField(asset, "_key", key);
            SetPrivateField(asset, "_version", version);
            asset.Payload = payload;
            return asset;
        }

        private static void SetPrivateField(
            ScriptableObjectConfigAsset asset,
            string name,
            object value)
        {
            typeof(ScriptableObjectConfigAsset)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(asset, value);
        }

        private T Track<T>(T value) where T : Object
        {
            _objects.Add(value);
            return value;
        }

        private ResourceService Track(ResourceService service)
        {
            _resources.Add(service);
            return service;
        }

        private ConfigService Track(ConfigService service)
        {
            _services.Add(service);
            return service;
        }

        private static T Await<T>(ValueTask<T> task)
        {
            return task.AsTask().GetAwaiter().GetResult();
        }

        private static void Await(ValueTask task)
        {
            task.AsTask().GetAwaiter().GetResult();
        }

        private static IEnumerator WaitForTask(Task task)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Task did not complete in time.");
                }

                yield return null;
            }
        }

        private static IEnumerator WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Condition did not become true.");
                }

                yield return null;
            }
        }

        [Serializable]
        public sealed class GameplayConfig
        {
            public int Damage { get; set; }

            public Dictionary<string, int> Multipliers { get; set; }

            public NestedConfig Nested { get; set; }
        }

        [Serializable]
        public sealed class NestedConfig
        {
            public string Label { get; set; }
        }

        private sealed class TestConfigAsset : ScriptableObjectConfigAsset
        {
            public GameplayConfig Payload { get; set; }

            public override Type PayloadType => typeof(GameplayConfig);

            public override object GetPayload()
            {
                return Payload;
            }
        }

        private sealed class DelegateValidator<T> : IConfigValidator<T>
        {
            private readonly Action<string, T> _validate;

            public DelegateValidator(Action<string, T> validate)
            {
                _validate = validate;
            }

            public void Validate(string key, T value)
            {
                _validate(key, value);
            }
        }

        private sealed class SequenceProvider : IConfigProvider
        {
            private readonly Queue<object> _results = new Queue<object>();

            public SequenceProvider(
                string name,
                params ConfigProviderSnapshot[] snapshots)
            {
                Name = name;
                for (var index = 0; index < snapshots.Length; index++)
                {
                    Enqueue(snapshots[index]);
                }
            }

            public string Name { get; }

            public void Enqueue(ConfigProviderSnapshot snapshot)
            {
                _results.Enqueue(snapshot);
            }

            public void Enqueue(Exception exception)
            {
                _results.Enqueue(exception);
            }

            public ValueTask<ConfigProviderSnapshot> LoadAsync(
                CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                var result = _results.Dequeue();
                if (result is Exception exception)
                {
                    throw exception;
                }

                return new ValueTask<ConfigProviderSnapshot>(
                    (ConfigProviderSnapshot)result);
            }
        }

        private sealed class ControlledProvider : IConfigProvider
        {
            private readonly List<TaskCompletionSource<ConfigProviderSnapshot>>
                _loads =
                    new List<TaskCompletionSource<ConfigProviderSnapshot>>();

            public string Name => "Controlled";

            public int LoadCallCount
            {
                get
                {
                    lock (_loads)
                    {
                        return _loads.Count;
                    }
                }
            }

            public ValueTask<ConfigProviderSnapshot> LoadAsync(
                CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                var completion =
                    new TaskCompletionSource<ConfigProviderSnapshot>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_loads)
                {
                    _loads.Add(completion);
                }

                return new ValueTask<ConfigProviderSnapshot>(completion.Task);
            }

            public void Complete(
                int index,
                ConfigProviderSnapshot snapshot)
            {
                TaskCompletionSource<ConfigProviderSnapshot> completion;
                lock (_loads)
                {
                    completion = _loads[index];
                }

                completion.SetResult(snapshot);
            }
        }

        private sealed class TrackingOwner : IDisposable
        {
            public int DisposeCount { get; private set; }

            public Exception DisposeException { get; set; }

            public Action OnDispose { get; set; }

            public void Dispose()
            {
                DisposeCount++;
                OnDispose?.Invoke();
                if (DisposeException != null)
                {
                    throw DisposeException;
                }
            }
        }

        private sealed class RecordingEventBus : IEventBus
        {
            public List<ConfigChanged> Changes { get; } =
                new List<ConfigChanged>();

            public Exception PublishException { get; set; }

            public Action<ConfigChanged> OnPublish { get; set; }

            public EventBusDiagnostics Diagnostics => null;

            public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
            {
                return new TrackingOwner();
            }

            public IDisposable Subscribe<TEvent>(
                ModuleScope ownerScope,
                Action<TEvent> handler)
            {
                return ownerScope.Own<IDisposable>(new TrackingOwner());
            }

            public void Publish<TEvent>(TEvent value)
            {
                if (PublishException != null)
                {
                    throw PublishException;
                }

                if (value is ConfigChanged changed)
                {
                    Changes.Add(changed);
                    OnPublish?.Invoke(changed);
                }
            }

            public void Enqueue<TEvent>(TEvent value)
            {
            }
        }

        private sealed class RecordingLogger : IFrameworkLogger
        {
            public List<Exception> Errors { get; } = new List<Exception>();

            public void Debug(
                string moduleId,
                string category,
                string message)
            {
            }

            public void Info(
                string moduleId,
                string category,
                string message)
            {
            }

            public void Warning(
                string moduleId,
                string category,
                string message)
            {
            }

            public void Error(
                string moduleId,
                string category,
                string message,
                Exception exception)
            {
                Errors.Add(exception);
            }
        }

        private sealed class FakeResourceBackend : IResourceBackend
        {
            private readonly Queue<object> _assets = new Queue<object>();
            private readonly Queue<object> _labels = new Queue<object>();

            public int StartCount { get; private set; }

            public PendingOperation<T> EnqueueCompletedAsset<T>(T value)
                where T : Object
            {
                var operation = EnqueueAsset<T>();
                operation.Succeed(value);
                return operation;
            }

            public PendingOperation<T> EnqueueAsset<T>() where T : Object
            {
                var operation = new PendingOperation<T>();
                _assets.Enqueue(operation);
                return operation;
            }

            public PendingOperation<IReadOnlyList<T>> EnqueueCompletedLabel<T>(
                params T[] values)
                where T : Object
            {
                var operation =
                    new PendingOperation<IReadOnlyList<T>>();
                operation.Succeed(Array.AsReadOnly(values));
                _labels.Enqueue(operation);
                return operation;
            }

            public IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
                where T : Object
            {
                StartCount++;
                return (IResourceOperation<T>)_assets.Dequeue();
            }

            public IResourceOperation<GameObject> InstantiateAsync(
                ResourceKey key,
                Transform parent)
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<IReadOnlyList<T>> LoadByLabelAsync<T>(
                string label)
                where T : Object
            {
                StartCount++;
                return (IResourceOperation<IReadOnlyList<T>>)_labels.Dequeue();
            }

            public IResourceOperation<SceneInstance> LoadSceneAsync(
                ResourceKey key,
                LoadSceneMode mode,
                bool activateOnLoad)
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<SceneInstance> UnloadSceneAsync(
                SceneInstance scene)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class PendingOperation<T> : IResourceOperation<T>
        {
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>();
            private int _released;

            public Task<T> Task => _completion.Task;

            public int UnderlyingReleaseCount { get; private set; }

            public void Succeed(T value)
            {
                _completion.SetResult(value);
            }

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    UnderlyingReleaseCount++;
                }
            }
        }

        private sealed class ProvidedResourceModule : IFrameworkModule
        {
            private readonly IResourceService _resources;

            public ProvidedResourceModule(IResourceService resources)
            {
                _resources = resources;
            }

            public string Id => "Resource";

            public IReadOnlyCollection<string> Dependencies =>
                Array.Empty<string>();

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                context.ModuleScope.RegisterInstance(_resources);
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

        private sealed class TestValidationException : Exception
        {
        }

        private sealed class TestLoadException : Exception
        {
        }

        private sealed class TestCleanupException : Exception
        {
        }

        private sealed class TestPublishException : Exception
        {
        }

        private sealed class NonPumpingSynchronizationContext :
            SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state)
            {
                // Intentionally does not pump posted continuations.
            }
        }
    }
}
