using UnityEngine;

namespace ArkFramework
{
    /// <summary>
    /// 定义平台预制体中的一个相机 Rig。一个平台可以同时声明多个 Rig。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraRig : MonoBehaviour
    {
        [SerializeField]
        private string _id;

        [SerializeField]
        private bool _activeByDefault;

        [SerializeField]
        private Transform _poseRoot;

        public string Id => _id;

        public bool ActiveByDefault => _activeByDefault;

        /// <summary>
        /// 场景相机位置同步的目标节点。未指定时使用当前节点。
        /// XR 扩展可把它指向 XR Origin 等实际移动节点。
        /// </summary>
        public Transform PoseRoot => _poseRoot == null ? transform : _poseRoot;
    }
}
