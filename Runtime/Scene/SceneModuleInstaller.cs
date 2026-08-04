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
                    BuiltInModuleIds.EventBus,
                    BuiltInModuleIds.Table
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(ISceneService) });

        [SerializeField]
        private string _sceneTablePath;

        public string SceneTablePath => _sceneTablePath;

        public override string ModuleId => BuiltInModuleIds.Scene;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new SceneModule(_sceneTablePath);
        }
    }
}
