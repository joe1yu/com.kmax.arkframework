using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    public interface IPlatformService : IAsyncDisposable
    {
        GameObject Root { get; }

        IReadOnlyList<PlatformUIRoot> UIRoots { get; }

        bool TryGetUIRoot(string id, out RectTransform root);

        RectTransform GetUIRoot(string id);
    }
}
