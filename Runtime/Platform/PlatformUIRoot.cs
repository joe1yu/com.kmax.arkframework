using UnityEngine;

namespace ArkFramework
{
    /// <summary>
    /// 标记平台预制体中可承载业务 UI 的根节点。
    /// 节点的层级、坐标和所属 Canvas 完全由平台预制体控制。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlatformUIRoot : MonoBehaviour
    {
        [SerializeField]
        private string _id;

        public string Id => _id;

        public RectTransform RectTransform => transform as RectTransform;
    }
}
