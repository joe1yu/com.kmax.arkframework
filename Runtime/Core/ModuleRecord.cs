using System;

namespace ArkFramework
{
    public sealed class ModuleRecord
    {
        internal ModuleRecord(ModuleDescriptor descriptor)
        {
            Descriptor = descriptor;
            State = ModuleState.Registered;
            LastStateChangedUtc = DateTime.UtcNow;
        }

        public ModuleDescriptor Descriptor { get; }

        public IFrameworkModule Module { get; internal set; }

        public ModuleState State { get; internal set; }

        public Exception LastException { get; internal set; }

        public DateTime LastStateChangedUtc { get; internal set; }

        public TimeSpan InitializeDuration { get; internal set; }

        public TimeSpan StartDuration { get; internal set; }

        public TimeSpan StopDuration { get; internal set; }

        public TimeSpan DisposeDuration { get; internal set; }

        internal ModuleScope Scope { get; set; }
    }
}
