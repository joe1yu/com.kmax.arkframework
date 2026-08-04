using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class RigServiceTests
    {
        private GameObject _prefab;
        private GameObject _sceneCameraObject;
        private PlatformService _platform;
        private RigService _service;

        [SetUp]
        public void SetUp()
        {
            _prefab = CreatePlatformPrefab();
            _platform = new PlatformService(
                _prefab,
                dontDestroyOnLoad: false);
            _service = new RigService(_platform);

            _sceneCameraObject = new GameObject(
                "Scene Camera",
                typeof(Camera),
                typeof(SceneCameraBinding),
                typeof(RigTestCameraComponent));
            SetField(
                _sceneCameraObject.GetComponent<SceneCameraBinding>(),
                "_rigId",
                "Main");
            SetField(
                _sceneCameraObject.GetComponent<SceneCameraBinding>(),
                "_slotId",
                "Primary");
            SetField(
                _sceneCameraObject.GetComponent<SceneCameraBinding>(),
                "_poseSource",
                true);
        }

        [TearDown]
        public void TearDown()
        {
            _service?.Dispose();
            if (_platform != null)
            {
                _platform.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Destroy(_sceneCameraObject);
            Destroy(_prefab);
        }

        [Test]
        public void ConstructorCollectsMultipleRigsAndActivatesDefaultRig()
        {
            Assert.That(_service.Rigs, Has.Count.EqualTo(2));
            Assert.That(_service.ActiveRigId, Is.EqualTo("Main"));
            Assert.That(_service.GetRig("Main").gameObject.activeSelf, Is.True);
            Assert.That(_service.GetRig("XR").gameObject.activeSelf, Is.False);

            _service.ActivateRig("XR");

            Assert.That(_service.ActiveRigId, Is.EqualTo("XR"));
            Assert.That(_service.GetRig("Main").gameObject.activeSelf, Is.False);
            Assert.That(_service.GetRig("XR").gameObject.activeSelf, Is.True);
        }

        [Test]
        public void SynchronizeCopiesPoseCameraAndWhitelistedComponent()
        {
            _sceneCameraObject.transform.SetPositionAndRotation(
                new Vector3(2f, 3f, -8f),
                Quaternion.Euler(5f, 15f, 0f));
            var sourceCamera = _sceneCameraObject.GetComponent<Camera>();
            sourceCamera.orthographic = true;
            sourceCamera.orthographicSize = 12f;
            sourceCamera.backgroundColor = Color.magenta;
            _sceneCameraObject.GetComponent<RigTestCameraComponent>().Value =
                17;

            var options = new SceneCameraSyncOptions(
                "Main",
                SceneCameraSyncFlags.RigPose |
                SceneCameraSyncFlags.CameraSettings |
                SceneCameraSyncFlags.Components,
                new[] { typeof(RigTestCameraComponent).FullName },
                disableSceneCameras: true);
            var result = _service.SynchronizeActiveScene(options);
            var targetRig = _service.GetRig("Main");
            var targetCamera = targetRig
                .GetComponentInChildren<RigCameraSlot>(true)
                .Camera;

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.MatchedCameraCount, Is.EqualTo(1));
            Assert.That(result.SynchronizedPoseCount, Is.EqualTo(1));
            Assert.That(result.SynchronizedCameraCount, Is.EqualTo(1));
            Assert.That(result.SynchronizedComponentCount, Is.EqualTo(1));
            Assert.That(result.DisabledSceneCameraCount, Is.EqualTo(1));
            Assert.That(
                Vector3.Distance(
                    targetCamera.transform.position,
                    _sceneCameraObject.transform.position),
                Is.LessThan(0.0001f));
            Assert.That(
                Quaternion.Angle(
                    targetCamera.transform.rotation,
                    _sceneCameraObject.transform.rotation),
                Is.LessThan(0.0001f));
            Assert.That(targetCamera.orthographic, Is.True);
            Assert.That(targetCamera.orthographicSize, Is.EqualTo(12f));
            Assert.That(
                targetCamera.GetComponent<RigTestCameraComponent>().Value,
                Is.EqualTo(17));
            Assert.That(sourceCamera.enabled, Is.False);
        }

        [Test]
        public void RegisteredSynchronizerOverridesDefaultComponentCopy()
        {
            _sceneCameraObject.GetComponent<RigTestCameraComponent>().Value =
                7;
            _service.RegisterComponentSynchronizer(
                new TestComponentSynchronizer());

            var result = _service.SynchronizeActiveScene(
                new SceneCameraSyncOptions(
                    "Main",
                    SceneCameraSyncFlags.Components,
                    new[] { typeof(RigTestCameraComponent).FullName }));
            var target = _service.GetRig("Main")
                .GetComponentInChildren<RigCameraSlot>(true)
                .GetComponent<RigTestCameraComponent>();

            Assert.That(result.Succeeded, Is.True);
            Assert.That(target.Value, Is.EqualTo(99));
        }

        private static GameObject CreatePlatformPrefab()
        {
            var root = new GameObject("Platform Prefab");
            CreateRig(root.transform, "Main", "Primary", true);
            CreateRig(root.transform, "XR", "Head", false);
            return root;
        }

        private static void CreateRig(
            Transform parent,
            string id,
            string slotId,
            bool activeByDefault)
        {
            var rigObject = new GameObject(id + " Rig", typeof(CameraRig));
            rigObject.transform.SetParent(parent, false);
            var rig = rigObject.GetComponent<CameraRig>();
            SetField(rig, "_id", id);
            SetField(rig, "_activeByDefault", activeByDefault);
            SetField(rig, "_poseRoot", rigObject.transform);

            var cameraObject = new GameObject(
                slotId + " Camera",
                typeof(Camera),
                typeof(RigCameraSlot));
            cameraObject.transform.SetParent(rigObject.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 1f, 0f);
            cameraObject.transform.localRotation = Quaternion.Euler(
                3f,
                0f,
                0f);
            SetField(
                cameraObject.GetComponent<RigCameraSlot>(),
                "_id",
                slotId);
        }

        private static void SetField(
            object target,
            string fieldName,
            object value)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        private static void Destroy(Object value)
        {
            if (value != null)
            {
                Object.DestroyImmediate(value);
            }
        }

        private sealed class TestComponentSynchronizer :
            IRigComponentSynchronizer
        {
            public bool CanSynchronize(Type componentType)
            {
                return componentType == typeof(RigTestCameraComponent);
            }

            public void Synchronize(Component source, Component target)
            {
                ((RigTestCameraComponent)target).Value = 99;
            }
        }
    }

    public sealed class RigTestCameraComponent : MonoBehaviour
    {
        public int Value;
    }
}
