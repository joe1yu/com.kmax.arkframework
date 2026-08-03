namespace ArkFramework
{
    public interface IPoolable
    {
        void OnRent();

        void OnReturn();
    }
}
