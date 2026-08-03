namespace ArkFramework
{
    public interface IObjectPool<T>
    {
        T Rent();

        void Return(T item);

        void Prewarm(int count);

        void Clear();

        PoolDiagnostics Diagnostics { get; }
    }
}
