#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Caching.Tests
{
    // Cut 2 of docs/cultcache-publication-cut.md: the cache keeps its own observers and catches per observer, so no
    // observer decides whether another receives a change. R3 routes a subscriber's exception to the unhandled handler;
    // these tests install a fail-fast (rethrowing) handler, the only way an exception reaches the cache.
    [NonParallelizable]
    public class CultCacheObserverTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(ObservedPing) });

        private Action<Exception> _previousHandler = _ => { };

        [SetUp]
        public void FailFast()
        {
            _previousHandler = ObservableSystem.GetUnhandledExceptionHandler();
            ObservableSystem.RegisterUnhandledExceptionHandler(exception => throw exception);
        }

        [TearDown]
        public void Restore() => ObservableSystem.RegisterUnhandledExceptionHandler(_previousHandler);

        [CultDocument("tests.observed_ping", "tests.observed_ping.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class ObservedPing
        {
            [Key(0)]
            public string Text = string.Empty;
        }

        private static CultRecordKey KeyOf(string text) => new($"observed:{text}");

        private static void Send(CultCache cache, params string[] texts) =>
            cache.Commit(batch =>
            {
                foreach (var text in texts)
                    batch.Upsert(typeof(ObservedPing), new ObservedPing { Text = text }, KeyOf(text));
            });

        [Test]
        public void AnEscapedObserverExceptionReachesTheWriterAfterEveryObserverSawEveryChange()
        {
            using var cache = new CultCache(Registry);
            var thrower = new List<string>();
            var later = new List<string>();
            using var first = cache.Watch<ObservedPing>().Subscribe(change =>
            {
                thrower.Add(change.Document!.Text);
                if (change.Document.Text == "boom")
                    throw new InvalidOperationException("observer boom");
            });
            using var second = cache.Watch<ObservedPing>().Subscribe(change => later.Add(change.Document!.Text));

            var escaped = Assert.Throws<InvalidOperationException>(() => Send(cache, "boom", "after"))!;

            Assert.Multiple(() =>
            {
                Assert.That(escaped.Message, Is.EqualTo("observer boom"));
                Assert.That(later, Is.EqualTo(new[] { "boom", "after" }), "the observer after the thrower saw every change of the hold");
                Assert.That(thrower, Is.EqualTo(new[] { "boom", "after" }), "the thrower kept receiving after its own exception");
                Assert.That(cache.Get<ObservedPing>(KeyOf("boom")), Is.Not.Null, "the commit stands");
                Assert.That(cache.Get<ObservedPing>(KeyOf("after")), Is.Not.Null);
            });

            Send(cache, "later");
            Assert.That(thrower, Is.EqualTo(new[] { "boom", "after", "later" }), "the thrower stayed subscribed");
            Assert.That(later, Is.EqualTo(new[] { "boom", "after", "later" }));
        }

        [Test]
        public void SeveralEscapedObserverExceptionsArriveTogether()
        {
            using var cache = new CultCache(Registry);
            using var one = cache.Watch<ObservedPing>().Subscribe(_ => throw new InvalidOperationException("one"));
            using var two = cache.Watch<ObservedPing>().Subscribe(_ => throw new InvalidOperationException("two"));

            var escaped = Assert.Throws<AggregateException>(() => Send(cache, "boom"))!;

            Assert.That(escaped.InnerExceptions.Select(exception => exception.Message), Is.EqualTo(new[] { "one", "two" }));
        }

        [Test]
        public void ObserversMayJoinAndLeaveDuringADeliveryWithoutBreakingTheOthers()
        {
            using var cache = new CultCache(Registry);
            var a = new List<long>();
            var b = new List<long>();
            var c = new List<long>();
            var d = new List<long>();
            IDisposable? leaving = null;
            IDisposable? joined = null;
            using var first = cache.Watch<ObservedPing>().Subscribe(change =>
            {
                a.Add(change.Sequence);
                if (a.Count != 1)
                    return;
                leaving!.Dispose();
                joined = cache.Watch<ObservedPing>().Subscribe(next => c.Add(next.Sequence));
            });
            leaving = cache.Watch<ObservedPing>().Subscribe(change => b.Add(change.Sequence));
            using var last = cache.Watch<ObservedPing>().Subscribe(change => d.Add(change.Sequence));

            Send(cache, "one", "two");
            joined?.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(a, Is.EqualTo(new[] { 1L, 2L }));
                Assert.That(d, Is.EqualTo(new[] { 1L, 2L }), "an observer that did not move still saw everything");
                Assert.That(b, Does.Not.Contain(2L), "a disposed observer stops receiving");
                Assert.That(c, Is.EqualTo(new[] { 2L }), "an observer that joined mid-hold receives from the next change");
            });
        }

        [Test]
        public void DisposeCompletesObserversAndACompletedCacheCompletesNewOnes()
        {
            var cache = new CultCache(Registry);
            var completed = 0;
            using var before = cache.Watch<ObservedPing>().Subscribe(_ => { }, _ => completed++);

            cache.Dispose();
            Assert.That(completed, Is.EqualTo(1));

            using var after = cache.Watch<ObservedPing>().Subscribe(_ => { }, _ => completed++);
            Assert.That(completed, Is.EqualTo(2), "subscribing to a disposed cache completes at once");
        }
    }
}
