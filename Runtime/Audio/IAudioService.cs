using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Audio;

namespace ArkFramework
{
    public interface IAudioService
    {
        ValueTask<IAudioHandle> PlayAsync(
            ResourceKey key,
            AudioPlayOptions options,
            CancellationToken token = default);

        ValueTask StopAsync(
            IAudioHandle handle,
            float fadeSeconds = 0f,
            CancellationToken token = default);

        void SetChannelVolume(AudioChannel channel, float volume);

        void SetChannelMuted(AudioChannel channel, bool muted);

        void SetChannelPaused(AudioChannel channel, bool paused);

        void SetChannelMixerGroup(
            AudioChannel channel,
            AudioMixerGroup group);

        AudioDiagnostics Diagnostics { get; }

        ValueTask StopAsync(CancellationToken token = default);
    }
}
