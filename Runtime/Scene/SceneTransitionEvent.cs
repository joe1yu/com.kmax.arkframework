using System;

namespace ArkFramework
{
    public readonly struct SceneTransitionEvent
    {
        internal SceneTransitionEvent(
            long requestId,
            SceneRequest request,
            SceneTransitionStage stage,
            float progress,
            SceneTransitionStage? failureStage,
            Exception exception)
        {
            RequestId = requestId;
            Request = request;
            Stage = stage;
            Progress = progress;
            FailureStage = failureStage;
            Exception = exception;
        }

        public long RequestId { get; }

        public SceneRequest Request { get; }

        public ResourceKey Key => Request.Key;

        public string SceneId => Request.Id;

        public SceneCameraSyncOptions CameraSync => Request.CameraSync;

        public SceneLoadMode Mode => Request.Mode;

        public SceneTransitionStage Stage { get; }

        public float Progress { get; }

        public SceneTransitionStage? FailureStage { get; }

        public Exception Exception { get; }
    }
}
