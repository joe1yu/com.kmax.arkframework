using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class ServiceContainerTests
    {
        [Test]
        public void SingletonFactory_IsInvokedOnce()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var invocationCount = 0;
            scope.RegisterSingleton<object>(_ =>
            {
                invocationCount++;
                return new object();
            });

            var first = container.Resolve<object>();
            var second = container.Resolve<object>();

            Assert.That(second, Is.SameAs(first));
            Assert.That(invocationCount, Is.EqualTo(1));
        }

        [Test]
        public void TransientFactory_IsInvokedForEveryResolve()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var invocationCount = 0;
            scope.RegisterTransient<object>(_ =>
            {
                invocationCount++;
                return new object();
            });

            var first = container.Resolve<object>();
            var second = container.Resolve<object>();

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(invocationCount, Is.EqualTo(2));
        }

        [Test]
        public void SingletonFactoryReturningNull_IsInvokedOnce()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var invocationCount = 0;
            scope.RegisterSingleton<SampleService>(_ =>
            {
                invocationCount++;
                return null;
            });

            Assert.That(container.Resolve<SampleService>(), Is.Null);
            Assert.That(container.Resolve<SampleService>(), Is.Null);
            Assert.That(invocationCount, Is.EqualTo(1));
        }

        [Test]
        public void TransientFactoryReturningNull_IsInvokedForEveryResolve()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var invocationCount = 0;
            scope.RegisterTransient<SampleService>(_ =>
            {
                invocationCount++;
                return null;
            });

            Assert.That(container.Resolve<SampleService>(), Is.Null);
            Assert.That(container.Resolve<SampleService>(), Is.Null);
            Assert.That(invocationCount, Is.EqualTo(2));
        }

        [Test]
        public void NullService_IsNotAddedToOwnedInstances()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterTransient<SampleService>(_ => null);
            container.Resolve<SampleService>();

            var field = typeof(ModuleScope).GetField(
                "_ownedInstances",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var ownedInstances = (ICollection)field.GetValue(scope);

            Assert.That(ownedInstances.Count, Is.Zero);
        }

        [Test]
        public void InstanceRegistration_ReturnsSameInstance()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var instance = new object();
            scope.RegisterInstance(instance);

            Assert.That(container.Resolve<object>(), Is.SameAs(instance));
        }

        [Test]
        public void DuplicateRegistration_ThrowsWithServiceType()
        {
            var container = new ServiceContainer();
            var firstScope = container.CreateScope("first");
            var secondScope = container.CreateScope("second");
            firstScope.RegisterInstance(new SampleService());

            var exception = Assert.Throws<InvalidOperationException>(
                () => secondScope.RegisterInstance(new SampleService()));

            StringAssert.Contains(typeof(SampleService).FullName, exception.Message);
        }

        [Test]
        public void MissingRegistration_ThrowsWithServiceType()
        {
            var container = new ServiceContainer();

            var exception = Assert.Throws<InvalidOperationException>(
                () => container.Resolve<SampleService>());

            StringAssert.Contains(typeof(SampleService).FullName, exception.Message);
        }

        [Test]
        public void CircularResolution_ThrowsWithResolutionPath()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterSingleton<FirstCircularService>(
                resolver => new FirstCircularService(resolver.Resolve<SecondCircularService>()));
            scope.RegisterSingleton<SecondCircularService>(
                resolver => new SecondCircularService(resolver.Resolve<FirstCircularService>()));

            var exception = Assert.Throws<InvalidOperationException>(
                () => container.Resolve<FirstCircularService>());

            var expectedPath =
                $"{typeof(FirstCircularService).FullName} -> " +
                $"{typeof(SecondCircularService).FullName} -> " +
                typeof(FirstCircularService).FullName;
            StringAssert.Contains(expectedPath, exception.Message);
        }

        [Test]
        public void RemovingScope_DisposesOwnedServicesInReverseOrder()
        {
            var disposalOrder = new List<string>();
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterTransient<FirstOwnedService>(
                _ => new FirstOwnedService(new DisposableProbe("first", disposalOrder)));
            scope.RegisterTransient<SecondOwnedService>(
                _ => new SecondOwnedService(new AsyncDisposableProbe("second", disposalOrder)));
            container.Resolve<FirstOwnedService>();
            container.Resolve<SecondOwnedService>();

            Await(scope.DisposeAsync());

            Assert.That(disposalOrder, Is.EqualTo(new[] { "second", "first" }));
        }

        [UnityTest]
        public IEnumerator RemovingScope_AwaitsIncompleteAsyncDisposal()
        {
            var disposalOrder = new List<string>();
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var asyncProbe = new GatedAsyncDisposableProbe(
                "async-started",
                "async-finished",
                disposalOrder);
            scope.RegisterInstance<IDisposable>(
                new DisposableProbe("sync", disposalOrder));
            scope.RegisterInstance(asyncProbe);

            var disposeTask = scope.DisposeAsync().AsTask();

            Assert.That(disposeTask.IsCompleted, Is.False);
            Assert.That(disposalOrder, Is.EqualTo(new[] { "async-started" }));
            yield return null;
            Assert.That(disposeTask.IsCompleted, Is.False);
            Assert.That(disposalOrder, Is.EqualTo(new[] { "async-started" }));

            asyncProbe.Complete();
            for (var frame = 0; frame < 60 && !disposeTask.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(disposeTask.IsCompleted, Is.True);
            disposeTask.GetAwaiter().GetResult();
            Assert.That(
                disposalOrder,
                Is.EqualTo(new[] { "async-started", "async-finished", "sync" }));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t")]
        public void CreateScope_InvalidOwnerId_Throws(string ownerId)
        {
            var container = new ServiceContainer();

            Assert.Throws<ArgumentException>(() => container.CreateScope(ownerId));
        }

        [Test]
        public void DisposedScope_CannotRegister()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            Await(scope.DisposeAsync());

            Assert.Throws<ObjectDisposedException>(
                () => scope.RegisterInstance(new SampleService()));
        }

        [Test]
        public void DisposedScope_RemovesRegistrations()
        {
            var container = new ServiceContainer();
            var firstScope = container.CreateScope("first");
            firstScope.RegisterInstance(new SampleService());
            Await(firstScope.DisposeAsync());
            var secondScope = container.CreateScope("second");

            Assert.DoesNotThrow(() => secondScope.RegisterInstance(new SampleService()));
        }

        [Test]
        public void SingletonFactoryFailure_IsNotCached()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var invocationCount = 0;
            scope.RegisterSingleton<SampleService>(_ =>
            {
                invocationCount++;
                throw new TestFactoryException();
            });

            Assert.Throws<TestFactoryException>(() => container.Resolve<SampleService>());
            Assert.Throws<TestFactoryException>(() => container.Resolve<SampleService>());
            Assert.That(invocationCount, Is.EqualTo(2));
        }

        [Test]
        public void TryResolve_MissingRegistration_ReturnsFalse()
        {
            var container = new ServiceContainer();

            var resolved = container.TryResolve<SampleService>(out var service);

            Assert.That(resolved, Is.False);
            Assert.That(service, Is.Null);
        }

        [Test]
        public void TryResolve_FactoryException_IsPropagated()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterTransient<SampleService>(_ => throw new TestFactoryException());

            Assert.Throws<TestFactoryException>(
                () => container.TryResolve<SampleService>(out _));
        }

        [Test]
        public void TryResolve_CircularResolution_IsPropagated()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterSingleton<FirstCircularService>(
                resolver => new FirstCircularService(resolver.Resolve<SecondCircularService>()));
            scope.RegisterSingleton<SecondCircularService>(
                resolver => new SecondCircularService(resolver.Resolve<FirstCircularService>()));

            Assert.Throws<InvalidOperationException>(
                () => container.TryResolve<FirstCircularService>(out _));
        }

        [Test]
        public void TryResolve_DisposedRegistration_IsPropagated()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            var probe = new ResolveDuringDisposeProbe(
                () => container.TryResolve<SampleService>(out _));
            scope.RegisterInstance(probe);
            scope.RegisterInstance(new SampleService());

            Await(scope.DisposeAsync());

            Assert.That(probe.Exception, Is.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public void SameOwnedReference_IsDisposedOnce()
        {
            var disposalOrder = new List<string>();
            var instance = new DisposableProbe("instance", disposalOrder);
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterInstance<IDisposable>(instance);
            scope.RegisterInstance(instance);

            Await(scope.DisposeAsync());

            Assert.That(disposalOrder, Is.EqualTo(new[] { "instance" }));
        }

        [Test]
        public void OwnAndRegisteredService_SameReference_IsDisposedOnce()
        {
            var disposalOrder = new List<string>();
            var instance = new DisposableProbe("instance", disposalOrder);
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterInstance<IDisposable>(instance);

            Assert.That(scope.Own(instance), Is.SameAs(instance));
            Await(scope.DisposeAsync());

            Assert.That(disposalOrder, Is.EqualTo(new[] { "instance" }));
        }

        [Test]
        public void Own_NullInstance_IsRejected()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");

            Assert.Throws<ArgumentNullException>(
                () => scope.Own<SampleService>(null));
        }

        [Test]
        public void Own_DisposedScope_IsRejected()
        {
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            Await(scope.DisposeAsync());

            Assert.Throws<ObjectDisposedException>(
                () => scope.Own(new SampleService()));
        }

        [Test]
        public void Own_DisposesInReverseOrderAndContinuesAfterFailure()
        {
            var disposalOrder = new List<string>();
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.Own(new DisposableProbe("first", disposalOrder));
            scope.Own(new ThrowingDisposableProbe("second", disposalOrder));

            Assert.Throws<TestDisposalException>(
                () => Await(scope.DisposeAsync()));

            Assert.That(disposalOrder, Is.EqualTo(new[] { "second", "first" }));
        }

        [Test]
        public void DisposalFailure_DoesNotSkipRemainingOwnedServices()
        {
            var disposalOrder = new List<string>();
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterInstance<IDisposable>(
                new DisposableProbe("first", disposalOrder));
            scope.RegisterInstance(
                new ThrowingDisposableProbe("second", disposalOrder));

            Assert.Throws<TestDisposalException>(() => Await(scope.DisposeAsync()));

            Assert.That(disposalOrder, Is.EqualTo(new[] { "second", "first" }));
        }

        [Test]
        public void DisposalFailure_StillRemovesRegistrations()
        {
            var disposalOrder = new List<string>();
            var container = new ServiceContainer();
            var firstScope = container.CreateScope("first");
            firstScope.RegisterInstance(
                new ThrowingDisposableProbe("first", disposalOrder));
            Assert.Throws<TestDisposalException>(
                () => Await(firstScope.DisposeAsync()));
            var secondScope = container.CreateScope("second");

            Assert.DoesNotThrow(
                () => secondScope.RegisterInstance(
                    new ThrowingDisposableProbe("second", disposalOrder)));
        }

        [Test]
        public void ScopeDisposal_DuringFactoryResolution_IsRejected()
        {
            var disposalOrder = new List<string>();
            var container = new ServiceContainer();
            var scope = container.CreateScope("module");
            scope.RegisterSingleton<DisposableProbe>(_ =>
            {
                Assert.Throws<InvalidOperationException>(
                    () => Await(scope.DisposeAsync()));
                return new DisposableProbe("service", disposalOrder);
            });

            var service = container.Resolve<DisposableProbe>();
            Assert.That(container.Resolve<DisposableProbe>(), Is.SameAs(service));
            Await(scope.DisposeAsync());

            Assert.That(disposalOrder, Is.EqualTo(new[] { "service" }));
        }

        private sealed class SampleService
        {
        }

        private static void Await(ValueTask operation)
        {
            operation.AsTask().GetAwaiter().GetResult();
        }

        private sealed class FirstCircularService
        {
            public FirstCircularService(SecondCircularService dependency)
            {
            }
        }

        private sealed class SecondCircularService
        {
            public SecondCircularService(FirstCircularService dependency)
            {
            }
        }

        private sealed class FirstOwnedService : IDisposable
        {
            private readonly IDisposable _probe;

            public FirstOwnedService(IDisposable probe)
            {
                _probe = probe;
            }

            public void Dispose()
            {
                _probe.Dispose();
            }
        }

        private sealed class SecondOwnedService : IAsyncDisposable
        {
            private readonly IAsyncDisposable _probe;

            public SecondOwnedService(IAsyncDisposable probe)
            {
                _probe = probe;
            }

            public ValueTask DisposeAsync()
            {
                return _probe.DisposeAsync();
            }
        }

        private sealed class DisposableProbe : IDisposable
        {
            private readonly string _name;
            private readonly IList<string> _disposalOrder;

            public DisposableProbe(string name, IList<string> disposalOrder)
            {
                _name = name;
                _disposalOrder = disposalOrder;
            }

            public void Dispose()
            {
                _disposalOrder.Add(_name);
            }
        }

        private sealed class AsyncDisposableProbe : IAsyncDisposable
        {
            private readonly string _name;
            private readonly IList<string> _disposalOrder;

            public AsyncDisposableProbe(string name, IList<string> disposalOrder)
            {
                _name = name;
                _disposalOrder = disposalOrder;
            }

            public ValueTask DisposeAsync()
            {
                _disposalOrder.Add(_name);
                return default;
            }
        }

        private sealed class GatedAsyncDisposableProbe : IAsyncDisposable
        {
            private readonly string _startedName;
            private readonly string _finishedName;
            private readonly IList<string> _disposalOrder;
            private readonly TaskCompletionSource<bool> _completion =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public GatedAsyncDisposableProbe(
                string startedName,
                string finishedName,
                IList<string> disposalOrder)
            {
                _startedName = startedName;
                _finishedName = finishedName;
                _disposalOrder = disposalOrder;
            }

            public ValueTask DisposeAsync()
            {
                return new ValueTask(DisposeAfterCompletionAsync());
            }

            public void Complete()
            {
                _completion.SetResult(true);
            }

            private async Task DisposeAfterCompletionAsync()
            {
                _disposalOrder.Add(_startedName);
                await _completion.Task;
                _disposalOrder.Add(_finishedName);
            }
        }

        private sealed class ThrowingDisposableProbe : IDisposable
        {
            private readonly string _name;
            private readonly IList<string> _disposalOrder;

            public ThrowingDisposableProbe(string name, IList<string> disposalOrder)
            {
                _name = name;
                _disposalOrder = disposalOrder;
            }

            public void Dispose()
            {
                _disposalOrder.Add(_name);
                throw new TestDisposalException();
            }
        }

        private sealed class ResolveDuringDisposeProbe : IAsyncDisposable
        {
            private readonly Action _resolve;

            public ResolveDuringDisposeProbe(Action resolve)
            {
                _resolve = resolve;
            }

            public Exception Exception { get; private set; }

            public ValueTask DisposeAsync()
            {
                try
                {
                    _resolve();
                }
                catch (Exception exception)
                {
                    Exception = exception;
                }

                return default;
            }
        }

        private sealed class TestFactoryException : Exception
        {
        }

        private sealed class TestDisposalException : Exception
        {
        }
    }
}
