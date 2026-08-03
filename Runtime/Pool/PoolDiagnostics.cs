namespace ArkFramework
{
    public sealed class PoolDiagnostics
    {
        internal PoolDiagnostics(
            long totalCreatedCount,
            int activeCount,
            int idleCount,
            int peakActiveCount,
            double hitRate)
        {
            TotalCreatedCount = totalCreatedCount;
            ActiveCount = activeCount;
            IdleCount = idleCount;
            PeakActiveCount = peakActiveCount;
            HitRate = hitRate;
        }

        public long TotalCreatedCount { get; }

        public int ActiveCount { get; }

        public int IdleCount { get; }

        public int PeakActiveCount { get; }

        public double HitRate { get; }
    }
}
