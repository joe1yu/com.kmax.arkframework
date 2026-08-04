using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "RigModuleInstaller",
        menuName = "ArkFramework/Modules/Rig")]
    public sealed class RigModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> RigDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Platform,
                    BuiltInModuleIds.EventBus
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IRigService) });

        public override string ModuleId => BuiltInModuleIds.Rig;

        public override IReadOnlyCollection<string> Dependencies =>
            RigDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new RigModule();
        }
    }
}
