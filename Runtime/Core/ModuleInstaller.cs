using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    public abstract class ModuleInstaller : ScriptableObject
    {
        private static readonly IReadOnlyCollection<Type> EmptyServiceTypes =
            Array.AsReadOnly(Array.Empty<Type>());

        public abstract string ModuleId { get; }

        public abstract IReadOnlyCollection<string> Dependencies { get; }

        public virtual IReadOnlyCollection<Type> ServiceTypes => EmptyServiceTypes;

        public abstract IFrameworkModule CreateModule();
    }
}
