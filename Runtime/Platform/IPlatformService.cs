using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ArkFramework
{
    public interface IPlatformService : IAsyncDisposable
    {
        GameObject Root { get; }

        IReadOnlyList<PlatformUIRoot> UIRoots { get; }

        [Obsolete(
            "仅用于兼容旧版已导入 Sample；平台模块不管理 EventSystem。")]
        EventSystem EventSystem { get; }

        [Obsolete(
            "仅用于兼容旧版已导入 Sample；请通过平台预制体自行管理 Canvas。")]
        IReadOnlyList<Canvas> Canvases { get; }

        bool TryGetUIRoot(string id, out RectTransform root);

        RectTransform GetUIRoot(string id);
    }
}
