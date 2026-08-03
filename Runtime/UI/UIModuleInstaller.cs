using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "UIModuleInstaller",
        menuName = "ArkFramework/Modules/UI")]
    public sealed class UIModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Resource,
                    BuiltInModuleIds.Pool,
                    BuiltInModuleIds.EventBus
                });
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IUIService) });

        public override string ModuleId => BuiltInModuleIds.UI;

        public override IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new UIModule();
        }
    }
}
