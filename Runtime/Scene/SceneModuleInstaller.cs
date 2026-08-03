using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "SceneModuleInstaller",
        menuName = "ArkFramework/Modules/Scene")]
    public sealed class SceneModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Resource,
                    BuiltInModuleIds.EventBus
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(ISceneService) });

        public override string ModuleId => BuiltInModuleIds.Scene;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new SceneModule();
        }
    }
}
