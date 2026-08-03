namespace ArkFramework
{
    public interface IConfigValidator<T>
    {
        void Validate(string key, T value);
    }
}
