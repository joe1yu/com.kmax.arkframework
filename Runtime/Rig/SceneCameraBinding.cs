using UnityEngine;

namespace ArkFramework
{
    /// <summary>
    /// 把场景中的相机映射到指定 Rig 和相机槽位。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class SceneCameraBinding : MonoBehaviour
    {
        [SerializeField]
        private string _rigId;

        [SerializeField]
        private string _slotId;

        [SerializeField]
        private bool _poseSource;

        public string RigId => _rigId;

        public string SlotId => _slotId;

        /// <summary>
        /// 指定此相机的位置和旋转用于移动 Rig 的 PoseRoot。
        /// 每次同步最多允许一个匹配相机声明为位置来源。
        /// </summary>
        public bool PoseSource => _poseSource;

        public Camera Camera => GetComponent<Camera>();
    }
}
