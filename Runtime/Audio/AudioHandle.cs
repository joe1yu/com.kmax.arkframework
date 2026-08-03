using System;

namespace ArkFramework
{
    public sealed class AudioHandle : IAudioHandle
    {
        private readonly AudioService _owner;

        internal AudioHandle(
            AudioService owner,
            Guid instanceId,
            ResourceKey resourceKey,
            AudioChannel channel)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            InstanceId = instanceId;
            ResourceKey = resourceKey;
            Channel = channel;
        }

        public Guid InstanceId { get; }

        public ResourceKey ResourceKey { get; }

        public AudioChannel Channel { get; }

        public bool IsValid => _owner.IsHandleValid(this);

        internal bool IsOwnedBy(AudioService owner)
        {
            return ReferenceEquals(_owner, owner);
        }
    }
}
