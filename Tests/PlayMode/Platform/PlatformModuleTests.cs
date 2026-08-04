using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class PlatformModuleTests
    {
        private readonly List<Object> _createdObjects =
            new List<Object>();
        private readonly List<PlatformService> _services =
            new List<PlatformService>();
        private readonly List<FrameworkRuntime> _runtimes =
            new List<FrameworkRuntime>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (var index = _runtimes.Count - 1; index >= 0; index--)
            {
                var task = _runtimes[index].DisposeAsync().AsTask();
                yield return WaitFor(task);
                task.GetAwaiter().GetResult();
            }

            _runtimes.Clear();
            for (var index = _services.Count - 1; index >= 0; index--)
            {
                var task = _services[index].DisposeAsync().AsTask();
                yield return WaitFor(task);
                task.GetAwaiter().GetResult();
            }

            _services.Clear();
            for (var index = _createdObjects.Count - 1; index >= 0; index--)
            {
                if (_createdObjects[index] != null)
                {
                    if (_createdObjects[index] is UIRoot uiRoot)
                    {
                        Object.Destroy(uiRoot.gameObject);
                    }
                    else
                    {
                        Object.Destroy(_createdObjects[index]);
                    }
                }
            }

            _createdObjects.Clear();
            yield return null;
        }

        [Test]
        public void Installer_DeclaresPlatformServiceAndCreatesModule()
        {
            var installer = Track(
                ScriptableObject.CreateInstance<PlatformModuleInstaller>());

            Assert.That(
                installer.ModuleId,
                Is.EqualTo(BuiltInModuleIds.Platform));
            Assert.That(installer.Dependencies, Is.Empty);
            Assert.That(
                installer.ServiceTypes,
                Is.EquivalentTo(new[] { typeof(IPlatformService) }));
            Assert.That(installer.PlatformPrefab, Is.Null);
            Assert.That(installer.DontDestroyOnLoad, Is.True);
            Assert.That(installer.CreateModule(), Is.TypeOf<PlatformModule>());
        }

        [Test]
        public void Service_InstantiatesPrefabAndUsesItsEventSystem()
        {
            var template = CreateTemplate(withEventSystem: true);
            var service = Track(new PlatformService(template, false));

            Assert.That(service.Root, Is.Not.SameAs(template));
            Assert.That(service.Root.name, Is.EqualTo(template.name));
            Assert.That(
                service.EventSystem.transform.IsChildOf(
                    service.Root.transform),
                Is.True);
            Assert.That(service.Canvases, Has.Count.EqualTo(1));
            Assert.That(
                service.Canvases[0].transform.IsChildOf(
                    service.Root.transform),
                Is.True);
        }

        [Test]
        public void Service_CreatesFallbackEventSystemWhenNoneExists()
        {
            var template = CreateTemplate(withEventSystem: false);
            var service = Track(new PlatformService(template, false));

            Assert.That(service.EventSystem, Is.Not.Null);
            Assert.That(
                service.EventSystem.GetComponent<StandaloneInputModule>(),
                Is.Not.Null);
            Assert.That(
                service.EventSystem.transform.parent,
                Is.EqualTo(service.Root.transform));
        }

        [Test]
        public void Service_RejectsConflictingRuntimeEventSystems()
        {
            var external = Track(
                new GameObject(
                    "External EventSystem",
                    typeof(EventSystem),
                    typeof(StandaloneInputModule)));
            var template = CreateTemplate(withEventSystem: true);

            var exception = Assert.Throws<InvalidOperationException>(
                () => new PlatformService(template, false));

            StringAssert.Contains(
                "another runtime EventSystem",
                exception.Message);
            Assert.That(external, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator Module_ConfiguresExistingAndLateCanvasesOnce()
        {
            var template = CreateTemplate(withEventSystem: false);
            template.AddComponent<TestRaycasterConfigurator>();
            var existing = CreateCanvas("Existing Canvas", withRaycaster: true);
            var runtime = Track(new FrameworkRuntime());
            var startTask = runtime.StartAsync(
                    new[]
                    {
                        new ModuleDescriptor(
                            BuiltInModuleIds.Platform,
                            Array.Empty<string>(),
                            0,
                            () => new PlatformModule(template, false))
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            var service = runtime.Services.Resolve<IPlatformService>();
            var configurator =
                service.Root.GetComponent<TestRaycasterConfigurator>();
            var platformCanvas =
                service.Root.GetComponentInChildren<Canvas>(true);
            Assert.That(existing.GetComponent<TestRaycaster>(), Is.Not.Null);
            Assert.That(
                platformCanvas.GetComponent<TestRaycaster>(),
                Is.Not.Null);
            Assert.That(configurator.ConfigureCount, Is.EqualTo(2));

            var late = CreateCanvas("Late Canvas", withRaycaster: true);
            runtime.Update(0f);
            runtime.Update(0f);

            Assert.That(late.GetComponent<TestRaycaster>(), Is.Not.Null);
            Assert.That(
                late.GetComponents<TestRaycaster>(),
                Has.Length.EqualTo(1));
            Assert.That(configurator.ConfigureCount, Is.EqualTo(3));
            yield return null;
            Assert.That(
                late.GetComponent<GraphicRaycaster>(),
                Is.TypeOf<TestRaycaster>());
        }

        [UnityTest]
        public IEnumerator Service_ConfiguresAllCanvasesCreatedByUIRoot()
        {
            var template = CreateTemplate(withEventSystem: false);
            template.AddComponent<TestRaycasterConfigurator>();
            var service = Track(new PlatformService(template, false));
            var uiRoot = Track(UIRoot.Create(false));

            service.RefreshCanvases();

            foreach (var layer in uiRoot.Layers)
            {
                Assert.That(
                    layer.Root.GetComponent<TestRaycaster>(),
                    Is.Not.Null,
                    layer.Layer + " canvas was not configured.");
            }

            yield return null;
        }

        [Test]
        public void Service_RejectsInvalidPlatformRaycasterType()
        {
            var template = CreateTemplate(withEventSystem: false);
            template.AddComponent<InvalidRaycasterConfigurator>();

            var exception = Assert.Throws<InvalidOperationException>(
                () => new PlatformService(template, false));

            StringAssert.Contains("BaseRaycaster", exception.Message);
        }

        private GameObject CreateTemplate(bool withEventSystem)
        {
            var template = Track(new GameObject("Platform Template"));
            var canvas = new GameObject(
                "Platform Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster));
            canvas.transform.SetParent(template.transform, false);
            if (withEventSystem)
            {
                var eventSystem = new GameObject(
                    "Platform EventSystem",
                    typeof(EventSystem),
                    typeof(StandaloneInputModule));
                eventSystem.transform.SetParent(template.transform, false);
            }

            return template;
        }

        private GameObject CreateCanvas(string name, bool withRaycaster)
        {
            var canvas = Track(
                new GameObject(
                    name,
                    typeof(RectTransform),
                    typeof(Canvas)));
            if (withRaycaster)
            {
                canvas.AddComponent<GraphicRaycaster>();
            }

            return canvas;
        }

        private T Track<T>(T value) where T : Object
        {
            _createdObjects.Add(value);
            return value;
        }

        private PlatformService Track(PlatformService service)
        {
            _services.Add(service);
            return service;
        }

        private FrameworkRuntime Track(FrameworkRuntime runtime)
        {
            _runtimes.Add(runtime);
            return runtime;
        }

        private static IEnumerator WaitFor(Task task)
        {
            for (var frame = 0; frame < 120 && !task.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, "Async operation timed out.");
        }

        public sealed class TestRaycaster : GraphicRaycaster
        {
        }

        public sealed class TestRaycasterConfigurator :
            PlatformGraphicRaycasterConfigurator
        {
            public int ConfigureCount { get; private set; }

            public override Type RaycasterType => typeof(TestRaycaster);

            protected override void ConfigureRaycaster(
                Canvas canvas,
                BaseRaycaster raycaster)
            {
                ConfigureCount++;
            }
        }

        public sealed class InvalidRaycasterConfigurator :
            PlatformGraphicRaycasterConfigurator
        {
            public override Type RaycasterType => typeof(Canvas);
        }
    }
}
