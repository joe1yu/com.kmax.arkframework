using NUnit.Framework;

namespace ArkFramework.Tests
{
    public sealed class BuiltInModuleIdsTests
    {
        [TestCase(BuiltInModuleIds.EventBus, "EventBus")]
        [TestCase(BuiltInModuleIds.Platform, "Platform")]
        [TestCase(BuiltInModuleIds.Resource, "Resource")]
        [TestCase(BuiltInModuleIds.Pool, "Pool")]
        [TestCase(BuiltInModuleIds.Config, "Config")]
        [TestCase(BuiltInModuleIds.Fsm, "FSM")]
        [TestCase(BuiltInModuleIds.Scene, "Scene")]
        [TestCase(BuiltInModuleIds.UI, "UI")]
        [TestCase(BuiltInModuleIds.Audio, "Audio")]
        [TestCase(BuiltInModuleIds.ActionKit, "ActionKit")]
        [TestCase(BuiltInModuleIds.Table, "Table")]
        [TestCase(BuiltInModuleIds.Procedure, "Procedure")]
        public void BuiltInId_IsStable(string actual, string expected)
        {
            Assert.That(actual, Is.EqualTo(expected));
        }
    }
}
