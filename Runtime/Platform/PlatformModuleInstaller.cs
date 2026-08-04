using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(
        fileName = "PlatformModuleInstaller",
        menuName = "ArkFramework/Modules/Platform")]
    public sealed class PlatformModuleInstaller : ModuleInstaller
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.Empty<string>();
        private static readonly IReadOnlyCollection<Type> Services =
            Array.AsReadOnly(new[] { typeof(IPlatformService) });

        [SerializeField]
        private GameObject _platformPrefab;

        [SerializeField]
        private bool _dontDestroyOnLoad = true;

        public GameObject PlatformPrefab => _platformPrefab;

        public new bool DontDestroyOnLoad => _dontDestroyOnLoad;

        public override string ModuleId => BuiltInModuleIds.Platform;

        public override IReadOnlyCollection<string> Dependencies =>
            NoDependencies;

        public override IReadOnlyCollection<Type> ServiceTypes => Services;

        public override IFrameworkModule CreateModule()
        {
            return new PlatformModule(
                _platformPrefab,
                _dontDestroyOnLoad);
        }
    }
}
