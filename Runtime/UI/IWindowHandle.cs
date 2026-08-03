using System;

namespace ArkFramework
{
    public interface IWindowHandle
    {
        string DescriptorId { get; }
        string WindowId { get; }
        Guid InstanceId { get; }
        Type WindowType { get; }
        bool IsValid { get; }
    }
}
