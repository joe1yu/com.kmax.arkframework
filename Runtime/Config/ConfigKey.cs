using System;

namespace ArkFramework
{
    public readonly struct ConfigKey : IEquatable<ConfigKey>
    {
        public ConfigKey(Type type, string key)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "A config key cannot be null, empty, or whitespace.",
                    nameof(key));
            }

            Key = key;
        }

        public Type Type { get; }

        public string Key { get; }

        public bool Equals(ConfigKey other)
        {
            return Type == other.Type &&
                string.Equals(Key, other.Key, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ConfigKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Type?.GetHashCode() ?? 0) * 397) ^
                    (Key == null
                        ? 0
                        : StringComparer.Ordinal.GetHashCode(Key));
            }
        }

        public override string ToString()
        {
            return $"{Type?.FullName ?? "<null>"}:{Key ?? "<null>"}";
        }

        public static bool operator ==(ConfigKey left, ConfigKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ConfigKey left, ConfigKey right)
        {
            return !left.Equals(right);
        }
    }
}
