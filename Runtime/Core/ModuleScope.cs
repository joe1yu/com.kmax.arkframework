using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class ModuleScope : IAsyncDisposable
    {
        private readonly ServiceContainer _container;
        private readonly List<Type> _serviceTypes = new List<Type>();
        private readonly List<object> _ownedInstances = new List<object>();
        private readonly HashSet<object> _ownedReferences =
            new HashSet<object>(ReferenceEqualityComparer.Instance);
        private int _activeResolutionCount;
        private bool _isDisposed;

        internal ModuleScope(ServiceContainer container, string ownerId)
        {
            _container = container;
            OwnerId = ownerId;
        }

        internal string OwnerId { get; }

        public void RegisterSingleton<T>(Func<ServiceContainer, T> factory)
        {
            EnsureActive();
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            Register(ServiceRegistration.CreateSingleton(factory, this));
        }

        public void RegisterTransient<T>(Func<ServiceContainer, T> factory)
        {
            EnsureActive();
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            Register(ServiceRegistration.CreateTransient(factory, this));
        }

        public void RegisterInstance<T>(T instance)
        {
            EnsureActive();
            Register(ServiceRegistration.CreateInstance(instance, this));
            TrackOwned(instance);
        }

        public T Own<T>(T instance) where T : class
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            EnsureActive();
            TrackOwned(instance);
            return instance;
        }

        public ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return default;
            }

            if (_activeResolutionCount != 0)
            {
                throw FrameworkExceptions.ScopeDisposalDuringResolution(OwnerId);
            }

            _isDisposed = true;
            _container.MarkDisposed(this, _serviceTypes);
            return DisposeOwnedInstancesAsync();
        }

        internal void BeginResolution()
        {
            EnsureActive();
            _activeResolutionCount++;
        }

        internal void EndResolution()
        {
            _activeResolutionCount--;
        }

        internal void TrackOwned(object instance)
        {
            if (instance != null && _ownedReferences.Add(instance))
            {
                _ownedInstances.Add(instance);
            }
        }

        private async ValueTask DisposeOwnedInstancesAsync()
        {
            ExceptionDispatchInfo firstFailure = null;
            try
            {
                for (var index = _ownedInstances.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        await DisposeOwnedAsync(_ownedInstances[index]);
                    }
                    catch (Exception exception)
                    {
                        if (firstFailure == null)
                        {
                            firstFailure = ExceptionDispatchInfo.Capture(exception);
                        }
                    }
                }
            }
            finally
            {
                _container.Remove(this, _serviceTypes);
                _serviceTypes.Clear();
                _ownedInstances.Clear();
                _ownedReferences.Clear();
            }

            if (firstFailure != null)
            {
                firstFailure.Throw();
            }
        }

        private void Register(ServiceRegistration registration)
        {
            _container.Register(registration);
            _serviceTypes.Add(registration.ServiceType);
        }

        private void EnsureActive()
        {
            if (_isDisposed)
            {
                throw FrameworkExceptions.DisposedScope(OwnerId);
            }
        }

        private static async ValueTask DisposeOwnedAsync(object instance)
        {
            if (instance is Object unityObject)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(unityObject);
                }
                else
                {
                    Object.DestroyImmediate(unityObject);
                }

                return;
            }

            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
                return;
            }

            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance =
                new ReferenceEqualityComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }
    }
}
