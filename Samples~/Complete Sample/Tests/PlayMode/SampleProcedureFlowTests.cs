#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ArkFramework.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ArkFramework.Samples.Tests
{
    public sealed class SampleProcedureFlowTests :
        IPrebuildSetup,
        IPostBuildCleanup
    {
        private const string SetupActiveKey =
            "ArkFramework.SampleProcedureFlowTests.SetupActive";
        private const string PreviousBuilderIndexKey =
            "ArkFramework.SampleProcedureFlowTests.PreviousBuilderIndex";
        private const double TimeoutSeconds = 20d;

        private FrameworkHost _host;
        private Scene _cleanupScene;

        void IPrebuildSetup.Setup()
        {
            if (EditorPrefs.GetBool(SetupActiveKey))
            {
                RestoreBuilder();
            }

            var settings =
                AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "The generated sample Addressables settings are missing.");
            }

            EditorPrefs.SetInt(
                PreviousBuilderIndexKey,
                settings.ActivePlayModeDataBuilderIndex);
            EditorPrefs.SetBool(SetupActiveKey, true);
            try
            {
                var fastModeIndex = settings.DataBuilders.FindIndex(
                    builder => builder is BuildScriptFastMode);
                if (fastModeIndex < 0)
                {
                    throw new InvalidOperationException(
                        "Addressables settings have no FastMode builder.");
                }

                settings.ActivePlayModeDataBuilderIndex = fastModeIndex;
                AddressablesSampleBuilder.RebuildSampleContent();
                AddressablesSampleBuilder.GeneratePlayModeData();
            }
            catch
            {
                RestoreBuilder();
                throw;
            }
        }

        void IPostBuildCleanup.Cleanup()
        {
            RestoreBuilder();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            var host = _host != null
                ? _host
                : FrameworkHost.Current;
            Task stop = null;
            Exception stopStartFailure = null;
            if (host != null && host.Runtime != null)
            {
                try
                {
                    EnsureCleanupScene();
                    stop = host.StopRuntimeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    stopStartFailure = exception;
                }
            }

            if (stop != null)
            {
                var elapsed = Stopwatch.StartNew();
                while (!stop.IsCompleted &&
                       elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
                {
                    yield return null;
                }

                elapsed.Stop();
            }

            if (host != null)
            {
                Object.Destroy(host.gameObject);
            }

            _host = null;
            yield return null;
            if (stopStartFailure != null)
            {
                throw stopStartFailure;
            }

            if (stop != null)
            {
                Assert.That(
                    stop.IsCompleted,
                    Is.True,
                    "Framework runtime cleanup timed out.");
                Observe(stop);
            }
        }

        [UnityTest]
        public IEnumerator GeneratedBootstrapRunsTheRealButtonFlowAndCleansUp()
        {
            var bootstrapIndex =
                SceneUtility.GetBuildIndexByScenePath(
                    SampleAssetPaths.BootstrapScenePath);
            Assert.That(
                bootstrapIndex,
                Is.GreaterThanOrEqualTo(0),
                "The generated Bootstrap scene is not in build settings.");
            var load = SceneManager.LoadSceneAsync(
                bootstrapIndex,
                LoadSceneMode.Single);
            Assert.That(load, Is.Not.Null);
            yield return WaitForCondition(
                () => load.isDone,
                "Loading the generated Bootstrap scene timed out.");
            yield return WaitForCondition(
                TryGetStableMainMenu,
                "Bootstrap did not reach a stable MainMenu state.");

            _host = FrameworkHost.Current;
            Assert.That(_host, Is.Not.Null);
            var runtime = _host.Runtime;
            Assert.That(runtime, Is.Not.Null);
            var platform =
                runtime.Services.Resolve<IPlatformService>();
            var procedures =
                runtime.Services.Resolve<IProcedureService>();
            var flow = runtime.Services.Resolve<ISampleFlow>();
            var config = runtime.Services.Resolve<IConfigService>();
            var scenes = runtime.Services.Resolve<ISceneService>();
            var rig = runtime.Services.Resolve<IRigService>();
            var ui = runtime.Services.Resolve<IUIService>();
            var sampleUI = runtime.Services.Resolve<ISampleUIService>();
            var tables = runtime.Services.Resolve<ITableService>();
            var audio = runtime.Services.Resolve<IAudioService>();
            var resources =
                runtime.Services.Resolve<IResourceService>();
            var pool = runtime.Services.Resolve<IGameObjectPool>();

            Assert.That(platform.Root, Is.Not.Null);
            Assert.That(platform.UIRoots, Has.Count.EqualTo(5));
            Assert.That(
                platform.GetUIRoot("Normal"),
                Is.Not.Null);

            Assert.That(
                tables.TryGetLoaded<SampleUIRow>(
                    SampleContent.UITablePath,
                    out var uiTable),
                Is.True);
            Assert.That(uiTable.Count, Is.EqualTo(3));
            Assert.That(
                sampleUI.Get(SampleContent.MainMenuWindowId).Address,
                Is.EqualTo("sample/ui/main-menu"));
            Assert.That(
                sampleUI.Get(SampleContent.GameplayHudWindowId).Address,
                Is.EqualTo("sample/ui/gameplay-hud"));

            AssertMainMenuStable(
                procedures,
                flow,
                config,
                scenes,
                ui,
                audio);
            AssertRigSynchronized(rig, scenes);
            var mainMenu = (MainMenuWindow)flow.ActiveWindow;
            Assert.That(mainMenu.PlayButton, Is.Not.Null);
            Assert.That(
                mainMenu.transform.parent,
                Is.SameAs(
                    platform.GetUIRoot(
                        sampleUI.Get(
                            SampleContent.MainMenuWindowId).RootId)));
            mainMenu.PlayButton.onClick.Invoke();

            yield return WaitForCondition(
                () =>
                    procedures.CurrentProcedureId ==
                    SampleContent.GameplayProcedureId &&
                    flow.ActiveProcedureId ==
                    SampleContent.GameplayProcedureId &&
                    flow.ActiveWindow is GameplayHudWindow,
                "The Main Menu Play button did not reach stable Gameplay.");
            AssertGameplayStable(
                procedures,
                flow,
                scenes,
                ui,
                audio);
            AssertRigSynchronized(rig, scenes);
            var hud = (GameplayHudWindow)flow.ActiveWindow;
            Assert.That(hud.BackButton, Is.Not.Null);
            hud.BackButton.onClick.Invoke();

            yield return WaitForCondition(
                TryGetStableMainMenu,
                "The Gameplay HUD Back button did not return to MainMenu.");
            AssertMainMenuStable(
                procedures,
                flow,
                config,
                scenes,
                ui,
                audio);
            AssertRigSynchronized(rig, scenes);

            EnsureCleanupScene();
            var stop = _host.StopRuntimeAsync().AsTask();
            yield return WaitForTask(
                stop,
                "Stopping the sample FrameworkHost runtime timed out.");
            Observe(stop);
            Assert.That(
                resources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                resources.Diagnostics.InflightOperationCount,
                Is.Zero);
            Assert.That(ui.Diagnostics.OpenCount, Is.Zero);
            Assert.That(ui.Diagnostics.OpeningCount, Is.Zero);
            Assert.That(ui.Diagnostics.ClosingCount, Is.Zero);
            Assert.That(audio.Diagnostics.Entries, Is.Empty);
            Assert.That(audio.Diagnostics.PendingLoadCount, Is.Zero);
            Assert.That(
                pool.Diagnostics.Values.Sum(
                    item => item.ActiveCount),
                Is.Zero);
        }

        private static bool TryGetStableMainMenu()
        {
            var host = FrameworkHost.Current;
            var runtime = host == null ? null : host.Runtime;
            if (runtime == null ||
                !runtime.Services.TryResolve<IProcedureService>(
                    out var procedures) ||
                !runtime.Services.TryResolve<ISampleFlow>(out var flow))
            {
                return false;
            }

            return procedures.CurrentProcedureId ==
                   SampleContent.MainMenuProcedureId &&
                   flow.ActiveProcedureId ==
                   SampleContent.MainMenuProcedureId &&
                   flow.ActiveWindow is MainMenuWindow;
        }

        private static void AssertMainMenuStable(
            IProcedureService procedures,
            ISampleFlow flow,
            IConfigService config,
            ISceneService scenes,
            IUIService ui,
            IAudioService audio)
        {
            Assert.That(
                procedures.CurrentProcedureId,
                Is.EqualTo(SampleContent.MainMenuProcedureId));
            Assert.That(
                flow.ActiveProcedureId,
                Is.EqualTo(SampleContent.MainMenuProcedureId));
            Assert.That(flow.ActiveWindow, Is.TypeOf<MainMenuWindow>());
            Assert.That(
                scenes.ActiveSceneKey.Value,
                Is.EqualTo(SampleContent.MainMenuSceneAddress));
            Assert.That(
                scenes.ActiveSceneId,
                Is.EqualTo(SampleContent.MainMenuSceneId));
            Assert.That(scenes.ActiveSceneName, Is.EqualTo("MainMenu"));

            var payload =
                config.Get<GameplayConfig>(
                    SampleContent.GameplayConfigKey);
            Assert.That(payload.StartingLives, Is.EqualTo(5));
            Assert.That(payload.MoveSpeed, Is.EqualTo(6.5f));
            Assert.That(
                payload.WelcomeMessage,
                Is.EqualTo("JSON override"));
            var key = new ConfigKey(
                typeof(GameplayConfig),
                SampleContent.GameplayConfigKey);
            Assert.That(
                config.Diagnostics.Entries[key].Source,
                Is.EqualTo(JsonConfigProvider.DefaultName));
            Assert.That(
                config.Diagnostics.Entries[key].Version,
                Is.EqualTo("2"));

            AssertStableWindow(
                ui,
                SampleContent.MainMenuWindowId);
            Assert.That(
                audio.Diagnostics.CurrentMusicKey?.Value,
                Is.EqualTo(SampleContent.MenuMusicAddress));
        }

        private static void AssertGameplayStable(
            IProcedureService procedures,
            ISampleFlow flow,
            ISceneService scenes,
            IUIService ui,
            IAudioService audio)
        {
            Assert.That(
                procedures.CurrentProcedureId,
                Is.EqualTo(SampleContent.GameplayProcedureId));
            Assert.That(
                flow.ActiveProcedureId,
                Is.EqualTo(SampleContent.GameplayProcedureId));
            Assert.That(
                flow.ActiveWindow,
                Is.TypeOf<GameplayHudWindow>());
            Assert.That(
                scenes.ActiveSceneKey.Value,
                Is.EqualTo(SampleContent.GameplaySceneAddress));
            Assert.That(
                scenes.ActiveSceneId,
                Is.EqualTo(SampleContent.GameplaySceneId));
            Assert.That(scenes.ActiveSceneName, Is.EqualTo("Gameplay"));
            AssertStableWindow(
                ui,
                SampleContent.GameplayHudWindowId);
            Assert.That(
                audio.Diagnostics.CurrentMusicKey?.Value,
                Is.EqualTo(SampleContent.GameplayMusicAddress));
        }

        private static void AssertRigSynchronized(
            IRigService rig,
            ISceneService scenes)
        {
            Assert.That(rig.ActiveRigId, Is.EqualTo(SampleContent.MainRigId));
            Assert.That(rig.LastSyncResult.Succeeded, Is.True);
            Assert.That(
                rig.LastSyncResult.SceneName,
                Is.EqualTo(scenes.ActiveSceneName));
            Assert.That(rig.LastSyncResult.MatchedCameraCount, Is.EqualTo(1));
            Assert.That(
                rig.LastSyncResult.SynchronizedPoseCount,
                Is.EqualTo(1));
            Assert.That(
                rig.LastSyncResult.SynchronizedCameraCount,
                Is.EqualTo(1));
            Assert.That(
                rig.LastSyncResult.DisabledSceneCameraCount,
                Is.EqualTo(1));

            var bindings = SceneManager.GetActiveScene()
                .GetRootGameObjects()
                .SelectMany(root =>
                    root.GetComponentsInChildren<SceneCameraBinding>(true))
                .ToArray();
            Assert.That(bindings, Has.Length.EqualTo(1));
            Assert.That(bindings[0].Camera.enabled, Is.False);

            var target = rig.ActiveRig
                .GetComponentsInChildren<RigCameraSlot>(true)
                .Single(slot =>
                    slot.Id == SampleContent.MainCameraSlotId)
                .Camera;
            Assert.That(target.enabled, Is.True);
            Assert.That(target.orthographic, Is.True);
            Assert.That(
                target.backgroundColor,
                Is.EqualTo(bindings[0].Camera.backgroundColor));
        }

        private static void AssertStableWindow(
            IUIService ui,
            string expectedId)
        {
            var windows = ui.Diagnostics.Windows
                .Where(
                    window =>
                        window.DescriptorId ==
                            SampleContent.MainMenuWindowId ||
                        window.DescriptorId ==
                            SampleContent.GameplayHudWindowId ||
                        window.DescriptorId ==
                            SampleContent.LoadingWindowId)
                .ToArray();
            Assert.That(windows, Has.Length.EqualTo(1));
            Assert.That(
                windows[0].DescriptorId,
                Is.EqualTo(expectedId));
            Assert.That(
                windows[0].State,
                Is.EqualTo(UIWindowState.Open));
            Assert.That(
                windows.Any(
                    window =>
                        window.DescriptorId ==
                        SampleContent.LoadingWindowId),
                Is.False);
        }

        private static IEnumerator WaitForCondition(
            Func<bool> condition,
            string timeoutMessage)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() &&
                   elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(
                condition(),
                Is.True,
                timeoutMessage + " Elapsed real seconds: " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") + ".");
        }

        private static IEnumerator WaitForTask(
            Task task,
            string timeoutMessage)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted &&
                   elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(
                task.IsCompleted,
                Is.True,
                timeoutMessage + " Elapsed real seconds: " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") + ".");
        }

        private static void Observe(Task task)
        {
            task.GetAwaiter().GetResult();
        }

        private void EnsureCleanupScene()
        {
            if (_cleanupScene.IsValid() && _cleanupScene.isLoaded)
            {
                SceneManager.SetActiveScene(_cleanupScene);
                return;
            }

            _cleanupScene = SceneManager.CreateScene(
                "ArkFramework Sample Test Cleanup");
            SceneManager.SetActiveScene(_cleanupScene);
        }

        private static void RestoreBuilder()
        {
            if (!EditorPrefs.GetBool(SetupActiveKey))
            {
                return;
            }

            var settings =
                AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "Cannot restore the Addressables play-mode builder " +
                    "because the generated settings are unavailable.");
            }

            settings.ActivePlayModeDataBuilderIndex =
                EditorPrefs.GetInt(PreviousBuilderIndexKey);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            EditorPrefs.DeleteKey(SetupActiveKey);
            EditorPrefs.DeleteKey(PreviousBuilderIndexKey);
        }
    }
}
#endif
