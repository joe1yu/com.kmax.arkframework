using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public enum UIWindowState
    {
        Opening,
        Open,
        Closing,
        Cached
    }

    public sealed class UILayerDiagnostics
    {
        internal UILayerDiagnostics(
            UILayer layer,
            string rootName,
            int sortingOrder)
        {
            Layer = layer;
            RootName = rootName;
            SortingOrder = sortingOrder;
        }

        public UILayer Layer { get; }

        public string RootName { get; }

        public int SortingOrder { get; }
    }

    public sealed class UIWindowDiagnostics
    {
        internal UIWindowDiagnostics(
            string descriptorId,
            Guid instanceId,
            UILayer layer,
            UIWindowState state)
        {
            DescriptorId = descriptorId;
            InstanceId = instanceId;
            Layer = layer;
            State = state;
        }

        public string DescriptorId { get; }

        public Guid InstanceId { get; }

        public UILayer Layer { get; }

        public UIWindowState State { get; }
    }

    public sealed class UIDiagnostics
    {
        internal UIDiagnostics(
            IReadOnlyList<UILayerDiagnostics> layers,
            IReadOnlyList<UIWindowDiagnostics> windows,
            IReadOnlyList<Guid> normalNavigation,
            IReadOnlyList<Guid> popupNavigation,
            Guid? maskPopupInstanceId,
            Exception recentException)
        {
            Layers = layers;
            Windows = windows;
            NormalNavigation = normalNavigation;
            PopupNavigation = popupNavigation;
            MaskPopupInstanceId = maskPopupInstanceId;
            RecentException = recentException;

            foreach (var window in windows)
            {
                switch (window.State)
                {
                    case UIWindowState.Opening:
                        OpeningCount++;
                        break;
                    case UIWindowState.Open:
                        OpenCount++;
                        break;
                    case UIWindowState.Closing:
                        ClosingCount++;
                        break;
                    case UIWindowState.Cached:
                        CachedCount++;
                        break;
                }
            }
        }

        public IReadOnlyList<UILayerDiagnostics> Layers { get; }

        public IReadOnlyList<UIWindowDiagnostics> Windows { get; }

        public IReadOnlyList<Guid> NormalNavigation { get; }

        public IReadOnlyList<Guid> PopupNavigation { get; }

        public int OpeningCount { get; }

        public int OpenCount { get; }

        public int ClosingCount { get; }

        public int CachedCount { get; }

        public Guid? MaskPopupInstanceId { get; }

        public Exception RecentException { get; }

        internal static IReadOnlyList<T> ReadOnly<T>(T[] values)
        {
            return new ReadOnlyCollection<T>(values);
        }
    }
}
