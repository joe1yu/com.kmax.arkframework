using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class AudioService : IAudioService, IAsyncDisposable
    {
        private readonly IResourceService _resources;
        private readonly int _mainThreadId;
        private readonly CancellationTokenSource _lifetime =
            new CancellationTokenSource();
        private readonly Dictionary<AudioChannel, ChannelState> _channels =
            new Dictionary<AudioChannel, ChannelState>();
        private readonly Dictionary<Guid, Entry> _entries =
            new Dictionary<Guid, Entry>();
        private readonly Dictionary<Guid, PendingEntry> _pending =
            new Dictionary<Guid, PendingEntry>();
        private readonly Dictionary<ResourceKey, Task<AudioHandle>>
            _musicFlights =
                new Dictionary<ResourceKey, Task<AudioHandle>>();
        private readonly Dictionary<ResourceKey, long>
            _musicFlightSequences =
                new Dictionary<ResourceKey, long>();
        private readonly HashSet<Task> _pendingTasks = new HashSet<Task>();
        private readonly HashSet<Task> _mandatoryCleanupTasks =
            new HashSet<Task>();
        private readonly List<Exception> _mandatoryCleanupFailures =
            new List<Exception>();
        private readonly GameObject _root;
        private readonly AudioRootMarker _rootMarker;
        private readonly AudioDriver _driver;
        private readonly AudioSource[] _musicSources;
        private readonly ObjectPool<AudioSourcePoolItem> _oneShotPool;
        private long _nextPoolSequence;
        private long _nextMusicRequestSequence;
        private long _latestMusicRequestSequence;
        private Entry _currentMusic;
        private Exception _recentException;
        private bool _stopped;
        private int _disposed;
        private Task _stopTask;
        private Action<AudioSource> _playbackStartingForTesting;
        private Action<AudioHandle> _playbackCommittedForTesting;
        private Action<AudioSource> _sourceResettingForTesting;

        public AudioService(
            IResourceService resources,
            bool dontDestroyOnLoad = true)
        {
            EnsureUnityConstructionThread();
            _resources =
                resources ?? throw new ArgumentNullException(nameof(resources));
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EnsureNoLiveRoot();
            for (var value = AudioChannel.Music;
                 value <= AudioChannel.Voice;
                 value++)
            {
                _channels.Add(value, new ChannelState(value));
            }

            _root = new GameObject(
                "[ArkFramework.Audio]",
                typeof(AudioRootMarker),
                typeof(AudioDriver),
                typeof(AudioSource),
                typeof(AudioSource));
            _root.hideFlags = HideFlags.HideAndDontSave;
            if (dontDestroyOnLoad && Application.isPlaying)
            {
                Object.DontDestroyOnLoad(_root);
            }

            _rootMarker = _root.GetComponent<AudioRootMarker>();
            _driver = _root.GetComponent<AudioDriver>();
            _driver.Bind(this);
            _musicSources = _root.GetComponents<AudioSource>();
            for (var index = 0; index < _musicSources.Length; index++)
            {
                ResetSource(_musicSources[index]);
            }

            _oneShotPool = new ObjectPool<AudioSourcePoolItem>(
                () => new AudioSourcePoolItem(
                    _root.transform,
                    ++_nextPoolSequence),
                maxIdleCapacity: 32,
                onRent: item => item.Rent(),
                onReturn: item => item.Return(),
                onDestroy: item => item.Destroy());
        }

        public AudioDiagnostics Diagnostics
        {
            get
            {
                EnsureMainThread();
                var channels = new AudioChannelDiagnostics[_channels.Count];
                var channelIndex = 0;
                foreach (var channel in _channels.Values)
                {
                    channels[channelIndex++] =
                        new AudioChannelDiagnostics(
                            channel.Channel,
                            channel.Volume,
                            channel.Muted,
                            channel.Paused,
                            channel.MixerGroup == null
                                ? null
                                : channel.MixerGroup.name);
                }

                Array.Sort(
                    channels,
                    (left, right) =>
                        left.Channel.CompareTo(right.Channel));
                var entries =
                    new AudioEntryDiagnostics[
                        _pending.Count + _entries.Count];
                var entryIndex = 0;
                foreach (var pending in _pending.Values)
                {
                    entries[entryIndex++] =
                        new AudioEntryDiagnostics(
                            pending.InstanceId,
                            pending.Key,
                            pending.Options.Channel,
                            pending.Options.Loop,
                            AudioPlaybackState.Pending,
                            pending.Options.Volume,
                            0f);
                }

                foreach (var entry in _entries.Values)
                {
                    entries[entryIndex++] =
                        new AudioEntryDiagnostics(
                            entry.InstanceId,
                            entry.Key,
                            entry.Options.Channel,
                            entry.Options.Loop,
                            DiagnosticState(entry),
                            entry.Options.Volume,
                            EffectiveVolume(entry));
                }

                Array.Sort(
                    entries,
                    (left, right) =>
                        left.InstanceId.CompareTo(right.InstanceId));
                return new AudioDiagnostics(
                    AudioDiagnostics.ReadOnly(channels),
                    _currentMusic == null
                        ? (ResourceKey?)null
                        : _currentMusic.Key,
                    _currentMusic?.Handle,
                    AudioDiagnostics.ReadOnly(entries),
                    _oneShotPool.Diagnostics,
                    _pending.Count,
                    _recentException);
            }
        }

        public ValueTask<IAudioHandle> PlayAsync(
            ResourceKey key,
            AudioPlayOptions options,
            CancellationToken token = default)
        {
            // 先调用默认实现，保留参数、取消和主线程校验的同步异常语义。
            var operation = PlayDefaultAsync(key, options, token);
            return new ValueTask<IAudioHandle>(
                AsAudioHandleAsync(operation));
        }

        private static async Task<IAudioHandle> AsAudioHandleAsync(
            ValueTask<AudioHandle> operation)
        {
            return await operation;
        }

        private ValueTask<AudioHandle> PlayDefaultAsync(
            ResourceKey key,
            AudioPlayOptions options,
            CancellationToken token = default)
        {
            EnsureMainThread();
            ValidateKey(key);
            AudioValidation.ValidateChannel(options.Channel);
            AudioValidation.ValidateUnit(
                options.Volume,
                nameof(options.Volume));
            AudioValidation.ValidateFade(
                options.FadeSeconds,
                nameof(options.FadeSeconds));
            if (token.IsCancellationRequested)
            {
                return new ValueTask<AudioHandle>(
                    Task.FromCanceled<AudioHandle>(token));
            }

            EnsureRunning();

            Task<AudioHandle> canonical;
            if (options.Channel == AudioChannel.Music)
            {
                if (_currentMusic != null &&
                    _currentMusic.Key == key &&
                    _entries.ContainsKey(_currentMusic.InstanceId))
                {
                    _latestMusicRequestSequence =
                        ++_nextMusicRequestSequence;
                    canonical =
                        Task.FromResult(_currentMusic.Handle);
                }
                else if (_musicFlights.TryGetValue(key, out canonical) &&
                         !canonical.IsCompleted)
                {
                    _latestMusicRequestSequence =
                        _musicFlightSequences[key];
                }
                else
                {
                    _musicFlights.Remove(key);
                    _musicFlightSequences.Remove(key);
                    var sequence = ++_nextMusicRequestSequence;
                    _latestMusicRequestSequence = sequence;
                    canonical = StartPlay(
                        key,
                        options,
                        isMusic: true,
                        sequence,
                        CancellationToken.None);
                    _musicFlights.Add(key, canonical);
                    _musicFlightSequences.Add(key, sequence);
                    _ = RemoveMusicFlightAsync(key, canonical);
                }
            }
            else
            {
                canonical = StartPlay(
                    key,
                    options,
                    isMusic: false,
                    sequence: 0,
                    acquisitionToken: token);
            }

            return new ValueTask<AudioHandle>(
                options.Channel == AudioChannel.Music
                    ? AwaitMusicCallerAsync(canonical, token)
                    : AwaitOneShotCallerAsync(canonical, token));
        }

        public ValueTask StopAsync(
            IAudioHandle handle,
            float fadeSeconds = 0f,
            CancellationToken token = default)
        {
            EnsureMainThread();
            AudioValidation.ValidateFade(fadeSeconds, nameof(fadeSeconds));
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            if (!(handle is AudioHandle defaultHandle))
            {
                throw new ArgumentException(
                    "The audio handle was not created by the default AudioService.",
                    nameof(handle));
            }

            if (!defaultHandle.IsOwnedBy(this))
            {
                throw new ArgumentException(
                    "The audio handle belongs to another Audio service.",
                    nameof(handle));
            }

            Task cleanup;
            if (!_entries.TryGetValue(defaultHandle.InstanceId, out var entry) ||
                !ReferenceEquals(entry.Handle, defaultHandle))
            {
                cleanup = Task.CompletedTask;
            }
            else
            {
                cleanup = BeginStop(entry, fadeSeconds);
            }

            return new ValueTask(AwaitCallerAsync(cleanup, token));
        }

        public void SetChannelVolume(AudioChannel channel, float volume)
        {
            EnsureMainThread();
            EnsureRunning();
            AudioValidation.ValidateChannel(channel);
            AudioValidation.ValidateUnit(volume, nameof(volume));
            _channels[channel].Volume = volume;
            ApplyChannel(channel);
        }

        public void SetChannelMuted(AudioChannel channel, bool muted)
        {
            EnsureMainThread();
            EnsureRunning();
            AudioValidation.ValidateChannel(channel);
            _channels[channel].Muted = muted;
            ApplyChannel(channel);
        }

        public void SetChannelPaused(AudioChannel channel, bool paused)
        {
            EnsureMainThread();
            EnsureRunning();
            AudioValidation.ValidateChannel(channel);
            var state = _channels[channel];
            if (state.Paused == paused)
            {
                return;
            }

            state.Paused = paused;
            foreach (var entry in _entries.Values)
            {
                if (entry.Options.Channel != channel ||
                    entry.Source == null)
                {
                    continue;
                }

                if (paused)
                {
                    entry.Source.Pause();
                }
                else
                {
                    entry.Source.UnPause();
                }
            }
        }

        public void SetChannelMixerGroup(
            AudioChannel channel,
            AudioMixerGroup group)
        {
            EnsureMainThread();
            EnsureRunning();
            AudioValidation.ValidateChannel(channel);
            _channels[channel].MixerGroup = group;
            ApplyChannel(channel);
        }

        public ValueTask StopAsync(CancellationToken token = default)
        {
            EnsureMainThread();
            var stopTask = EnsureStopStarted();
            return new ValueTask(AwaitCallerAsync(stopTask, token));
        }

        public ValueTask DisposeAsync()
        {
            EnsureMainThread();
            return new ValueTask(DisposeCoreAsync());
        }

        internal bool IsHandleValid(AudioHandle handle)
        {
            EnsureMainThread();
            return handle != null &&
                   handle.IsOwnedBy(this) &&
                   _entries.TryGetValue(handle.InstanceId, out var entry) &&
                   ReferenceEquals(entry.Handle, handle);
        }

        internal void TickForTesting(float unscaledDeltaTime)
        {
            Tick(unscaledDeltaTime);
        }

        internal void Tick(float unscaledDeltaTime)
        {
            EnsureMainThread();
            if (_stopped ||
                float.IsNaN(unscaledDeltaTime) ||
                float.IsInfinity(unscaledDeltaTime) ||
                unscaledDeltaTime < 0f)
            {
                return;
            }

            var entries = _entries.Values.ToArray();
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (!_entries.ContainsKey(entry.InstanceId))
                {
                    continue;
                }

                var channel = _channels[entry.Options.Channel];
                if (channel.Paused)
                {
                    if (Application.isPlaying)
                    {
                        entry.LastRealtime =
                            Time.realtimeSinceStartup;
                    }

                    continue;
                }

                try
                {
                    AdvanceFade(entry, unscaledDeltaTime);
                    if (!_entries.ContainsKey(entry.InstanceId))
                    {
                        continue;
                    }

                    if (!entry.Options.Loop)
                    {
                        if (Application.isPlaying &&
                            entry.NaturalEndGuard !=
                            NaturalEndGuardState.Running)
                        {
                            entry.NaturalEndGuard =
                                entry.NaturalEndGuard ==
                                NaturalEndGuardState.AwaitingFirstDriverTick
                                    ? NaturalEndGuardState
                                        .ProtectingCallerObservableFrame
                                    : NaturalEndGuardState.Running;
                            entry.LastRealtime =
                                Time.realtimeSinceStartup;
                            continue;
                        }
                        else
                        {
                            var elapsed = unscaledDeltaTime;
                            if (Application.isPlaying)
                            {
                                var now = Time.realtimeSinceStartup;
                                elapsed = Mathf.Max(
                                    0f,
                                    now - entry.LastRealtime);
                                entry.LastRealtime = now;
                            }

                            entry.RemainingSeconds -= elapsed;
                            if (entry.RemainingSeconds <= 0f)
                            {
                                FinalizeEntry(entry);
                                continue;
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    RecordException(exception);
                    CompleteStop(entry, exception);
                }
            }
        }

        private Task<AudioHandle> StartPlay(
            ResourceKey key,
            AudioPlayOptions options,
            bool isMusic,
            long sequence,
            CancellationToken acquisitionToken)
        {
            var pending = new PendingEntry(
                Guid.NewGuid(),
                key,
                options,
                sequence,
                acquisitionToken);
            var completion = new TaskCompletionSource<AudioHandle>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var canonical = completion.Task;
            _pending.Add(pending.InstanceId, pending);
            _pendingTasks.Add(canonical);
            _ = CompletePlayAsync(
                pending,
                isMusic,
                completion);
            return canonical;
        }

        private async Task CompletePlayAsync(
            PendingEntry pending,
            bool isMusic,
            TaskCompletionSource<AudioHandle> completion)
        {
            IAssetLease<AudioClip> lease = null;
            AudioHandle result = null;
            Exception failure = null;
            OperationCanceledException cancellation = null;
            try
            {
                lease = await _resources.LoadAsync<AudioClip>(
                    pending.Key,
                    CancellationToken.None);
                if (lease == null ||
                    lease.Asset == null)
                {
                    throw new InvalidOperationException(
                        "Audio resource '" + pending.Key.Value +
                        "' returned a null or destroyed AudioClip.");
                }

                pending.AcquisitionToken.ThrowIfCancellationRequested();
                if (_stopped || _lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(_lifetime.Token);
                }

                if (isMusic &&
                    pending.Sequence != _latestMusicRequestSequence)
                {
                    throw new OperationCanceledException(
                        new CancellationToken(canceled: true));
                }

                var entry = isMusic
                    ? BindMusic(pending, lease)
                    : BindOneShot(pending, lease);
                lease = null;
                if (!isMusic &&
                    pending.AcquisitionToken.IsCancellationRequested)
                {
                    try
                    {
                        FinalizeEntry(entry);
                    }
                    catch (Exception cleanup)
                    {
                        throw new AudioCleanupException(
                            "Canceled AudioSource rollback failed.",
                            cleanup);
                    }

                    throw new OperationCanceledException(
                        pending.AcquisitionToken);
                }

                if (!isMusic)
                {
                    _playbackCommittedForTesting?.Invoke(entry.Handle);
                }

                result = entry.Handle;
            }
            catch (OperationCanceledException exception)
            {
                cancellation = exception;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (lease != null)
                {
                    try
                    {
                        lease.Dispose();
                    }
                    catch (Exception exception)
                    {
                        var primary = failure ??
                            (Exception)cancellation;
                        failure = new AudioCleanupException(
                            "Audio acquisition rollback failed.",
                            Combine(primary, exception));
                        cancellation = null;
                    }
                }

                _pending.Remove(pending.InstanceId);
                _pendingTasks.Remove(completion.Task);
            }

            if (failure != null)
            {
                RecordException(failure);
                completion.TrySetException(failure);
            }
            else if (cancellation != null)
            {
                completion.TrySetCanceled(
                    cancellation.CancellationToken
                        .IsCancellationRequested
                        ? cancellation.CancellationToken
                        : new CancellationToken(canceled: true));
            }
            else
            {
                completion.TrySetResult(result);
            }
        }

        private Entry BindOneShot(
            PendingEntry pending,
            IAssetLease<AudioClip> lease)
        {
            AudioSourcePoolItem item = null;
            try
            {
                item = _oneShotPool.Rent();
                var entry = CreateEntry(
                    pending,
                    lease,
                    item.Source,
                    item);
                ConfigureAndPlay(entry);
                _entries.Add(entry.InstanceId, entry);
                return entry;
            }
            catch
            {
                if (item != null)
                {
                    _oneShotPool.Return(item);
                }

                throw;
            }
        }

        private Entry BindMusic(
            PendingEntry pending,
            IAssetLease<AudioClip> lease)
        {
            Entry olderOutgoing = null;
            foreach (var candidate in _entries.Values)
            {
                if (candidate.Options.Channel == AudioChannel.Music &&
                    !ReferenceEquals(candidate, _currentMusic))
                {
                    olderOutgoing = candidate;
                    break;
                }
            }

            if (olderOutgoing != null)
            {
                FinalizeEntry(olderOutgoing);
            }

            var oldCurrent = _currentMusic;
            var source =
                oldCurrent == null ||
                ReferenceEquals(oldCurrent.Source, _musicSources[1])
                    ? _musicSources[0]
                    : _musicSources[1];
            var entry = CreateEntry(
                pending,
                lease,
                source,
                null);
            try
            {
                ConfigureAndPlay(entry);
                _entries.Add(entry.InstanceId, entry);
                _currentMusic = entry;
            }
            catch (Exception playbackFailure)
            {
                _entries.Remove(entry.InstanceId);
                if (ReferenceEquals(_currentMusic, entry))
                {
                    _currentMusic = oldCurrent;
                }

                Exception failure = playbackFailure;
                try
                {
                    ResetSourceForCleanup(source);
                }
                catch (Exception cleanupFailure)
                {
                    failure = Combine(failure, cleanupFailure);
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }

            if (oldCurrent != null)
            {
                if (pending.Options.FadeSeconds <= 0f)
                {
                    try
                    {
                        FinalizeEntry(oldCurrent);
                    }
                    catch (Exception replacementFailure)
                    {
                        _entries.Remove(entry.InstanceId);
                        _currentMusic = null;
                        Exception failure = replacementFailure;
                        try
                        {
                            ResetSourceForCleanup(source);
                        }
                        catch (Exception cleanupFailure)
                        {
                            failure = Combine(failure, cleanupFailure);
                        }

                        ExceptionDispatchInfo.Capture(failure).Throw();
                        throw;
                    }
                }
                else
                {
                    StartFade(
                        oldCurrent,
                        0f,
                        pending.Options.FadeSeconds,
                        finishOnComplete: true);
                }
            }

            return entry;
        }

        private Entry CreateEntry(
            PendingEntry pending,
            IAssetLease<AudioClip> lease,
            AudioSource source,
            AudioSourcePoolItem poolItem)
        {
            var handle = new AudioHandle(
                this,
                pending.InstanceId,
                pending.Key,
                pending.Options.Channel);
            return new Entry(
                pending.InstanceId,
                pending.Key,
                pending.Options,
                handle,
                lease,
                source,
                poolItem,
                lease.Asset.length);
        }

        private void ConfigureAndPlay(Entry entry)
        {
            if (entry.Source == null)
            {
                throw new InvalidOperationException(
                    "The AudioSource was destroyed before playback.");
            }

            var channel = _channels[entry.Options.Channel];
            entry.Source.Stop();
            entry.Source.clip = entry.Lease.Asset;
            entry.Source.loop = entry.Options.Loop;
            entry.Source.outputAudioMixerGroup = channel.MixerGroup;
            entry.Source.mute = channel.Muted;
            entry.Gain = entry.Options.FadeSeconds > 0f ? 0f : 1f;
            entry.State = entry.Options.FadeSeconds > 0f
                ? AudioPlaybackState.FadingIn
                : AudioPlaybackState.Playing;
            if (entry.Options.FadeSeconds > 0f)
            {
                StartFade(
                    entry,
                    1f,
                    entry.Options.FadeSeconds,
                    finishOnComplete: false);
            }

            ApplyVolume(entry);
            _playbackStartingForTesting?.Invoke(entry.Source);
            entry.Source.Play();
            if (channel.Paused)
            {
                entry.Source.Pause();
            }
        }

        private Task BeginStop(Entry entry, float fadeSeconds)
        {
            if (entry.StopTask != null)
            {
                return entry.StopTask;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            entry.StopCompletion = completion;
            entry.StopTask = completion.Task;
            if (fadeSeconds <= 0f)
            {
                try
                {
                    FinalizeEntry(entry);
                }
                catch (Exception exception)
                {
                    RecordException(exception);
                    CompleteStop(entry, exception);
                }
            }
            else
            {
                StartFade(
                    entry,
                    0f,
                    fadeSeconds,
                    finishOnComplete: true);
            }

            return entry.StopTask;
        }

        private void StartFade(
            Entry entry,
            float target,
            float duration,
            bool finishOnComplete)
        {
            entry.FadeStartGain = entry.Gain;
            entry.FadeTargetGain = target;
            entry.FadeDuration = duration;
            entry.FadeElapsed = 0f;
            entry.FinishAfterFade = finishOnComplete;
            entry.State = target <= 0f
                ? AudioPlaybackState.FadingOut
                : AudioPlaybackState.FadingIn;
            ApplyVolume(entry);
        }

        private void AdvanceFade(Entry entry, float deltaTime)
        {
            if (entry.State != AudioPlaybackState.FadingIn &&
                entry.State != AudioPlaybackState.FadingOut)
            {
                return;
            }

            entry.FadeElapsed += deltaTime;
            var progress = entry.FadeDuration <= 0f
                ? 1f
                : Mathf.Clamp01(entry.FadeElapsed / entry.FadeDuration);
            entry.Gain = Mathf.Lerp(
                entry.FadeStartGain,
                entry.FadeTargetGain,
                progress);
            ApplyVolume(entry);
            if (progress < 1f)
            {
                return;
            }

            if (entry.FinishAfterFade)
            {
                FinalizeEntry(entry);
            }
            else
            {
                entry.State = AudioPlaybackState.Playing;
            }
        }

        private void ApplyChannel(AudioChannel channel)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Options.Channel != channel)
                {
                    continue;
                }

                if (entry.Source != null)
                {
                    entry.Source.outputAudioMixerGroup =
                        _channels[channel].MixerGroup;
                    entry.Source.mute = _channels[channel].Muted;
                    ApplyVolume(entry);
                }
            }
        }

        private void ApplyVolume(Entry entry)
        {
            if (entry.Source != null)
            {
                entry.Source.volume = EffectiveVolume(entry);
            }
        }

        private float EffectiveVolume(Entry entry)
        {
            return entry.Options.Volume *
                   _channels[entry.Options.Channel].Volume *
                   entry.Gain;
        }

        private AudioPlaybackState DiagnosticState(Entry entry)
        {
            return _channels[entry.Options.Channel].Paused
                ? AudioPlaybackState.Paused
                : entry.State;
        }

        private void FinalizeEntry(Entry entry)
        {
            if (!_entries.Remove(entry.InstanceId))
            {
                return;
            }

            if (ReferenceEquals(_currentMusic, entry))
            {
                _currentMusic = null;
            }

            Exception failure = null;
            try
            {
                if (entry.Source != null)
                {
                    entry.Source.Stop();
                    entry.Source.clip = null;
                    entry.Source.outputAudioMixerGroup = null;
                }
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            if (entry.PoolItem != null)
            {
                try
                {
                    _oneShotPool.Return(entry.PoolItem);
                }
                catch (Exception exception)
                {
                    failure = Combine(failure, exception);
                }
            }
            else
            {
                try
                {
                    ResetSourceForCleanup(entry.Source);
                }
                catch (Exception exception)
                {
                    failure = Combine(failure, exception);
                }
            }

            try
            {
                entry.Lease?.Dispose();
                entry.Lease = null;
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            CompleteStop(entry, failure);
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static void CompleteStop(Entry entry, Exception failure)
        {
            if (entry.StopCompletion == null)
            {
                return;
            }

            if (failure == null)
            {
                entry.StopCompletion.TrySetResult(true);
            }
            else
            {
                entry.StopCompletion.TrySetException(failure);
            }
        }

        private Task EnsureStopStarted()
        {
            if (_stopTask != null)
            {
                return _stopTask;
            }

            _stopped = true;

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            Exception cancellationFailure = null;
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
                RecordException(exception);
            }

            _ = StopCoreAsync(completion, cancellationFailure);
            return _stopTask;
        }

        private async Task StopCoreAsync(
            TaskCompletionSource<bool> completion,
            Exception failure)
        {
            try
            {
                var pending = _pendingTasks.ToArray();
                for (var index = 0; index < pending.Length; index++)
                {
                    try
                    {
                        await pending[index];
                    }
                    catch (OperationCanceledException)
                    {
                        // Service cancellation is expected.
                    }
                    catch (AudioCleanupException exception)
                    {
                        failure = Combine(failure, exception);
                    }
                    catch
                    {
                        // Load failures are already reported to their callers
                        // and diagnostics; Stop still performs cleanup.
                    }
                }

                while (_mandatoryCleanupTasks.Count > 0)
                {
                    var mandatoryCleanup =
                        _mandatoryCleanupTasks.ToArray();
                    for (var index = 0;
                         index < mandatoryCleanup.Length;
                        index++)
                    {
                        await mandatoryCleanup[index];
                        _mandatoryCleanupTasks.Remove(
                            mandatoryCleanup[index]);
                    }
                }

                for (var index = 0;
                     index < _mandatoryCleanupFailures.Count;
                     index++)
                {
                    var cleanupFailure =
                        _mandatoryCleanupFailures[index];
                    if (!ContainsExceptionReference(
                            failure,
                            cleanupFailure))
                    {
                        failure = Combine(
                            failure,
                            cleanupFailure);
                    }
                }

                _mandatoryCleanupFailures.Clear();

                var entries = _entries.Values.ToArray();
                for (var index = 0; index < entries.Length; index++)
                {
                    try
                    {
                        FinalizeEntry(entries[index]);
                    }
                    catch (Exception exception)
                    {
                        failure = Combine(failure, exception);
                    }
                }

                try
                {
                    _oneShotPool.Clear();
                }
                catch (Exception exception)
                {
                    failure = Combine(failure, exception);
                }

                try
                {
                    await DestroyRootAsync();
                }
                catch (Exception exception)
                {
                    failure = Combine(failure, exception);
                }
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }

            if (failure == null)
            {
                completion.TrySetResult(true);
            }
            else
            {
                RecordException(failure);
                completion.TrySetException(failure);
            }
        }

        private async Task DestroyRootAsync()
        {
            if (_driver != null)
            {
                _driver.Unbind(this);
            }

            if (_root != null)
            {
                _root.SetActive(false);
                if (Application.isPlaying)
                {
                    Object.Destroy(_root);
                }
                else
                {
                    Object.DestroyImmediate(_root);
                }
            }

            if (_rootMarker != null)
            {
                await _rootMarker.Destroyed;
            }
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                await EnsureStopStarted();
            }
            finally
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _lifetime.Dispose();
                }
            }
        }

        private async Task RemoveMusicFlightAsync(
            ResourceKey key,
            Task<AudioHandle> canonical)
        {
            try
            {
                await canonical;
            }
            catch
            {
                // The canonical task reports its own failure.
            }
            finally
            {
                if (_musicFlights.TryGetValue(key, out var current) &&
                    ReferenceEquals(current, canonical))
                {
                    _musicFlights.Remove(key);
                    _musicFlightSequences.Remove(key);
                }
            }
        }

        private static async Task<T> AwaitCallerAsync<T>(
            Task<T> task,
            CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                ObserveAbandoned(task);
                throw new OperationCanceledException(token);
            }

            if (!token.CanBeCanceled || task.IsCompleted)
            {
                return await task;
            }

            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(
                       state =>
                           ((TaskCompletionSource<bool>)state)
                           .TrySetResult(true),
                       canceled))
            {
                if (task != await Task.WhenAny(task, canceled.Task))
                {
                    ObserveAbandoned(task);
                    throw new OperationCanceledException(token);
                }
            }

            return await task;
        }

        private async Task<AudioHandle> AwaitOneShotCallerAsync(
            Task<AudioHandle> canonical,
            CancellationToken token)
        {
            try
            {
                var handle =
                    await AwaitCallerAsync(canonical, token);
                MarkCallerCommit(handle);
                return handle;
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
                var rollback =
                    RollbackAbandonedOneShotAsync(canonical);
                TrackMandatoryCleanup(rollback);
                throw;
            }
        }

        private async Task<AudioHandle> AwaitMusicCallerAsync(
            Task<AudioHandle> canonical,
            CancellationToken token)
        {
            var handle = await AwaitCallerAsync(canonical, token);
            MarkCallerCommit(handle);
            return handle;
        }

        private void MarkCallerCommit(AudioHandle handle)
        {
            if (handle != null &&
                _entries.TryGetValue(handle.InstanceId, out var entry) &&
                ReferenceEquals(entry.Handle, handle) &&
                !entry.CallerTaskCommitted)
            {
                entry.CallerTaskCommitted = true;
                ResetNaturalEndBaseline(entry);
            }
        }

        private static void ResetNaturalEndBaseline(Entry entry)
        {
            entry.LastRealtime = Time.realtimeSinceStartup;
            entry.NaturalEndGuard =
                NaturalEndGuardState.AwaitingFirstDriverTick;
        }

        private void TrackMandatoryCleanup(Task cleanup)
        {
            var observer = ObserveMandatoryCleanupAsync(cleanup);
            if (observer.IsCompleted)
            {
                return;
            }

            _mandatoryCleanupTasks.Add(observer);
            _ = RemoveMandatoryCleanupAsync(observer);
        }

        private async Task ObserveMandatoryCleanupAsync(Task cleanup)
        {
            try
            {
                await cleanup;
            }
            catch (OperationCanceledException)
            {
                // The canonical acquisition performed its own rollback.
            }
            catch (Exception exception)
            {
                RecordException(exception);
                _mandatoryCleanupFailures.Add(exception);
            }
        }

        private async Task RemoveMandatoryCleanupAsync(Task observer)
        {
            await observer;
            _mandatoryCleanupTasks.Remove(observer);
        }

        private async Task RollbackAbandonedOneShotAsync(
            Task<AudioHandle> canonical)
        {
            AudioHandle handle;
            try
            {
                handle = await canonical;
            }
            catch (OperationCanceledException)
            {
                // The canonical acquisition performed its own rollback.
                return;
            }
            catch (AudioCleanupException)
            {
                // Canonical rollback cleanup is mandatory and must be joined
                // by the service Stop barrier.
                throw;
            }
            catch
            {
                // Ordinary load or playback failure belongs to the canonical
                // play task; it must not poison a later service Stop.
                return;
            }

            if (_entries.TryGetValue(
                    handle.InstanceId,
                    out var entry) &&
                ReferenceEquals(entry.Handle, handle))
            {
                await BeginStop(entry, 0f);
            }
        }

        private static async Task AwaitCallerAsync(
            Task task,
            CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                ObserveAbandoned(task);
                throw new OperationCanceledException(token);
            }

            if (!token.CanBeCanceled || task.IsCompleted)
            {
                await task;
                return;
            }

            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(
                       state =>
                           ((TaskCompletionSource<bool>)state)
                           .TrySetResult(true),
                       canceled))
            {
                if (task != await Task.WhenAny(task, canceled.Task))
                {
                    ObserveAbandoned(task);
                    throw new OperationCanceledException(token);
                }
            }

            await task;
        }

        private static void ObserveAbandoned(Task task)
        {
            _ = ObserveAbandonedAsync(task);
        }

        private static async Task ObserveAbandonedAsync(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // Caller cancellation only abandons the wait.
            }
        }

        private void RecordException(Exception exception)
        {
            if (!(exception is OperationCanceledException))
            {
                _recentException = exception;
            }
        }

        private void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "AudioService operations must run on the Unity main thread.");
            }
        }

        private static void EnsureUnityConstructionThread()
        {
            var context = SynchronizationContext.Current;
            if (!IsUnityMainThreadContext(context))
            {
                throw new InvalidOperationException(
                    "AudioService must be created on the Unity main thread.");
            }
        }

        private static bool IsUnityMainThreadContext(
            SynchronizationContext context)
        {
            return context != null &&
                   context.GetType().Assembly == typeof(GameObject).Assembly;
        }

        private static void EnsureNoLiveRoot()
        {
            var markers =
                Resources.FindObjectsOfTypeAll<AudioRootMarker>();
            for (var index = 0; index < markers.Length; index++)
            {
                var marker = markers[index];
                if (marker != null)
                {
                    throw new InvalidOperationException(
                        "Only one live AudioService root is allowed.");
                }
            }
        }

        private void EnsureRunning()
        {
            if (_stopped || Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(AudioService));
            }
        }

        private static void ValidateKey(ResourceKey key)
        {
            if (string.IsNullOrWhiteSpace(key.Value))
            {
                throw new ArgumentException(
                    "An audio ResourceKey cannot be the default value.",
                    nameof(key));
            }
        }

        private static void ResetSource(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();
            source.playOnAwake = false;
            source.clip = null;
            source.outputAudioMixerGroup = null;
            source.loop = false;
            source.volume = 1f;
            source.mute = false;
        }

        private void ResetSourceForCleanup(AudioSource source)
        {
            _sourceResettingForTesting?.Invoke(source);
            ResetSource(source);
        }

        private static Exception Combine(
            Exception first,
            Exception second)
        {
            if (first == null)
            {
                return second;
            }

            if (second == null)
            {
                return first;
            }

            return new AggregateException(first, second);
        }

        private static bool ContainsExceptionReference(
            Exception root,
            Exception candidate)
        {
            if (root == null || candidate == null)
            {
                return false;
            }

            if (ReferenceEquals(root, candidate))
            {
                return true;
            }

            if (root is AggregateException aggregate)
            {
                for (var index = 0;
                     index < aggregate.InnerExceptions.Count;
                     index++)
                {
                    if (ContainsExceptionReference(
                            aggregate.InnerExceptions[index],
                            candidate))
                    {
                        return true;
                    }
                }

                return false;
            }

            return root.InnerException != null &&
                   ContainsExceptionReference(
                       root.InnerException,
                       candidate);
        }

        private enum NaturalEndGuardState
        {
            AwaitingFirstDriverTick,
            ProtectingCallerObservableFrame,
            Running
        }

        private sealed class ChannelState
        {
            public ChannelState(AudioChannel channel)
            {
                Channel = channel;
                Volume = 1f;
            }

            public AudioChannel Channel { get; }

            public float Volume { get; set; }

            public bool Muted { get; set; }

            public bool Paused { get; set; }

            public AudioMixerGroup MixerGroup { get; set; }
        }

        private sealed class PendingEntry
        {
            public PendingEntry(
                Guid instanceId,
                ResourceKey key,
                AudioPlayOptions options,
                long sequence,
                CancellationToken acquisitionToken)
            {
                InstanceId = instanceId;
                Key = key;
                Options = options;
                Sequence = sequence;
                AcquisitionToken = acquisitionToken;
            }

            public Guid InstanceId { get; }

            public ResourceKey Key { get; }

            public AudioPlayOptions Options { get; }

            public long Sequence { get; }

            public CancellationToken AcquisitionToken { get; }
        }

        private sealed class Entry
        {
            public Entry(
                Guid instanceId,
                ResourceKey key,
                AudioPlayOptions options,
                AudioHandle handle,
                IAssetLease<AudioClip> lease,
                AudioSource source,
                AudioSourcePoolItem poolItem,
                float remainingSeconds)
            {
                InstanceId = instanceId;
                Key = key;
                Options = options;
                Handle = handle;
                Lease = lease;
                Source = source;
                PoolItem = poolItem;
                RemainingSeconds = remainingSeconds;
                LastRealtime = Time.realtimeSinceStartup;
                NaturalEndGuard =
                    NaturalEndGuardState.AwaitingFirstDriverTick;
            }

            public Guid InstanceId { get; }

            public ResourceKey Key { get; }

            public AudioPlayOptions Options { get; }

            public AudioHandle Handle { get; }

            public IAssetLease<AudioClip> Lease { get; set; }

            public AudioSource Source { get; }

            public AudioSourcePoolItem PoolItem { get; }

            public AudioPlaybackState State { get; set; }

            public float Gain { get; set; }

            public float FadeStartGain { get; set; }

            public float FadeTargetGain { get; set; }

            public float FadeDuration { get; set; }

            public float FadeElapsed { get; set; }

            public bool FinishAfterFade { get; set; }

            public float RemainingSeconds { get; set; }

            public float LastRealtime { get; set; }

            public bool CallerTaskCommitted { get; set; }

            public NaturalEndGuardState NaturalEndGuard { get; set; }

            public Task StopTask { get; set; }

            public TaskCompletionSource<bool> StopCompletion { get; set; }
        }
    }

    internal sealed class AudioCleanupException : Exception
    {
        public AudioCleanupException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class AudioDriver : MonoBehaviour
    {
        private AudioService _service;

        internal void Bind(AudioService service)
        {
            _service = service;
        }

        internal void Unbind(AudioService service)
        {
            if (ReferenceEquals(_service, service))
            {
                _service = null;
            }
        }

        private void Update()
        {
            _service?.Tick(Time.unscaledDeltaTime);
        }
    }

    internal sealed class AudioRootMarker : MonoBehaviour
    {
        private readonly TaskCompletionSource<bool> _destroyed =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Destroyed => _destroyed.Task;

        private void OnDestroy()
        {
            _destroyed.TrySetResult(true);
        }
    }
}
