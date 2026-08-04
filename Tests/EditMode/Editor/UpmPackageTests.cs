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
        public void Package_ResolvesExpectedIdentity()
        {
            var packageInfo = PackageInfo.FindForAssembly(
                typeof(FrameworkHost).Assembly);

            Assert.That(packageInfo, Is.Not.Null);
            Assert.That(packageInfo.name, Is.EqualTo(PackageId));
            Assert.That(packageInfo.version, Is.EqualTo("0.1.0"));
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
            Assert.That(
                File.Exists(
                    sampleRoot +
                    "/Tests/EditMode/ArkFramework.Samples.EditModeTests.asmdef"),
                Is.True);
            Assert.That(
                File.Exists(
                    sampleRoot +
                    "/Tests/PlayMode/ArkFramework.Samples.PlayModeTests.asmdef"),
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
            Assert.That(
                File.Exists(
                    PackageRoot +
                    "/Tests/EditMode/Editor/AddressablesSampleBuilderTests.cs"),
                Is.False);
            Assert.That(
                Directory.Exists(PackageRoot + "/Tests/PlayMode/Samples"),
                Is.False);
            StringAssert.Contains("\"testables\"", projectManifest);
            StringAssert.Contains(
                "\"" + PackageId + "\"",
                projectManifest);
        }

        [Test]
        public void CompleteSample_IsDiscoverableThroughPackageManager()
        {
            var packageInfo = PackageInfo.FindForAssembly(
                typeof(FrameworkHost).Assembly);
            var sample = Sample.FindByPackage(PackageId, packageInfo.version)
                .Single(candidate =>
                    candidate.displayName == "Complete Sample");

            Assert.That(sample.displayName, Is.EqualTo("Complete Sample"));
        }
    }
}
