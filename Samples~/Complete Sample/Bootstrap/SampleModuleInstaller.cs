using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework.Samples
{
    [CreateAssetMenu(
        fileName = "SampleModuleInstaller",
        menuName = "ArkFramework/Samples/Procedure Module")]
    public sealed class SampleModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Fsm,
                    BuiltInModuleIds.Config,
                    BuiltInModuleIds.Table,
                    BuiltInModuleIds.Scene,
                    BuiltInModuleIds.UI,
                    BuiltInModuleIds.Audio,
                    BuiltInModuleIds.ActionKit
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(
                new[]
                {
                    typeof(IProcedureService),
                    typeof(ISampleFlow),
                    typeof(ISampleUIService)
                });

        public override string ModuleId => BuiltInModuleIds.Procedure;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new SampleProcedureModule();
        }
    }
}
