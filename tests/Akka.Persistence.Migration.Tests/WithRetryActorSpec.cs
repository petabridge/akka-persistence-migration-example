using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Persistence.Migration.Actors;
using Akka.Persistence.Migration.Tests.Infrastructure;
using Akka.Persistence.Query.InMemory;
using Akka.Persistence.TestKit;
using FluentAssertions.Extensions;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Akka.Persistence.Migration.Tests;

public class WithRetryActorSpec: Akka.Hosting.TestKit.TestKit
{
    private readonly MsSqliteContainer _fixture;
    private ITestSnapshotStore? _snapshots;
    private ITestJournal? _journal;

    public WithRetryActorSpec(ITestOutputHelper output)
        : base(nameof(PersistenceWriterActorSpec), output)
    {
        _fixture = new MsSqliteContainer();
        if (!_fixture.InitializeAsync().Wait(5.Seconds()))
            throw new TestTimeoutException(5000);
    }

    #region Test hosting setup

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            // Setup fixture
            .AddHocon(
                ConfigurationFactory.FromResource<TestSnapshotStore>("Akka.Persistence.TestKit.config.conf"),
                HoconAddMode.Append)
            .AddHocon(InMemoryReadJournal.DefaultConfiguration(), HoconAddMode.Append)
            .AddHocon(
                "akka.persistence.query.journal.inmem.write-plugin = akka.persistence.journal.test",
                HoconAddMode.Prepend);
    }

    protected override async Task BeforeTestStart()
    {
        await base.BeforeTestStart();
        
        var persistence = Persistence.Instance.Apply(Sys);
        var snapshotActor = persistence.SnapshotStoreFor("akka.persistence.snapshot-store.test");
        _snapshots = TestSnapshotStore.FromRef(snapshotActor);
        
        var journalActor = persistence.JournalFor("akka.persistence.journal.test");
        _journal = TestJournal.FromRef(journalActor);
    }

    protected override async Task AfterAllAsync()
    {
        await base.AfterAllAsync();
        await _fixture.DisposeAsync();
    }

    public class TestRetryActor: ReceivePersistenceWithRetryActor<int, int>
    {
        public override string PersistenceId { get; }
        protected override int MaxRetries => 3;
        protected override TimeSpan RetryInterval => 200.Milliseconds();
        
        private readonly IActorRef _probe;

        public TestRetryActor(string persistenceId, IActorRef probe)
        {
            PersistenceId = persistenceId;
            _probe = probe;
            
            Command<DoPersist>(_ => PersistWithRetry(0, _ =>
            {
                _probe.Tell(new PersistSuccess());
            }));
            
            Command<DoSnapshot>(_ => SaveSnapshotWithRetry(0));
            
            CommandRetry();
        }
        
        protected override void OnPersistFailure(Exception cause)
        {
            _probe.Tell(new PersistFailure());
        }

        protected override void OnSaveSnapshotSuccess(SaveSnapshotSuccess success)
        {
            _probe.Tell(new SnapshotSuccess());
        }

        protected override void OnSaveSnapshotFailure(Exception cause)
        {
            _probe.Tell(new SnapshotFailure());
        }

        public sealed record DoPersist;
        public sealed record PersistFailure;
        public sealed record PersistSuccess;

        public sealed record DoSnapshot;
        public sealed record SnapshotFailure;
        public sealed record SnapshotSuccess;
    }
    
    #endregion

    [Fact(DisplayName = "WithRetryActor should normally succeed on persist")]
    public async Task PersistSuccessTest()
    {
        var testActor = Sys.ActorOf(Props.Create(() => new TestRetryActor("a", TestActor)), "testActor");
        testActor.Tell(new TestRetryActor.DoPersist());
        await ExpectMsgAsync<TestRetryActor.PersistSuccess>();
    }
    
    [Fact(DisplayName = "WithRetryActor should fail immediately on persist fail")]
    public async Task PersistFailTest()
    {
        await WithJournalWrite(
            behavior => behavior.SetInterceptorAsync(new RepeatJournalFail(1, NoopJournal.Instance)), 
            async () =>
            {
                var testActor = Sys.ActorOf(Props.Create(() => new TestRetryActor("a", TestActor)), "testActor");
                testActor.Tell(new TestRetryActor.DoPersist());
                await ExpectMsgAsync<TestRetryActor.PersistFailure>();
            });
    }

    [Fact(DisplayName = "WithRetryActor should retry persist reject")]
    public async Task PersistRejectRetryTest()
    {
        await WithJournalWrite(
            behavior => behavior.SetInterceptorAsync(new RepeatJournalReject(2, NoopJournal.Instance)), 
            async () =>
            {
                var testActor = Sys.ActorOf(Props.Create(() => new TestRetryActor("a", TestActor)), "testActor");
                testActor.Tell(new TestRetryActor.DoPersist());
                await ExpectMsgAsync<TestRetryActor.PersistSuccess>();
            });
    }

    [Fact(DisplayName = "WithRetryActor should fail when exceeds persist reject max retries")]
    public async Task PersistRejectFailTest()
    {
        await WithJournalWrite(
            behavior => behavior.SetInterceptorAsync(new RepeatJournalReject(3, NoopJournal.Instance)), 
            async () =>
            {
                var testActor = Sys.ActorOf(Props.Create(() => new TestRetryActor("a", TestActor)), "testActor");
                testActor.Tell(new TestRetryActor.DoPersist());
                await ExpectMsgAsync<TestRetryActor.PersistFailure>();
            });
    }
    
    [Fact(DisplayName = "WithRetryActor should normally succeed on save snapshot")]
    public async Task SnapshotSuccessTest()
    {
        var testActor = Sys.ActorOf(Props.Create(() => new TestRetryActor("a", TestActor)), "testActor");
        testActor.Tell(new TestRetryActor.DoSnapshot());
        await ExpectMsgAsync<TestRetryActor.SnapshotSuccess>();
    }
    
    [Fact(DisplayName = "WithRetryActor should retry snapshot failure")]
    public async Task SnapshotRetryTest()
    {
        await WithSnapshotSave(
            behavior => behavior.SetInterceptorAsync(new RepeatSnapshotFail(2, NoopSnapshot.Instance)), 
            async () =>
            {
                var testActor = Sys.ActorOf(Props.Create(() => new TestRetryActor("a", TestActor)), "testActor");
                testActor.Tell(new TestRetryActor.DoSnapshot());
                await ExpectMsgAsync<TestRetryActor.SnapshotSuccess>();
            });
    }

    [Fact(DisplayName = "WithRetryActor should fail when exceeds snapshot max retries")]
    public async Task SnapshotFailTest()
    {
        await WithSnapshotSave(
            behavior => behavior.SetInterceptorAsync(new RepeatSnapshotFail(3, NoopSnapshot.Instance)), 
            async () =>
            {
                var testActor = Sys.ActorOf(Props.Create(() => new TestRetryActor("a", TestActor)), "testActor");
                testActor.Tell(new TestRetryActor.DoSnapshot());
                await ExpectMsgAsync<TestRetryActor.SnapshotFailure>();
            });
    }
    
    #region Helper methods

    private async Task WithJournalWrite(Func<JournalWriteBehavior, Task> behaviorSelector, Func<Task> execution)
    {
        if (behaviorSelector == null) throw new ArgumentNullException(nameof(behaviorSelector));
        if (execution == null) throw new ArgumentNullException(nameof(execution));

        try
        {
            await behaviorSelector(_journal!.OnWrite);
            await execution();
        }
        finally
        {
            await _journal!.OnWrite.Pass();
        }
    }
    
    private async Task WithSnapshotSave(Func<SnapshotStoreSaveBehavior, Task> behaviorSelector, Func<Task> execution)
    {
        if (behaviorSelector == null) throw new ArgumentNullException(nameof(behaviorSelector));
        if (execution == null) throw new ArgumentNullException(nameof(execution));

        try
        {
            await behaviorSelector(_snapshots!.OnSave);
            await execution();
        }
        finally
        {
            await _snapshots!.OnSave.Pass();
        }
    }
    
    private sealed class RepeatJournalFail: IJournalInterceptor
    {
        private readonly IJournalInterceptor _next;
        private readonly int _failCount;
        private int _count;
        
        public RepeatJournalFail(int failCount, IJournalInterceptor next)
        {
            _failCount = failCount;
            _next = next;
        }
        
        public async Task InterceptAsync(IPersistentRepresentation message)
        {
            _count++;
            if (_count <= _failCount)
                throw new TestJournalFailureException();
            await _next.InterceptAsync(message);
        }
    }
    
    private sealed class RepeatJournalReject: IJournalInterceptor
    {
        private readonly IJournalInterceptor _next;
        private readonly int _failCount;
        private int _count;
        
        public RepeatJournalReject(int failCount, IJournalInterceptor next)
        {
            _failCount = failCount;
            _next = next;
        }
        
        public async Task InterceptAsync(IPersistentRepresentation message)
        {
            _count++;
            if (_count <= _failCount)
                throw new TestJournalRejectionException();
            await _next.InterceptAsync(message);
        }
    }
    
    private sealed class RepeatSnapshotFail: ISnapshotStoreInterceptor
    {
        private readonly ISnapshotStoreInterceptor _next;
        private readonly int _failCount;
        private int _count;
        
        public RepeatSnapshotFail(int failCount, ISnapshotStoreInterceptor next)
        {
            _failCount = failCount;
            _next = next;
        }
        
        public async Task InterceptAsync(string persistenceId, SnapshotSelectionCriteria criteria)
        {
            _count++;
            if (_count <= _failCount)
                throw new TestSnapshotStoreFailureException();
            await _next.InterceptAsync(persistenceId, criteria);
        }
    }

    private sealed class NoopJournal : IJournalInterceptor
    {
        public static readonly IJournalInterceptor Instance = new NoopJournal();

        public Task InterceptAsync(IPersistentRepresentation message) => Task.FromResult(true);
    }
    
    private sealed class NoopSnapshot : ISnapshotStoreInterceptor
    {
        public static readonly ISnapshotStoreInterceptor Instance = new NoopSnapshot();

        public Task InterceptAsync(string persistenceId, SnapshotSelectionCriteria criteria) => Task.FromResult(true);
    }
    
    #endregion
    
}