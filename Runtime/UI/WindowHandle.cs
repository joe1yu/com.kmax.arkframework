using System;

namespace ArkFramework
{
    public sealed class WindowHandle : IWindowHandle
    {
        private readonly UIService _owner;

        internal WindowHandle(
            UIService owner,
            string descriptorId,
            Guid instanceId,
            Type windowType)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            DescriptorId = descriptorId;
            InstanceId = instanceId;
            WindowType = windowType;
        }

        public string DescriptorId { get; }

        public string WindowId => DescriptorId;

        public Guid InstanceId { get; }

        public Type WindowType { get; }

        public bool IsValid => _owner.IsHandleValid(this);

        internal bool IsOwnedBy(UIService service)
        {
            return ReferenceEquals(_owner, service);
        }
    }
}
