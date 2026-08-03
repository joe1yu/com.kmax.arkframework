using System;
using System.Collections.Generic;

namespace ArkFramework
{
    public sealed class SceneDiagnostics
    {
        internal SceneDiagnostics(
            ResourceKey activeSceneKey,
            string activeSceneName,
            bool isTransitioning,
            int queueLength,
            SceneTransitionStage? currentStage,
            IReadOnlyList<ResourceKey> ownedSceneKeys,
            Exception lastException)
        {
            ActiveSceneKey = activeSceneKey;
            ActiveSceneName = activeSceneName ?? string.Empty;
            IsTransitioning = isTransitioning;
            QueueLength = queueLength;
            CurrentStage = currentStage;
            OwnedSceneKeys = ownedSceneKeys ??
                throw new ArgumentNullException(nameof(ownedSceneKeys));
            LastException = lastException;
        }

        public ResourceKey ActiveSceneKey { get; }

        public string ActiveSceneName { get; }

        public bool IsTransitioning { get; }

        public int QueueLength { get; }

        public SceneTransitionStage? CurrentStage { get; }

        public IReadOnlyList<ResourceKey> OwnedSceneKeys { get; }

        public Exception LastException { get; }
    }
}
