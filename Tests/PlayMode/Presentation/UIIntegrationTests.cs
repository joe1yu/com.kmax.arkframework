#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class UIIntegrationTests :
        IPrebuildSetup,
        IPostBuildCleanup
    {
        private static readonly string PreferencePrefix =
            BuildPreferencePrefix();

        private static string SetupActiveKey =>
            PreferencePrefix + "SetupActive";

        private static string RunIdKey =>
            PreferencePrefix + "RunId";

        private static string SettingsExistedKey =>
            PreferencePrefix + "SettingsExisted";

        private static string PreviousSettingsPathKey =>
            PreferencePrefix + "PreviousSettingsPath";

        private static string PreviousBuilderIndexKey =>
            PreferencePrefix + "PreviousBuilderIndex";

        private ResourceService _resources;
        private GameObjectPool _pool;
        private UIService _service;
        private UIRoot _root;
        private GameObject _eventSystemObject;
        private string _runId;

        private string PrefabAddress =>
            BuildPrefabAddress(_runId);

        private string CachePrefabAddress =>
            BuildCachePrefabAddress(_runId);

        void IPrebuildSetup.Setup()
        {
            if (EditorPrefs.GetBool(SetupActiveKey))
            {
                CleanupEditorAssets();
            }

            try
            {
                var runId = Guid.NewGuid().ToString("N");
                EditorPrefs.SetString(
                    RunIdKey,
                    runId);
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
                var tempRoot = BuildTempRoot(runId);
                var groupName = BuildGroupName(runId);
                if (AssetDatabase.IsValidFolder(tempRoot) ||
                    settings.FindGroup(groupName) != null)
                {
                    throw new InvalidOperationException(
                        "The unique UI integration test asset scope already exists.");
                }

                AssetDatabase.CreateFolder(
                    "Assets",
                    "ArkFrameworkUIIntegrationTests_" + runId);

                var source = new GameObject(
                    "UIIntegrationWindow",
                    typeof(RectTransform),
                    typeof(UIIntegrationWindow));
                try
                {
                    var buttonObject = new GameObject(
                        "ActionButton",
                        typeof(RectTransform),
                        typeof(CanvasRenderer),
                        typeof(Image),
                        typeof(Button));
                    buttonObject.transform.SetParent(source.transform, false);
                    var rect =
                        buttonObject.GetComponent<RectTransform>();
                    rect.anchorMin = new Vector2(0.4f, 0.4f);
                    rect.anchorMax = new Vector2(0.6f, 0.6f);
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    PrefabUtility.SaveAsPrefabAsset(
                        source,
                        BuildPrefabPath(runId));
                }
                finally
                {
                    Object.DestroyImmediate(source);
                }

                var cacheSource = new GameObject(
                    "UICacheIntegrationWindow",
                    typeof(RectTransform),
                    typeof(UICacheIntegrationWindow));
                try
                {
                    PrefabUtility.SaveAsPrefabAsset(
                        cacheSource,
                        BuildCachePrefabPath(runId));
                }
                finally
                {
                    Object.DestroyImmediate(cacheSource);
                }

                var group = settings.CreateGroup(
                    groupName,
                    false,
                    false,
                    false,
                    null,
                    typeof(BundledAssetGroupSchema));
                var entry = settings.CreateOrMoveEntry(
                    AssetDatabase.AssetPathToGUID(
                        BuildPrefabPath(runId)),
                    group,
                    false,
                    false);
                entry.SetAddress(
                    BuildPrefabAddress(runId),
                    false);
                var cacheEntry = settings.CreateOrMoveEntry(
                    AssetDatabase.AssetPathToGUID(
                        BuildCachePrefabPath(runId)),
                    group,
                    false,
                    false);
                cacheEntry.SetAddress(
                    BuildCachePrefabAddress(runId),
                    false);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(
                    ImportAssetOptions.ForceSynchronousImport);

                var buildResult = settings.ActivePlayModeDataBuilder
                    .BuildData<AddressablesPlayModeBuildResult>(
                        new AddressablesDataBuilderInput(settings));
                if (!string.IsNullOrEmpty(buildResult.Error))
                {
                    throw new InvalidOperationException(buildResult.Error);
                }
            }
            catch
            {
                CleanupEditorAssets();
                throw;
            }
        }

        void IPostBuildCleanup.Cleanup()
        {
            CleanupEditorAssets();
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _runId = EditorPrefs.GetString(RunIdKey);
            Assert.That(
                _runId,
                Is.Not.Null.And.Not.Empty,
                "The project-scoped Addressables test run ID is missing.");
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
            _pool = new GameObjectPool(_resources);
            _eventSystemObject = new GameObject(
                "UIIntegrationTests.EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            _root = UIRoot.Create();
            _service = new UIService(
                _resources,
                _pool,
                new EmptyEventBus(),
                _root);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_service != null)
            {
                var stop = _service.StopAsync().AsTask();
                yield return WaitForTask(stop);
                Observe(stop);
                _service = null;
            }

            _pool?.Dispose();
            _pool = null;
            if (_resources != null)
            {
                var stop = _resources.StopAsync().AsTask();
                yield return WaitForTask(stop);
                Observe(stop);
                var dispose = _resources.DisposeAsync().AsTask();
                yield return WaitForTask(dispose);
                Observe(dispose);
                _resources = null;
            }

            if (_eventSystemObject != null)
            {
                Object.Destroy(_eventSystemObject);
                _eventSystemObject = null;
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator AddressableWindowStackOwnsInputMaskCacheAndDestroy()
        {
            _service.Register<UIIntegrationWindow>(
                new UIWindowDescriptor(
                    "normal",
                    new ResourceKey(PrefabAddress),
                    UILayer.Normal,
                    UIWindowMode.SingleInstance,
                    cacheOnClose: false,
                    requiresMask: false,
                    closeOnMaskClick: false,
                    blocksInput: false));
            _service.Register<UICacheIntegrationWindow>(
                new UIWindowDescriptor(
                    "popup",
                    new ResourceKey(CachePrefabAddress),
                    UILayer.Popup,
                    UIWindowMode.SingleInstance,
                    cacheOnClose: true,
                    requiresMask: true,
                    closeOnMaskClick: true,
                    blocksInput: true));

            var normalOpen =
                _service.OpenAsync<UIIntegrationWindow>().AsTask();
            yield return WaitForTask(normalOpen);
            var normal = normalOpen.GetAwaiter().GetResult();
            var normalWindow =
                (UIIntegrationWindow)_service.GetWindow(normal);
            Assert.That(
                normalWindow.transform.parent,
                Is.SameAs(_root.GetLayerRoot(UILayer.Normal)));
            Assert.That(_root.Layers.Count, Is.EqualTo(5));
            Assert.That(
                _root.Layers[4].SortingOrder,
                Is.GreaterThan(_root.Layers[3].SortingOrder));
            Assert.That(EventSystem.current, Is.Not.Null);

            var clickCount = 0;
            normalWindow.ActionButton.onClick.AddListener(
                () => clickCount++);
            Canvas.ForceUpdateCanvases();
            RaycastAndClick(
                RectTransformUtility.WorldToScreenPoint(
                    null,
                    normalWindow.ActionButton.transform.position),
                normalWindow.ActionButton.gameObject);
            Assert.That(clickCount, Is.EqualTo(1));

            var destroyObject = normalWindow.gameObject;
            var normalClose = _service.CloseAsync(normal).AsTask();
            yield return WaitForTask(normalClose);
            Observe(normalClose);
            yield return null;
            Assert.That(destroyObject == null, Is.True);
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);

            var popupOpen =
                _service.OpenAsync<UICacheIntegrationWindow>().AsTask();
            yield return WaitForTask(popupOpen);
            var popup = popupOpen.GetAwaiter().GetResult();
            var popupWindow = _service.GetWindow(popup);
            var cachedObject = popupWindow.gameObject;
            Assert.That(_root.Mask.gameObject.activeSelf, Is.True);
            Assert.That(
                _root.Mask.transform.GetSiblingIndex(),
                Is.EqualTo(popupWindow.transform.GetSiblingIndex() - 1));

            Canvas.ForceUpdateCanvases();
            RaycastAndClick(
                RectTransformUtility.WorldToScreenPoint(
                    null,
                    _root.Mask.transform.position),
                _root.Mask.gameObject);
            yield return WaitUntil(() => !popup.IsValid);
            Assert.That(cachedObject, Is.Not.Null);
            Assert.That(cachedObject.activeSelf, Is.False);
            Assert.That(
                _resources.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(1));

            var popupReopen =
                _service.OpenAsync<UICacheIntegrationWindow>().AsTask();
            yield return WaitForTask(popupReopen);
            var reopened = popupReopen.GetAwaiter().GetResult();
            Assert.That(
                _service.GetWindow(reopened).gameObject,
                Is.SameAs(cachedObject));
            Assert.That(cachedObject.activeSelf, Is.True);

            var popupClose = _service.CloseAsync(reopened).AsTask();
            yield return WaitForTask(popupClose);
            Observe(popupClose);
            var serviceStop = _service.StopAsync().AsTask();
            yield return WaitForTask(serviceStop);
            Observe(serviceStop);
            _service = null;
            Assert.That(_root == null, Is.True);
            _root = UIRoot.Create();
            yield return null;
            Assert.That(EventSystem.current, Is.Not.Null);
            Assert.That(
                Object.FindObjectsOfType<EventSystem>().Length,
                Is.EqualTo(1));
            var replacementRootDispose = _root.DisposeAsync().AsTask();
            yield return WaitForTask(replacementRootDispose);
            Observe(replacementRootDispose);
            _root = null;
            _pool.Dispose();
            _pool = null;
            yield return null;
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        private static void CleanupEditorAssets()
        {
            if (!EditorPrefs.GetBool(SetupActiveKey))
            {
                return;
            }

            var settings =
                AddressableAssetSettingsDefaultObject.Settings;
            var runId = EditorPrefs.GetString(RunIdKey);
            if (settings != null)
            {
                var group = string.IsNullOrEmpty(runId)
                    ? null
                    : settings.FindGroup(BuildGroupName(runId));
                if (group != null)
                {
                    settings.RemoveGroup(group);
                }
            }

            if (!string.IsNullOrEmpty(runId))
            {
                AssetDatabase.DeleteAsset(BuildTempRoot(runId));
            }
            if (EditorPrefs.GetBool(SettingsExistedKey))
            {
                var previousSettings =
                    AssetDatabase.LoadAssetAtPath<
                        AddressableAssetSettings>(
                        EditorPrefs.GetString(
                            PreviousSettingsPathKey));
                if (previousSettings != null)
                {
                    previousSettings.ActivePlayModeDataBuilderIndex =
                        EditorPrefs.GetInt(
                            PreviousBuilderIndexKey);
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
            EditorPrefs.DeleteKey(RunIdKey);
            EditorPrefs.DeleteKey(SettingsExistedKey);
            EditorPrefs.DeleteKey(PreviousSettingsPathKey);
            EditorPrefs.DeleteKey(PreviousBuilderIndexKey);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(
                ImportAssetOptions.ForceSynchronousImport);
        }

        private static string BuildPreferencePrefix()
        {
            var normalizedProjectPath = Application.dataPath
                .Replace('\\', '/')
                .TrimEnd('/')
                .ToUpperInvariant();
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(
                    Encoding.UTF8.GetBytes(normalizedProjectPath));
                return "ArkFramework.UIIntegrationTests." +
                       BitConverter.ToString(hash, 0, 8)
                           .Replace("-", string.Empty) +
                       ".";
            }
        }

        private static string BuildTempRoot(string runId)
        {
            return "Assets/ArkFrameworkUIIntegrationTests_" + runId;
        }

        private static string BuildPrefabPath(string runId)
        {
            return BuildTempRoot(runId) + "/IntegrationWindow.prefab";
        }

        private static string BuildCachePrefabPath(string runId)
        {
            return BuildTempRoot(runId) +
                   "/CacheIntegrationWindow.prefab";
        }

        private static string BuildPrefabAddress(string runId)
        {
            return "ark-framework-tests/ui-window-" + runId;
        }

        private static string BuildCachePrefabAddress(string runId)
        {
            return "ark-framework-tests/ui-cache-window-" + runId;
        }

        private static string BuildGroupName(string runId)
        {
            return "ArkFramework UI PlayMode Tests " + runId;
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
                "UI integration task timed out after " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") +
                " real seconds.");
        }

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() &&
                   elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(
                condition(),
                Is.True,
                "UI integration state timed out after " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") +
                " real seconds.");
        }

        private static void RaycastAndClick(
            Vector2 screenPosition,
            GameObject expectedTarget)
        {
            var eventSystem = EventSystem.current;
            Assert.That(eventSystem, Is.Not.Null);
            var pointer = new PointerEventData(eventSystem)
            {
                position = screenPosition,
                button = PointerEventData.InputButton.Left
            };
            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointer, results);
            var hit = results.FirstOrDefault(
                result => result.gameObject == expectedTarget);
            Assert.That(
                hit.gameObject,
                Is.SameAs(expectedTarget),
                "The real EventSystem/GraphicRaycaster path did not hit the expected UI target.");
            Assert.That(hit.module, Is.TypeOf<GraphicRaycaster>());
            ExecuteEvents.ExecuteHierarchy(
                hit.gameObject,
                pointer,
                ExecuteEvents.pointerClickHandler);
        }

        private static void Observe(Task task)
        {
            if (task.IsFaulted)
            {
                task.GetAwaiter().GetResult();
            }
        }

        private sealed class EmptyEventBus : IEventBus
        {
            public EventBusDiagnostics Diagnostics => null;

            public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
            {
                return new EmptyDisposable();
            }

            public IDisposable Subscribe<TEvent>(
                ModuleScope ownerScope,
                Action<TEvent> handler)
            {
                return ownerScope.Own(Subscribe(handler));
            }

            public void Publish<TEvent>(TEvent value)
            {
            }

            public void Enqueue<TEvent>(TEvent value)
            {
            }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

    }
}
#endif
