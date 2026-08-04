using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ArkFramework
{
    public interface IPlatformService : IAsyncDisposable
    {
        GameObject Root { get; }

        EventSystem EventSystem { get; }

        IReadOnlyList<Canvas> Canvases { get; }

        void RefreshCanvases();
    }
}
