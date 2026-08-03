using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public enum AudioPlaybackState
    {
        Pending,
        Playing,
        FadingIn,
        FadingOut,
        Paused
    }

    public sealed class AudioChannelDiagnostics
    {
        internal AudioChannelDiagnostics(
            AudioChannel channel,
            float volume,
            bool muted,
            bool paused,
            string mixerGroupName)
        {
            Channel = channel;
            Volume = volume;
            Muted = muted;
            Paused = paused;
            MixerGroupName = mixerGroupName;
        }

        public AudioChannel Channel { get; }

        public float Volume { get; }

        public bool Muted { get; }

        public bool Paused { get; }

        public string MixerGroupName { get; }
    }

    public sealed class AudioEntryDiagnostics
    {
        internal AudioEntryDiagnostics(
            Guid instanceId,
            ResourceKey resourceKey,
            AudioChannel channel,
            bool loop,
            AudioPlaybackState state,
            float playVolume,
            float effectiveVolume)
        {
            InstanceId = instanceId;
            ResourceKey = resourceKey;
            Channel = channel;
            Loop = loop;
            State = state;
            PlayVolume = playVolume;
            EffectiveVolume = effectiveVolume;
        }

        public Guid InstanceId { get; }

        public ResourceKey ResourceKey { get; }

        public AudioChannel Channel { get; }

        public bool Loop { get; }

        public AudioPlaybackState State { get; }

        public float PlayVolume { get; }

        public float EffectiveVolume { get; }
    }

    public sealed class AudioDiagnostics
    {
        internal AudioDiagnostics(
            IReadOnlyList<AudioChannelDiagnostics> channels,
            ResourceKey? currentMusicKey,
            IAudioHandle currentMusicHandle,
            IReadOnlyList<AudioEntryDiagnostics> entries,
            PoolDiagnostics oneShotPool,
            int pendingLoadCount,
            Exception recentException)
        {
            Channels = channels;
            CurrentMusicKey = currentMusicKey;
            CurrentMusicHandle = currentMusicHandle;
            Entries = entries;
            OneShotPool = oneShotPool;
            PendingLoadCount = pendingLoadCount;
            RecentException = recentException;
        }

        public IReadOnlyList<AudioChannelDiagnostics> Channels { get; }

        public ResourceKey? CurrentMusicKey { get; }

        public IAudioHandle CurrentMusicHandle { get; }

        public IReadOnlyList<AudioEntryDiagnostics> Entries { get; }

        public PoolDiagnostics OneShotPool { get; }

        public int PendingLoadCount { get; }

        public Exception RecentException { get; }

        internal static IReadOnlyList<T> ReadOnly<T>(T[] values)
        {
            return new ReadOnlyCollection<T>(values);
        }
    }
}
