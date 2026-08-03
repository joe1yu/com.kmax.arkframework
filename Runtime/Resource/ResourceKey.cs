using System;

namespace ArkFramework
{
    public readonly struct ResourceKey : IEquatable<ResourceKey>
    {
        public ResourceKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "A resource key cannot be null, empty, or whitespace.",
                    nameof(value));
            }

            Value = value;
        }

        public string Value { get; }

        public bool Equals(ResourceKey other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ResourceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value == null
                ? 0
                : StringComparer.Ordinal.GetHashCode(Value);
        }

        public override string ToString()
        {
            return Value ?? string.Empty;
        }

        public static bool operator ==(ResourceKey left, ResourceKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ResourceKey left, ResourceKey right)
        {
            return !left.Equals(right);
        }
    }
}
