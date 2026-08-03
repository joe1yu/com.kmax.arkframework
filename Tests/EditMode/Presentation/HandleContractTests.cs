using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.Audio;

namespace ArkFramework.Tests
{
    public sealed class HandleContractTests
    {
        [Test]
        public void AlternativeServicesCanUseTheirOwnHandleImplementations()
        {
            IUIService ui = new AlternativeUIService();
            IAudioService audio = new AlternativeAudioService();

            var window = ui
                .OpenAsync<TestWindow>()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var sound = audio
                .PlayAsync(
                    new ResourceKey("sound"),
                    new AudioPlayOptions(AudioChannel.SFX))
                .AsTask()
                .GetAwaiter()
                .GetResult();

            Assert.That(window.DescriptorId, Is.EqualTo("alternative"));
            Assert.That(window.IsValid, Is.True);
            Assert.That(sound.ResourceKey.Value, Is.EqualTo("sound"));
            Assert.That(sound.IsValid, Is.True);
        }

        private sealed class AlternativeWindowHandle : IWindowHandle
        {
            public string DescriptorId => "alternative";
            public string WindowId => DescriptorId;
            public Guid InstanceId { get; } = Guid.NewGuid();
            public Type WindowType => typeof(TestWindow);
            public bool IsValid => true;
        }

        private sealed class AlternativeAudioHandle : IAudioHandle
        {
            public AlternativeAudioHandle(ResourceKey key)
            {
                ResourceKey = key;
            }

            public Guid InstanceId { get; } = Guid.NewGuid();
            public ResourceKey ResourceKey { get; }
            public AudioChannel Channel => AudioChannel.SFX;
            public bool IsValid => true;
        }

        private sealed class AlternativeUIService : IUIService
        {
            public UIDiagnostics Diagnostics => null;

            public void Register<TWindow>(UIWindowDescriptor descriptor)
                where TWindow : UIWindow
            {
            }

            public ValueTask<IWindowHandle> OpenAsync<TWindow>(
                object parameter = null,
                CancellationToken token = default)
                where TWindow : UIWindow
            {
                return new ValueTask<IWindowHandle>(
                    new AlternativeWindowHandle());
            }

            public ValueTask CloseAsync(
                IWindowHandle handle,
                CancellationToken token = default)
            {
                return default;
            }

            public ValueTask<bool> BackAsync(
                CancellationToken token = default)
            {
                return new ValueTask<bool>(false);
            }

            public bool TryGetWindow(
                IWindowHandle handle,
                out UIWindow window)
            {
                window = null;
                return false;
            }

            public ValueTask StopAsync(CancellationToken token = default)
            {
                return default;
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class AlternativeAudioService : IAudioService
        {
            public AudioDiagnostics Diagnostics => null;

            public ValueTask<IAudioHandle> PlayAsync(
                ResourceKey key,
                AudioPlayOptions options,
                CancellationToken token = default)
            {
                return new ValueTask<IAudioHandle>(
                    new AlternativeAudioHandle(key));
            }

            public ValueTask StopAsync(
                IAudioHandle handle,
                float fadeSeconds = 0f,
                CancellationToken token = default)
            {
                return default;
            }

            public void SetChannelVolume(AudioChannel channel, float volume)
            {
            }

            public void SetChannelMuted(AudioChannel channel, bool muted)
            {
            }

            public void SetChannelPaused(AudioChannel channel, bool paused)
            {
            }

            public void SetChannelMixerGroup(
                AudioChannel channel,
                AudioMixerGroup group)
            {
            }

            public ValueTask StopAsync(CancellationToken token = default)
            {
                return default;
            }
        }

        private sealed class TestWindow : UIWindow
        {
        }
    }
}
