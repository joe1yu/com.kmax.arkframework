using System;
using UnityEditor;

namespace ArkFramework.Editor
{
    /// <summary>
    /// 根据示例程序集的稳定 GUID 定位导入目录，使 Samples 可以安装到任意路径。
    /// </summary>
    public static class SampleAssetPaths
    {
        private const string SampleAssemblyGuid =
            "d3f54545589d5b641bfa6f0680428f09";

        private static string _sampleRoot;

        public static string SampleRoot =>
            _sampleRoot ?? (_sampleRoot = ResolveSampleRoot());

        public static string GeneratedRoot => SampleRoot + "/Generated";

        public static string EmptySceneTemplatePath =>
            SampleRoot + "/Editor/EmptySceneTemplate.unity";

        public static string ProfilePath =>
            GeneratedRoot + "/Profile/ArkFrameworkSampleProfile.asset";

        public static string BootstrapScenePath =>
            GeneratedRoot + "/Scenes/Bootstrap.unity";

        public static string MainMenuScenePath =>
            GeneratedRoot + "/Scenes/MainMenu.unity";

        public static string GameplayScenePath =>
            GeneratedRoot + "/Scenes/Gameplay.unity";

        private static string ResolveSampleRoot()
        {
            var assemblyPath =
                AssetDatabase.GUIDToAssetPath(SampleAssemblyGuid);
            if (string.IsNullOrEmpty(assemblyPath))
            {
                throw new InvalidOperationException(
                    "ArkFramework Complete Sample is not imported. Import " +
                    "it from Package Manager before rebuilding sample content.");
            }

            const string suffix = "/ArkFramework.Samples.asmdef";
            if (!assemblyPath.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The ArkFramework sample assembly is at an unexpected " +
                    "path: '" + assemblyPath + "'.");
            }

            return assemblyPath.Substring(
                0,
                assemblyPath.Length - suffix.Length);
        }
    }
}
