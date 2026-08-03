using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace ArkFramework.Tests
{
    public sealed class ObjectPoolTests
    {
        [Test]
        public void Constructor_RejectsValueTypesAndInvalidCapacities()
        {
            Assert.Throws<NotSupportedException>(
                () => new ObjectPool<int>(() => 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ObjectPool<TestItem>(
                    () => new TestItem("item"),
                    initialCapacity: -1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ObjectPool<TestItem>(
                    () => new TestItem("item"),
                    maxIdleCapacity: -1));
            Assert.Throws<ArgumentException>(
                () => new ObjectPool<TestItem>(
                    () => new TestItem("item"),
                    initialCapacity: 2,
                    maxIdleCapacity: 1));
        }

        [Test]
        public void InitialCapacity_PrewarmsIdleItemsAndInvokesCreateCallbacks()
        {
            var events = new List<string>();
            var nextId = 0;

            var pool = new ObjectPool<TestItem>(
                () => new TestItem((++nextId).ToString()),
                initialCapacity: 2,
                maxIdleCapacity: 3,
                onCreate: item => events.Add("create:" + item.Id));

            Assert.That(
                events,
                Is.EqualTo(new[] { "create:1", "create:2" }));
            Assert.That(pool.Diagnostics.TotalCreatedCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.IdleCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.PeakActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.HitRate, Is.Zero);
        }

        [Test]
        public void Rent_ReturnAndPrewarm_ReuseByReferenceAndTrackDiagnostics()
        {
            var created = 0;
            var pool = new ObjectPool<TestItem>(
                () => new TestItem((++created).ToString()),
                maxIdleCapacity: 3);

            pool.Prewarm(1);
            var first = pool.Rent();
            var second = pool.Rent();

            Assert.That(created, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.TotalCreatedCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.ActiveCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);
            Assert.That(pool.Diagnostics.PeakActiveCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.HitRate, Is.EqualTo(0.5d));

            pool.Return(first);
            var reused = pool.Rent();

            Assert.That(reused, Is.SameAs(first));
            Assert.That(pool.Diagnostics.TotalCreatedCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.ActiveCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);
            Assert.That(pool.Diagnostics.PeakActiveCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.HitRate, Is.EqualTo(2d / 3d));

            pool.Return(reused);
            pool.Return(second);
        }

        [Test]
        public void Callbacks_RunInCreateRentReturnDestroyOrder()
        {
            var events = new List<string>();
            var pool = new ObjectPool<TestItem>(
                () =>
                {
                    events.Add("factory");
                    return new TestItem("item");
                },
                maxIdleCapacity: 0,
                onCreate: _ => events.Add("create"),
                onRent: _ => events.Add("rent"),
                onReturn: _ => events.Add("return"),
                onDestroy: _ => events.Add("destroy"));

            var item = pool.Rent();
            pool.Return(item);

            Assert.That(
                events,
                Is.EqualTo(
                    new[]
                    {
                        "factory",
                        "create",
                        "rent",
                        "return",
                        "destroy"
                    }));
            Assert.That(pool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);
        }

        [Test]
        public void ReturnBeyondIdleCapacity_DestroysReturnedItem()
        {
            var destroyed = new List<TestItem>();
            var pool = new ObjectPool<TestItem>(
                () => new TestItem("item"),
                maxIdleCapacity: 1,
                onDestroy: destroyed.Add);
            var first = pool.Rent();
            var second = pool.Rent();

            pool.Return(first);
            pool.Return(second);

            Assert.That(pool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.IdleCount, Is.EqualTo(1));
            Assert.That(destroyed, Is.EqualTo(new[] { second }));
        }

        [Test]
        public void Return_RejectsDuplicateAndWrongPoolByReferenceIdentity()
        {
            var firstPool = new ObjectPool<EqualItem>(
                () => new EqualItem(),
                maxIdleCapacity: 1);
            var secondPool = new ObjectPool<EqualItem>(
                () => new EqualItem(),
                maxIdleCapacity: 1);
            var first = firstPool.Rent();
            var second = secondPool.Rent();
            Assert.That(first, Is.EqualTo(second));

            var wrongPool = Assert.Throws<InvalidOperationException>(
                () => secondPool.Return(first));
            Assert.That(wrongPool.Message, Does.Contain("different pool"));
            Assert.That(firstPool.Diagnostics.ActiveCount, Is.EqualTo(1));
            Assert.That(secondPool.Diagnostics.ActiveCount, Is.EqualTo(1));

            firstPool.Return(first);
            var duplicate = Assert.Throws<InvalidOperationException>(
                () => firstPool.Return(first));
            Assert.That(duplicate.Message, Does.Contain("already"));
            Assert.That(firstPool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(firstPool.Diagnostics.IdleCount, Is.EqualTo(1));

            secondPool.Return(second);
        }

        [Test]
        public void FactoryReturningNull_FailsWithoutChangingDiagnostics()
        {
            var pool = new ObjectPool<TestItem>(
                () => null,
                maxIdleCapacity: 1);

            Assert.Throws<InvalidOperationException>(() => pool.Rent());

            Assert.That(pool.Diagnostics.TotalCreatedCount, Is.Zero);
            Assert.That(pool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);
            Assert.That(pool.Diagnostics.PeakActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.HitRate, Is.Zero);
        }

        [Test]
        public void FactoryReturningItemOwnedByAnotherPool_DoesNotDestroyIt()
        {
            var shared = new TestItem("shared");
            var firstPool = new ObjectPool<TestItem>(
                () => shared,
                maxIdleCapacity: 1);
            var destroyedBySecondPool = 0;
            var secondPool = new ObjectPool<TestItem>(
                () => shared,
                maxIdleCapacity: 1,
                onDestroy: _ => destroyedBySecondPool++);
            var active = firstPool.Rent();

            Assert.Throws<InvalidOperationException>(() => secondPool.Rent());

            Assert.That(destroyedBySecondPool, Is.Zero);
            Assert.That(firstPool.Diagnostics.ActiveCount, Is.EqualTo(1));
            Assert.That(secondPool.Diagnostics.TotalCreatedCount, Is.Zero);
            Assert.That(secondPool.Diagnostics.ActiveCount, Is.Zero);
            firstPool.Return(active);
        }

        [Test]
        public void Clear_DestroysOnlyIdleAndPoolRemainsReusable()
        {
            var destroyed = new List<TestItem>();
            var created = 0;
            var pool = new ObjectPool<TestItem>(
                () => new TestItem((++created).ToString()),
                initialCapacity: 2,
                maxIdleCapacity: 2,
                onDestroy: destroyed.Add);
            var active = pool.Rent();

            pool.Clear();

            Assert.That(destroyed.Count, Is.EqualTo(1));
            Assert.That(destroyed[0], Is.Not.SameAs(active));
            Assert.That(pool.Diagnostics.ActiveCount, Is.EqualTo(1));
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);

            pool.Return(active);
            var reused = pool.Rent();
            Assert.That(reused, Is.SameAs(active));

            pool.Return(reused);
            pool.Clear();
            var afterClear = pool.Rent();
            Assert.That(afterClear, Is.Not.SameAs(active));
            Assert.That(created, Is.EqualTo(3));
            pool.Return(afterClear);
        }

        [Test]
        public void CallbackFailures_LeaveItemsInOneClosedOwnershipState()
        {
            var rentFailure = new TestCallbackException("rent");
            var returnFailure = new TestCallbackException("return");
            var destroyed = new List<TestItem>();
            var failRent = true;
            var failReturn = true;
            var pool = new ObjectPool<TestItem>(
                () => new TestItem("item"),
                maxIdleCapacity: 1,
                onRent: _ =>
                {
                    if (failRent)
                    {
                        throw rentFailure;
                    }
                },
                onReturn: _ =>
                {
                    if (failReturn)
                    {
                        throw returnFailure;
                    }
                },
                onDestroy: destroyed.Add);

            var thrownRent = Assert.Throws<TestCallbackException>(
                () => pool.Rent());
            Assert.That(thrownRent, Is.SameAs(rentFailure));
            Assert.That(destroyed.Count, Is.EqualTo(1));
            Assert.That(pool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);

            failRent = false;
            var item = pool.Rent();
            var thrownReturn = Assert.Throws<TestCallbackException>(
                () => pool.Return(item));
            Assert.That(thrownReturn, Is.SameAs(returnFailure));
            Assert.That(destroyed.Count, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => pool.Return(item));

            failReturn = false;
            var replacement = pool.Rent();
            pool.Return(replacement);
        }

        [Test]
        public void Clear_WhenDestroyCallbackFails_CleansEveryIdleItem()
        {
            var calls = 0;
            var failure = new TestCallbackException("destroy");
            var pool = new ObjectPool<TestItem>(
                () => new TestItem("item"),
                initialCapacity: 2,
                maxIdleCapacity: 2,
                onDestroy: _ =>
                {
                    calls++;
                    throw failure;
                });

            var thrown = Assert.Throws<TestCallbackException>(
                () => pool.Clear());

            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.ActiveCount, Is.Zero);
            Assert.That(pool.Diagnostics.IdleCount, Is.Zero);
        }

        private sealed class TestItem
        {
            public TestItem(string id)
            {
                Id = id;
            }

            public string Id { get; }
        }

        private sealed class EqualItem
        {
            public override bool Equals(object obj)
            {
                return obj is EqualItem;
            }

            public override int GetHashCode()
            {
                return 1;
            }
        }

        private sealed class TestCallbackException : Exception
        {
            public TestCallbackException(string message)
                : base(message)
            {
            }
        }
    }
}
