using System;
using UnityEngine;

namespace ArkFramework.Samples
{
    public sealed class GameplayConfigAsset :
        ScriptableObjectConfigAsset
    {
        [SerializeField]
        private GameplayConfig _payload = new GameplayConfig();

        public GameplayConfig Payload => _payload;

        public override Type PayloadType => typeof(GameplayConfig);

        public override object GetPayload()
        {
            return _payload;
        }
    }
}
