using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "ActionKitModuleInstaller",
        menuName = "ArkFramework/Modules/ActionKit")]
    public sealed class ActionKitModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IActionService) });

        public override string ModuleId => BuiltInModuleIds.ActionKit;

        public override IReadOnlyCollection<string> Dependencies =>
            Array.Empty<string>();

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new ActionKitModule();
        }
    }
}
