using System;

namespace ArkFramework
{
    public sealed class ProcedureContext
    {
        public ProcedureContext(ServiceContainer services)
        {
            Services = services ??
                       throw new ArgumentNullException(nameof(services));
        }

        public ServiceContainer Services { get; }

        public T Resolve<T>()
        {
            return Services.Resolve<T>();
        }

        public bool TryResolve<T>(out T service)
        {
            return Services.TryResolve(out service);
        }
    }
}
