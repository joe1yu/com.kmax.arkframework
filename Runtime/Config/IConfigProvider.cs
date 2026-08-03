using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IConfigProvider
    {
        string Name { get; }

        ValueTask<ConfigProviderSnapshot> LoadAsync(
            CancellationToken token = default);
    }

    public sealed class ConfigProviderSnapshot : IDisposable
    {
        private IDisposable[] _owners;

        public ConfigProviderSnapshot(
            IEnumerable<ConfigEntry> entries,
            IEnumerable<IDisposable> owners)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (owners == null)
            {
                throw new ArgumentNullException(nameof(owners));
            }

            var entryList = new List<ConfigEntry>();
            var keys = new HashSet<ConfigKey>();
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    throw new ArgumentException(
                        "A provider snapshot cannot contain a null entry.",
                        nameof(entries));
                }

                if (!keys.Add(entry.Key))
                {
                    throw new ArgumentException(
                        $"Provider snapshot contains duplicate config key " +
                        $"'{entry.Key}'.",
                        nameof(entries));
                }

                entryList.Add(entry);
            }

            var ownerList = new List<IDisposable>();
            var uniqueOwners = new HashSet<IDisposable>(
                ReferenceEqualityComparer<IDisposable>.Instance);
            foreach (var owner in owners)
            {
                if (owner == null)
                {
                    throw new ArgumentException(
                        "A provider snapshot cannot contain a null owner.",
                        nameof(owners));
                }

                if (uniqueOwners.Add(owner))
                {
                    ownerList.Add(owner);
                }
            }

            Entries = new ReadOnlyCollection<ConfigEntry>(entryList);
            _owners = ownerList.ToArray();
        }

        public IReadOnlyList<ConfigEntry> Entries { get; }

        public void Dispose()
        {
            var owners = Interlocked.Exchange(ref _owners, null);
            var cleanupFailure = ConfigCleanup.DisposeAll(owners);
            if (cleanupFailure != null)
            {
                throw cleanupFailure;
            }
        }
    }

    internal static class ConfigCleanup
    {
        public static Exception DisposeAll(
            IReadOnlyList<IDisposable> owners)
        {
            if (owners == null)
            {
                return null;
            }

            List<Exception> failures = null;
            for (var index = owners.Count - 1; index >= 0; index--)
            {
                try
                {
                    owners[index]?.Dispose();
                }
                catch (Exception exception)
                {
                    if (failures == null)
                    {
                        failures = new List<Exception>();
                    }

                    if (exception is AggregateException aggregate)
                    {
                        failures.AddRange(aggregate.Flatten().InnerExceptions);
                    }
                    else
                    {
                        failures.Add(exception);
                    }
                }
            }

            return failures == null
                ? null
                : new AggregateException(
                    "One or more config resources failed to release.",
                    failures);
        }

        public static void ThrowPrimaryWithCleanup(
            Exception primary,
            Exception cleanup)
        {
            if (cleanup == null)
            {
                ExceptionDispatchInfo.Capture(primary).Throw();
            }

            var failures = new List<Exception> { primary };
            if (cleanup is AggregateException aggregate)
            {
                failures.AddRange(aggregate.Flatten().InnerExceptions);
            }
            else
            {
                failures.Add(cleanup);
            }

            throw new AggregateException(
                "Config loading failed and candidate cleanup also failed.",
                failures);
        }
    }

    internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance =
            new ReferenceEqualityComparer<T>();

        public bool Equals(T left, T right)
        {
            return ReferenceEquals(left, right);
        }

        public int GetHashCode(T value)
        {
            return RuntimeHelpers.GetHashCode(value);
        }
    }
}
