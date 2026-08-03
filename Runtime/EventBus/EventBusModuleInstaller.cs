using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "EventBusModuleInstaller",
        menuName = "ArkFramework/Modules/Event Bus")]
    public sealed class EventBusModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.AsReadOnly(Array.Empty<string>());
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IEventBus) });

        public override string ModuleId => BuiltInModuleIds.EventBus;

        public override IReadOnlyCollection<string> Dependencies =>
            NoDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new EventBusModule();
        }
    }
}
