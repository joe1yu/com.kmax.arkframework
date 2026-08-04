using System;
using UnityEngine;

namespace ArkFramework
{
    /// <summary>
    /// 为需要特殊复制方式的相机组件提供扩展点，例如未来的 XR 组件。
    /// </summary>
    public interface IRigComponentSynchronizer
    {
        bool CanSynchronize(Type componentType);

        void Synchronize(Component source, Component target);
    }
}
