namespace ArkFramework
{
    public interface IUpdateModule
    {
        void Update(float deltaTime);
    }

    public interface ILateUpdateModule
    {
        void LateUpdate(float deltaTime);
    }

    public interface IFixedUpdateModule
    {
        void FixedUpdate(float fixedDeltaTime);
    }
}
