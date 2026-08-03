using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "FsmModuleInstaller",
        menuName = "ArkFramework/Modules/FSM")]
    public sealed class FsmModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.AsReadOnly(Array.Empty<string>());
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IFsmService) });

        public override string ModuleId => BuiltInModuleIds.Fsm;

        public override IReadOnlyCollection<string> Dependencies =>
            NoDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new FsmModule();
        }
    }
}
