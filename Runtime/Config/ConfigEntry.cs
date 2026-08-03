using System;

namespace ArkFramework
{
    public sealed class ConfigEntry
    {
        public ConfigEntry(
            ConfigKey key,
            object value,
            string source,
            string version,
            IDisposable ownership = null)
        {
            if (key.Type == null || string.IsNullOrWhiteSpace(key.Key))
            {
                throw new ArgumentException(
                    "A config entry requires a valid config key.",
                    nameof(key));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (!key.Type.IsInstanceOfType(value))
            {
                throw new ArgumentException(
                    $"Value type '{value.GetType().FullName}' cannot be assigned " +
                    $"to config type '{key.Type.FullName}'.",
                    nameof(value));
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException(
                    "A config source cannot be null, empty, or whitespace.",
                    nameof(source));
            }

            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            Key = key;
            Value = value;
            Source = source;
            Version = version;
            Ownership = ownership;
        }

        public ConfigKey Key { get; }

        public object Value { get; }

        public string Source { get; }

        public string Version { get; }

        public IDisposable Ownership { get; }
    }
}
