using System;

namespace ArkFramework.Samples
{
    /// <summary>
    /// Sample UI.csv 对应的数据行。
    /// </summary>
    [Serializable]
    public sealed class SampleUIRow
    {
        public string Id { get; set; }

        public string WindowType { get; set; }

        public string Address { get; set; }

        public UILayer Layer { get; set; }

        public UIWindowMode Mode { get; set; }

        public bool CacheOnClose { get; set; }

        public bool RequiresMask { get; set; }

        public bool CloseOnMaskClick { get; set; }

        public bool BlocksInput { get; set; }

        public bool AllowBack { get; set; }
    }
}
