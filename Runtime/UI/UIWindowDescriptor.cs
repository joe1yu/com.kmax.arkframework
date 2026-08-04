using System;

namespace ArkFramework
{
    public sealed class UIWindowDescriptor
    {
        public UIWindowDescriptor(
            string id,
            ResourceKey resourceKey,
            UILayer layer,
            UIWindowMode mode,
            bool cacheOnClose,
            bool requiresMask,
            bool closeOnMaskClick,
            bool blocksInput,
            bool? allowBack = null,
            string rootId = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A stable window descriptor ID is required.",
                    nameof(id));
            }

            if (string.IsNullOrWhiteSpace(resourceKey.Value))
            {
                throw new ArgumentException(
                    "An Addressable prefab resource key is required.",
                    nameof(resourceKey));
            }

            if (!Enum.IsDefined(typeof(UILayer), layer))
            {
                throw new ArgumentOutOfRangeException(nameof(layer));
            }

            if (!Enum.IsDefined(typeof(UIWindowMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (layer != UILayer.Popup &&
                (requiresMask || closeOnMaskClick))
            {
                throw new ArgumentException(
                    "Only Popup windows can use the shared popup mask.");
            }

            if (closeOnMaskClick && !requiresMask)
            {
                throw new ArgumentException(
                    "Mask click close requires the popup mask.");
            }

            if (blocksInput && !requiresMask)
            {
                throw new ArgumentException(
                    "Mask input blocking requires the popup mask.");
            }

            if (allowBack == true &&
                layer != UILayer.Normal &&
                layer != UILayer.Popup)
            {
                throw new ArgumentException(
                    "Only Normal and Popup windows can participate in Back navigation.");
            }

            Id = id;
            ResourceKey = resourceKey;
            Layer = layer;
            Mode = mode;
            CacheOnClose = cacheOnClose;
            RequiresMask = requiresMask;
            CloseOnMaskClick = closeOnMaskClick;
            BlocksInput = blocksInput;
            AllowBack = allowBack ??
                (layer == UILayer.Normal || layer == UILayer.Popup);
            RootId = string.IsNullOrWhiteSpace(rootId)
                ? layer.ToString()
                : rootId.Trim();
        }

        public string Id { get; }

        public ResourceKey ResourceKey { get; }

        public UILayer Layer { get; }

        public UIWindowMode Mode { get; }

        public bool CacheOnClose { get; }

        public bool RequiresMask { get; }

        public bool CloseOnMaskClick { get; }

        public bool BlocksInput { get; }

        public bool AllowBack { get; }

        public string RootId { get; }
    }
}
