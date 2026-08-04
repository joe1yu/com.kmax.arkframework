using UnityEngine;

namespace ArkFramework
{
    /// <summary>
    /// 标记 Rig 中可由场景相机同步的一个相机槽位。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class RigCameraSlot : MonoBehaviour
    {
        [SerializeField]
        private string _id;

        public string Id => _id;

        public Camera Camera => GetComponent<Camera>();
    }
}
