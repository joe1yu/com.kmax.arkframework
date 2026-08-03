using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

namespace ArkFramework.Tests
{
    public sealed class DiagnosticsPlayerCompileTests
    {
        [Test]
        [Category("PlayerCompile")]
        public void NonDevelopmentPlayerScriptsCompileWithInertOverlayBranch()
        {
            var outputDirectory = Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                "TestResults",
                "task16-player-compile",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDirectory);
            var settings = new ScriptCompilationSettings
            {
                group = BuildTargetGroup.Standalone,
                target = BuildTarget.StandaloneWindows64,
                options = (ScriptCompilationOptions)0
            };

            PlayerBuildInterface.CompilePlayerScripts(
                settings,
                outputDirectory);

            var diagnosticsAssemblies = Directory.GetFiles(
                outputDirectory,
                "ArkFramework.Diagnostics.dll",
                SearchOption.AllDirectories);
            Assert.That(diagnosticsAssemblies, Has.Length.EqualTo(1));
            var assemblyText = Encoding.ASCII.GetString(
                File.ReadAllBytes(diagnosticsAssemblies[0]));
            Assert.That(
                assemblyText,
                Does.Contain(nameof(RuntimeDebugOverlay)));
            Assert.That(assemblyText, Does.Not.Contain("PageButton."));
            Assert.That(assemblyText, Does.Not.Contain("ContentScroll"));
            Assert.That(
                assemblyText,
                Does.Not.Contain("Runtime diagnostics snapshot"));
        }
    }
}
