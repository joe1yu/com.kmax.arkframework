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
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class AddressablesResourceTests :
        IPrebuildSetup,
        IPostBuildCleanup
    {
        private const string TempRoot =
            "Assets/ArkFrameworkAddressablesResourceTests";
        private const string AssetPath = TempRoot + "/ResourceAsset.asset";
        private const string PrefabPath = TempRoot + "/ResourcePrefab.prefab";
        private const string AssetAddress =
            "ark-framework-tests/resource-asset";
        private const string PrefabAddress =
            "ark-framework-tests/resource-prefab";
        private const string GroupName =
            "ArkFramework Resource PlayMode Tests";
        private const string SetupActiveKey =
            "ArkFramework.ResourceTests.SetupActive";
        private const string SettingsExistedKey =
            "ArkFramework.ResourceTests.SettingsExisted";
        private const string PreviousSettingsPathKey =
            "ArkFramework.ResourceTests.PreviousSettingsPath";
        private const string PreviousBuilderIndexKey =
            "ArkFramework.ResourceTests.PreviousBuilderIndex";

        private Action<
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle,
            Exception> _previousExceptionHandler;
        private ResourceService _service;

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
                    "ArkFrameworkAddressablesResourceTests");
            }

            var asset = ScriptableObject.CreateInstance<FrameworkProfile>();
            asset.name = "Addressables Resource Test Profile";
            AssetDatabase.CreateAsset(asset, AssetPath);

            var prefabSource = new GameObject("ResourcePrefab");
            try
            {
                PrefabUtility.SaveAsPrefabAsset(prefabSource, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(prefabSource);
            }

            var group = settings.CreateGroup(
                GroupName,
                false,
                false,
                false,
                null,
                typeof(BundledAssetGroupSchema));
            var assetEntry = settings.CreateOrMoveEntry(
                AssetDatabase.AssetPathToGUID(AssetPath),
                group,
                false,
                false);
            assetEntry.SetAddress(AssetAddress, false);
            var prefabEntry = settings.CreateOrMoveEntry(
                AssetDatabase.AssetPathToGUID(PrefabPath),
                group,
                false,
                false);
            prefabEntry.SetAddress(PrefabAddress, false);
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
                // The failed-key test asserts the propagated exception directly.
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

            _service = new ResourceService(
                new AddressablesResourceBackend());
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_service != null)
            {
                var diagnostics = _service.Diagnostics;
                if (diagnostics.InflightOperationCount != 0)
                {
                    _service = null;
                    Assert.Fail(
                        "ResourceService teardown abandoned " +
                        diagnostics.InflightOperationCount +
                        " incomplete Addressables operation(s) to avoid " +
                        "waiting forever after a failed test.");
                }

                var stopTask = _service.StopAsync().AsTask();
                yield return WaitForTask(stopTask);
                stopTask.GetAwaiter().GetResult();
                var disposeTask = _service.DisposeAsync().AsTask();
                yield return WaitForTask(disposeTask);
                disposeTask.GetAwaiter().GetResult();
                _service = null;
            }
        }

        [UnityTest]
        public IEnumerator LoadAsset_DisposeClearsDiagnostics()
        {
            var task = _service
                .LoadAsync<FrameworkProfile>(
                    new ResourceKey(AssetAddress))
                .AsTask();
            yield return WaitForTask(task);

            var lease = task.GetAwaiter().GetResult();
            Assert.That(
                lease.Asset.name,
                Is.EqualTo("ResourceAsset"));
            Assert.That(
                _service.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(1));

            lease.Dispose();

            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                _service.Diagnostics.InflightOperationCount,
                Is.Zero);
        }

        [UnityTest]
        public IEnumerator Instantiate_DisposeReleasesInstance()
        {
            var task = _service
                .InstantiateAsync(new ResourceKey(PrefabAddress))
                .AsTask();
            yield return WaitForTask(task);

            var lease = task.GetAwaiter().GetResult();
            var instance = lease.Instance;
            Assert.That(instance, Is.Not.Null);
            Assert.That(instance.name, Does.StartWith("ResourcePrefab"));

            lease.Dispose();
            yield return null;

            Assert.That(instance == null, Is.True);
            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                _service.Diagnostics.InflightOperationCount,
                Is.Zero);
        }

        [UnityTest]
        public IEnumerator MissingKey_PropagatesAndLeavesNoOwnership()
        {
            var task = _service
                .LoadAsync<FrameworkProfile>(
                    new ResourceKey(
                        "ark-framework-tests/definitely-missing"))
                .AsTask();
            yield return WaitForTask(task);

            Assert.That(task.IsFaulted, Is.True);
            var exception = Assert.Catch<Exception>(
                () => task.GetAwaiter().GetResult());
            Assert.That(
                exception.GetType().Name,
                Is.EqualTo("InvalidKeyException"));
            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                _service.Diagnostics.InflightOperationCount,
                Is.Zero);
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
                   elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(
                task.IsCompleted,
                Is.True,
                "Async operation timed out after " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") +
                " real seconds.");
        }

    }

}
#endif
