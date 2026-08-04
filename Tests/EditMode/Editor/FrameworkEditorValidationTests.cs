using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ArkFramework.Editor;
using NUnit.Framework;
using UnityEngine;

namespace ArkFramework.Editor.Tests
{
    public sealed class FrameworkEditorValidationTests
    {
        private readonly List<UnityEngine.Object> _objects =
            new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (var index = _objects.Count - 1; index >= 0; index--)
            {
                UnityEngine.Object.DestroyImmediate(_objects[index]);
            }

            _objects.Clear();
        }

        [Test]
        public void Validate_RejectsDuplicateModuleId()
        {
            var result = Validate(
                Installer("Core"),
                Installer("Core"));

            AssertIssue(result, FrameworkEditorIssueCodes.DuplicateModuleId);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_RejectsMissingDependency()
        {
            var result = Validate(Installer("UI", new[] { "Resource" }));

            AssertIssue(result, FrameworkEditorIssueCodes.MissingDependency);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_RejectsDependencyCycle()
        {
            var result = Validate(
                Installer("A", new[] { "B" }),
                Installer("B", new[] { "C" }),
                Installer("C", new[] { "A" }));

            var issue = AssertIssue(
                result,
                FrameworkEditorIssueCodes.DependencyCycle);
            StringAssert.Contains("A", issue.Message);
            StringAssert.Contains("B", issue.Message);
            StringAssert.Contains("C", issue.Message);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_RejectsDuplicateServiceWithinAndAcrossInstallers()
        {
            var result = Validate(
                Installer(
                    "Core",
                    services: new[] { typeof(IService), typeof(IService) }),
                Installer("Feature", services: new[] { typeof(IService) }));

            Assert.That(
                result.Issues.Count(
                    issue => issue.Code ==
                        FrameworkEditorIssueCodes.DuplicateServiceDeclaration),
                Is.EqualTo(2));
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_ComputesStableTopologicalStartupOrder()
        {
            var result = Validate(
                Installer("Resource", new[] { "Core" }),
                Installer("UI", new[] { "Core" }),
                Installer("Core"),
                Installer("Pool", new[] { "Core" }));

            Assert.That(result.IsValid, Is.True);
            CollectionAssert.AreEqual(
                new[] { "Core", "Resource", "UI", "Pool" },
                result.StartupOrder);
        }

        [Test]
        public void Validate_ReturnsReadOnlySnapshots()
        {
            var dependencies = new List<string>();
            var installer = Installer("Core", dependencies);
            var result = Validate(installer);

            installer.Id = "Changed";
            dependencies.Add("Missing");

            CollectionAssert.AreEqual(new[] { "Core" }, result.StartupOrder);
            Assert.Throws<NotSupportedException>(
                () => ((IList<string>)result.StartupOrder).Add("Other"));
            Assert.Throws<NotSupportedException>(
                () => ((IList<FrameworkEditorIssue>)result.Issues).Add(null));
        }

        [Test]
        public void Validate_ReportsNullInstaller()
        {
            var result = Validate((ModuleInstaller)null);

            var issue = AssertIssue(
                result,
                FrameworkEditorIssueCodes.NullInstaller);
            Assert.That(issue.InstallerIndex, Is.EqualTo(0));
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [TestCase(MetadataFailure.ModuleId)]
        [TestCase(MetadataFailure.Dependencies)]
        [TestCase(MetadataFailure.ServiceTypes)]
        public void Validate_ConvertsThrowingMetadataToIssue(
            MetadataFailure failure)
        {
            var installer = Installer("Core");
            installer.Failure = failure;

            Assert.DoesNotThrow(() => Validate(installer));
            var result = Validate(installer);

            var issue = AssertIssue(
                result,
                FrameworkEditorIssueCodes.MetadataAccessFailed);
            Assert.That(issue.InstallerIndex, Is.EqualTo(0));
            StringAssert.Contains(failure.ToString(), issue.Message);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_DoesNotCreateModules()
        {
            var installer = Installer("Core");

            var result = Validate(installer);

            Assert.That(result.IsValid, Is.True);
            Assert.That(installer.CreateModuleCallCount, Is.Zero);
        }

        [Test]
        public void Validate_RejectsInvalidIdentifiersAndDependencyMetadata()
        {
            var result = Validate(
                Installer(" "),
                Installer("NullDependencies", dependencies: null),
                Installer(
                    "BadDependencies",
                    new[] { null, " ", "BadDependencies", "Core", "Core" }),
                Installer("Core"));

            AssertIssue(result, FrameworkEditorIssueCodes.InvalidModuleId);
            AssertIssue(result, FrameworkEditorIssueCodes.NullDependencies);
            Assert.That(
                result.Issues.Count(
                    issue => issue.Code ==
                        FrameworkEditorIssueCodes.InvalidDependencyId),
                Is.EqualTo(2));
            AssertIssue(result, FrameworkEditorIssueCodes.SelfDependency);
            AssertIssue(result, FrameworkEditorIssueCodes.DuplicateDependency);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_RejectsNullServiceDeclaration()
        {
            var result = Validate(
                Installer("Core", services: new Type[] { null }));

            AssertIssue(result, FrameworkEditorIssueCodes.NullServiceType);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_RejectsNullServiceTypesCollection()
        {
            var installer = Installer("Core");
            installer.DeclaredServices = null;

            var result = Validate(installer);

            AssertIssue(result, FrameworkEditorIssueCodes.NullServiceTypes);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [TestCase(CollectionMetadata.Dependencies, CollectionFailure.Count)]
        [TestCase(
            CollectionMetadata.Dependencies,
            CollectionFailure.GetEnumerator)]
        [TestCase(CollectionMetadata.Dependencies, CollectionFailure.MoveNext)]
        [TestCase(CollectionMetadata.Dependencies, CollectionFailure.Current)]
        [TestCase(CollectionMetadata.Dependencies, CollectionFailure.Dispose)]
        [TestCase(CollectionMetadata.ServiceTypes, CollectionFailure.Count)]
        [TestCase(
            CollectionMetadata.ServiceTypes,
            CollectionFailure.GetEnumerator)]
        [TestCase(CollectionMetadata.ServiceTypes, CollectionFailure.MoveNext)]
        [TestCase(CollectionMetadata.ServiceTypes, CollectionFailure.Current)]
        [TestCase(CollectionMetadata.ServiceTypes, CollectionFailure.Dispose)]
        public void Validate_ConvertsThrowingCollectionToMetadataIssue(
            CollectionMetadata metadata,
            CollectionFailure failure)
        {
            var installer = Installer("Feature");
            if (metadata == CollectionMetadata.Dependencies)
            {
                installer.DependencyIds =
                    new ThrowingCollection<string>(failure, "Core");
            }
            else
            {
                installer.DeclaredServices =
                    new ThrowingCollection<Type>(failure, typeof(IService));
            }

            FrameworkEditorValidationResult result = null;
            Assert.DoesNotThrow(
                () => result = Validate(Installer("Core"), installer));

            var issue = AssertIssue(
                result,
                FrameworkEditorIssueCodes.MetadataAccessFailed);
            StringAssert.Contains(metadata.ToString(), issue.Message);
            Assert.That(result.StartupOrder, Is.Empty);
        }

        [Test]
        public void Validate_NullProfileThrows()
        {
            Assert.Throws<ArgumentNullException>(
                () => FrameworkEditorValidation.Validate(null));
        }

        [Test]
        public void Validate_OrdersIssuesDeterministically()
        {
            var result = Validate(
                Installer("UI", new[] { "Missing" }),
                Installer("UI"));

            CollectionAssert.AreEqual(
                new[]
                {
                    FrameworkEditorIssueCodes.DuplicateModuleId,
                    FrameworkEditorIssueCodes.MissingDependency
                },
                result.Issues.Select(issue => issue.Code));
        }

        [Test]
        public void DefaultInstallers_MatchModuleContracts()
        {
            foreach (var expectation in DefaultInstallerExpectations)
            {
                var installer = Create(expectation.InstallerType);

                Assert.That(
                    installer.ModuleId,
                    Is.EqualTo(expectation.ModuleId),
                    expectation.InstallerType.Name);
                CollectionAssert.AreEqual(
                    expectation.Dependencies,
                    installer.Dependencies,
                    expectation.InstallerType.Name);
                CollectionAssert.AreEqual(
                    expectation.ServiceTypes,
                    installer.ServiceTypes,
                    expectation.InstallerType.Name);

                var first = installer.CreateModule();
                var second = installer.CreateModule();
                Assert.That(first, Is.TypeOf(expectation.ModuleType));
                Assert.That(second, Is.TypeOf(expectation.ModuleType));
                Assert.That(second, Is.Not.SameAs(first));
                Assert.That(first.Id, Is.EqualTo(installer.ModuleId));
                CollectionAssert.AreEqual(
                    installer.Dependencies,
                    first.Dependencies);

                var menu = expectation.InstallerType
                    .GetCustomAttribute<CreateAssetMenuAttribute>();
                Assert.That(menu, Is.Not.Null);
                Assert.That(
                    menu.menuName,
                    Is.EqualTo(expectation.MenuName));
            }
        }

        [Test]
        public void DefaultInstallers_CreateValidCompleteProfile()
        {
            var installers = DefaultInstallerExpectations
                .Select(expectation => Create(expectation.InstallerType))
                .ToArray();
            var profile = Create<FrameworkProfile>();
            SetInstallers(profile, installers);

            var validation = FrameworkEditorValidation.Validate(profile);
            var descriptors = profile.CreateDescriptors();
            var sorted = ModuleGraph.Sort(descriptors);

            Assert.That(validation.IsValid, Is.True);
            Assert.That(descriptors, Has.Count.EqualTo(13));
            Assert.That(sorted, Has.Count.EqualTo(13));
            Assert.That(
                installers.SelectMany(installer => installer.ServiceTypes)
                    .Distinct()
                    .Count(),
                Is.EqualTo(
                    installers.Sum(installer => installer.ServiceTypes.Count)));
        }

        private FrameworkEditorValidationResult Validate(
            params ModuleInstaller[] installers)
        {
            var profile = Create<FrameworkProfile>();
            SetInstallers(profile, installers);
            return FrameworkEditorValidation.Validate(profile);
        }

        private FakeInstaller Installer(
            string id,
            IReadOnlyCollection<string> dependencies = default,
            IReadOnlyCollection<Type> services = default)
        {
            var installer = Create<FakeInstaller>();
            installer.Id = id;
            installer.DependencyIds = dependencies ?? Array.Empty<string>();
            installer.DeclaredServices = services ?? Array.Empty<Type>();
            installer.DependenciesWereExplicitlyNull = dependencies == null &&
                string.Equals(id, "NullDependencies", StringComparison.Ordinal);
            return installer;
        }

        private T Create<T>() where T : ScriptableObject
        {
            var value = ScriptableObject.CreateInstance<T>();
            _objects.Add(value);
            return value;
        }

        private ModuleInstaller Create(Type installerType)
        {
            var value = (ModuleInstaller)ScriptableObject.CreateInstance(
                installerType);
            _objects.Add(value);
            return value;
        }

        private static FrameworkEditorIssue AssertIssue(
            FrameworkEditorValidationResult result,
            string code)
        {
            var issue = result.Issues.FirstOrDefault(
                candidate => candidate.Code == code);
            Assert.That(issue, Is.Not.Null, $"Expected issue code '{code}'.");
            Assert.That(issue.Severity, Is.EqualTo(FrameworkEditorIssueSeverity.Error));
            return issue;
        }

        private static void SetInstallers(
            FrameworkProfile profile,
            IEnumerable<ModuleInstaller> installers)
        {
            var field = typeof(FrameworkProfile).GetField(
                "_installers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(profile, new List<ModuleInstaller>(installers));
        }

        private interface IService
        {
        }

        private static readonly InstallerExpectation[]
            DefaultInstallerExpectations =
            {
                new InstallerExpectation(
                    typeof(EventBusModuleInstaller),
                    typeof(EventBusModule),
                    BuiltInModuleIds.EventBus,
                    Array.Empty<string>(),
                    new[] { typeof(IEventBus) },
                    "ArkFramework/Modules/Event Bus"),
                new InstallerExpectation(
                    typeof(PlatformModuleInstaller),
                    typeof(PlatformModule),
                    BuiltInModuleIds.Platform,
                    Array.Empty<string>(),
                    new[] { typeof(IPlatformService) },
                    "ArkFramework/Modules/Platform"),
                new InstallerExpectation(
                    typeof(RigModuleInstaller),
                    typeof(RigModule),
                    BuiltInModuleIds.Rig,
                    new[]
                    {
                        BuiltInModuleIds.Platform,
                        BuiltInModuleIds.EventBus
                    },
                    new[] { typeof(IRigService) },
                    "ArkFramework/Modules/Rig"),
                new InstallerExpectation(
                    typeof(ResourceModuleInstaller),
                    typeof(ResourceModule),
                    BuiltInModuleIds.Resource,
                    Array.Empty<string>(),
                    new[]
                    {
                        typeof(IResourceService),
                        typeof(ISceneResourceLoader),
                        typeof(ISceneTransactionResourceLoader)
                    },
                    "ArkFramework/Modules/Resource"),
                new InstallerExpectation(
                    typeof(PoolModuleInstaller),
                    typeof(PoolModule),
                    BuiltInModuleIds.Pool,
                    new[] { BuiltInModuleIds.Resource },
                    new[] { typeof(IGameObjectPool) },
                    "ArkFramework/Modules/Pool"),
                new InstallerExpectation(
                    typeof(ConfigModuleInstaller),
                    typeof(ConfigModule),
                    BuiltInModuleIds.Config,
                    new[]
                    {
                        BuiltInModuleIds.Resource,
                        BuiltInModuleIds.EventBus
                    },
                    new[] { typeof(IConfigService) },
                    "ArkFramework/Modules/Config"),
                new InstallerExpectation(
                    typeof(FsmModuleInstaller),
                    typeof(FsmModule),
                    BuiltInModuleIds.Fsm,
                    Array.Empty<string>(),
                    new[] { typeof(IFsmService) },
                    "ArkFramework/Modules/FSM"),
                new InstallerExpectation(
                    typeof(SceneModuleInstaller),
                    typeof(SceneModule),
                    BuiltInModuleIds.Scene,
                    new[]
                    {
                        BuiltInModuleIds.Resource,
                        BuiltInModuleIds.EventBus,
                        BuiltInModuleIds.Table
                    },
                    new[] { typeof(ISceneService) },
                    "ArkFramework/Modules/Scene"),
                new InstallerExpectation(
                    typeof(UIModuleInstaller),
                    typeof(UIModule),
                    BuiltInModuleIds.UI,
                    new[]
                    {
                        BuiltInModuleIds.Resource,
                        BuiltInModuleIds.Pool,
                        BuiltInModuleIds.EventBus
                    },
                    new[] { typeof(IUIService) },
                    "ArkFramework/Modules/UI"),
                new InstallerExpectation(
                    typeof(AudioModuleInstaller),
                    typeof(AudioModule),
                    BuiltInModuleIds.Audio,
                    new[]
                    {
                        BuiltInModuleIds.Resource,
                        BuiltInModuleIds.Pool
                    },
                    new[] { typeof(IAudioService) },
                    "ArkFramework/Modules/Audio"),
                new InstallerExpectation(
                    typeof(ActionKitModuleInstaller),
                    typeof(ActionKitModule),
                    BuiltInModuleIds.ActionKit,
                    Array.Empty<string>(),
                    new[] { typeof(IActionService) },
                    "ArkFramework/Modules/ActionKit"),
                new InstallerExpectation(
                    typeof(TableModuleInstaller),
                    typeof(TableModule),
                    BuiltInModuleIds.Table,
                    Array.Empty<string>(),
                    new[] { typeof(ITableService) },
                    "ArkFramework/Modules/Table"),
                new InstallerExpectation(
                    typeof(ProcedureModuleInstaller),
                    typeof(ProcedureModule),
                    BuiltInModuleIds.Procedure,
                    new[]
                    {
                        BuiltInModuleIds.Fsm,
                        BuiltInModuleIds.Config,
                        BuiltInModuleIds.Scene,
                        BuiltInModuleIds.UI,
                        BuiltInModuleIds.Audio
                    },
                    new[] { typeof(IProcedureService) },
                    "ArkFramework/Modules/Procedure")
            };

        private sealed class InstallerExpectation
        {
            public InstallerExpectation(
                Type installerType,
                Type moduleType,
                string moduleId,
                IReadOnlyCollection<string> dependencies,
                IReadOnlyCollection<Type> serviceTypes,
                string menuName)
            {
                InstallerType = installerType;
                ModuleType = moduleType;
                ModuleId = moduleId;
                Dependencies = dependencies;
                ServiceTypes = serviceTypes;
                MenuName = menuName;
            }

            public Type InstallerType { get; }
            public Type ModuleType { get; }
            public string ModuleId { get; }
            public IReadOnlyCollection<string> Dependencies { get; }
            public IReadOnlyCollection<Type> ServiceTypes { get; }
            public string MenuName { get; }
        }

        public enum MetadataFailure
        {
            None,
            ModuleId,
            Dependencies,
            ServiceTypes
        }

        public enum CollectionMetadata
        {
            Dependencies,
            ServiceTypes
        }

        public enum CollectionFailure
        {
            Count,
            GetEnumerator,
            MoveNext,
            Current,
            Dispose
        }

        private sealed class FakeInstaller : ModuleInstaller
        {
            public string Id { get; set; }

            public IReadOnlyCollection<string> DependencyIds { get; set; }

            public IReadOnlyCollection<Type> DeclaredServices { get; set; }

            public bool DependenciesWereExplicitlyNull { get; set; }

            public MetadataFailure Failure { get; set; }

            public int CreateModuleCallCount { get; private set; }

            public override string ModuleId
            {
                get
                {
                    ThrowIf(MetadataFailure.ModuleId);
                    return Id;
                }
            }

            public override IReadOnlyCollection<string> Dependencies
            {
                get
                {
                    ThrowIf(MetadataFailure.Dependencies);
                    return DependenciesWereExplicitlyNull
                        ? null
                        : DependencyIds;
                }
            }

            public override IReadOnlyCollection<Type> ServiceTypes
            {
                get
                {
                    ThrowIf(MetadataFailure.ServiceTypes);
                    return DeclaredServices;
                }
            }

            public override IFrameworkModule CreateModule()
            {
                CreateModuleCallCount++;
                throw new InvalidOperationException(
                    "Validation must not create modules.");
            }

            private void ThrowIf(MetadataFailure failure)
            {
                if (Failure == failure)
                {
                    throw new InvalidOperationException(
                        $"{failure} getter failed.");
                }
            }
        }

        private sealed class ThrowingCollection<T> : IReadOnlyCollection<T>
        {
            private readonly CollectionFailure _failure;
            private readonly T _value;

            public ThrowingCollection(CollectionFailure failure, T value)
            {
                _failure = failure;
                _value = value;
            }

            public int Count
            {
                get
                {
                    ThrowIf(CollectionFailure.Count);
                    return 1;
                }
            }

            public IEnumerator<T> GetEnumerator()
            {
                ThrowIf(CollectionFailure.GetEnumerator);
                return new ThrowingEnumerator<T>(_failure, _value);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            private void ThrowIf(CollectionFailure failure)
            {
                if (_failure == failure)
                {
                    throw new InvalidOperationException(
                        $"{failure} failed.");
                }
            }
        }

        private sealed class ThrowingEnumerator<T> : IEnumerator<T>
        {
            private readonly CollectionFailure _failure;
            private readonly T _value;
            private bool _moved;

            public ThrowingEnumerator(CollectionFailure failure, T value)
            {
                _failure = failure;
                _value = value;
            }

            public T Current
            {
                get
                {
                    ThrowIf(CollectionFailure.Current);
                    return _value;
                }
            }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                ThrowIf(CollectionFailure.MoveNext);
                if (_moved)
                {
                    return false;
                }

                _moved = true;
                return true;
            }

            public void Reset()
            {
                _moved = false;
            }

            public void Dispose()
            {
                ThrowIf(CollectionFailure.Dispose);
            }

            private void ThrowIf(CollectionFailure failure)
            {
                if (_failure == failure)
                {
                    throw new InvalidOperationException(
                        $"{failure} failed.");
                }
            }
        }
    }
}
