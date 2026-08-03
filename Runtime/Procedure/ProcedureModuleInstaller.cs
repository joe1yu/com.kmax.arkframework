using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "ProcedureModuleInstaller",
        menuName = "ArkFramework/Modules/Procedure")]
    public sealed class ProcedureModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Fsm,
                    BuiltInModuleIds.Config,
                    BuiltInModuleIds.Scene,
                    BuiltInModuleIds.UI,
                    BuiltInModuleIds.Audio
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IProcedureService) });

        public override string ModuleId => BuiltInModuleIds.Procedure;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new ProcedureModule();
        }
    }
}
