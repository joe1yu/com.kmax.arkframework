using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "TableModuleInstaller",
        menuName = "ArkFramework/Modules/Table")]
    public sealed class TableModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(ITableService) });

        public override string ModuleId => BuiltInModuleIds.Table;

        public override IReadOnlyCollection<string> Dependencies =>
            Array.Empty<string>();

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new TableModule();
        }
    }
}
