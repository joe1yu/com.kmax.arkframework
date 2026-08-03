using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "ResourceModuleInstaller",
        menuName = "ArkFramework/Modules/Resource")]
    public sealed class ResourceModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.AsReadOnly(Array.Empty<string>());
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(
                new[]
                {
                    typeof(IResourceService),
                    typeof(ISceneResourceLoader),
                    typeof(ISceneTransactionResourceLoader)
                });

        public override string ModuleId => BuiltInModuleIds.Resource;

        public override IReadOnlyCollection<string> Dependencies =>
            NoDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new ResourceModule();
        }
    }
}
