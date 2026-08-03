using System;
using System.Collections.Generic;

namespace ArkFramework
{
    public sealed class ServiceContainer
    {
        private readonly Dictionary<Type, ServiceRegistration> _registrations =
            new Dictionary<Type, ServiceRegistration>();
        private readonly List<Type> _resolutionPath = new List<Type>();

        public ModuleScope CreateScope(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                throw new ArgumentException(
                    "A module scope owner ID cannot be null, empty, or whitespace.",
                    nameof(ownerId));
            }

            return new ModuleScope(this, ownerId);
        }

        public T Resolve<T>()
        {
            return (T)Resolve(typeof(T));
        }

        public bool TryResolve<T>(out T service)
        {
            if (!_registrations.ContainsKey(typeof(T)))
            {
                service = default;
                return false;
            }

            service = Resolve<T>();
            return true;
        }

        internal void Register(ServiceRegistration registration)
        {
            if (_registrations.TryGetValue(registration.ServiceType, out var existing))
            {
                throw FrameworkExceptions.DuplicateRegistration(
                    registration.ServiceType,
                    existing.OwnerId);
            }

            _registrations.Add(registration.ServiceType, registration);
        }

        internal void MarkDisposed(ModuleScope owner, IReadOnlyList<Type> serviceTypes)
        {
            for (var index = 0; index < serviceTypes.Count; index++)
            {
                if (_registrations.TryGetValue(
                        serviceTypes[index],
                        out var registration) &&
                    ReferenceEquals(registration.Owner, owner))
                {
                    registration.MarkDisposed();
                }
            }
        }

        internal void Remove(ModuleScope owner, IReadOnlyList<Type> serviceTypes)
        {
            for (var index = 0; index < serviceTypes.Count; index++)
            {
                if (_registrations.TryGetValue(
                        serviceTypes[index],
                        out var registration) &&
                    ReferenceEquals(registration.Owner, owner))
                {
                    _registrations.Remove(serviceTypes[index]);
                }
            }
        }

        private object Resolve(Type serviceType)
        {
            if (!_registrations.TryGetValue(serviceType, out var registration))
            {
                throw FrameworkExceptions.MissingRegistration(serviceType);
            }

            if (_resolutionPath.Contains(serviceType))
            {
                throw FrameworkExceptions.CircularResolution(_resolutionPath, serviceType);
            }

            registration.Owner.BeginResolution();
            _resolutionPath.Add(serviceType);
            try
            {
                var instance = registration.Resolve(this, out var created);
                if (created)
                {
                    registration.Owner.TrackOwned(instance);
                }

                return instance;
            }
            finally
            {
                _resolutionPath.RemoveAt(_resolutionPath.Count - 1);
                registration.Owner.EndResolution();
            }
        }
    }
}
