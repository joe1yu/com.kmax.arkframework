using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArkFramework.Samples;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArkFramework.Editor
{
    public static class AddressablesSampleBuilder
    {
        private static readonly string AudioRoot =
            SampleAssetPaths.GeneratedRoot + "/Audio";
        private static readonly string ConfigRoot =
            SampleAssetPaths.GeneratedRoot + "/Config";
        private static readonly string InstallerRoot =
            SampleAssetPaths.GeneratedRoot + "/Installers";
        private static readonly string PrefabRoot =
            SampleAssetPaths.GeneratedRoot + "/Prefabs";
        private static readonly string ProfileRoot =
            SampleAssetPaths.GeneratedRoot + "/Profile";
        private static readonly string SceneRoot =
            SampleAssetPaths.GeneratedRoot + "/Scenes";
        private const string TableRoot =
            "Assets/StreamingAssets/ArkFrameworkSample";

        private static readonly string MenuMusicPath =
            AudioRoot + "/MenuMusic.wav";
        private static readonly string GameplayMusicPath =
            AudioRoot + "/GameplayMusic.wav";
        private static readonly string GameplayConfigPath =
            ConfigRoot + "/GameplayConfig.asset";
        private static readonly string GameplayJsonPath =
            ConfigRoot + "/gameplay.json";
        private static readonly string ManifestJsonPath =
            ConfigRoot + "/manifest.json";
        private static readonly string MainMenuPrefabPath =
            PrefabRoot + "/MainMenuWindow.prefab";
        private static readonly string GameplayHudPrefabPath =
            PrefabRoot + "/GameplayHudWindow.prefab";
        private static readonly string LoadingPrefabPath =
            PrefabRoot + "/LoadingWindow.prefab";
        private static readonly string PlatformPrefabPath =
            PrefabRoot + "/PlatformRoot.prefab";
        private const string UITablePath =
            TableRoot + "/UI.csv";

        private static readonly string[] LegacyBuildScenePaths =
        {
            "Assets/ArkFramework/Samples/Generated/Scenes/Bootstrap.unity",
            "Assets/ArkFramework/Samples/Generated/Scenes/MainMenu.unity",
            "Assets/ArkFramework/Samples/Generated/Scenes/Gameplay.unity"
        };

        private static readonly string[] RequiredFolders =
        {
            SampleAssetPaths.GeneratedRoot,
            AudioRoot,
            ConfigRoot,
            InstallerRoot,
            PrefabRoot,
            ProfileRoot,
            SceneRoot,
            TableRoot
        };

        [MenuItem("ArkFramework/Samples/Rebuild Sample Content")]
        public static void Rebuild()
        {
            try
            {
                RebuildSampleContent();
                GeneratePlayModeData();
                Debug.Log(
                    "ArkFramework sample content rebuilt successfully.");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "ArkFramework sample rebuild failed. Inspect the inner " +
                    "exception and verify the generated paths and active " +
                    "Addressables play-mode data builder.",
                    exception);
            }
        }

        public static void RebuildFromCommandLine()
        {
            Rebuild();
        }

        public static void RebuildSampleContent()
        {
            try
            {
                EnsureFolders();
                DeleteLegacyAudioAssets();
                CreateOrUpdateAudioClip(
                    MenuMusicPath,
                    330f);
                CreateOrUpdateAudioClip(
                    GameplayMusicPath,
                    440f);
                CreateOrUpdateConfig();
                WriteConfigJson();
                WriteUITable();
                CreateOrUpdateMainMenuPrefab();
                CreateOrUpdateGameplayHudPrefab();
                CreateOrUpdateLoadingPrefab();
                CreateOrUpdatePlatformPrefab();
                var installers = CreateOrUpdateInstallers();
                var profile = CreateOrUpdateProfile(installers);
                CreateOrUpdateBootstrapScene(profile);
                CreateOrUpdatePresentationScene(
                    SampleAssetPaths.MainMenuScenePath,
                    "ArkFramework Sample MainMenu",
                    "MAIN MENU",
                    new Color(0.05f, 0.13f, 0.28f, 1f));
                CreateOrUpdatePresentationScene(
                    SampleAssetPaths.GameplayScenePath,
                    "ArkFramework Sample Gameplay",
                    "GAMEPLAY",
                    new Color(0.07f, 0.27f, 0.14f, 1f));
                UpdateAddressables();
                UpdateBuildScenes();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(
                    ImportAssetOptions.ForceSynchronousImport);
                ValidateGeneratedProfile(
                    AssetDatabase.LoadAssetAtPath<FrameworkProfile>(
                        SampleAssetPaths.ProfilePath));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Failed to create or update ArkFramework sample content.",
                    exception);
            }
        }

        public static void GeneratePlayModeData()
        {
            var settings =
                AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "Addressables settings do not exist. Rebuild the sample " +
                    "content before generating play-mode data.");
            }

            var builder = settings.ActivePlayModeDataBuilder;
            if (builder == null)
            {
                throw new InvalidOperationException(
                    "Addressables has no active play-mode data builder.");
            }

            var result = builder.BuildData<
                AddressablesPlayModeBuildResult>(
                    new AddressablesDataBuilderInput(settings));
            if (result == null)
            {
                throw new InvalidOperationException(
                    "The active Addressables play-mode data builder returned " +
                    "no result.");
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                throw new InvalidOperationException(
                    "Addressables play-mode data generation failed: " +
                    result.Error);
            }
        }

        private static void EnsureFolders()
        {
            for (var index = 0; index < RequiredFolders.Length; index++)
            {
                EnsureFolder(RequiredFolders[index]);
            }
        }

        private static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return;
            }

            var separator = assetPath.LastIndexOf('/');
            if (separator <= 0)
            {
                throw new ArgumentException(
                    "A project-relative folder path is required.",
                    nameof(assetPath));
            }

            var parent = assetPath.Substring(0, separator);
            var name = assetPath.Substring(separator + 1);
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(
                    AssetDatabase.CreateFolder(parent, name)))
            {
                throw new InvalidOperationException(
                    "Failed to create sample folder '" + assetPath + "'.");
            }
        }

        private static void CreateOrUpdateAudioClip(
            string path,
            float frequency)
        {
            const int sampleRate = 22050;
            const float seconds = 0.4f;
            var sampleCount = Mathf.CeilToInt(sampleRate * seconds);
            var dataSize = sampleCount * sizeof(short);
            byte[] bytes;
            using (var stream = new MemoryStream(44 + dataSize))
            using (var writer = new BinaryWriter(
                       stream,
                       Encoding.ASCII,
                       true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * sizeof(short));
                writer.Write((short)sizeof(short));
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);
                for (var index = 0; index < sampleCount; index++)
                {
                    var sample = Mathf.Sin(
                        2f * Mathf.PI * frequency * index / sampleRate);
                    writer.Write(
                        (short)Mathf.RoundToInt(
                            sample * 0.025f * short.MaxValue));
                }

                writer.Flush();
                bytes = stream.ToArray();
            }

            var projectRoot =
                Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new InvalidOperationException(
                    "Could not resolve the Unity project root.");
            }

            var absolutePath = Path.Combine(
                projectRoot,
                path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath) ||
                !File.ReadAllBytes(absolutePath).SequenceEqual(bytes))
            {
                File.WriteAllBytes(absolutePath, bytes);
            }

            AssetDatabase.ImportAsset(
                path,
                ImportAssetOptions.ForceSynchronousImport);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null || clip.samples != sampleCount)
            {
                throw new InvalidOperationException(
                    "Failed to import generated audio clip at '" +
                    path + "'.");
            }
        }

        private static void DeleteLegacyAudioAssets()
        {
            var legacyPaths = new[]
            {
                AudioRoot + "/MenuMusic.asset",
                AudioRoot + "/GameplayMusic.asset"
            };
            for (var index = 0; index < legacyPaths.Length; index++)
            {
                var legacyPath = legacyPaths[index];
                if (!string.IsNullOrEmpty(
                        AssetDatabase.AssetPathToGUID(legacyPath)))
                {
                    AssetDatabase.DeleteAsset(legacyPath);
                    if (!string.IsNullOrEmpty(
                            AssetDatabase.AssetPathToGUID(legacyPath)))
                    {
                        throw new InvalidOperationException(
                            "Failed to remove obsolete generated audio " +
                            "asset '" + legacyPath + "'.");
                    }
                }
            }
        }

        private static void CreateOrUpdateConfig()
        {
            var asset =
                AssetDatabase.LoadAssetAtPath<GameplayConfigAsset>(
                    GameplayConfigPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<
                    GameplayConfigAsset>();
                asset.name = "ArkFramework Sample Gameplay Config";
                AssetDatabase.CreateAsset(asset, GameplayConfigPath);
            }

            var serialized = new SerializedObject(asset);
            serialized.FindProperty("_key").stringValue =
                SampleContent.GameplayConfigKey;
            serialized.FindProperty("_version").stringValue = "1";
            var payload = serialized.FindProperty("_payload");
            payload.FindPropertyRelative("StartingLives").intValue = 3;
            payload.FindPropertyRelative("MoveSpeed").floatValue = 4f;
            payload.FindPropertyRelative("WelcomeMessage").stringValue =
                "Scriptable default";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void WriteConfigJson()
        {
            WriteTextIfChanged(
                GameplayJsonPath,
                "{\n" +
                "  \"StartingLives\": 5,\n" +
                "  \"MoveSpeed\": 6.5,\n" +
                "  \"WelcomeMessage\": \"JSON override\"\n" +
                "}\n");
            var typeName =
                typeof(GameplayConfig).AssemblyQualifiedName;
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new InvalidOperationException(
                    "Could not resolve the sample GameplayConfig assembly " +
                    "qualified type name.");
            }

            WriteTextIfChanged(
                ManifestJsonPath,
                "{\n" +
                "  \"entries\": [\n" +
                "    {\n" +
                "      \"type\": \"" +
                EscapeJson(typeName) + "\",\n" +
                "      \"key\": \"" +
                SampleContent.GameplayConfigKey + "\",\n" +
                "      \"version\": \"2\",\n" +
                "      \"address\": \"" +
                SampleContent.GameplayJsonAddress + "\"\n" +
                "    }\n" +
                "  ]\n" +
                "}\n");
            AssetDatabase.ImportAsset(
                GameplayJsonPath,
                ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(
                ManifestJsonPath,
                ImportAssetOptions.ForceSynchronousImport);
        }

        private static void WriteTextIfChanged(
            string assetPath,
            string content)
        {
            var projectRoot =
                Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new InvalidOperationException(
                    "Could not resolve the Unity project root.");
            }

            var absolutePath = Path.Combine(
                projectRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolutePath) &&
                string.Equals(
                    File.ReadAllText(absolutePath),
                    content,
                    StringComparison.Ordinal))
            {
                return;
            }

            File.WriteAllText(
                absolutePath,
                content,
                new UTF8Encoding(false));
        }

        private static void WriteUITable()
        {
            var content =
                "#class,ArkFramework.Samples.SampleUIRow\n" +
                "#fields,Id,WindowType,Address,Layer,Mode,CacheOnClose," +
                "RequiresMask,CloseOnMaskClick,BlocksInput,AllowBack\n" +
                "#types,string,string,string,UILayer,UIWindowMode,bool," +
                "bool,bool,bool,bool\n" +
                "#key,Id\n" +
                "#comments,配表ID,窗口类型,Addressables地址,UI层级," +
                "窗口模式,关闭缓存,需要遮罩,点击遮罩关闭,阻挡输入,允许返回\n" +
                "," + SampleContent.MainMenuWindowId + ",MainMenuWindow," +
                "sample/ui/main-menu" +
                ",Normal,SingleInstance,false,false,false,false,true\n" +
                "," + SampleContent.GameplayHudWindowId +
                ",GameplayHudWindow," +
                "sample/ui/gameplay-hud" +
                ",Normal,SingleInstance,false,false,false,false,true\n" +
                "//,Sample.Disabled,MainMenuWindow,sample/ui/disabled," +
                "Normal,SingleInstance,false,false,false,false,true\n" +
                "," + SampleContent.LoadingWindowId + ",LoadingWindow," +
                "sample/ui/loading" +
                ",System,SingleInstance,false,false,false,false,false\n";
            WriteTextIfChanged(UITablePath, content);
            AssetDatabase.ImportAsset(
                UITablePath,
                ImportAssetOptions.ForceSynchronousImport);
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void CreateOrUpdateMainMenuPrefab()
        {
            var root = CreateWindowRoot<MainMenuWindow>(
                "Sample Main Menu Window",
                new Color(0.06f, 0.18f, 0.38f, 0.96f),
                out var window);
            try
            {
                CreateLabel(
                    root.transform,
                    "Title",
                    "ARK FRAMEWORK SAMPLE",
                    new Vector2(0.1f, 0.66f),
                    new Vector2(0.9f, 0.9f),
                    42);
                var button = CreateButton(
                    root.transform,
                    "PlayButton",
                    "PLAY",
                    new Vector2(0.32f, 0.28f),
                    new Vector2(0.68f, 0.52f));
                AssignObjectReference(
                    window,
                    "_playButton",
                    button);
                SavePrefab(root, MainMenuPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void CreateOrUpdateGameplayHudPrefab()
        {
            var root = CreateWindowRoot<GameplayHudWindow>(
                "Sample Gameplay HUD Window",
                new Color(0.04f, 0.16f, 0.08f, 0.72f),
                out var window);
            try
            {
                CreateLabel(
                    root.transform,
                    "Status",
                    "GAMEPLAY HUD",
                    new Vector2(0.28f, 0.82f),
                    new Vector2(0.72f, 0.96f),
                    34);
                var button = CreateButton(
                    root.transform,
                    "BackButton",
                    "BACK TO MENU",
                    new Vector2(0.04f, 0.05f),
                    new Vector2(0.28f, 0.18f));
                AssignObjectReference(
                    window,
                    "_backButton",
                    button);
                SavePrefab(root, GameplayHudPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void CreateOrUpdateLoadingPrefab()
        {
            var root = CreateWindowRoot<LoadingWindow>(
                "Sample Loading Window",
                new Color(0.02f, 0.025f, 0.04f, 0.9f),
                out _);
            try
            {
                CreateLabel(
                    root.transform,
                    "LoadingLabel",
                    "LOADING...",
                    new Vector2(0.25f, 0.42f),
                    new Vector2(0.75f, 0.58f),
                    38);
                SavePrefab(root, LoadingPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void CreateOrUpdatePlatformPrefab()
        {
            var root = new GameObject("ArkFramework Sample Platform");
            try
            {
                var canvasObject = new GameObject(
                    "Platform Canvas",
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                canvasObject.transform.SetParent(root.transform, false);
                Stretch(canvasObject.GetComponent<RectTransform>());
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = -1000;
                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode =
                    CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);

                var eventSystem = new GameObject(
                    "Platform EventSystem",
                    typeof(EventSystem),
                    typeof(StandaloneInputModule));
                eventSystem.transform.SetParent(root.transform, false);
                SavePrefab(root, PlatformPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static GameObject CreateWindowRoot<TWindow>(
            string name,
            Color background,
            out TWindow window)
            where TWindow : UIWindow
        {
            var root = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(TWindow));
            var rect = root.GetComponent<RectTransform>();
            Stretch(rect);
            var image = root.GetComponent<Image>();
            image.color = background;
            window = root.GetComponent<TWindow>();
            return root;
        }

        private static void AssignObjectReference(
            Object owner,
            string fieldName,
            Object value)
        {
            var serialized = new SerializedObject(owner);
            var property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                throw new InvalidOperationException(
                    "Serialized field '" + fieldName +
                    "' was not found on '" + owner.GetType().FullName + "'.");
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Button CreateButton(
            Transform parent,
            string name,
            string caption,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            var buttonObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            SetRect(
                buttonObject.GetComponent<RectTransform>(),
                anchorMin,
                anchorMax);
            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.16f, 0.55f, 0.92f, 1f);
            var button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            CreateLabel(
                buttonObject.transform,
                "Label",
                caption,
                Vector2.zero,
                Vector2.one,
                28);
            return button;
        }

        private static Text CreateLabel(
            Transform parent,
            string name,
            string value,
            Vector2 anchorMin,
            Vector2 anchorMax,
            int fontSize)
        {
            var labelObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));
            labelObject.transform.SetParent(parent, false);
            SetRect(
                labelObject.GetComponent<RectTransform>(),
                anchorMin,
                anchorMax);
            var label = labelObject.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.text = value;
            label.fontSize = fontSize;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        private static void SavePrefab(GameObject root, string path)
        {
            if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
            {
                throw new InvalidOperationException(
                    "Failed to save sample prefab '" + path + "'.");
            }
        }

        private static List<ModuleInstaller>
            CreateOrUpdateInstallers()
        {
            var installers = new List<ModuleInstaller>(12)
            {
                CreateOrUpdateInstaller<EventBusModuleInstaller>("EventBus"),
                CreateOrUpdatePlatformInstaller(),
                CreateOrUpdateInstaller<ResourceModuleInstaller>("Resource"),
                CreateOrUpdateInstaller<PoolModuleInstaller>("Pool")
            };
            var config =
                CreateOrUpdateInstaller<ConfigModuleInstaller>("Config");
            var serializedConfig = new SerializedObject(config);
            serializedConfig.FindProperty("_scriptableObjectLabel")
                .stringValue = SampleContent.ScriptableConfigLabel;
            serializedConfig.FindProperty("_jsonManifestAddress")
                .stringValue = SampleContent.ConfigManifestAddress;
            serializedConfig.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(config);
            installers.Add(config);
            installers.Add(
                CreateOrUpdateInstaller<TableModuleInstaller>("Table"));
            installers.Add(
                CreateOrUpdateInstaller<FsmModuleInstaller>("FSM"));
            installers.Add(
                CreateOrUpdateInstaller<SceneModuleInstaller>("Scene"));
            installers.Add(
                CreateOrUpdateInstaller<UIModuleInstaller>("UI"));
            installers.Add(
                CreateOrUpdateInstaller<AudioModuleInstaller>("Audio"));
            installers.Add(
                CreateOrUpdateInstaller<ActionKitModuleInstaller>(
                    "ActionKit"));
            installers.Add(
                CreateOrUpdateInstaller<SampleModuleInstaller>("Procedure"));
            return installers;
        }

        private static PlatformModuleInstaller
            CreateOrUpdatePlatformInstaller()
        {
            var installer =
                CreateOrUpdateInstaller<PlatformModuleInstaller>("Platform");
            var serialized = new SerializedObject(installer);
            serialized.FindProperty("_platformPrefab").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<GameObject>(PlatformPrefabPath);
            serialized.FindProperty("_dontDestroyOnLoad").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(installer);
            return installer;
        }

        private static T CreateOrUpdateInstaller<T>(string assetName)
            where T : ModuleInstaller
        {
            var path = InstallerRoot + "/" + assetName + "Installer.asset";
            var installer = AssetDatabase.LoadAssetAtPath<T>(path);
            if (installer == null)
            {
                var existing = AssetDatabase.LoadMainAssetAtPath(path);
                installer = existing == null
                    ? CreateInstallerAsset<T>(path, assetName)
                    : ReplaceInstallerAsset<T>(path, assetName);
            }

            installer.name = assetName + "Installer";
            EditorUtility.SetDirty(installer);
            return installer;
        }

        private static T CreateInstallerAsset<T>(
            string path,
            string assetName)
            where T : ModuleInstaller
        {
            var installer = ScriptableObject.CreateInstance<T>();
            installer.name = assetName + "Installer";
            AssetDatabase.CreateAsset(installer, path);
            return installer;
        }

        private static T ReplaceInstallerAsset<T>(
            string path,
            string assetName)
            where T : ModuleInstaller
        {
            var temporaryPath = AssetDatabase.GenerateUniqueAssetPath(
                InstallerRoot + "/InstallerMigration.asset");
            CreateInstallerAsset<T>(temporaryPath, assetName);
            AssetDatabase.SaveAssets();
            try
            {
                // 只替换资产内容，保留目标 .meta，从而保持 Profile 外部引用稳定。
                File.Copy(
                    Path.GetFullPath(temporaryPath),
                    Path.GetFullPath(path),
                    overwrite: true);
            }
            finally
            {
                AssetDatabase.DeleteAsset(temporaryPath);
            }

            AssetDatabase.ImportAsset(
                path,
                ImportAssetOptions.ForceSynchronousImport |
                ImportAssetOptions.ForceUpdate);
            var installer = AssetDatabase.LoadAssetAtPath<T>(path);
            if (installer == null)
            {
                throw new InvalidOperationException(
                    $"Failed to migrate installer asset '{path}' to " +
                    $"{typeof(T).Name}.");
            }

            return installer;
        }

        private static FrameworkProfile CreateOrUpdateProfile(
            IReadOnlyList<ModuleInstaller> installers)
        {
            var profile =
                AssetDatabase.LoadAssetAtPath<FrameworkProfile>(
                    SampleAssetPaths.ProfilePath);
            if (profile == null)
            {
                profile =
                    ScriptableObject.CreateInstance<FrameworkProfile>();
                profile.name = "ArkFramework Sample Profile";
                AssetDatabase.CreateAsset(
                    profile,
                    SampleAssetPaths.ProfilePath);
            }

            var serialized = new SerializedObject(profile);
            var property = serialized.FindProperty("_installers");
            property.arraySize = installers.Count;
            for (var index = 0; index < installers.Count; index++)
            {
                property.GetArrayElementAtIndex(index)
                    .objectReferenceValue = installers[index];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static void ValidateGeneratedProfile(
            FrameworkProfile profile)
        {
            if (profile == null)
            {
                throw new InvalidOperationException(
                    "The generated sample profile could not be reloaded from '" +
                    SampleAssetPaths.ProfilePath + "'.");
            }

            var result = FrameworkEditorValidation.Validate(profile);
            if (result.IsValid)
            {
                return;
            }

            throw new InvalidOperationException(
                "Generated sample profile is invalid: " +
                string.Join(
                    " | ",
                    result.Issues.Select(
                        issue => issue.Code + ": " + issue.Message)));
        }

        private static void CreateOrUpdateBootstrapScene(
            FrameworkProfile profile)
        {
            var previousActiveScene = SceneManager.GetActiveScene();
            var scene = OpenGeneratedScene(
                SampleAssetPaths.BootstrapScenePath,
                out var closeAfter);
            try
            {
                SceneManager.SetActiveScene(scene);
                CreateScenePresentation(
                    "ArkFramework Sample Bootstrap",
                    "BOOTSTRAP",
                    new Color(0.20f, 0.08f, 0.28f, 1f));

                var hostObject = new GameObject("Framework Host");
                var host = hostObject.AddComponent<FrameworkHost>();
                host.Configure(profile);
                EditorUtility.SetDirty(host);

                var overlayObject =
                    new GameObject("Runtime Debug Overlay");
                overlayObject.AddComponent<RuntimeDebugOverlay>();
                if (!EditorSceneManager.SaveScene(
                        scene,
                        SampleAssetPaths.BootstrapScenePath))
                {
                    throw new InvalidOperationException(
                        "Failed to save the sample Bootstrap scene.");
                }
            }
            finally
            {
                RestoreActiveScene(previousActiveScene);
                if (closeAfter)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void CreateOrUpdatePresentationScene(
            string path,
            string rootName,
            string label,
            Color background)
        {
            var previousActiveScene = SceneManager.GetActiveScene();
            var scene = OpenGeneratedScene(path, out var closeAfter);
            try
            {
                SceneManager.SetActiveScene(scene);
                CreateScenePresentation(rootName, label, background);
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new InvalidOperationException(
                        "Failed to save sample scene '" + path + "'.");
                }
            }
            finally
            {
                RestoreActiveScene(previousActiveScene);
                if (closeAfter)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static Scene OpenGeneratedScene(
            string path,
            out bool closeAfter)
        {
            var scene = SceneManager.GetSceneByPath(path);
            closeAfter = !scene.IsValid() || !scene.isLoaded;
            if (closeAfter)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    if (!AssetDatabase.CopyAsset(
                            SampleAssetPaths.EmptySceneTemplatePath,
                            path))
                    {
                        throw new InvalidOperationException(
                            "Failed to create sample scene from the empty " +
                            "scene template at '" + path + "'.");
                    }

                    AssetDatabase.ImportAsset(
                        path,
                        ImportAssetOptions.ForceSynchronousImport);
                }

                scene = EditorSceneManager.OpenScene(
                    path,
                    OpenSceneMode.Additive);
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                Object.DestroyImmediate(root);
            }

            return scene;
        }

        private static void RestoreActiveScene(Scene scene)
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                SceneManager.SetActiveScene(scene);
            }
        }

        private static void CreateScenePresentation(
            string rootName,
            string label,
            Color background)
        {
            var cameraObject = new GameObject(
                "Sample Camera",
                typeof(Camera));
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.orthographic = true;

            var canvasObject = new GameObject(
                rootName,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            CreateLabel(
                canvasObject.transform,
                "Scene Label",
                label,
                new Vector2(0.1f, 0.36f),
                new Vector2(0.9f, 0.64f),
                68);
        }

        private static IReadOnlyDictionary<string, string> ReadUIAddresses()
        {
            var document = CsvTableDocument.Parse(
                File.ReadAllText(UITablePath),
                UITablePath);
            var idColumn = document.Schema.Columns.Single(
                column => column.Name == "Id");
            var addressColumn = document.Schema.Columns.Single(
                column => column.Name == "Address");

            // Addressables 注册也读取同一份表，避免运行时地址与构建地址双写。
            return document.Rows.ToDictionary(
                row => row.Cells[idColumn.Index],
                row => row.Cells[addressColumn.Index],
                StringComparer.Ordinal);
        }

        private static void UpdateAddressables()
        {
            var uiAddresses = ReadUIAddresses();
            var settings =
                AddressableAssetSettingsDefaultObject.GetSettings(true);
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "Could not create or load Addressables settings.");
            }

            var groups = settings.groups
                .Where(
                    group =>
                        group != null &&
                        string.Equals(
                            group.Name,
                            SampleContent.AddressablesGroupName,
                            StringComparison.Ordinal))
                .ToArray();
            if (groups.Length > 1)
            {
                throw new InvalidOperationException(
                    "Multiple Addressables groups named '" +
                    SampleContent.AddressablesGroupName +
                    "' exist. Merge them before rebuilding the sample.");
            }

            var group = groups.Length == 1
                ? groups[0]
                : settings.CreateGroup(
                    SampleContent.AddressablesGroupName,
                    false,
                    false,
                    false,
                    null,
                    typeof(BundledAssetGroupSchema),
                    typeof(ContentUpdateGroupSchema));
            if (group == null)
            {
                throw new InvalidOperationException(
                    "Could not create the ArkFramework sample " +
                    "Addressables group.");
            }

            settings.AddLabel(
                SampleContent.ScriptableConfigLabel,
                false);
            AddOrMoveEntry(
                settings,
                group,
                GameplayConfigPath,
                SampleContent.GameplayScriptableAddress,
                SampleContent.ScriptableConfigLabel);
            AddOrMoveEntry(
                settings,
                group,
                ManifestJsonPath,
                SampleContent.ConfigManifestAddress);
            AddOrMoveEntry(
                settings,
                group,
                GameplayJsonPath,
                SampleContent.GameplayJsonAddress);
            AddOrMoveEntry(
                settings,
                group,
                SampleAssetPaths.MainMenuScenePath,
                SampleContent.MainMenuSceneAddress);
            AddOrMoveEntry(
                settings,
                group,
                SampleAssetPaths.GameplayScenePath,
                SampleContent.GameplaySceneAddress);
            AddOrMoveEntry(
                settings,
                group,
                MainMenuPrefabPath,
                uiAddresses[SampleContent.MainMenuWindowId]);
            AddOrMoveEntry(
                settings,
                group,
                GameplayHudPrefabPath,
                uiAddresses[SampleContent.GameplayHudWindowId]);
            AddOrMoveEntry(
                settings,
                group,
                LoadingPrefabPath,
                uiAddresses[SampleContent.LoadingWindowId]);
            AddOrMoveEntry(
                settings,
                group,
                MenuMusicPath,
                SampleContent.MenuMusicAddress);
            AddOrMoveEntry(
                settings,
                group,
                GameplayMusicPath,
                SampleContent.GameplayMusicAddress);
            EditorUtility.SetDirty(group);
            EditorUtility.SetDirty(settings);
        }

        private static AddressableAssetEntry AddOrMoveEntry(
            AddressableAssetSettings settings,
            AddressableAssetGroup group,
            string path,
            string address,
            string label = null)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException(
                    "Generated sample asset has no GUID: '" + path + "'.");
            }

            var entry = settings.CreateOrMoveEntry(
                guid,
                group,
                false,
                false);
            if (entry == null)
            {
                throw new InvalidOperationException(
                    "Could not create Addressables entry for '" + path + "'.");
            }

            entry.SetAddress(address, false);
            if (!string.IsNullOrEmpty(label))
            {
                entry.SetLabel(label, true, false, false);
            }

            return entry;
        }

        private static void UpdateBuildScenes()
        {
            var samplePaths = new HashSet<string>(
                new[]
                {
                    SampleAssetPaths.BootstrapScenePath,
                    SampleAssetPaths.MainMenuScenePath,
                    SampleAssetPaths.GameplayScenePath
                },
                StringComparer.Ordinal);
            var scenes = EditorBuildSettings.scenes
                .Where(
                    scene =>
                        !samplePaths.Contains(scene.path) &&
                        !LegacyBuildScenePaths.Contains(scene.path))
                .ToList();

            // MainMenu 与 Gameplay 由 Addressables 按表中地址加载，不能同时加入
            // Build Settings，否则 Addressables 会自动移除它们的地址条目。
            EditorBuildSettings.scenes = scenes.ToArray();
            scenes.Add(
                new EditorBuildSettingsScene(
                    SampleAssetPaths.BootstrapScenePath,
                    true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void Stretch(RectTransform transform)
        {
            SetRect(transform, Vector2.zero, Vector2.one);
        }

        private static void SetRect(
            RectTransform transform,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            transform.anchorMin = anchorMin;
            transform.anchorMax = anchorMax;
            transform.offsetMin = Vector2.zero;
            transform.offsetMax = Vector2.zero;
            transform.localScale = Vector3.one;
        }
    }
}
