using System;

namespace ArkFramework
{
    public readonly struct AudioPlayOptions
    {
        public AudioPlayOptions(
            AudioChannel channel,
            bool loop = false,
            float volume = 1f,
            float fadeSeconds = 0f)
        {
            AudioValidation.ValidateChannel(channel);
            AudioValidation.ValidateUnit(volume, nameof(volume));
            AudioValidation.ValidateFade(fadeSeconds, nameof(fadeSeconds));
            Channel = channel;
            Loop = loop;
            Volume = volume;
            FadeSeconds = fadeSeconds;
        }

        public AudioChannel Channel { get; }

        public bool Loop { get; }

        public float Volume { get; }

        public float FadeSeconds { get; }
    }

    internal static class AudioValidation
    {
        public static void ValidateChannel(AudioChannel channel)
        {
            if (channel < AudioChannel.Music ||
                channel > AudioChannel.Voice)
            {
                throw new ArgumentOutOfRangeException(nameof(channel));
            }
        }

        public static void ValidateUnit(float value, string parameterName)
        {
            if (float.IsNaN(value) ||
                float.IsInfinity(value) ||
                value < 0f ||
                value > 1f)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        public static void ValidateFade(float value, string parameterName)
        {
            if (float.IsNaN(value) ||
                float.IsInfinity(value) ||
                value < 0f)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}
