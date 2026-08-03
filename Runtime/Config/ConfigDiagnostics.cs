using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public sealed class ConfigDiagnostics
    {
        public ConfigDiagnostics(
            IReadOnlyDictionary<ConfigKey, ConfigEntryDiagnostics> entries,
            DateTime? lastSuccessfulReloadUtc)
            : this(
                entries,
                lastSuccessfulReloadUtc,
                null,
                null)
        {
        }

        public ConfigDiagnostics(
            IReadOnlyDictionary<ConfigKey, ConfigEntryDiagnostics> entries,
            DateTime? lastSuccessfulReloadUtc,
            bool? lastValidationSucceeded,
            string lastValidationError)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            Entries =
                new ReadOnlyDictionary<ConfigKey, ConfigEntryDiagnostics>(
                    new Dictionary<ConfigKey, ConfigEntryDiagnostics>(entries));
            LastSuccessfulReloadUtc = lastSuccessfulReloadUtc;
            LastValidationSucceeded = lastValidationSucceeded;
            LastValidationError = lastValidationError;
        }

        public IReadOnlyDictionary<ConfigKey, ConfigEntryDiagnostics> Entries
        {
            get;
        }

        public DateTime? LastSuccessfulReloadUtc { get; }

        public bool? LastValidationSucceeded { get; }

        public string LastValidationError { get; }
    }

    public sealed class ConfigEntryDiagnostics
    {
        public ConfigEntryDiagnostics(string source, string version)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException(
                    "A config diagnostic source is required.",
                    nameof(source));
            }

            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            Source = source;
            Version = version;
        }

        public string Source { get; }

        public string Version { get; }
    }
}
