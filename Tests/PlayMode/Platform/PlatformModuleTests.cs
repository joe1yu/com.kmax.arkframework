using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private static readonly FieldInfo UIRootIdField =
            typeof(PlatformUIRoot).GetField(
                "_id",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<Object> _createdObjects = new List<Object>();
        private readonly List<UIRoot> _uiRoots = new List<UIRoot>();
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
            for (var index = _uiRoots.Count - 1; index >= 0; index--)
            {
                if (_uiRoots[index] != null)
                {
                    var task = _uiRoots[index].DisposeAsync().AsTask();
                    yield return WaitFor(task);
                    task.GetAwaiter().GetResult();
                }
            }

            _uiRoots.Clear();
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
                    Object.Destroy(_createdObjects[index]);
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

            Assert.That(installer.ModuleId, Is.EqualTo(BuiltInModuleIds.Platform));
            Assert.That(installer.Dependencies, Is.Empty);
            Assert.That(
                installer.ServiceTypes,
                Is.EquivalentTo(new[] { typeof(IPlatformService) }));
            Assert.That(installer.PlatformPrefab, Is.Null);
            Assert.That(installer.DontDestroyOnLoad, Is.True);
            Assert.That(installer.CreateModule(), Is.TypeOf<PlatformModule>());
        }

        [Test]
        public void Service_FindsDeepUIRootsAndPreservesEveryCanvasMode()
        {
            var template = CreateTemplateWithThreeCanvasModes();
            var service = Track(new PlatformService(template, false));

            Assert.That(service.Root, Is.Not.SameAs(template));
            Assert.That(service.UIRoots, Has.Count.EqualTo(3));
            Assert.That(
                service.UIRoots.Select(root => root.Id),
                Is.EquivalentTo(new[] { "Overlay", "Camera", "World" }));
            Assert.That(
                service.GetUIRoot("World").anchoredPosition3D,
                Is.EqualTo(new Vector3(11f, 22f, 33f)));
            Assert.That(
                service.Root.GetComponentsInChildren<Canvas>(true)
                    .Select(canvas => canvas.renderMode),
                Is.EquivalentTo(
                    new[]
                    {
                        RenderMode.ScreenSpaceOverlay,
                        RenderMode.ScreenSpaceCamera,
                        RenderMode.WorldSpace
                    }));
            Assert.That(
                service.UIRoots.All(root => root.transform.parent.parent !=
                                            service.Root.transform),
                Is.True);
        }

        [Test]
        public void Service_DoesNotCreateOrValidateEventSystems()
        {
            var first = Track(new GameObject("First", typeof(EventSystem)));
            var second = Track(new GameObject("Second", typeof(EventSystem)));
            var template = CreateTemplateWithThreeCanvasModes();

            var service = Track(new PlatformService(template, false));

            Assert.That(
                service.Root.GetComponentsInChildren<EventSystem>(true),
                Is.Empty);
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator Service_ConfiguresOnlyPrefabCanvases()
        {
            var template = CreateTemplateWithThreeCanvasModes();
            template.AddComponent<TestRaycasterConfigurator>();
            var external = CreateCanvas(
                Track(new GameObject("External")),
                "External Canvas",
                RenderMode.ScreenSpaceOverlay,
                Vector3.zero);

            var service = Track(new PlatformService(template, false));
            yield return null;

            var platformCanvases =
                service.Root.GetComponentsInChildren<Canvas>(true);
            Assert.That(
                platformCanvases.All(
                    canvas => canvas.GetComponent<TestRaycaster>() != null),
                Is.True);
            Assert.That(
                platformCanvases.All(
                    canvas => canvas.GetComponents<TestRaycaster>().Length == 1),
                Is.True);
            Assert.That(external.GetComponent<TestRaycaster>(), Is.Null);
            Assert.That(
                external.GetComponent<GraphicRaycaster>(),
                Is.TypeOf<GraphicRaycaster>());
        }

        [Test]
        public void Service_RejectsInvalidOrDuplicateUIRoots()
        {
            var missingId = Track(new GameObject("Missing Id"));
            CreateUIRoot(missingId.transform, null, Vector3.zero);
            Assert.Throws<InvalidOperationException>(
                () => new PlatformService(missingId, false));

            var duplicate = Track(new GameObject("Duplicate"));
            CreateUIRoot(duplicate.transform, "Same", Vector3.zero);
            CreateUIRoot(duplicate.transform, "Same", Vector3.zero);
            Assert.Throws<InvalidOperationException>(
                () => new PlatformService(duplicate, false));
        }

        [UnityTest]
        public IEnumerator UIRoot_BindsNamedPlatformRootsWithoutOwningTransforms()
        {
            var template = Track(new GameObject("Platform Template"));
            CreateUIRoot(template.transform, "Normal", new Vector3(1f, 2f, 3f));
            CreateUIRoot(template.transform, "Popup", new Vector3(4f, 5f, 6f));
            CreateUIRoot(template.transform, "WorldPanel", new Vector3(7f, 8f, 9f));
            var service = Track(new PlatformService(template, false));
            var root = Track(UIRoot.Create(service));

            Assert.That(root.GetLayerRoot(UILayer.Normal),
                Is.SameAs(service.GetUIRoot("Normal")));
            Assert.That(root.GetRoot("WorldPanel"),
                Is.SameAs(service.GetUIRoot("WorldPanel")));

            var worldRoot = service.GetUIRoot("WorldPanel");
            worldRoot.anchoredPosition3D = new Vector3(10f, 20f, 30f);
            yield return null;
            Assert.That(root.GetRoot("WorldPanel").anchoredPosition3D,
                Is.EqualTo(new Vector3(10f, 20f, 30f)));
            Assert.Throws<KeyNotFoundException>(() => root.GetRoot("Missing"));
        }

        [Test]
        public void Service_RejectsInvalidPlatformRaycasterType()
        {
            var template = CreateTemplateWithThreeCanvasModes();
            template.AddComponent<InvalidRaycasterConfigurator>();

            var exception = Assert.Throws<InvalidOperationException>(
                () => new PlatformService(template, false));

            StringAssert.Contains("BaseRaycaster", exception.Message);
        }

        private GameObject CreateTemplateWithThreeCanvasModes()
        {
            var template = Track(new GameObject("Platform Template"));
            var nested = new GameObject("Nested");
            nested.transform.SetParent(template.transform, false);
            CreateCanvas(nested, "Overlay", RenderMode.ScreenSpaceOverlay,
                Vector3.zero);
            CreateCanvas(nested, "Camera", RenderMode.ScreenSpaceCamera,
                Vector3.zero);
            CreateCanvas(nested, "World", RenderMode.WorldSpace,
                new Vector3(11f, 22f, 33f));
            return template;
        }

        private static Canvas CreateCanvas(
            GameObject parent,
            string id,
            RenderMode renderMode,
            Vector3 rootPosition)
        {
            var canvasObject = new GameObject(
                id + " Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(parent.transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            if (renderMode == RenderMode.ScreenSpaceCamera)
            {
                var cameraObject = new GameObject(
                    id + " Camera",
                    typeof(Camera));
                cameraObject.transform.SetParent(parent.transform, false);
                canvas.worldCamera = cameraObject.GetComponent<Camera>();
            }

            canvas.renderMode = renderMode;
            var group = new GameObject(id + " Group", typeof(RectTransform));
            group.transform.SetParent(canvasObject.transform, false);
            CreateUIRoot(group.transform, id, rootPosition);
            return canvas;
        }

        private static PlatformUIRoot CreateUIRoot(
            Transform parent,
            string id,
            Vector3 localPosition)
        {
            var rootObject = new GameObject(
                (id ?? "Missing") + " UI Root",
                typeof(RectTransform),
                typeof(PlatformUIRoot));
            rootObject.transform.SetParent(parent, false);
            rootObject.GetComponent<RectTransform>().anchoredPosition3D =
                localPosition;
            var root = rootObject.GetComponent<PlatformUIRoot>();
            UIRootIdField.SetValue(root, id);
            return root;
        }

        private T Track<T>(T value) where T : Object
        {
            _createdObjects.Add(value);
            return value;
        }

        private UIRoot Track(UIRoot root)
        {
            _uiRoots.Add(root);
            return root;
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
            public override Type RaycasterType => typeof(TestRaycaster);
        }

        public sealed class InvalidRaycasterConfigurator :
            PlatformGraphicRaycasterConfigurator
        {
            public override Type RaycasterType => typeof(Canvas);
        }
    }
}
