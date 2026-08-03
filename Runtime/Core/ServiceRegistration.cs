using System;

namespace ArkFramework
{
    internal enum ServiceLifetime
    {
        Instance,
        Singleton,
        Transient
    }

    internal sealed class ServiceRegistration
    {
        private readonly Func<ServiceContainer, object> _factory;
        private object _singletonValue;
        private bool _hasSingletonValue;

        private ServiceRegistration(
            Type serviceType,
            ServiceLifetime lifetime,
            Func<ServiceContainer, object> factory,
            object singletonValue,
            bool hasSingletonValue,
            ModuleScope owner)
        {
            ServiceType = serviceType;
            Lifetime = lifetime;
            _factory = factory;
            _singletonValue = singletonValue;
            _hasSingletonValue = hasSingletonValue;
            Owner = owner;
            OwnerId = owner.OwnerId;
        }

        public Type ServiceType { get; }

        public ServiceLifetime Lifetime { get; }

        public string OwnerId { get; }

        public ModuleScope Owner { get; }

        public bool IsDisposed { get; private set; }

        public static ServiceRegistration CreateSingleton<T>(
            Func<ServiceContainer, T> factory,
            ModuleScope owner)
        {
            return new ServiceRegistration(
                typeof(T),
                ServiceLifetime.Singleton,
                container => factory(container),
                null,
                false,
                owner);
        }

        public static ServiceRegistration CreateTransient<T>(
            Func<ServiceContainer, T> factory,
            ModuleScope owner)
        {
            return new ServiceRegistration(
                typeof(T),
                ServiceLifetime.Transient,
                container => factory(container),
                null,
                false,
                owner);
        }

        public static ServiceRegistration CreateInstance<T>(T instance, ModuleScope owner)
        {
            return new ServiceRegistration(
                typeof(T),
                ServiceLifetime.Instance,
                null,
                instance,
                true,
                owner);
        }

        public object Resolve(ServiceContainer container, out bool created)
        {
            if (IsDisposed)
            {
                throw FrameworkExceptions.DisposedScope(OwnerId, ServiceType);
            }

            if (Lifetime == ServiceLifetime.Instance || _hasSingletonValue)
            {
                created = false;
                return _singletonValue;
            }

            var instance = _factory(container);
            created = true;
            if (Lifetime == ServiceLifetime.Singleton)
            {
                _singletonValue = instance;
                _hasSingletonValue = true;
            }

            return instance;
        }

        public void MarkDisposed()
        {
            IsDisposed = true;
        }
    }
}
