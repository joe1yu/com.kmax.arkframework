using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

namespace ArkFramework.Editor
{
    public static class FrameworkSampleImporter
    {
        private const string CompleteSampleName = "Complete Sample";

        [MenuItem("ArkFramework/Samples/Import Complete Sample")]
        public static void ImportCompleteSample()
        {
            var packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(FrameworkHost).Assembly);
            if (packageInfo == null)
            {
                throw new InvalidOperationException(
                    "Could not resolve the ArkFramework package.");
            }

            var matches = Sample.FindByPackage(
                    packageInfo.name,
                    packageInfo.version)
                .Where(sample =>
                    string.Equals(
                        sample.displayName,
                        CompleteSampleName,
                        StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one '{CompleteSampleName}' in " +
                    $"{packageInfo.name}@{packageInfo.version}, but found " +
                    $"{matches.Length}.");
            }

            var options =
                Sample.ImportOptions.OverridePreviousImports |
                Sample.ImportOptions.HideImportWindow;
            if (!matches[0].Import(options))
            {
                throw new InvalidOperationException(
                    $"Failed to import '{CompleteSampleName}'.");
            }

            Debug.Log(
                $"ArkFramework {CompleteSampleName} imported to " +
                $"'{matches[0].importPath}'.");
        }

        public static void ImportCompleteSampleFromCommandLine()
        {
            ImportCompleteSample();
        }
    }
}
