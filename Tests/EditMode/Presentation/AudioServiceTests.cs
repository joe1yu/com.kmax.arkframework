using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class AudioServiceTests
    {
        private FakeResourceBackend _backend;
        private ResourceService _resources;
        private AudioService _service;
        private HashSet<int> _initialAudioSourceIds;
        private List<FrameworkRuntime> _auxiliaryRuntimes;
        private List<FakeResourceBackend> _auxiliaryBackends;
        private List<ResourceService> _auxiliaryResources;

        [SetUp]
        public void SetUp()
        {
            _initialAudioSourceIds = SnapshotAudioSourceIds();
            _auxiliaryRuntimes = new List<FrameworkRuntime>();
            _auxiliaryBackends = new List<FakeResourceBackend>();
            _auxiliaryResources = new List<ResourceService>();
            _backend = new FakeResourceBackend();
            _resources = new ResourceService(_backend);
            _service = new AudioService(
                _resources,
                dontDestroyOnLoad: false);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _backend?.FailAllPending();
            foreach (var backend in _auxiliaryBackends)
            {
                backend.FailAllPending();
            }

            foreach (var runtime in _auxiliaryRuntimes)
            {
                var runtimeStop =
                    runtime.StopAsync(CancellationToken.None).AsTask();
                yield return WaitFor(runtimeStop);
                Observe(runtimeStop);
                var runtimeDispose = runtime.DisposeAsync().AsTask();
                yield return WaitFor(runtimeDispose);
                Observe(runtimeDispose);
            }

            if (_service != null)
            {
                var stop = _service.StopAsync().AsTask();
                yield return WaitFor(stop);
                Observe(stop);
            }

            foreach (var resources in _auxiliaryResources)
            {
                var auxiliaryDispose =
                    resources.DisposeAsync().AsTask();
                yield return WaitFor(auxiliaryDispose);
                Observe(auxiliaryDispose);
            }

            if (_resources != null)
            {
                var dispose = _resources.DisposeAsync().AsTask();
                yield return WaitFor(dispose);
                Observe(dispose);
            }

            foreach (var source in
                     Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (source != null &&
                    !_initialAudioSourceIds.Contains(source.GetInstanceID()) &&
                    IsRuntimeObject(source))
                {
                    Object.DestroyImmediate(source.gameObject);
                }
            }
        }

        [Test]
        public void OptionsRejectInvalidChannelVolumeAndFade()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AudioPlayOptions((AudioChannel)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AudioPlayOptions(
                    AudioChannel.SFX,
                    volume: -0.01f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AudioPlayOptions(
                    AudioChannel.SFX,
                    volume: 1.01f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AudioPlayOptions(
                    AudioChannel.SFX,
                    volume: float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AudioPlayOptions(
                    AudioChannel.SFX,
                    volume: float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AudioPlayOptions(
                    AudioChannel.SFX,
                    fadeSeconds: -0.01f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AudioPlayOptions(
                    AudioChannel.SFX,
                    fadeSeconds: float.NaN));

            var options = new AudioPlayOptions(
                AudioChannel.Voice,
                loop: true,
                volume: 0.25f,
                fadeSeconds: 0.5f);
            Assert.That(options.Channel, Is.EqualTo(AudioChannel.Voice));
            Assert.That(options.Loop, Is.True);
            Assert.That(options.Volume, Is.EqualTo(0.25f));
            Assert.That(options.FadeSeconds, Is.EqualTo(0.5f));
        }

        [Test]
        public void RootOwnsExactlyTwoPersistentMusicSourcesAndAnEmptyOneShotPool()
        {
            var created = Resources.FindObjectsOfTypeAll<AudioSource>()
                .Where(
                    source =>
                        source != null &&
                        !_initialAudioSourceIds.Contains(source.GetInstanceID()) &&
                        IsRuntimeObject(source))
                .ToArray();

            Assert.That(created.Length, Is.EqualTo(2));
            Assert.That(created.All(source => !source.playOnAwake), Is.True);
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.Zero);
            Assert.That(_service.Diagnostics.OneShotPool.IdleCount, Is.Zero);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
            Assert.That(_service.Diagnostics.Channels.Count, Is.EqualTo(4));
        }

        [Test]
        public void SecondLiveServiceIsRejectedWithoutCreatingAnotherRoot()
        {
            Assert.Throws<InvalidOperationException>(
                () => new AudioService(
                    _resources,
                    dontDestroyOnLoad: false));
            Assert.That(FindAudioRoots().Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator BackgroundFirstConstructionCannotClaimUnityThread()
        {
            var background = Task.Run(
                () =>
                {
                    try
                    {
                        new AudioService(
                            _resources,
                            dontDestroyOnLoad: false);
                        return (Exception)null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                });
            yield return WaitFor(background);
            Assert.That(
                background.GetAwaiter().GetResult(),
                Is.TypeOf<InvalidOperationException>());
            Assert.That(FindAudioRoots().Length, Is.EqualTo(1));
        }

        [Test]
        public void ConstructionThreadGateUsesUnityAssemblyIdentity()
        {
            var method = typeof(AudioService).GetMethod(
                "IsUnityMainThreadContext",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.Invoke(
                    null,
                    new object[] { SynchronizationContext.Current }),
                Is.True);
            Assert.That(
                method.Invoke(
                    null,
                    new object[] { new SynchronizationContext() }),
                Is.False);
        }

        [UnityTest]
        public IEnumerator OneShotChannelsOverlapAndOwnIndependentLeasesAndSources()
        {
            _backend.Enqueue(Clip("sfx-a", 1f));
            _backend.Enqueue(Clip("sfx-b", 1f));
            var first = _service.PlayAsync(
                new ResourceKey("sfx-a"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            var second = _service.PlayAsync(
                new ResourceKey("sfx-b"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            yield return WaitFor(first, second);

            var firstHandle = first.GetAwaiter().GetResult();
            var secondHandle = second.GetAwaiter().GetResult();
            Assert.That(firstHandle.InstanceId, Is.Not.EqualTo(secondHandle.InstanceId));
            Assert.That(firstHandle.IsValid, Is.True);
            Assert.That(secondHandle.IsValid, Is.True);
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(2));
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.EqualTo(2));
            Assert.That(_service.Diagnostics.Entries.Count, Is.EqualTo(2));

            var stopFirst = _service.StopAsync(firstHandle).AsTask();
            yield return WaitFor(stopFirst);
            Observe(stopFirst);
            Assert.That(firstHandle.IsValid, Is.False);
            Assert.That(secondHandle.IsValid, Is.True);
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.OneShotPool.IdleCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SameMusicSequentialAndConcurrentPlayIsSingleFlight()
        {
            var operation = _backend.EnqueueGated(Clip("music", 2f));
            var first = _service.PlayAsync(
                new ResourceKey("music"),
                new AudioPlayOptions(AudioChannel.Music, loop: true)).AsTask();
            var second = _service.PlayAsync(
                new ResourceKey("music"),
                new AudioPlayOptions(AudioChannel.Music, loop: false)).AsTask();
            Assert.That(_backend.LoadCount, Is.EqualTo(1));
            operation.Complete();
            yield return WaitFor(first, second);

            var firstHandle = first.GetAwaiter().GetResult();
            var secondHandle = second.GetAwaiter().GetResult();
            Assert.That(secondHandle, Is.SameAs(firstHandle));
            Assert.That(firstHandle.IsValid, Is.True);
            Assert.That(_backend.LoadCount, Is.EqualTo(1));
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));

            var third = _service.PlayAsync(
                new ResourceKey("music"),
                new AudioPlayOptions(AudioChannel.Music)).AsTask();
            yield return WaitFor(third);
            Assert.That(third.GetAwaiter().GetResult(), Is.SameAs(firstHandle));
            Assert.That(_backend.LoadCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SharedMusicFailureIsObservedAndCanRetry()
        {
            var operation = _backend.EnqueueGated(Clip("bad", 1f));
            var expected = new InvalidOperationException("load failed");
            var first = _service.PlayAsync(
                new ResourceKey("bad"),
                new AudioPlayOptions(AudioChannel.Music)).AsTask();
            var second = _service.PlayAsync(
                new ResourceKey("bad"),
                new AudioPlayOptions(AudioChannel.Music)).AsTask();
            operation.Fail(expected);
            yield return WaitFor(first, second);

            Assert.That(first.IsFaulted, Is.True);
            Assert.That(second.IsFaulted, Is.True);
            Assert.That(
                _service.Diagnostics.RecentException.ToString(),
                Does.Contain("load failed"));
            Assert.That(_service.Diagnostics.PendingLoadCount, Is.Zero);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);

            _backend.Enqueue(Clip("retry", 1f));
            var retry = _service.PlayAsync(
                new ResourceKey("bad"),
                new AudioPlayOptions(AudioChannel.Music)).AsTask();
            yield return WaitFor(retry);
            Assert.That(retry.IsCompletedSuccessfully, Is.True);
            Assert.That(_backend.LoadCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator MusicFadeCrossFadeAndThirdTrackNeverOwnMoreThanTwoLeases()
        {
            _backend.Enqueue(Clip("a", 5f));
            var first = _service.PlayAsync(
                new ResourceKey("a"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true,
                    volume: 0.8f,
                    fadeSeconds: 1f)).AsTask();
            yield return WaitFor(first);
            var a = first.GetAwaiter().GetResult();
            Tick(0.25f);
            Assert.That(
                Entry(a).EffectiveVolume,
                Is.EqualTo(0.2f).Within(0.02f));

            _backend.Enqueue(Clip("b", 5f));
            var second = _service.PlayAsync(
                new ResourceKey("b"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true,
                    fadeSeconds: 1f)).AsTask();
            yield return WaitFor(second);
            var b = second.GetAwaiter().GetResult();
            Tick(0.25f);
            Assert.That(_service.Diagnostics.Entries.Count, Is.EqualTo(2));
            Assert.That(Entry(a).State, Is.EqualTo(AudioPlaybackState.FadingOut));
            Assert.That(Entry(b).State, Is.EqualTo(AudioPlaybackState.FadingIn));

            _backend.Enqueue(Clip("c", 5f));
            var third = _service.PlayAsync(
                new ResourceKey("c"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true,
                    fadeSeconds: 1f)).AsTask();
            yield return WaitFor(third);
            var c = third.GetAwaiter().GetResult();
            Assert.That(a.IsValid, Is.False);
            Assert.That(b.IsValid, Is.True);
            Assert.That(c.IsValid, Is.True);
            Assert.That(_service.Diagnostics.Entries.Count, Is.EqualTo(2));
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(2));

            Tick(1f);
            Assert.That(b.IsValid, Is.False);
            Assert.That(c.IsValid, Is.True);
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
            Assert.That(
                _service.Diagnostics.CurrentMusicKey.Value.Value,
                Is.EqualTo("c"));
        }

        [UnityTest]
        public IEnumerator MusicReplacementCleanupFailureDoesNotOrphanAcceptedTrack()
        {
            _backend.Enqueue(
                Clip("music-a", 5f),
                new InvalidOperationException("old music release failed"));
            var first = _service.PlayAsync(
                new ResourceKey("music-a"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            yield return WaitFor(first);
            var a = first.GetAwaiter().GetResult();

            _backend.Enqueue(Clip("music-b", 5f));
            var second = _service.PlayAsync(
                new ResourceKey("music-b"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            yield return WaitFor(second);

            Assert.That(second.IsFaulted, Is.True);
            Assert.That(a.IsValid, Is.False);
            Assert.That(_service.Diagnostics.CurrentMusicHandle, Is.Null);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(
                Resources.FindObjectsOfTypeAll<AudioSource>()
                    .Where(IsRuntimeObject)
                    .All(source => source == null || source.clip == null),
                Is.True);
            Assert.That(
                _service.Diagnostics.RecentException.ToString(),
                Does.Contain("old music release failed"));
        }

        [UnityTest]
        public IEnumerator MusicConfigureFailureRollsBackSourceAndPreservesOldCurrent()
        {
            _backend.Enqueue(Clip("stable-music", 5f));
            var first = _service.PlayAsync(
                new ResourceKey("stable-music"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            yield return WaitFor(first);
            var stable = first.GetAwaiter().GetResult();

            var hookField = typeof(AudioService).GetField(
                "_playbackStartingForTesting",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(
                hookField,
                Is.Not.Null,
                "Audio needs a narrow internal failure seam to verify transactional source rollback.");
            hookField.SetValue(
                _service,
                new Action<AudioSource>(
                    source =>
                    {
                        if (source.clip != null &&
                            source.clip.name == "failing-music")
                        {
                            throw new InvalidOperationException(
                                "source play failed");
                        }
                    }));
            Task<IAudioHandle> failed = null;
            try
            {
                _backend.Enqueue(Clip("failing-music", 5f));
                failed = _service.PlayAsync(
                    new ResourceKey("failing-music"),
                    new AudioPlayOptions(
                        AudioChannel.Music,
                        loop: true,
                        volume: 0.25f)).AsTask();
                yield return WaitFor(failed);
            }
            finally
            {
                hookField.SetValue(_service, null);
            }

            Assert.That(failed.IsFaulted, Is.True);
            Assert.That(stable.IsValid, Is.True);
            Assert.That(_service.Diagnostics.CurrentMusicHandle, Is.SameAs(stable));
            Assert.That(_service.Diagnostics.Entries.Count, Is.EqualTo(1));
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
            var unused = Resources.FindObjectsOfTypeAll<AudioSource>()
                .Where(IsRuntimeObject)
                .Single(source => source.clip == null);
            Assert.That(unused.loop, Is.False);
            Assert.That(unused.outputAudioMixerGroup, Is.Null);
            Assert.That(unused.volume, Is.EqualTo(1f));
            Assert.That(unused.mute, Is.False);
        }

        [UnityTest]
        public IEnumerator MusicResetFailureStillReleasesLeaseAndCompletesStop()
        {
            _backend.Enqueue(Clip("reset-failure", 5f));
            var play = _service.PlayAsync(
                new ResourceKey("reset-failure"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            yield return WaitFor(play);
            var handle = play.GetAwaiter().GetResult();
            var resetHook = typeof(AudioService).GetField(
                "_sourceResettingForTesting",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(resetHook, Is.Not.Null);
            var injected = false;
            resetHook.SetValue(
                _service,
                new Action<AudioSource>(
                    source =>
                    {
                        if (!injected)
                        {
                            injected = true;
                            throw new InvalidOperationException(
                                "music reset failed");
                        }
                    }));

            Task stop;
            try
            {
                stop = _service.StopAsync(handle).AsTask();
                yield return WaitFor(stop);
            }
            finally
            {
                resetHook.SetValue(_service, null);
            }

            Assert.That(stop.IsFaulted, Is.True);
            Assert.That(
                stop.Exception.ToString(),
                Does.Contain("music reset failed"));
            Assert.That(handle.IsValid, Is.False);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
        }

        [UnityTest]
        public IEnumerator MusicConfigureAndResetFailuresAreAggregated()
        {
            _backend.Enqueue(Clip("double-failure", 5f));
            var playHook = typeof(AudioService).GetField(
                "_playbackStartingForTesting",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var resetHook = typeof(AudioService).GetField(
                "_sourceResettingForTesting",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(playHook, Is.Not.Null);
            Assert.That(resetHook, Is.Not.Null);
            playHook.SetValue(
                _service,
                new Action<AudioSource>(
                    source =>
                    {
                        throw new InvalidOperationException(
                            "music configure failed");
                    }));
            resetHook.SetValue(
                _service,
                new Action<AudioSource>(
                    source =>
                    {
                        throw new InvalidOperationException(
                            "music rollback reset failed");
                    }));

            Task<IAudioHandle> play = null;
            try
            {
                play = _service.PlayAsync(
                    new ResourceKey("double-failure"),
                    new AudioPlayOptions(
                        AudioChannel.Music,
                        loop: true)).AsTask();
                yield return WaitFor(play);
            }
            finally
            {
                playHook.SetValue(_service, null);
                resetHook.SetValue(_service, null);
            }

            Assert.That(play.IsFaulted, Is.True);
            Assert.That(
                play.Exception.ToString(),
                Does.Contain("music configure failed"));
            Assert.That(
                play.Exception.ToString(),
                Does.Contain("music rollback reset failed"));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
        }

        [UnityTest]
        public IEnumerator LaterAcceptedMusicWinsWhenDifferentKeysCompleteOutOfOrder()
        {
            var operationA = _backend.EnqueueGated(Clip("ordered-a", 5f));
            var operationB = _backend.EnqueueGated(Clip("ordered-b", 5f));
            var first = _service.PlayAsync(
                new ResourceKey("ordered-a"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            var second = _service.PlayAsync(
                new ResourceKey("ordered-b"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();

            operationB.Complete();
            yield return WaitFor(second);
            var b = second.GetAwaiter().GetResult();
            operationA.Complete();
            yield return WaitFor(first);

            Assert.That(first.IsCanceled, Is.True);
            Assert.That(b.IsValid, Is.True);
            Assert.That(_service.Diagnostics.CurrentMusicHandle, Is.SameAs(b));
            Assert.That(
                _service.Diagnostics.CurrentMusicKey.Value.Value,
                Is.EqualTo("ordered-b"));
            Assert.That(_service.Diagnostics.Entries.Count, Is.EqualTo(1));
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RepeatingPendingMusicPromotesItsAcceptedKeyWithoutReloading()
        {
            var operationA = _backend.EnqueueGated(Clip("promoted-a", 5f));
            var operationB = _backend.EnqueueGated(Clip("promoted-b", 5f));
            var firstA = _service.PlayAsync(
                new ResourceKey("promoted-a"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            var b = _service.PlayAsync(
                new ResourceKey("promoted-b"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            var repeatedA = _service.PlayAsync(
                new ResourceKey("promoted-a"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            Assert.That(_backend.LoadCount, Is.EqualTo(2));

            operationB.Complete();
            yield return WaitFor(b);
            operationA.Complete();
            yield return WaitFor(firstA, repeatedA);

            Assert.That(b.IsCanceled, Is.True);
            Assert.That(firstA.IsCompletedSuccessfully, Is.True);
            Assert.That(repeatedA.IsCompletedSuccessfully, Is.True);
            var accepted = firstA.GetAwaiter().GetResult();
            Assert.That(
                repeatedA.GetAwaiter().GetResult(),
                Is.SameAs(accepted));
            Assert.That(
                _service.Diagnostics.CurrentMusicKey.Value.Value,
                Is.EqualTo("promoted-a"));
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RepeatingCurrentMusicSupersedesPendingReplacement()
        {
            _backend.Enqueue(Clip("current-a", 5f));
            var first = _service.PlayAsync(
                new ResourceKey("current-a"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            yield return WaitFor(first);
            var a = first.GetAwaiter().GetResult();

            var operationB = _backend.EnqueueGated(Clip("pending-b", 5f));
            var b = _service.PlayAsync(
                new ResourceKey("pending-b"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true)).AsTask();
            var repeatedA = _service.PlayAsync(
                new ResourceKey("current-a"),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: false)).AsTask();
            yield return WaitFor(repeatedA);
            Assert.That(repeatedA.GetAwaiter().GetResult(), Is.SameAs(a));

            operationB.Complete();
            yield return WaitFor(b);
            Assert.That(b.IsCanceled, Is.True);
            Assert.That(a.IsValid, Is.True);
            Assert.That(_service.Diagnostics.CurrentMusicHandle, Is.SameAs(a));
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ChannelStateAppliesImmediatelyAndToNewPlaybackWhilePauseFreezesFade()
        {
            _backend.Enqueue(Clip("voice", 3f));
            var play = _service.PlayAsync(
                new ResourceKey("voice"),
                new AudioPlayOptions(
                    AudioChannel.Voice,
                    loop: true,
                    volume: 0.5f,
                    fadeSeconds: 1f)).AsTask();
            yield return WaitFor(play);
            var handle = play.GetAwaiter().GetResult();
            Tick(0.25f);

            _service.SetChannelVolume(AudioChannel.Voice, 0.4f);
            _service.SetChannelMuted(AudioChannel.Voice, true);
            _service.SetChannelPaused(AudioChannel.Voice, true);
            var before = Entry(handle).EffectiveVolume;
            Tick(0.5f);
            var paused = Entry(handle);
            Assert.That(paused.EffectiveVolume, Is.EqualTo(before).Within(0.001f));
            Assert.That(paused.State, Is.EqualTo(AudioPlaybackState.Paused));
            Assert.That(Channel(AudioChannel.Voice).Volume, Is.EqualTo(0.4f));
            Assert.That(Channel(AudioChannel.Voice).Muted, Is.True);
            Assert.That(Channel(AudioChannel.Voice).Paused, Is.True);

            _service.SetChannelPaused(AudioChannel.Voice, false);
            _service.SetChannelMuted(AudioChannel.Voice, false);
            Tick(0.25f);
            Assert.That(
                Entry(handle).EffectiveVolume,
                Is.EqualTo(0.1f).Within(0.02f));

            _backend.Enqueue(Clip("voice-two", 3f));
            var second = _service.PlayAsync(
                new ResourceKey("voice-two"),
                new AudioPlayOptions(
                    AudioChannel.Voice,
                    loop: true,
                    volume: 0.5f)).AsTask();
            yield return WaitFor(second);
            Assert.That(
                Entry(second.GetAwaiter().GetResult()).EffectiveVolume,
                Is.EqualTo(0.2f).Within(0.02f));
        }

        [UnityTest]
        public IEnumerator NonNullMixerGroupAppliesToExistingAndNewSources()
        {
            var mixerPath =
                "Assets/ArkFrameworkAudioMixerTest_" +
                Guid.NewGuid().ToString("N") +
                ".mixer";
            try
            {
                var controllerType = typeof(Editor).Assembly.GetType(
                    "UnityEditor.Audio.AudioMixerController",
                    throwOnError: true);
                var create = controllerType.GetMethod(
                    "CreateMixerControllerAtPath",
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);
                Assert.That(create, Is.Not.Null);
                var mixer =
                    (AudioMixer)create.Invoke(
                        null,
                        new object[] { mixerPath });
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(
                    ImportAssetOptions.ForceSynchronousImport);
                var group =
                    mixer.FindMatchingGroups("Master").Single();
                _service.SetChannelMixerGroup(AudioChannel.SFX, group);
                _backend.Enqueue(Clip("mixer-existing", 5f));
                var first = _service.PlayAsync(
                    new ResourceKey("mixer-existing"),
                    new AudioPlayOptions(
                        AudioChannel.SFX,
                        loop: true)).AsTask();
                yield return WaitFor(first);
                var firstSource =
                    Resources.FindObjectsOfTypeAll<AudioSource>()
                        .Single(
                            source =>
                                IsRuntimeObject(source) &&
                                source.clip != null &&
                                source.clip.name == "mixer-existing");
                Assert.That(
                    firstSource.outputAudioMixerGroup,
                    Is.SameAs(group));

                _service.SetChannelMixerGroup(AudioChannel.SFX, null);
                Assert.That(firstSource.outputAudioMixerGroup, Is.Null);
                _service.SetChannelMixerGroup(AudioChannel.SFX, group);
                _backend.Enqueue(Clip("mixer-new", 5f));
                var second = _service.PlayAsync(
                    new ResourceKey("mixer-new"),
                    new AudioPlayOptions(
                        AudioChannel.SFX,
                        loop: true)).AsTask();
                yield return WaitFor(second);
                var secondSource =
                    Resources.FindObjectsOfTypeAll<AudioSource>()
                        .Single(
                            source =>
                                IsRuntimeObject(source) &&
                                source.clip != null &&
                                source.clip.name == "mixer-new");
                Assert.That(
                    firstSource.outputAudioMixerGroup,
                    Is.SameAs(group));
                Assert.That(
                    secondSource.outputAudioMixerGroup,
                    Is.SameAs(group));
                Assert.That(
                    Channel(AudioChannel.SFX).MixerGroupName,
                    Is.EqualTo(group.name));
            }
            finally
            {
                try
                {
                    if (_service != null)
                    {
                        _service.SetChannelMixerGroup(
                            AudioChannel.SFX,
                            null);
                    }
                }
                finally
                {
                    if (AssetDatabase.LoadMainAssetAtPath(mixerPath) != null)
                    {
                        Assert.That(
                            AssetDatabase.DeleteAsset(mixerPath),
                            Is.True,
                            "Failed to delete the unique Audio mixer test asset.");
                        AssetDatabase.Refresh(
                            ImportAssetOptions.ForceSynchronousImport);
                        Assert.That(
                            AssetDatabase.LoadMainAssetAtPath(mixerPath),
                            Is.Null,
                            "The unique Audio mixer test asset still exists after deletion.");
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator NaturalEndReleasesNonLoopingButLoopingRequiresStop()
        {
            _backend.Enqueue(Clip("short", 0.1f));
            _backend.Enqueue(Clip("loop", 0.1f));
            var oneShot = _service.PlayAsync(
                new ResourceKey("short"),
                new AudioPlayOptions(AudioChannel.UI)).AsTask();
            var looping = _service.PlayAsync(
                new ResourceKey("loop"),
                new AudioPlayOptions(AudioChannel.SFX, loop: true)).AsTask();
            yield return WaitFor(oneShot, looping);
            var oneShotHandle = oneShot.GetAwaiter().GetResult();
            var loopingHandle = looping.GetAwaiter().GetResult();

            Tick(0.01f);
            Assert.That(oneShotHandle.IsValid, Is.True);
            Tick(0.2f);
            Assert.That(oneShotHandle.IsValid, Is.False);
            Assert.That(loopingHandle.IsValid, Is.True);
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator PausedOneShotFreezesNaturalEndUntilUnpaused()
        {
            _backend.Enqueue(Clip("paused-natural", 0.1f));
            var play = _service.PlayAsync(
                new ResourceKey("paused-natural"),
                new AudioPlayOptions(AudioChannel.UI)).AsTask();
            yield return WaitFor(play);
            var handle = play.GetAwaiter().GetResult();
            _service.SetChannelPaused(AudioChannel.UI, true);
            for (var index = 0; index < 6; index++)
            {
                Tick(1f);
            }

            Assert.That(handle.IsValid, Is.True);
            Assert.That(Entry(handle).State, Is.EqualTo(AudioPlaybackState.Paused));
            _service.SetChannelPaused(AudioChannel.UI, false);
            Tick(0.2f);
            Assert.That(handle.IsValid, Is.False);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [UnityTest]
        public IEnumerator NonLoopingMusicNaturalEndClearsCurrentAndLease()
        {
            _backend.Enqueue(Clip("music-natural", 0.1f));
            var play = _service.PlayAsync(
                new ResourceKey("music-natural"),
                new AudioPlayOptions(AudioChannel.Music)).AsTask();
            yield return WaitFor(play);
            var handle = play.GetAwaiter().GetResult();
            Tick(0.2f);
            Assert.That(handle.IsValid, Is.False);
            Assert.That(_service.Diagnostics.CurrentMusicHandle, Is.Null);
            Assert.That(_service.Diagnostics.CurrentMusicKey, Is.Null);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ActiveStopFadeRemainsValidUntilMandatoryCleanupCompletes()
        {
            _backend.Enqueue(Clip("music", 5f));
            var play = _service.PlayAsync(
                new ResourceKey("music"),
                new AudioPlayOptions(AudioChannel.Music, loop: true)).AsTask();
            yield return WaitFor(play);
            var handle = play.GetAwaiter().GetResult();

            Task canceledWait;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                canceledWait = _service.StopAsync(
                    handle,
                    fadeSeconds: 1f,
                    token: cancellation.Token).AsTask();
                yield return WaitFor(canceledWait);
                Assert.That(canceledWait.IsCanceled, Is.True);
            }

            Assert.That(handle.IsValid, Is.True);
            Assert.That(Entry(handle).State, Is.EqualTo(AudioPlaybackState.FadingOut));
            Tick(1f);
            Assert.That(handle.IsValid, Is.False);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);

            var duplicate = _service.StopAsync(handle).AsTask();
            yield return WaitFor(duplicate);
            Assert.That(duplicate.IsCompletedSuccessfully, Is.True);
        }

        [UnityTest]
        public IEnumerator ForeignHandleIsRejectedAndStaleHandleIsStable()
        {
            _backend.Enqueue(Clip("foreign", 1f));
            var play = _service.PlayAsync(
                new ResourceKey("foreign"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            yield return WaitFor(play);
            var foreign = play.GetAwaiter().GetResult();

            var originalStop = _service.StopAsync(foreign).AsTask();
            yield return WaitFor(originalStop);
            Observe(originalStop);
            Assert.That(foreign.IsValid, Is.False);
            var serviceStop = _service.StopAsync().AsTask();
            yield return WaitFor(serviceStop);
            Observe(serviceStop);
            _service = null;

            var replacement = new AudioService(
                _resources,
                dontDestroyOnLoad: false);
            Assert.Throws<ArgumentException>(
                () => replacement.StopAsync(foreign));
            var replacementStop = replacement.StopAsync().AsTask();
            yield return WaitFor(replacementStop);
            Observe(replacementStop);
        }

        [UnityTest]
        public IEnumerator PreCanceledPlayDoesNotLoadAndCallerCanceledMusicWaitDoesNotCancelCanonical()
        {
            Task preCanceled;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                preCanceled = _service.PlayAsync(
                    new ResourceKey("pre"),
                    new AudioPlayOptions(AudioChannel.SFX),
                    cancellation.Token).AsTask();
                yield return WaitFor(preCanceled);
                Assert.That(preCanceled.IsCanceled, Is.True);
            }

            Assert.That(_backend.LoadCount, Is.Zero);
            var operation = _backend.EnqueueGated(Clip("shared", 1f));
            using (var cancellation = new CancellationTokenSource())
            {
                var abandoned = _service.PlayAsync(
                    new ResourceKey("shared"),
                    new AudioPlayOptions(AudioChannel.Music),
                    cancellation.Token).AsTask();
                var survivor = _service.PlayAsync(
                    new ResourceKey("shared"),
                    new AudioPlayOptions(AudioChannel.Music)).AsTask();
                cancellation.Cancel();
                yield return WaitFor(abandoned);
                Assert.That(abandoned.IsCanceled, Is.True);
                operation.Complete();
                yield return WaitFor(survivor);
                Assert.That(survivor.IsCompletedSuccessfully, Is.True);
                Assert.That(_backend.LoadCount, Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator OneShotCallerCancellationCannotCreateOrphanPlayback()
        {
            var operation = _backend.EnqueueGated(Clip("abandoned-sfx", 5f));
            using (var cancellation = new CancellationTokenSource())
            {
                var play = _service.PlayAsync(
                    new ResourceKey("abandoned-sfx"),
                    new AudioPlayOptions(
                        AudioChannel.SFX,
                        loop: true),
                    cancellation.Token).AsTask();
                cancellation.Cancel();
                yield return WaitFor(play);
                Assert.That(play.IsCanceled, Is.True);
                operation.Complete();
                yield return WaitUntil(
                    () => _resources.Diagnostics.InflightOperationCount == 0);
            }

            Assert.That(_service.Diagnostics.PendingLoadCount, Is.Zero);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.Zero);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            var mandatoryField = typeof(AudioService).GetField(
                "_mandatoryCleanupTasks",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(mandatoryField, Is.Not.Null);
            yield return WaitUntil(
                () =>
                    ((ICollection<Task>)mandatoryField.GetValue(_service))
                    .Count == 0);
            Assert.That(
                ((ICollection<Task>)mandatoryField.GetValue(_service)).Count,
                Is.Zero,
                "Completed abandoned rollbacks must not be retained for the service lifetime.");
        }

        [UnityTest]
        public IEnumerator OneShotCancellationAtSourceCommitStillRollsBackPlayback()
        {
            _backend.Enqueue(Clip("commit-cancel-sfx", 5f));
            using (var cancellation = new CancellationTokenSource())
            {
                var hookField = typeof(AudioService).GetField(
                    "_playbackStartingForTesting",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(hookField, Is.Not.Null);
                hookField.SetValue(
                    _service,
                    new Action<AudioSource>(
                        source =>
                        {
                            if (source.clip != null &&
                                source.clip.name == "commit-cancel-sfx")
                            {
                                cancellation.Cancel();
                            }
                        }));
                Task<IAudioHandle> play;
                try
                {
                    play = _service.PlayAsync(
                        new ResourceKey("commit-cancel-sfx"),
                        new AudioPlayOptions(
                            AudioChannel.SFX,
                            loop: true),
                        cancellation.Token).AsTask();
                    yield return WaitFor(play);
                }
                finally
                {
                    hookField.SetValue(_service, null);
                }

                Assert.That(play.IsCanceled, Is.True);
            }

            Assert.That(_service.Diagnostics.Entries, Is.Empty);
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.Zero);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator NullClipLoadFailureAndStopPendingReleaseAllOperations()
        {
            _backend.Enqueue(null);
            var nullPlay = _service.PlayAsync(
                new ResourceKey("null"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            yield return WaitFor(nullPlay);
            Assert.That(nullPlay.IsFaulted, Is.True);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);

            var pending = _backend.EnqueueGated(Clip("pending", 1f));
            var play = _service.PlayAsync(
                new ResourceKey("pending"),
                new AudioPlayOptions(AudioChannel.Voice)).AsTask();
            var stop = _service.StopAsync().AsTask();
            Assert.That(stop.IsCompleted, Is.False);
            pending.Complete();
            yield return WaitFor(play, stop);
            Assert.That(play.IsCanceled, Is.True);
            Observe(stop);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(_service.Diagnostics.PendingLoadCount, Is.Zero);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
            _service = null;
        }

        [UnityTest]
        public IEnumerator StopWaitsForPendingBackendMandatoryCleanup()
        {
            var pending = _backend.EnqueueGated(Clip("pending-cancel", 1f));
            var play = _service.PlayAsync(
                new ResourceKey("pending-cancel"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            var stop = _service.StopAsync().AsTask();
            var observationEnd = Time.realtimeSinceStartup + 0.25f;
            while (Time.realtimeSinceStartup < observationEnd)
            {
                yield return null;
            }

            var stoppedBeforeBackend = stop.IsCompleted;
            pending.Complete();
            yield return WaitFor(play, stop);
            yield return WaitUntil(
                () => _resources.Diagnostics.InflightOperationCount == 0);
            Assert.That(
                stoppedBeforeBackend,
                Is.False,
                "Audio Stop must await its pending backend acquisition and rollback.");
            Assert.That(play.IsCanceled, Is.True);
            Assert.That(_service.Diagnostics.PendingLoadCount, Is.Zero);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            _service = null;
        }

        [UnityTest]
        public IEnumerator PendingRollbackFailureFaultsCanonicalStop()
        {
            var pending = _backend.EnqueueGated(
                Clip("pending-release-failure", 1f),
                new InvalidOperationException(
                    "pending release failed"));
            var play = _service.PlayAsync(
                new ResourceKey("pending-release-failure"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            var stop = _service.StopAsync().AsTask();
            var observationEnd = Time.realtimeSinceStartup + 0.25f;
            while (Time.realtimeSinceStartup < observationEnd)
            {
                yield return null;
            }

            var stoppedBeforeBackend = stop.IsCompleted;
            pending.Complete();
            yield return WaitFor(play, stop);
            yield return WaitUntil(
                () => _resources.Diagnostics.InflightOperationCount == 0);

            Assert.That(stoppedBeforeBackend, Is.False);
            Assert.That(play.IsFaulted, Is.True);
            Assert.That(stop.IsFaulted, Is.True);
            Assert.That(
                stop.Exception.ToString(),
                Does.Contain("pending release failed"));
            Assert.That(
                _service.Diagnostics.RecentException.ToString(),
                Does.Contain("pending release failed"));
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
            _service = null;
        }

        [UnityTest]
        public IEnumerator AbandonedRollbackFailureFaultsConcurrentCanonicalStop()
        {
            _backend.Enqueue(
                Clip("abandoned-cleanup-failure", 5f),
                new InvalidOperationException(
                    "abandoned cleanup failed"));
            using (var cancellation = new CancellationTokenSource())
            {
                var commitHook = typeof(AudioService).GetField(
                    "_playbackCommittedForTesting",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(commitHook, Is.Not.Null);
                Task canonicalStop = null;
                commitHook.SetValue(
                    _service,
                    new Action<AudioHandle>(
                        handle =>
                        {
                            cancellation.Cancel();
                            canonicalStop = _service.StopAsync().AsTask();
                        }));

                Task<IAudioHandle> play = null;
                try
                {
                    play = _service.PlayAsync(
                        new ResourceKey(
                            "abandoned-cleanup-failure"),
                        new AudioPlayOptions(
                            AudioChannel.SFX,
                            loop: true),
                        cancellation.Token).AsTask();
                    yield return WaitFor(play);
                    yield return WaitUntil(
                        () =>
                            canonicalStop != null &&
                            canonicalStop.IsCompleted);
                }
                finally
                {
                    commitHook.SetValue(_service, null);
                }

                Assert.That(play.IsCanceled, Is.True);
                Assert.That(canonicalStop.IsFaulted, Is.True);
                Assert.That(
                    canonicalStop.Exception.ToString(),
                    Does.Contain("abandoned cleanup failed"));
                Assert.That(
                    _service.Diagnostics.RecentException.ToString(),
                    Does.Contain("abandoned cleanup failed"));
                Assert.That(
                    _resources.Diagnostics.OutstandingLeases,
                    Is.Empty);
                Assert.That(_service.Diagnostics.Entries, Is.Empty);
                _service = null;
            }
        }

        [UnityTest]
        public IEnumerator AbandonedOrdinaryLoadFailureDoesNotFaultServiceStop()
        {
            var operation =
                _backend.EnqueueGated(Clip("abandoned-load-failure", 5f));
            using (var cancellation = new CancellationTokenSource())
            {
                var play = _service.PlayAsync(
                    new ResourceKey("abandoned-load-failure"),
                    new AudioPlayOptions(AudioChannel.SFX),
                    cancellation.Token).AsTask();
                cancellation.Cancel();
                yield return WaitFor(play);
                Assert.That(play.IsCanceled, Is.True);
                operation.Fail(
                    new InvalidOperationException(
                        "ordinary backend load failed"));
                yield return WaitUntil(
                    () =>
                        _resources.Diagnostics.InflightOperationCount == 0);
            }

            var stop = _service.StopAsync().AsTask();
            yield return WaitFor(stop);
            Assert.That(stop.IsCompletedSuccessfully, Is.True);
            Assert.That(_service.Diagnostics.Entries, Is.Empty);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            _service = null;
        }

        [UnityTest]
        public IEnumerator CanonicalCleanupFailureIsAggregatedByStopOnlyOnce()
        {
            var operation = _backend.EnqueueGated(
                Clip("single-owner-cleanup", 5f),
                new InvalidOperationException(
                    "single owner cleanup failed"));
            using (var cancellation = new CancellationTokenSource())
            {
                var play = _service.PlayAsync(
                    new ResourceKey("single-owner-cleanup"),
                    new AudioPlayOptions(AudioChannel.SFX),
                    cancellation.Token).AsTask();
                cancellation.Cancel();
                yield return WaitFor(play);
                Assert.That(play.IsCanceled, Is.True);
                var stop = _service.StopAsync().AsTask();
                operation.Complete();
                yield return WaitFor(stop);

                Assert.That(stop.IsFaulted, Is.True);
                var matchingFailures = stop.Exception
                    .Flatten()
                    .InnerExceptions
                    .Count(
                        exception =>
                            exception.ToString().Contains(
                                "single owner cleanup failed"));
                Assert.That(
                    matchingFailures,
                    Is.EqualTo(1),
                    "Stop must not aggregate the same canonical cleanup failure twice.");
                _service = null;
            }
        }

        [UnityTest]
        public IEnumerator TearDownUnblocksAbandonedGatedBackendOperation()
        {
            _backend.EnqueueGated(Clip("abandoned-by-test", 5f));
            _ = _service.PlayAsync(
                new ResourceKey("abandoned-by-test"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            yield return null;
            Assert.That(_service.Diagnostics.PendingLoadCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator LifetimeCancellationCallbackFailureFaultsCanonicalStop()
        {
            var originalStop = _service.StopAsync().AsTask();
            yield return WaitFor(originalStop);
            Observe(originalStop);
            _service = null;

            var service = new AudioService(
                _resources,
                dontDestroyOnLoad: false);
            var lifetimeField = typeof(AudioService).GetField(
                "_lifetime",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(lifetimeField, Is.Not.Null);
            var lifetime =
                (CancellationTokenSource)lifetimeField.GetValue(service);
            lifetime.Token.Register(
                () => throw new InvalidOperationException(
                    "lifetime cancellation failed"));

            var stop = service.StopAsync().AsTask();
            yield return WaitFor(stop);

            Assert.That(stop.IsFaulted, Is.True);
            Assert.That(
                stop.Exception.ToString(),
                Does.Contain("lifetime cancellation failed"));
            Assert.That(
                service.Diagnostics.RecentException.ToString(),
                Does.Contain("lifetime cancellation failed"));
            Assert.That(FindAudioRoots(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator StopCleansIdleActiveAndExternallyDestroyedRootExactOnce()
        {
            _backend.Enqueue(Clip("first", 1f));
            _backend.Enqueue(Clip("second", 1f));
            var first = _service.PlayAsync(
                new ResourceKey("first"),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            var second = _service.PlayAsync(
                new ResourceKey("second"),
                new AudioPlayOptions(AudioChannel.Voice, loop: true)).AsTask();
            yield return WaitFor(first, second);
            var firstHandle = first.GetAwaiter().GetResult();
            var secondHandle = second.GetAwaiter().GetResult();
            var firstStop = _service.StopAsync(firstHandle).AsTask();
            yield return WaitFor(firstStop);
            Observe(firstStop);
            var root = FindAudioRoot();
            Object.DestroyImmediate(root);

            var stop = _service.StopAsync().AsTask();
            var dispose = _service.DisposeAsync().AsTask();
            yield return WaitFor(stop, dispose);
            Observe(stop);
            Observe(dispose);
            Assert.That(secondHandle.IsValid, Is.False);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(2));
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.Zero);
            Assert.That(_service.Diagnostics.OneShotPool.IdleCount, Is.Zero);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            _service = null;
        }

        [UnityTest]
        public IEnumerator CleanupFailureDoesNotBlockOtherEntriesPoolOrRoot()
        {
            _backend.Enqueue(
                Clip("bad-release", 1f),
                new InvalidOperationException("release failed"));
            _backend.Enqueue(Clip("good-release", 1f));
            var first = _service.PlayAsync(
                new ResourceKey("bad-release"),
                new AudioPlayOptions(
                    AudioChannel.SFX,
                    loop: true)).AsTask();
            var second = _service.PlayAsync(
                new ResourceKey("good-release"),
                new AudioPlayOptions(
                    AudioChannel.Voice,
                    loop: true)).AsTask();
            yield return WaitFor(first, second);
            var firstHandle = first.GetAwaiter().GetResult();
            var secondHandle = second.GetAwaiter().GetResult();

            var stop = _service.StopAsync().AsTask();
            yield return WaitFor(stop);
            Assert.That(stop.IsFaulted, Is.True);
            Assert.That(firstHandle.IsValid, Is.False);
            Assert.That(secondHandle.IsValid, Is.False);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(2));
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.Zero);
            Assert.That(_service.Diagnostics.OneShotPool.IdleCount, Is.Zero);
            Assert.That(FindAudioRoots(), Is.Empty);
            Assert.That(
                _service.Diagnostics.RecentException.ToString(),
                Does.Contain("release failed"));
            _service = null;
        }

        [UnityTest]
        public IEnumerator PreCanceledServiceStopStartsCanonicalCleanup()
        {
            _backend.Enqueue(Clip("loop", 1f));
            var play = _service.PlayAsync(
                new ResourceKey("loop"),
                new AudioPlayOptions(AudioChannel.UI, loop: true)).AsTask();
            yield return WaitFor(play);
            var handle = play.GetAwaiter().GetResult();

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var canceled = _service.StopAsync(cancellation.Token).AsTask();
                yield return WaitFor(canceled);
                Assert.That(canceled.IsCanceled, Is.True);
            }

            var canonical = _service.StopAsync().AsTask();
            yield return WaitFor(canonical);
            Observe(canonical);
            Assert.That(handle.IsValid, Is.False);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            _service = null;
        }

        [UnityTest]
        public IEnumerator ChannelMutationValidationAndBackgroundUseAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _service.SetChannelVolume((AudioChannel)99, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _service.SetChannelMuted((AudioChannel)99, true));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _service.SetChannelPaused((AudioChannel)99, true));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _service.SetChannelMixerGroup((AudioChannel)99, null));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _service.SetChannelVolume(AudioChannel.SFX, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _service.SetChannelVolume(AudioChannel.SFX, 2f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _service.StopAsync(
                    null,
                    fadeSeconds: float.PositiveInfinity));

            var background = Task.Run(
                () => Assert.Throws<InvalidOperationException>(
                    () =>
                    {
                        var ignored = _service.Diagnostics;
                    }));
            yield return WaitFor(background);
            background.GetAwaiter().GetResult();
        }

        [Test]
        public void DiagnosticsAreReadonlySnapshots()
        {
            var diagnostics = _service.Diagnostics;
            Assert.Throws<NotSupportedException>(
                () => ((IList<AudioChannelDiagnostics>)diagnostics.Channels)
                    .Add(null));
            Assert.Throws<NotSupportedException>(
                () => ((IList<AudioEntryDiagnostics>)diagnostics.Entries)
                    .Add(null));
            _service.SetChannelVolume(AudioChannel.SFX, 0.3f);
            Assert.That(
                diagnostics.Channels.Single(
                    item => item.Channel == AudioChannel.SFX).Volume,
                Is.EqualTo(1f));
            Assert.That(Channel(AudioChannel.SFX).Volume, Is.EqualTo(0.3f));
        }

        [Test]
        public void StopRejectsAlternativeHandleImplementation()
        {
            Assert.Throws<ArgumentException>(
                () => _service.StopAsync(new AlternativeAudioHandle()));
        }

        [UnityTest]
        public IEnumerator ModuleDeclaresExactDependenciesAndPluggingRecreatesOwnedRoot()
        {
            var localStop = _service.StopAsync().AsTask();
            yield return WaitFor(localStop);
            Observe(localStop);
            _service = null;
            var runtime = new FrameworkRuntime();
            var runtimeBackend = new FakeResourceBackend();
            var runtimeResources = new ResourceService(runtimeBackend);
            _auxiliaryRuntimes.Add(runtime);
            _auxiliaryBackends.Add(runtimeBackend);
            _auxiliaryResources.Add(runtimeResources);
            var descriptors = new[]
            {
                new ModuleDescriptor(
                    "Resource",
                    Array.Empty<string>(),
                    0,
                    () => new TestResourceModule(runtimeResources)),
                new ModuleDescriptor(
                    "Pool",
                    new[] { "Resource" },
                    1,
                    () => new PoolModule()),
                new ModuleDescriptor(
                    "Audio",
                    new[] { "Resource", "Pool" },
                    2,
                    () => new AudioModule())
            };
            var start = runtime.StartAsync(
                descriptors,
                CancellationToken.None).AsTask();
            yield return WaitFor(start);
            Observe(start);
            var original = runtime.Services.Resolve<IAudioService>();
            runtimeBackend.Enqueue(Clip("plugging-active", 5f));
            var activePlay = original.PlayAsync(
                new ResourceKey("plugging-active"),
                new AudioPlayOptions(
                    AudioChannel.Voice,
                    loop: true)).AsTask();
            yield return WaitFor(activePlay);
            var activeHandle =
                activePlay.GetAwaiter().GetResult();
            Assert.That(activeHandle.IsValid, Is.True);

            var unload = runtime.UnloadAsync(
                "Audio",
                ModuleUnloadMode.RequireNoDependents,
                CancellationToken.None).AsTask();
            yield return WaitFor(unload);
            var unloadResult = unload.GetAwaiter().GetResult();
            var install = runtime.InstallAsync(
                new ModuleDescriptor(
                    "Audio",
                    new[] { "Resource", "Pool" },
                    2,
                    () => new AudioModule()),
                CancellationToken.None).AsTask();
            yield return WaitFor(install);
            Observe(install);
            var current = runtime.Services.Resolve<IAudioService>();
            CollectionAssert.AreEqual(
                new[] { "Audio" },
                unloadResult.UnloadedModuleIds);
            Assert.That(current, Is.Not.SameAs(original));
            Assert.That(activeHandle.IsValid, Is.False);
            Assert.That(original.Diagnostics.Entries, Is.Empty);
            Assert.That(runtimeBackend.ReleaseCount, Is.EqualTo(1));
            Assert.That(
                runtimeResources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(FindAudioRoots().Length, Is.EqualTo(1));

            var stop = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stop);
            Observe(stop);
            var dispose = runtime.DisposeAsync().AsTask();
            yield return WaitFor(dispose);
            Observe(dispose);

            var module = new AudioModule();
            Assert.That(module.Id, Is.EqualTo("Audio"));
            CollectionAssert.AreEqual(
                new[] { "Resource", "Pool" },
                module.Dependencies);
        }

        private AudioEntryDiagnostics Entry(IAudioHandle handle)
        {
            return _service.Diagnostics.Entries.Single(
                item => item.InstanceId == handle.InstanceId);
        }

        private sealed class AlternativeAudioHandle : IAudioHandle
        {
            public Guid InstanceId { get; } = Guid.NewGuid();
            public ResourceKey ResourceKey => new ResourceKey("alternative");
            public AudioChannel Channel => AudioChannel.SFX;
            public bool IsValid => true;
        }

        private AudioChannelDiagnostics Channel(AudioChannel channel)
        {
            return _service.Diagnostics.Channels.Single(
                item => item.Channel == channel);
        }

        private void Tick(float deltaTime)
        {
            var method = typeof(AudioService).GetMethod(
                "TickForTesting",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(_service, new object[] { deltaTime });
        }

        private static AudioClip Clip(string name, float seconds)
        {
            return AudioClip.Create(
                name,
                Math.Max(1, Mathf.CeilToInt(44100f * seconds)),
                1,
                44100,
                false);
        }

        private static GameObject FindAudioRoot()
        {
            return FindAudioRoots().Single();
        }

        private static GameObject[] FindAudioRoots()
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(
                    item =>
                        item != null &&
                        item.name == "[ArkFramework.Audio]" &&
                        (item.scene.IsValid() ||
                         (item.hideFlags & HideFlags.HideAndDontSave) != 0))
                .ToArray();
        }

        private static HashSet<int> SnapshotAudioSourceIds()
        {
            return new HashSet<int>(
                Resources.FindObjectsOfTypeAll<AudioSource>()
                    .Where(IsRuntimeObject)
                    .Select(item => item.GetInstanceID()));
        }

        private static bool IsRuntimeObject(Component component)
        {
            return component != null &&
                   (component.gameObject.scene.IsValid() ||
                    (component.gameObject.hideFlags &
                     HideFlags.HideAndDontSave) != 0);
        }

        private static IEnumerator WaitFor(params Task[] tasks)
        {
            var timeout = Time.realtimeSinceStartup + 5f;
            while (tasks.Any(task => !task.IsCompleted))
            {
                if (Time.realtimeSinceStartup >= timeout)
                {
                    Assert.Fail("Timed out waiting for Audio task.");
                }

                yield return null;
            }
        }

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var timeout = Time.realtimeSinceStartup + 5f;
            while (!condition())
            {
                if (Time.realtimeSinceStartup >= timeout)
                {
                    Assert.Fail("Timed out waiting for Audio state.");
                }

                yield return null;
            }
        }

        private static void Observe(Task task)
        {
            if (task.IsFaulted)
            {
                task.GetAwaiter().GetResult();
            }
        }

        private sealed class FakeResourceBackend : IResourceBackend
        {
            private readonly Queue<AudioOperation> _operations =
                new Queue<AudioOperation>();
            private readonly List<AudioOperation> _allOperations =
                new List<AudioOperation>();

            public int LoadCount { get; private set; }

            public int ReleaseCount { get; private set; }

            public AudioOperation Enqueue(
                AudioClip clip,
                Exception releaseFailure = null)
            {
                var operation = new AudioOperation(
                    clip,
                    gated: false,
                    () =>
                    {
                        ReleaseCount++;
                        if (releaseFailure != null)
                        {
                            throw releaseFailure;
                        }
                    });
                _operations.Enqueue(operation);
                _allOperations.Add(operation);
                return operation;
            }

            public AudioOperation EnqueueGated(
                AudioClip clip,
                Exception releaseFailure = null)
            {
                var operation = new AudioOperation(
                    clip,
                    gated: true,
                    () =>
                    {
                        ReleaseCount++;
                        if (releaseFailure != null)
                        {
                            throw releaseFailure;
                        }
                    });
                _operations.Enqueue(operation);
                _allOperations.Add(operation);
                return operation;
            }

            public void FailAllPending()
            {
                foreach (var operation in _allOperations)
                {
                    if (!operation.Task.IsCompleted)
                    {
                        operation.Fail(
                            new OperationCanceledException(
                                "Audio test backend operation was abandoned."));
                    }
                }
            }

            public IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
                where T : Object
            {
                LoadCount++;
                if (typeof(T) != typeof(AudioClip) ||
                    _operations.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Unexpected audio load for " + key.Value + ".");
                }

                return (IResourceOperation<T>)(object)_operations.Dequeue();
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
                throw new NotSupportedException();
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

        private sealed class AudioOperation : IResourceOperation<AudioClip>
        {
            private readonly TaskCompletionSource<AudioClip> _completion;
            private Action _release;

            public AudioOperation(
                AudioClip clip,
                bool gated,
                Action release)
            {
                Clip = clip;
                _release = release;
                _completion = new TaskCompletionSource<AudioClip>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!gated)
                {
                    _completion.SetResult(clip);
                }
            }

            public AudioClip Clip { get; }

            public Task<AudioClip> Task => _completion.Task;

            public void Complete()
            {
                _completion.TrySetResult(Clip);
            }

            public void Fail(Exception exception)
            {
                _completion.TrySetException(exception);
            }

            public void Release()
            {
                Interlocked.Exchange(ref _release, null)?.Invoke();
            }
        }

        private sealed class TestResourceModule : IFrameworkModule
        {
            private readonly ResourceService _service;

            public TestResourceModule(ResourceService service)
            {
                _service = service;
            }

            public string Id => "Resource";

            public IReadOnlyCollection<string> Dependencies =>
                Array.Empty<string>();

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                context.ModuleScope.RegisterInstance<IResourceService>(
                    _service);
                return default;
            }

            public ValueTask StartAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return default;
            }

            public ValueTask StopAsync(CancellationToken token)
            {
                return _service.StopAsync(token);
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

    }
}
