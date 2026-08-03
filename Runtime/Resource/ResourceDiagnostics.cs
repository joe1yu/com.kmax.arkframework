using System;
using System.Collections.Generic;

namespace ArkFramework
{
    public enum ResourceLeaseKind
    {
        Asset,
        Instance,
        Label,
        Scene
    }

    public sealed class ResourceLeaseDiagnostics
    {
        internal ResourceLeaseDiagnostics(
            long leaseId,
            ResourceLeaseKind kind,
            string keyOrLabel,
            Type assetType,
            DateTime createdUtc)
        {
            LeaseId = leaseId;
            Kind = kind;
            KeyOrLabel = keyOrLabel;
            AssetType = assetType;
            CreatedUtc = createdUtc;
        }

        public long LeaseId { get; }

        public ResourceLeaseKind Kind { get; }

        public string KeyOrLabel { get; }

        public Type AssetType { get; }

        public DateTime CreatedUtc { get; }
    }

    public sealed class ResourceDiagnostics
    {
        internal ResourceDiagnostics(
            IReadOnlyList<ResourceLeaseDiagnostics> outstandingLeases,
            int inflightOperationCount)
        {
            OutstandingLeases = outstandingLeases ??
                throw new ArgumentNullException(nameof(outstandingLeases));
            InflightOperationCount = inflightOperationCount;
        }

        public IReadOnlyList<ResourceLeaseDiagnostics> OutstandingLeases
        {
            get;
        }

        public int InflightOperationCount { get; }
    }
}
