using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArkFramework.Samples;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace ArkFramework.Editor.Tests
{
    public sealed class AddressablesSampleBuilderTests
    {
        private static readonly string GeneratedRoot =
            SampleAssetPaths.GeneratedRoot;
        private static readonly string GameplayConfigPath =
            GeneratedRoot + "/Config/GameplayConfig.asset";
        private static readonly string ManifestPath =
            GeneratedRoot + "/Config/manifest.json";
        private static readonly string MainMenuPrefabPath =
            GeneratedRoot + "/Prefabs/MainMenuWindow.prefab";
        private static readonly string GameplayHudPrefabPath =
            GeneratedRoot + "/Prefabs/GameplayHudWindow.prefab";
        private static readonly string LoadingPrefabPath =
            GeneratedRoot + "/Prefabs/LoadingWindow.prefab";
        private static readonly string PlatformPrefabPath =
            GeneratedRoot + "/Prefabs/PlatformRoot.prefab";
        private const string UITablePath =
            "Assets/StreamingAssets/ArkFrameworkSample/UI.csv";
        private const string SceneTablePath =
            "Assets/StreamingAssets/ArkFrameworkSample/Scenes.csv";

        private static readonly string[] ModuleIds =
        {
            "EventBus",
            "Platform",
            "Rig",
            "Resource",
            "Pool",
            "Config",
            "Table",
            "FSM",
            "Scene",
            "UI",
            "Audio",
            "ActionKit",
            "Procedure"
        };

        private static readonly Type[] InstallerTypes =
        {
            typeof(EventBusModuleInstaller),
            typeof(PlatformModuleInstaller),
            typeof(RigModuleInstaller),
            typeof(ResourceModuleInstaller),
            typeof(PoolModuleInstaller),
            typeof(ConfigModuleInstaller),
            typeof(TableModuleInstaller),
            typeof(FsmModuleInstaller),
            typeof(SceneModuleInstaller),
            typeof(UIModuleInstaller),
            typeof(AudioModuleInstaller),
            typeof(ActionKitModuleInstaller),
            typeof(SampleModuleInstaller)
        };

        private static readonly string[][] ModuleDependencies =
        {
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[]
            {
                BuiltInModuleIds.Platform,
                BuiltInModuleIds.EventBus
            },
            Array.Empty<string>(),
            new[] { BuiltInModuleIds.Resource },
            new[]
            {
                BuiltInModuleIds.Resource,
                BuiltInModuleIds.EventBus
            },
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[]
            {
                BuiltInModuleIds.Resource,
                BuiltInModuleIds.EventBus,
                BuiltInModuleIds.Table
            },
            new[]
            {
                BuiltInModuleIds.Resource,
                BuiltInModuleIds.Pool,
                BuiltInModuleIds.EventBus
            },
            new[]
            {
                BuiltInModuleIds.Resource,
                BuiltInModuleIds.Pool
            },
            Array.Empty<string>(),
            new[]
            {
                BuiltInModuleIds.Fsm,
                BuiltInModuleIds.Config,
                BuiltInModuleIds.Table,
                BuiltInModuleIds.Scene,
                BuiltInModuleIds.Rig,
                BuiltInModuleIds.UI,
                BuiltInModuleIds.Audio,
                BuiltInModuleIds.ActionKit
            }
        };

        private static readonly string[] ExpectedAddresses =
        {
            SampleContent.GameplayScriptableAddress,
            SampleContent.ConfigManifestAddress,
            SampleContent.GameplayJsonAddress,
            SampleContent.MainMenuSceneAddress,
            SampleContent.GameplaySceneAddress,
            "sample/ui/main-menu",
            "sample/ui/gameplay-hud",
            "sample/ui/loading",
            SampleContent.MenuMusicAddress,
            SampleContent.GameplayMusicAddress
        };

        private static readonly string[] GeneratedAssetPaths =
        {
            GeneratedRoot + "/Audio/MenuMusic.wav",
            GeneratedRoot + "/Audio/GameplayMusic.wav",
            GameplayConfigPath,
            GeneratedRoot + "/Config/gameplay.json",
            ManifestPath,
            GeneratedRoot + "/Installers/EventBusInstaller.asset",
            GeneratedRoot + "/Installers/PlatformInstaller.asset",
            GeneratedRoot + "/Installers/RigInstaller.asset",
            GeneratedRoot + "/Installers/ResourceInstaller.asset",
            GeneratedRoot + "/Installers/PoolInstaller.asset",
            GeneratedRoot + "/Installers/ConfigInstaller.asset",
            GeneratedRoot + "/Installers/TableInstaller.asset",
            GeneratedRoot + "/Installers/FSMInstaller.asset",
            GeneratedRoot + "/Installers/SceneInstaller.asset",
            GeneratedRoot + "/Installers/UIInstaller.asset",
            GeneratedRoot + "/Installers/AudioInstaller.asset",
            GeneratedRoot + "/Installers/ActionKitInstaller.asset",
            GeneratedRoot + "/Installers/ProcedureInstaller.asset",
            SampleAssetPaths.ProfilePath,
            MainMenuPrefabPath,
            GameplayHudPrefabPath,
            LoadingPrefabPath,
            PlatformPrefabPath,
            SampleAssetPaths.BootstrapScenePath,
            SampleAssetPaths.MainMenuScenePath,
            SampleAssetPaths.GameplayScenePath,
            UITablePath,
            SceneTablePath
        };

        private Snapshot _first;
        private Snapshot _second;

        [OneTimeSetUp]
        public void RebuildTwice()
        {
            AddressablesSampleBuilder.RebuildSampleContent();
            _first = Snapshot.Capture(GeneratedAssetPaths);
            AddressablesSampleBuilder.RebuildSampleContent();
            _second = Snapshot.Capture(GeneratedAssetPaths);
        }

        [Test]
        public void RebuildCreatesEveryRequiredGeneratedAsset()
        {
            foreach (var path in GeneratedAssetPaths)
            {
                Assert.That(
                    AssetDatabase.AssetPathToGUID(path),
                    Is.Not.Null.And.Not.Empty,
                    "Missing generated asset: " + path);
                Assert.That(
                    AssetDatabase.LoadMainAssetAtPath(path),
                    Is.Not.Null,
                    "Generated asset could not be loaded: " + path);
            }
        }

        [Test]
        public void ProfileHasThirteenOrderedModulesAndValidatesCleanly()
        {
            var profile =
                AssetDatabase.LoadAssetAtPath<FrameworkProfile>(
                    SampleAssetPaths.ProfilePath);

            Assert.That(profile, Is.Not.Null);
            Assert.That(
                profile.Installers.Select(installer => installer.ModuleId),
                Is.EqualTo(ModuleIds));
            Assert.That(
                profile.Installers.Select(installer => installer.GetType()),
                Is.EqualTo(InstallerTypes));
            for (var index = 0; index < profile.Installers.Count; index++)
            {
                CollectionAssert.AreEqual(
                    ModuleDependencies[index],
                    profile.Installers[index].Dependencies,
                    profile.Installers[index].name);
            }

            var validation = FrameworkEditorValidation.Validate(profile);
            Assert.That(
                validation.IsValid,
                Is.True,
                string.Join(
                    " | ",
                    validation.Issues.Select(
                        issue => issue.Code + ": " + issue.Message)));
        }

        [Test]
        public void ExactAddressesResolveOnceInsideTheSingleSampleGroup()
        {
            var settings =
                AddressableAssetSettingsDefaultObject.Settings;
            Assert.That(settings, Is.Not.Null);
            var sampleGroups = settings.groups
                .Where(
                    group =>
                        group != null &&
                        group.Name ==
                        SampleContent.AddressablesGroupName)
                .ToArray();
            Assert.That(sampleGroups, Has.Length.EqualTo(1));
            var allEntries = settings.groups
                .Where(group => group != null)
                .SelectMany(group => group.entries)
                .ToArray();

            foreach (var address in ExpectedAddresses)
            {
                var matches = allEntries
                    .Where(entry => entry.address == address)
                    .ToArray();
                Assert.That(
                    matches,
                    Has.Length.EqualTo(1),
                    "Address must resolve exactly once: " + address);
                Assert.That(
                    matches[0].parentGroup,
                    Is.SameAs(sampleGroups[0]));
            }

            Assert.That(
                sampleGroups[0].entries,
                Has.Count.EqualTo(ExpectedAddresses.Length));
        }

        [Test]
        public void ConfigLabelManifestAndPayloadMatchTheContract()
        {
            var settings =
                AddressableAssetSettingsDefaultObject.Settings;
            var configGuid =
                AssetDatabase.AssetPathToGUID(GameplayConfigPath);
            var configEntry = settings.FindAssetEntry(configGuid);
            Assert.That(configEntry, Is.Not.Null);
            Assert.That(
                configEntry.labels,
                Does.Contain(SampleContent.ScriptableConfigLabel));

            var config =
                AssetDatabase.LoadAssetAtPath<GameplayConfigAsset>(
                    GameplayConfigPath);
            Assert.That(config.Key, Is.EqualTo("gameplay"));
            Assert.That(config.Version, Is.EqualTo("1"));
            Assert.That(config.Payload.StartingLives, Is.EqualTo(3));
            Assert.That(config.Payload.MoveSpeed, Is.EqualTo(4f));
            Assert.That(
                config.Payload.WelcomeMessage,
                Is.EqualTo("Scriptable default"));

            var manifest =
                AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            Assert.That(
                manifest.text,
                Does.Contain(typeof(GameplayConfig).AssemblyQualifiedName));
            Assert.That(
                manifest.text,
                Does.Contain(
                    "\"address\": \"" +
                    SampleContent.GameplayJsonAddress + "\""));
            Assert.That(manifest.text, Does.Contain("\"version\": \"2\""));
        }

        [Test]
        public void PlatformPrefabDefinesNestedUIRootsAndUserEventSystem()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                PlatformPrefabPath);
            var installer =
                AssetDatabase.LoadAssetAtPath<PlatformModuleInstaller>(
                    GeneratedRoot +
                    "/Installers/PlatformInstaller.asset");

            Assert.That(prefab, Is.Not.Null);
            Assert.That(
                prefab.GetComponentsInChildren<Canvas>(true),
                Has.Length.EqualTo(1));
            Assert.That(
                prefab.GetComponentsInChildren<EventSystem>(true),
                Has.Length.EqualTo(1));
            var roots = prefab.GetComponentsInChildren<PlatformUIRoot>(true);
            Assert.That(roots, Has.Length.EqualTo(5));
            Assert.That(
                roots.Select(root => root.Id),
                Is.EquivalentTo(Enum.GetNames(typeof(UILayer))));
            Assert.That(
                roots.All(root => root.transform.parent.parent != prefab.transform),
                Is.True,
                "示例 UI 根节点应验证平台服务支持深层子物体。");
            Assert.That(installer, Is.Not.Null);
            Assert.That(installer.PlatformPrefab, Is.SameAs(prefab));
            Assert.That(installer.DontDestroyOnLoad, Is.True);
            var rigs = prefab.GetComponentsInChildren<CameraRig>(true);
            Assert.That(rigs, Has.Length.EqualTo(1));
            Assert.That(rigs[0].Id, Is.EqualTo(SampleContent.MainRigId));
            Assert.That(rigs[0].ActiveByDefault, Is.True);
            var slots = rigs[0].GetComponentsInChildren<RigCameraSlot>(true);
            Assert.That(slots, Has.Length.EqualTo(1));
            Assert.That(
                slots[0].Id,
                Is.EqualTo(SampleContent.MainCameraSlotId));
        }

        [Test]
        public void UITableContainsThreeIdAddressedWindows()
        {
            Assert.That(File.Exists(UITablePath), Is.True);
            var document = CsvTableDocument.Parse(
                File.ReadAllText(UITablePath),
                UITablePath);

            Assert.That(
                document.Schema.TargetTypeName,
                Is.EqualTo(typeof(SampleUIRow).FullName));
            Assert.That(document.Schema.KeyColumnName, Is.EqualTo("Id"));
            Assert.That(document.Rows, Has.Count.EqualTo(3));
            Assert.That(
                document.Rows.Select(row => row.Cells[0]),
                Is.EqualTo(
                    new[]
                    {
                        SampleContent.MainMenuWindowId,
                        SampleContent.GameplayHudWindowId,
                        SampleContent.LoadingWindowId
                    }));
            Assert.That(
                document.Rows.Select(row => row.Cells[2]),
                Is.EqualTo(
                    new[]
                    {
                        "sample/ui/main-menu",
                        "sample/ui/gameplay-hud",
                        "sample/ui/loading"
                    }));
            Assert.That(
                document.Rows.Select(row => row.Cells[3]),
                Is.EqualTo(new[] { "Normal", "Normal", "System" }));
        }

        [Test]
        public void SceneTableContainsTwoIdAddressedScenesAndRigPolicies()
        {
            Assert.That(File.Exists(SceneTablePath), Is.True);
            var document = CsvTableDocument.Parse(
                File.ReadAllText(SceneTablePath),
                SceneTablePath);

            Assert.That(
                document.Schema.TargetTypeName,
                Is.EqualTo(typeof(SceneTableRow).FullName));
            Assert.That(document.Schema.KeyColumnName, Is.EqualTo("Id"));
            Assert.That(document.Rows, Has.Count.EqualTo(2));
            Assert.That(
                document.Rows.Select(row => row.Cells[0]),
                Is.EqualTo(
                    new[]
                    {
                        SampleContent.MainMenuSceneId,
                        SampleContent.GameplaySceneId
                    }));
            Assert.That(
                document.Rows.Select(row => row.Cells[4]),
                Is.All.EqualTo(SampleContent.MainRigId));
            Assert.That(
                document.Rows.Select(row => row.Cells[5]),
                Is.All.EqualTo("true"));
            Assert.That(
                document.Rows.Select(row => row.Cells[9]),
                Is.All.EqualTo("true"));

            var installer =
                AssetDatabase.LoadAssetAtPath<SceneModuleInstaller>(
                    GeneratedRoot +
                    "/Installers/SceneInstaller.asset");
            Assert.That(installer.SceneTablePath, Is.EqualTo(
                SampleContent.SceneTablePath));
        }

        [Test]
        public void GeneratedAudioClipsContainPersistedSamples()
        {
            AssertPersistedAudioClip(
                GeneratedRoot + "/Audio/MenuMusic.wav");
            AssertPersistedAudioClip(
                GeneratedRoot + "/Audio/GameplayMusic.wav");
        }

        [Test]
        public void SecondRebuildPreservesGuidsAndCounts()
        {
            Assert.That(
                _second.Guids,
                Is.EqualTo(_first.Guids),
                "Generated GUIDs changed across rebuilds.");
            Assert.That(
                _second.SampleGroupCount,
                Is.EqualTo(_first.SampleGroupCount).And.EqualTo(1));
            Assert.That(
                _second.SampleGroupEntryCount,
                Is.EqualTo(_first.SampleGroupEntryCount)
                    .And.EqualTo(ExpectedAddresses.Length));
            Assert.That(
                _second.ProfileInstallerCount,
                Is.EqualTo(_first.ProfileInstallerCount).And.EqualTo(13));
            Assert.That(
                _second.SampleBuildSceneCount,
                Is.EqualTo(_first.SampleBuildSceneCount).And.EqualTo(1));
        }

        [Test]
        public void BootstrapContainsOneConfiguredHostAndOverlay()
        {
            var scene = EditorSceneManager.OpenScene(
                SampleAssetPaths.BootstrapScenePath,
                OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var hosts = roots
                    .SelectMany(
                        root =>
                            root.GetComponentsInChildren<
                                FrameworkHost>(true))
                    .ToArray();
                var overlays = roots
                    .SelectMany(
                        root =>
                            root.GetComponentsInChildren<
                                RuntimeDebugOverlay>(true))
                    .ToArray();
                Assert.That(hosts, Has.Length.EqualTo(1));
                Assert.That(overlays, Has.Length.EqualTo(1));
                var serialized = new SerializedObject(hosts[0]);
                Assert.That(
                    serialized.FindProperty("_profile")
                        .objectReferenceValue,
                    Is.EqualTo(
                        AssetDatabase.LoadAssetAtPath<FrameworkProfile>(
                            SampleAssetPaths.ProfilePath)));
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void PrefabsContainWindowTypesAndSerializedButtons()
        {
            var menu =
                AssetDatabase.LoadAssetAtPath<GameObject>(
                    MainMenuPrefabPath);
            var hud =
                AssetDatabase.LoadAssetAtPath<GameObject>(
                    GameplayHudPrefabPath);
            var loading =
                AssetDatabase.LoadAssetAtPath<GameObject>(
                    LoadingPrefabPath);

            Assert.That(
                menu.GetComponent<MainMenuWindow>(),
                Is.Not.Null);
            Assert.That(
                menu.GetComponent<MainMenuWindow>().PlayButton,
                Is.Not.Null);
            Assert.That(
                hud.GetComponent<GameplayHudWindow>(),
                Is.Not.Null);
            Assert.That(
                hud.GetComponent<GameplayHudWindow>().BackButton,
                Is.Not.Null);
            Assert.That(
                loading.GetComponent<LoadingWindow>(),
                Is.Not.Null);
        }

        [Test]
        public void BuildScenesContainOnlyBootstrapScene()
        {
            var paths = EditorBuildSettings.scenes
                .Select(scene => scene.path)
                .ToArray();
            Assert.That(
                paths.Count(
                    path => path == SampleAssetPaths.BootstrapScenePath),
                Is.EqualTo(1));
            Assert.That(
                paths.Last(),
                Is.EqualTo(SampleAssetPaths.BootstrapScenePath));
            Assert.That(
                paths,
                Does.Not.Contain(SampleAssetPaths.MainMenuScenePath));
            Assert.That(
                paths,
                Does.Not.Contain(SampleAssetPaths.GameplayScenePath));
            Assert.That(
                paths,
                Has.None.StartsWith(
                    "Assets/ArkFramework/Samples/Generated/Scenes/"));
        }

        [Test]
        public void RebuildPreservesTheOpenUserScene()
        {
            var scene = SceneManager.GetActiveScene();
            var marker = new GameObject("User Scene Marker");
            try
            {
                AddressablesSampleBuilder.RebuildSampleContent();

                Assert.That(scene.IsValid(), Is.True);
                Assert.That(scene.isLoaded, Is.True);
                Assert.That(
                    SceneManager.GetActiveScene(),
                    Is.EqualTo(scene));
                Assert.That(
                    scene.GetRootGameObjects()
                        .Any(root => root.name == "User Scene Marker"),
                    Is.True);
            }
            finally
            {
                if (marker != null)
                {
                    UnityEngine.Object.DestroyImmediate(marker);
                }
            }
        }

        private sealed class Snapshot
        {
            private Snapshot()
            {
            }

            public IReadOnlyDictionary<string, string> Guids { get; set; }

            public int SampleGroupCount { get; set; }

            public int SampleGroupEntryCount { get; set; }

            public int ProfileInstallerCount { get; set; }

            public int SampleBuildSceneCount { get; set; }

            public static Snapshot Capture(
                IReadOnlyList<string> paths)
            {
                var settings =
                    AddressableAssetSettingsDefaultObject.Settings;
                var groups = settings.groups
                    .Where(
                        group =>
                            group != null &&
                            group.Name ==
                            SampleContent.AddressablesGroupName)
                    .ToArray();
                var profile =
                    AssetDatabase.LoadAssetAtPath<FrameworkProfile>(
                        SampleAssetPaths.ProfilePath);
                var sampleScenePaths = new HashSet<string>(
                    new[]
                    {
                        SampleAssetPaths.BootstrapScenePath,
                        SampleAssetPaths.MainMenuScenePath,
                        SampleAssetPaths.GameplayScenePath
                    },
                    StringComparer.Ordinal);
                return new Snapshot
                {
                    Guids = paths.ToDictionary(
                        path => path,
                        AssetDatabase.AssetPathToGUID),
                    SampleGroupCount = groups.Length,
                    SampleGroupEntryCount =
                        groups.Sum(group => group.entries.Count),
                    ProfileInstallerCount =
                        profile == null
                            ? -1
                            : profile.Installers.Count,
                    SampleBuildSceneCount =
                        EditorBuildSettings.scenes.Count(
                            scene =>
                                sampleScenePaths.Contains(scene.path))
                };
            }
        }

        private static void AssertPersistedAudioClip(string path)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            Assert.That(clip, Is.Not.Null, path);
            Assert.That(clip.length, Is.GreaterThan(0.3f), path);
            var samples = new float[clip.samples * clip.channels];
            Assert.That(clip.GetData(samples, 0), Is.True, path);
            Assert.That(
                samples.Any(sample => Mathf.Abs(sample) > 0.001f),
                Is.True,
                path);
        }
    }
}
