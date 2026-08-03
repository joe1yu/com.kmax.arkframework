using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "PoolModuleInstaller",
        menuName = "ArkFramework/Modules/Pool")]
    public sealed class PoolModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(new[] { BuiltInModuleIds.Resource });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IGameObjectPool) });

        public override string ModuleId => BuiltInModuleIds.Pool;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new PoolModule();
        }
    }
}
