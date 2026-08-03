namespace ArkFramework
{
    public sealed class ModuleContext
    {
        internal ModuleContext(
            ServiceContainer services,
            ModuleScope moduleScope,
            IFrameworkLogger logger,
            string moduleId)
        {
            Services = services;
            ModuleScope = moduleScope;
            Logger = logger;
            ModuleId = moduleId;
        }

        public ServiceContainer Services { get; }

        public ModuleScope ModuleScope { get; }

        public IFrameworkLogger Logger { get; }

        public string ModuleId { get; }
    }
}
