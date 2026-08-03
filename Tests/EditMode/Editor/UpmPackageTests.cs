using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.UI;

namespace ArkFramework.Editor.Tests
{
    public sealed class UpmPackageTests
    {
        private const string PackageId = "com.kmax.arkframework";
        private const string PackageRoot =
            "Packages/com.kmax.arkframework";

        [Test]
        public void EmbeddedPackage_ResolvesExpectedIdentity()
        {
            var packageInfo = PackageInfo.FindForAssembly(
                typeof(FrameworkHost).Assembly);

            Assert.That(packageInfo, Is.Not.Null);
            Assert.That(packageInfo.name, Is.EqualTo(PackageId));
            Assert.That(packageInfo.version, Is.EqualTo("0.1.0"));
            Assert.That(packageInfo.source, Is.EqualTo(PackageSource.Embedded));
        }

        [Test]
        public void PublishedPackage_DoesNotReferenceRepositorySamples()
        {
            var editorAssemblyPath = Path.Combine(
                PackageRoot,
                "Editor",
                "ArkFramework.Editor.asmdef");
            var assemblyDefinition = File.ReadAllText(editorAssemblyPath);

            StringAssert.DoesNotContain("ArkFramework.Samples", assemblyDefinition);
            StringAssert.DoesNotContain("Unity.Addressables.Editor", assemblyDefinition);
        }

        [Test]
        public void Package_DeclaresImportableCompleteSample()
        {
            var manifest = File.ReadAllText(PackageRoot + "/package.json");
            var sampleRoot = PackageRoot + "/Samples~/Complete Sample";

            StringAssert.Contains("\"displayName\": \"Complete Sample\"", manifest);
            StringAssert.Contains("\"path\": \"Samples~/Complete Sample\"", manifest);
            Assert.That(File.Exists(sampleRoot + "/README.md"), Is.True);
            Assert.That(
                File.Exists(sampleRoot + "/ArkFramework.Samples.asmdef"),
                Is.True);
            Assert.That(
                File.Exists(
                    sampleRoot +
                    "/Editor/ArkFramework.Samples.Editor.asmdef"),
                Is.True);
            Assert.That(
                File.Exists(
                    sampleRoot + "/Generated/Scenes/Bootstrap.unity"),
                Is.True);
        }

        [Test]
        public void Package_ContainsEnabledUpmTests()
        {
            var projectManifest = File.ReadAllText(
                "Packages/manifest.json");

            Assert.That(
                Directory.Exists(PackageRoot + "/Tests/EditMode"),
                Is.True);
            Assert.That(
                Directory.Exists(PackageRoot + "/Tests/PlayMode"),
                Is.True);
            Assert.That(
                Directory.Exists("Assets/ArkFramework/Tests"),
                Is.False);
            StringAssert.Contains("\"testables\"", projectManifest);
            StringAssert.Contains(
                "\"" + PackageId + "\"",
                projectManifest);
        }

        [Test]
        public void CompleteSample_IsImportedThroughPackageManager()
        {
            var sample = Sample.FindByPackage(PackageId, "0.1.0")
                .Single(candidate =>
                    candidate.displayName == "Complete Sample");

            Assert.That(sample.isImported, Is.True);
            StringAssert.Contains(
                "Assets/Samples/ArkFramework/0.1.0/Complete Sample",
                sample.importPath.Replace('\\', '/'));
        }
    }
}
