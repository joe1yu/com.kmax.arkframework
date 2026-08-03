#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class SceneIntegrationTests :
        IPrebuildSetup,
        IPostBuildCleanup
    {
        private const string TempRoot =
            "Assets/ArkFrameworkSceneIntegrationTests";
        private const string FirstScenePath = TempRoot + "/First.unity";
        private const string SecondScenePath = TempRoot + "/Second.unity";
        private const string FirstAddress =
            "ark-framework-tests/scene-first";
        private const string SecondAddress =
            "ark-framework-tests/scene-second";
        private const string MissingAddress =
            "ark-framework-tests/scene-missing";
        private const string GroupName =
            "ArkFramework Scene PlayMode Tests";
        private const string SetupActiveKey =
            "ArkFramework.SceneTests.SetupActive";
        private const string SettingsExistedKey =
            "ArkFramework.SceneTests.SettingsExisted";
        private const string PreviousSettingsPathKey =
            "ArkFramework.SceneTests.PreviousSettingsPath";
        private const string PreviousBuilderIndexKey =
            "ArkFramework.SceneTests.PreviousBuilderIndex";

        private Action<
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle,
            Exception> _previousExceptionHandler;
        private ResourceService _resources;
        private SceneService _scenes;

        void IPrebuildSetup.Setup()
        {
            if (EditorPrefs.GetBool(SetupActiveKey))
            {
                CleanupEditorAssets();
            }

            var settingsExisted =
                AddressableAssetSettingsDefaultObject.SettingsExists;
            var previousSettings =
                AddressableAssetSettingsDefaultObject.Settings;
            EditorPrefs.SetBool(SettingsExistedKey, settingsExisted);
            EditorPrefs.SetString(
                PreviousSettingsPathKey,
                previousSettings == null
                    ? string.Empty
                    : AssetDatabase.GetAssetPath(previousSettings));
            EditorPrefs.SetInt(
                PreviousBuilderIndexKey,
                previousSettings == null
                    ? 0
                    : previousSettings.ActivePlayModeDataBuilderIndex);
            EditorPrefs.SetBool(SetupActiveKey, true);

            var settings =
                AddressableAssetSettingsDefaultObject.GetSettings(true);
            var fastModeIndex = settings.DataBuilders.FindIndex(
                builder => builder is BuildScriptFastMode);
            if (fastModeIndex < 0)
            {
                throw new InvalidOperationException(
                    "Addressables settings did not create a FastMode builder.");
            }

            settings.ActivePlayModeDataBuilderIndex = fastModeIndex;
            if (!AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.CreateFolder(
                    "Assets",
                    "ArkFrameworkSceneIntegrationTests");
            }

            CreateScene(FirstScenePath, "FirstSceneMarker");
            CreateScene(SecondScenePath, "SecondSceneMarker");

            var group = settings.CreateGroup(
                GroupName,
                false,
                false,
                false,
                null,
                typeof(BundledAssetGroupSchema));
            AddSceneEntry(settings, group, FirstScenePath, FirstAddress);
            AddSceneEntry(settings, group, SecondScenePath, SecondAddress);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var buildResult = settings.ActivePlayModeDataBuilder
                .BuildData<AddressablesPlayModeBuildResult>(
                    new AddressablesDataBuilderInput(settings));
            if (!string.IsNullOrEmpty(buildResult.Error))
            {
                throw new InvalidOperationException(buildResult.Error);
            }
        }

        void IPostBuildCleanup.Cleanup()
        {
            CleanupEditorAssets();
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _previousExceptionHandler = ResourceManager.ExceptionHandler;
            ResourceManager.ExceptionHandler = (_, __) =>
            {
                // The missing-key test asserts the propagated exception.
            };
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            ResourceManager.ExceptionHandler = _previousExceptionHandler;
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            var initialization = Addressables.InitializeAsync(
                autoReleaseHandle: false);
            yield return initialization;
            Assert.That(
                initialization.Status,
                Is.EqualTo(
                    UnityEngine.ResourceManagement.AsyncOperations
                        .AsyncOperationStatus.Succeeded),
                initialization.OperationException?.ToString());
            Addressables.Release(initialization);

            _resources = new ResourceService(
                new AddressablesResourceBackend());
            _scenes = new SceneService(
                new ResourceSceneBackend(_resources),
                new SilentEventBus());
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scenes != null)
            {
                var stop = _scenes.StopAsync().AsTask();
                yield return WaitForTask(stop);
                stop.GetAwaiter().GetResult();
                var dispose = _scenes.DisposeAsync().AsTask();
                yield return WaitForTask(dispose);
                dispose.GetAwaiter().GetResult();
                _scenes = null;
            }

            if (_resources != null)
            {
                var stop = _resources.StopAsync().AsTask();
                yield return WaitForTask(stop);
                stop.GetAwaiter().GetResult();
                var dispose = _resources.DisposeAsync().AsTask();
                yield return WaitForTask(dispose);
                dispose.GetAwaiter().GetResult();
                _resources = null;
            }
        }

        [UnityTest]
        public IEnumerator SingleActivatesTargetAndUnloadsPreviousOwnedScene()
        {
            var first = _scenes.LoadAsync(
                new SceneRequest(
                    new ResourceKey(FirstAddress),
                    SceneLoadMode.Additive,
                    true)).AsTask();
            yield return WaitForTask(first);
            first.GetAwaiter().GetResult();
            var firstScene = SceneManager.GetActiveScene();
            Assert.That(firstScene.path, Is.EqualTo(FirstScenePath));

            var second = _scenes.LoadAsync(
                new SceneRequest(
                    new ResourceKey(SecondAddress),
                    SceneLoadMode.Single,
                    true)).AsTask();
            yield return WaitForTask(second);
            second.GetAwaiter().GetResult();

            Assert.That(
                SceneManager.GetActiveScene().path,
                Is.EqualTo(SecondScenePath));
            Assert.That(firstScene.isLoaded, Is.False);
            Assert.That(
                _scenes.Diagnostics.OwnedSceneKeys.Count,
                Is.EqualTo(1));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator MissingKeyPreservesActiveAndLeaksNoTargetLease()
        {
            var first = _scenes.LoadAsync(
                new SceneRequest(
                    new ResourceKey(FirstAddress),
                    SceneLoadMode.Additive,
                    true)).AsTask();
            yield return WaitForTask(first);
            first.GetAwaiter().GetResult();
            var activeBefore = SceneManager.GetActiveScene();
            var leasesBefore =
                _resources.Diagnostics.OutstandingLeases.Count;

            var missing = _scenes.LoadAsync(
                new SceneRequest(
                    new ResourceKey(MissingAddress),
                    SceneLoadMode.Single,
                    true)).AsTask();
            yield return WaitForTask(missing);
            Assert.That(missing.IsFaulted, Is.True);
            Assert.Catch<Exception>(
                () => missing.GetAwaiter().GetResult());

            Assert.That(
                SceneManager.GetActiveScene(),
                Is.EqualTo(activeBefore));
            Assert.That(
                _scenes.Diagnostics.OwnedSceneKeys.Count,
                Is.EqualTo(1));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(leasesBefore));
            Assert.That(
                _resources.Diagnostics.InflightOperationCount,
                Is.Zero);
        }

        private static void CreateScene(string path, string markerName)
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Additive);
            new GameObject(markerName);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                throw new InvalidOperationException(
                    $"Failed to save temporary scene '{path}'.");
            }

            EditorSceneManager.CloseScene(scene, true);
        }

        private static void AddSceneEntry(
            AddressableAssetSettings settings,
            AddressableAssetGroup group,
            string path,
            string address)
        {
            var entry = settings.CreateOrMoveEntry(
                AssetDatabase.AssetPathToGUID(path),
                group,
                false,
                false);
            entry.SetAddress(address, false);
        }

        private static void CleanupEditorAssets()
        {
            if (!EditorPrefs.GetBool(SetupActiveKey))
            {
                return;
            }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings != null)
            {
                var group = settings.FindGroup(GroupName);
                if (group != null)
                {
                    settings.RemoveGroup(group);
                }
            }

            AssetDatabase.DeleteAsset(TempRoot);
            if (EditorPrefs.GetBool(SettingsExistedKey))
            {
                var previousSettings = AssetDatabase.LoadAssetAtPath<
                    AddressableAssetSettings>(
                    EditorPrefs.GetString(PreviousSettingsPathKey));
                if (previousSettings != null)
                {
                    previousSettings.ActivePlayModeDataBuilderIndex =
                        EditorPrefs.GetInt(PreviousBuilderIndexKey);
                    AddressableAssetSettingsDefaultObject.Settings =
                        previousSettings;
                    EditorUtility.SetDirty(previousSettings);
                }
            }
            else
            {
                AddressableAssetSettingsDefaultObject.Settings = null;
                EditorBuildSettings.RemoveConfigObject(
                    AddressableAssetSettingsDefaultObject
                        .kDefaultConfigObjectName);
                AssetDatabase.DeleteAsset(
                    AddressableAssetSettingsDefaultObject
                        .kDefaultConfigFolder);
            }

            EditorPrefs.DeleteKey(SetupActiveKey);
            EditorPrefs.DeleteKey(SettingsExistedKey);
            EditorPrefs.DeleteKey(PreviousSettingsPathKey);
            EditorPrefs.DeleteKey(PreviousBuilderIndexKey);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static IEnumerator WaitForTask(Task task)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted &&
                   elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                yield return null;
            }

            Assert.That(
                task.IsCompleted,
                Is.True,
                "Scene integration operation timed out after " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") +
                " seconds.");
        }

        private sealed class SilentEventBus : IEventBus
        {
            public EventBusDiagnostics Diagnostics => null;

            public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
            {
                return new NoopDisposable();
            }

            public IDisposable Subscribe<TEvent>(
                ModuleScope ownerScope,
                Action<TEvent> handler)
            {
                return new NoopDisposable();
            }

            public void Publish<TEvent>(TEvent value)
            {
            }

            public void Enqueue<TEvent>(TEvent value)
            {
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
#endif
