using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "AudioModuleInstaller",
        menuName = "ArkFramework/Modules/Audio")]
    public sealed class AudioModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Resource,
                    BuiltInModuleIds.Pool
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IAudioService) });

        public override string ModuleId => BuiltInModuleIds.Audio;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new AudioModule();
        }
    }
}
