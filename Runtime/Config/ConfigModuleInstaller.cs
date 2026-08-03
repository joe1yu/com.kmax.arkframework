using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "ConfigModuleInstaller",
        menuName = "ArkFramework/Modules/Config")]
    public sealed class ConfigModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Resource,
                    BuiltInModuleIds.EventBus
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IConfigService) });

        [SerializeField]
        private string _scriptableObjectLabel = "config-scriptable";

        [SerializeField]
        private string _jsonManifestAddress = "config/manifest";

        public override string ModuleId => BuiltInModuleIds.Config;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new ConfigModule(
                _scriptableObjectLabel,
                new ResourceKey(_jsonManifestAddress));
        }
    }
}
