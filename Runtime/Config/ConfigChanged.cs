using System;

namespace ArkFramework
{
    public readonly struct ConfigChanged
    {
        public ConfigChanged(
            Type type,
            string key,
            string oldSource,
            string newSource,
            string oldVersion,
            string newVersion)
        {
            Type = type;
            Key = key;
            OldSource = oldSource;
            NewSource = newSource;
            OldVersion = oldVersion;
            NewVersion = newVersion;
        }

        public Type Type { get; }

        public string Key { get; }

        public string OldSource { get; }

        public string NewSource { get; }

        public string OldVersion { get; }

        public string NewVersion { get; }
    }
}
