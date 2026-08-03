using System;

namespace ArkFramework.Samples
{
    [Serializable]
    public sealed class GameplayConfig
    {
        public int StartingLives;
        public float MoveSpeed;
        public string WelcomeMessage;
    }
}
