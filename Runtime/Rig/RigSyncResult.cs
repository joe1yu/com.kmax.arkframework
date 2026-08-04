using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public sealed class RigSyncResult
    {
        private static readonly IReadOnlyList<string> NoErrors =
            Array.AsReadOnly(Array.Empty<string>());

        internal RigSyncResult(
            string sceneName,
            string rigId,
            int matchedCameraCount,
            int synchronizedPoseCount,
            int synchronizedCameraCount,
            int synchronizedComponentCount,
            int disabledSceneCameraCount,
            IList<string> errors)
        {
            SceneName = sceneName ?? string.Empty;
            RigId = rigId ?? string.Empty;
            MatchedCameraCount = matchedCameraCount;
            SynchronizedPoseCount = synchronizedPoseCount;
            SynchronizedCameraCount = synchronizedCameraCount;
            SynchronizedComponentCount = synchronizedComponentCount;
            DisabledSceneCameraCount = disabledSceneCameraCount;
            Errors = errors == null || errors.Count == 0
                ? NoErrors
                : new ReadOnlyCollection<string>(
                    new List<string>(errors));
        }

        public string SceneName { get; }

        public string RigId { get; }

        public int MatchedCameraCount { get; }

        public int SynchronizedPoseCount { get; }

        public int SynchronizedCameraCount { get; }

        public int SynchronizedComponentCount { get; }

        public int DisabledSceneCameraCount { get; }

        public IReadOnlyList<string> Errors { get; }

        public bool Succeeded => Errors.Count == 0;

        internal static RigSyncResult Empty { get; } = new RigSyncResult(
            string.Empty,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            null);
    }
}
