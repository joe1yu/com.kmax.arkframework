using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    [CreateAssetMenu(menuName = "ArkFramework/Framework Profile")]
    public sealed class FrameworkProfile : ScriptableObject
    {
        private static readonly IReadOnlyList<ModuleInstaller> EmptyInstallers =
            Array.AsReadOnly(Array.Empty<ModuleInstaller>());

        [SerializeField]
        private List<ModuleInstaller> _installers = new List<ModuleInstaller>();

        public IReadOnlyList<ModuleInstaller> Installers
        {
            get
            {
                if (_installers == null || _installers.Count == 0)
                {
                    return EmptyInstallers;
                }

                return Array.AsReadOnly(_installers.ToArray());
            }
        }

        public IReadOnlyList<ModuleDescriptor> CreateDescriptors()
        {
            if (_installers == null || _installers.Count == 0)
            {
                return Array.AsReadOnly(Array.Empty<ModuleDescriptor>());
            }

            var descriptors = new ModuleDescriptor[_installers.Count];
            for (var index = 0; index < _installers.Count; index++)
            {
                var installer = _installers[index];
                if (installer == null)
                {
                    throw new InvalidOperationException(
                        $"Framework Profile contains a null installer at index {index}.");
                }

                descriptors[index] = new ModuleDescriptor(
                    installer.ModuleId,
                    installer.Dependencies,
                    index,
                    () => installer.CreateModule());
            }

            return Array.AsReadOnly(descriptors);
        }
    }
}
