#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class AudioIntegrationTests :
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

        private static string DefaultFolderExistedKey =>
            PreferencePrefix + "DefaultFolderExisted";

        private static string DefaultObjectExistedKey =>
            PreferencePrefix + "DefaultObjectExisted";

        private static string ConfigRegisteredKey =>
            PreferencePrefix + "ConfigRegistered";

        private static string ConfigObjectPathKey =>
            PreferencePrefix + "ConfigObjectPath";

        private static string ConfigSettingsGuidKey =>
            PreferencePrefix + "ConfigSettingsGuid";

        private ResourceService _resources;
        private AudioService _service;
        private string _runId;

        private string AddressA => BuildAddress(_runId, "a");

        private string AddressB => BuildAddress(_runId, "b");

        void IPrebuildSetup.Setup()
        {
            if (EditorPrefs.GetBool(SetupActiveKey))
            {
                CleanupEditorAssets();
            }

            try
            {
                var runId = Guid.NewGuid().ToString("N");
                EditorPrefs.SetString(RunIdKey, runId);
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
                EditorPrefs.SetBool(
                    DefaultFolderExistedKey,
                    AssetDatabase.IsValidFolder(
                        AddressableAssetSettingsDefaultObject
                            .kDefaultConfigFolder));
                EditorPrefs.SetBool(
                    DefaultObjectExistedKey,
                    AssetDatabase.LoadAssetAtPath<Object>(
                        BuildDefaultObjectPath()) != null);
                AddressableAssetSettingsDefaultObject configObject;
                var configRegistered =
                    EditorBuildSettings.TryGetConfigObject(
                        AddressableAssetSettingsDefaultObject
                            .kDefaultConfigObjectName,
                        out configObject);
                var originalConfigObject = configObject ??
                    AssetDatabase.LoadAssetAtPath<
                        AddressableAssetSettingsDefaultObject>(
                        BuildDefaultObjectPath());
                EditorPrefs.SetBool(
                    ConfigRegisteredKey,
                    configRegistered);
                EditorPrefs.SetString(
                    ConfigObjectPathKey,
                    originalConfigObject == null
                        ? string.Empty
                        : AssetDatabase.GetAssetPath(
                            originalConfigObject));
                EditorPrefs.SetString(
                    ConfigSettingsGuidKey,
                    ReadSettingsGuid(originalConfigObject));
                EditorPrefs.SetBool(SetupActiveKey, true);

                var tempRoot = BuildTempRoot(runId);
                if (AssetDatabase.IsValidFolder(tempRoot))
                {
                    throw new InvalidOperationException(
                        "The unique Audio integration asset scope already exists.");
                }

                AssetDatabase.CreateFolder(
                    "Assets",
                    "ArkFrameworkAudioIntegrationTests_" + runId);
                var settings = previousSettings;
                if (settings == null)
                {
                    settings = AddressableAssetSettings.Create(
                        tempRoot + "/Addressables",
                        "AudioTestSettings",
                        true,
                        true);
                    EnsureDefaultConfigFolder();
                    if (!configRegistered &&
                        originalConfigObject != null)
                    {
                        EditorBuildSettings.AddConfigObject(
                            AddressableAssetSettingsDefaultObject
                                .kDefaultConfigObjectName,
                            originalConfigObject,
                            true);
                    }

                    AddressableAssetSettingsDefaultObject.Settings =
                        settings;
                }

                var fastModeIndex = settings.DataBuilders.FindIndex(
                    builder => builder is BuildScriptFastMode);
                if (fastModeIndex < 0)
                {
                    throw new InvalidOperationException(
                        "Addressables did not provide a FastMode builder.");
                }

                settings.ActivePlayModeDataBuilderIndex = fastModeIndex;
                var groupName = BuildGroupName(runId);
                if (settings.FindGroup(groupName) != null)
                {
                    throw new InvalidOperationException(
                        "The unique Audio integration asset scope already exists.");
                }

                CreateClipAsset(BuildClipPath(runId, "a"), 0.8f, 330f);
                CreateClipAsset(BuildClipPath(runId, "b"), 0.18f, 440f);

                var group = settings.CreateGroup(
                    groupName,
                    false,
                    false,
                    false,
                    null,
                    typeof(BundledAssetGroupSchema));
                CreateEntry(
                    settings,
                    group,
                    BuildClipPath(runId, "a"),
                    BuildAddress(runId, "a"));
                CreateEntry(
                    settings,
                    group,
                    BuildClipPath(runId, "b"),
                    BuildAddress(runId, "b"));
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
            Assert.That(_runId, Is.Not.Null.And.Not.Empty);
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
            _service = new AudioService(_resources);
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

            yield return null;
        }

        [UnityTest]
        public IEnumerator AddressableClipsOverlapCrossFadeAndReleaseAllOwnership()
        {
            var firstOneShot = _service.PlayAsync(
                new ResourceKey(AddressA),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            var secondOneShot = _service.PlayAsync(
                new ResourceKey(AddressA),
                new AudioPlayOptions(AudioChannel.SFX)).AsTask();
            yield return WaitForTask(firstOneShot, secondOneShot);
            var first = firstOneShot.GetAwaiter().GetResult();
            var second = secondOneShot.GetAwaiter().GetResult();
            Assert.That(first.InstanceId, Is.Not.EqualTo(second.InstanceId));
            Assert.That(first.IsValid, Is.True);
            Assert.That(second.IsValid, Is.True);
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.EqualTo(2));
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(2));
            yield return null;
            Assert.That(
                first.IsValid && second.IsValid,
                Is.True,
                "Committed playback must remain valid for at least one frame.");

            var stopFirst = _service.StopAsync(first).AsTask();
            yield return WaitForTask(stopFirst);
            Observe(stopFirst);
            Assert.That(first.IsValid, Is.False);
            yield return WaitUntil(() => !second.IsValid);
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.Zero);
            Assert.That(_resources.Diagnostics.OutstandingLeases, Is.Empty);

            var musicA = _service.PlayAsync(
                new ResourceKey(AddressA),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true,
                    volume: 0.8f)).AsTask();
            yield return WaitForTask(musicA);
            var a = musicA.GetAwaiter().GetResult();
            _service.SetChannelVolume(AudioChannel.Music, 0.5f);
            _service.SetChannelMuted(AudioChannel.Music, true);
            _service.SetChannelPaused(AudioChannel.Music, true);
            var activeMusicSource =
                Resources.FindObjectsOfTypeAll<AudioSource>()
                    .Single(source => source != null && source.clip != null);
            Assert.That(activeMusicSource.mute, Is.True);
            Assert.That(
                activeMusicSource.volume,
                Is.EqualTo(0.4f).Within(0.02f));
            yield return new WaitForSecondsRealtime(0.08f);
            Assert.That(a.IsValid, Is.True);
            Assert.That(
                _service.Diagnostics.Channels.Single(
                    item => item.Channel == AudioChannel.Music).Paused,
                Is.True);

            _service.SetChannelPaused(AudioChannel.Music, false);
            _service.SetChannelMuted(AudioChannel.Music, false);
            var musicB = _service.PlayAsync(
                new ResourceKey(AddressB),
                new AudioPlayOptions(
                    AudioChannel.Music,
                    loop: true,
                    fadeSeconds: 0.05f)).AsTask();
            yield return WaitForTask(musicB);
            var b = musicB.GetAwaiter().GetResult();
            Assert.That(a.IsValid, Is.True);
            Assert.That(b.IsValid, Is.True);
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(2));
            yield return WaitUntil(() => !a.IsValid);
            Assert.That(b.IsValid, Is.True);
            Assert.That(_resources.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));

            var serviceStop = _service.StopAsync().AsTask();
            yield return WaitForTask(serviceStop);
            Observe(serviceStop);
            Assert.That(_service.Diagnostics.OneShotPool.ActiveCount, Is.Zero);
            Assert.That(_service.Diagnostics.OneShotPool.IdleCount, Is.Zero);
            _service = null;
            yield return null;
            Assert.That(b.IsValid, Is.False);
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(FindAudioRoots(), Is.Empty);
        }

        private static void CreateClipAsset(
            string path,
            float seconds,
            float frequency)
        {
            var sampleRate = 22050;
            var samples = Mathf.CeilToInt(sampleRate * seconds);
            var clip = AudioClip.Create(
                "AudioIntegration",
                samples,
                1,
                sampleRate,
                false);
            var data = new float[samples];
            for (var index = 0; index < data.Length; index++)
            {
                data[index] =
                    Mathf.Sin(
                        2f * Mathf.PI * frequency * index / sampleRate) *
                    0.02f;
            }

            clip.SetData(data, 0);
            AssetDatabase.CreateAsset(clip, path);
        }

        private static void CreateEntry(
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

            var runId = EditorPrefs.GetString(RunIdKey);
            var settings =
                AddressableAssetSettingsDefaultObject.Settings;
            if (settings != null && !string.IsNullOrEmpty(runId))
            {
                var group = settings.FindGroup(BuildGroupName(runId));
                if (group != null)
                {
                    settings.RemoveGroup(group);
                }
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
                        EditorPrefs.GetInt(PreviousBuilderIndexKey);
                    AddressableAssetSettingsDefaultObject.Settings =
                        previousSettings;
                    EditorUtility.SetDirty(previousSettings);
                }
            }
            RestoreOriginalConfigState();
            if (!EditorPrefs.GetBool(DefaultObjectExistedKey))
            {
                DeleteOwnedAsset(BuildDefaultObjectPath());
            }

            if (!EditorPrefs.GetBool(DefaultFolderExistedKey) &&
                AssetDatabase.IsValidFolder(
                    AddressableAssetSettingsDefaultObject
                        .kDefaultConfigFolder) &&
                AssetDatabase.FindAssets(
                    string.Empty,
                    new[]
                    {
                        AddressableAssetSettingsDefaultObject
                            .kDefaultConfigFolder
                    }).Length == 0)
            {
                DeleteOwnedAsset(
                    AddressableAssetSettingsDefaultObject
                        .kDefaultConfigFolder);
            }

            if (!string.IsNullOrEmpty(runId))
            {
                var ownedRoot = BuildTempRoot(runId);
                DeleteOwnedAsset(ownedRoot);
            }

            EditorPrefs.DeleteKey(SetupActiveKey);
            EditorPrefs.DeleteKey(RunIdKey);
            EditorPrefs.DeleteKey(SettingsExistedKey);
            EditorPrefs.DeleteKey(PreviousSettingsPathKey);
            EditorPrefs.DeleteKey(PreviousBuilderIndexKey);
            EditorPrefs.DeleteKey(DefaultFolderExistedKey);
            EditorPrefs.DeleteKey(DefaultObjectExistedKey);
            EditorPrefs.DeleteKey(ConfigRegisteredKey);
            EditorPrefs.DeleteKey(ConfigObjectPathKey);
            EditorPrefs.DeleteKey(ConfigSettingsGuidKey);
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
                return "ArkFramework.AudioIntegrationTests." +
                       BitConverter.ToString(hash, 0, 8)
                           .Replace("-", string.Empty) +
                       ".";
            }
        }

        private static void EnsureDefaultConfigFolder()
        {
            var folder =
                AddressableAssetSettingsDefaultObject.kDefaultConfigFolder;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder(
                    "Assets",
                    "AddressableAssetsData");
            }
        }

        private static string ReadSettingsGuid(
            AddressableAssetSettingsDefaultObject configObject)
        {
            if (configObject == null)
            {
                return string.Empty;
            }

            var serialized = new SerializedObject(configObject);
            return serialized.FindProperty(
                    "m_AddressableAssetSettingsGuid")
                .stringValue;
        }

        private static void RestoreOriginalConfigState()
        {
            var path = EditorPrefs.GetString(ConfigObjectPathKey);
            var configObject = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<
                    AddressableAssetSettingsDefaultObject>(path);
            if (configObject != null)
            {
                var serialized = new SerializedObject(configObject);
                serialized.FindProperty(
                        "m_AddressableAssetSettingsGuid")
                    .stringValue =
                    EditorPrefs.GetString(ConfigSettingsGuidKey);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(configObject);
            }

            EditorBuildSettings.RemoveConfigObject(
                AddressableAssetSettingsDefaultObject
                    .kDefaultConfigObjectName);
            if (EditorPrefs.GetBool(ConfigRegisteredKey))
            {
                if (configObject == null)
                {
                    throw new InvalidOperationException(
                        "The original Addressables config object could not be restored.");
                }

                EditorBuildSettings.AddConfigObject(
                    AddressableAssetSettingsDefaultObject
                        .kDefaultConfigObjectName,
                    configObject,
                    true);
            }
        }

        private static string BuildDefaultObjectPath()
        {
            return AddressableAssetSettingsDefaultObject
                       .kDefaultConfigFolder +
                   "/DefaultObject.asset";
        }

        private static void DeleteOwnedAsset(string path)
        {
            if ((AssetDatabase.IsValidFolder(path) ||
                 AssetDatabase.LoadAssetAtPath<Object>(path) != null) &&
                !AssetDatabase.DeleteAsset(path))
            {
                throw new InvalidOperationException(
                    "Failed to delete owned Audio integration asset '" +
                    path + "'.");
            }
        }

        private static string BuildTempRoot(string runId)
        {
            return "Assets/ArkFrameworkAudioIntegrationTests_" + runId;
        }

        private static string BuildClipPath(string runId, string suffix)
        {
            return BuildTempRoot(runId) + "/clip-" + suffix + ".asset";
        }

        private static string BuildAddress(string runId, string suffix)
        {
            return "ark-framework-tests/audio-" + suffix + "-" + runId;
        }

        private static string BuildGroupName(string runId)
        {
            return "ArkFramework Audio PlayMode Tests " + runId;
        }

        private static GameObject[] FindAudioRoots()
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(
                    item =>
                        item != null &&
                        item.name == "[ArkFramework.Audio]" &&
                        (item.scene.IsValid() ||
                         (item.hideFlags & HideFlags.HideAndDontSave) != 0))
                .ToArray();
        }

        private static IEnumerator WaitForTask(params Task[] tasks)
        {
            var elapsed = Stopwatch.StartNew();
            while (tasks.Any(task => !task.IsCompleted) &&
                   elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(
                tasks.All(task => task.IsCompleted),
                Is.True,
                "Audio integration task timed out after " +
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
                "Audio integration state timed out after " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") +
                " real seconds.");
        }

        private static void Observe(Task task)
        {
            if (task.IsFaulted)
            {
                task.GetAwaiter().GetResult();
            }
        }
    }
}
#endif
