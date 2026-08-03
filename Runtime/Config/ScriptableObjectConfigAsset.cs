using System;
using UnityEngine;

namespace ArkFramework
{
    public abstract class ScriptableObjectConfigAsset : ScriptableObject
    {
        [SerializeField]
        private string _key;

        [SerializeField]
        private string _version = string.Empty;

        public string Key => _key;

        public string Version => _version;

        public abstract Type PayloadType { get; }

        public abstract object GetPayload();
    }
}
