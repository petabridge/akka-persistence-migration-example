using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Persistence.Migration.Actors;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.Migration.Tests.Infrastructure;
using Akka.Persistence.Query;
using Akka.Persistence.Query.InMemory;
using Akka.Persistence.Sql.Hosting;
using Akka.Persistence.TestKit;
using Akka.Streams;
using Akka.TestKit;
using FluentAssertions;
using FluentAssertions.Extensions;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Akka.Persistence.Migration.Tests;

public class PersistenceWriterActorSpec: Akka.Hosting.TestKit.TestKit
{
    private readonly MsSqliteContainer _sourceFixture;
    private readonly MsSqliteContainer _migrationFixture;
    private ITestSnapshotStore? _snapshots;
    private ITestJournal? _journal;
    private MigrationOptions? _options;

    public PersistenceWriterActorSpec(ITestOutputHelper output)
        : base(nameof(PersistenceWriterActorSpec), output)
    {
        _sourceFixture = new MsSqliteContainer();
        if (!_sourceFixture.InitializeAsync().Wait(5.Seconds()))
            throw new TestTimeoutException(5000);
        
        _migrationFixture = new MsSqliteContainer();
        if (!_migrationFixture.InitializeAsync().Wait(5.Seconds()))
            throw new TestTimeoutException(5000);
    }

    #region Test hosting setup

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services
            .AddOptions<MigrationOptions>()
            .Configure(opt =>
            {
                opt.FromJournalId = "akka.persistence.journal.sqlite-source";
                opt.FromReadJournalId = "akka.persistence.query.journal.sqlite-source";
                opt.FromSnapshotStoreId = "akka.persistence.snapshot-store.sqlite-source";

                opt.ToJournalId = "akka.persistence.journal.test";
                opt.ToSnapshotStoreId = "akka.persistence.snapshot-store.test";

                opt.MigrateAllSnapshots = true;
                opt.RetryInterval = 200.Milliseconds();
                opt.MaxRetries = 3;

                opt.MigrationSqlOptions.ConnectionString = _migrationFixture.ConnectionString;
                opt.MigrationSqlOptions.PluginId = "migration";
                opt.MigrationSqlOptions.ProviderName = ProviderName.SQLiteMS;
            });
        
        base.ConfigureServices(context, services);
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<MigrationOptions>>().Value;
        builder
            // Setup source persistence plugin
            .WithSqlPersistence(
                connectionString: _sourceFixture.ConnectionString,
                providerName: ProviderName.SQLiteMS,
                pluginIdentifier: "sqlite-source",
                autoInitialize: true,
                isDefaultPlugin: false)
            // Make sure that query "write-plugin" setting points to the correct write plugin
            .AddHocon(
                hocon: $"{options.FromReadJournalId}.write-plugin = {options.FromJournalId.ToHocon()}", 
                addMode: HoconAddMode.Prepend)
            
            // Setup target persistence plugin
            .AddHocon(
                ConfigurationFactory.FromResource<TestSnapshotStore>("Akka.Persistence.TestKit.config.conf"), 
                HoconAddMode.Append)
            .AddHocon(InMemoryReadJournal.DefaultConfiguration(), HoconAddMode.Append)
            .AddHocon(
                "akka.persistence.query.journal.inmem.write-plugin = akka.persistence.journal.test", 
                HoconAddMode.Prepend)

            // Setup required migration related persistence plugins
            .WithSqlMigrationPersistence(provider)
            
            .WithActors((system, registry) =>
            {
                var testActor = system.ActorOf(TestPersistenceActor.Props(
                    "a",
                    "akka.persistence.journal.sqlite-source",
                    "akka.persistence.snapshot-store.sqlite-source"), "testActor");
                registry.Register<TestPersistenceActor>(testActor);
            });
    }

    protected override async Task BeforeTestStart()
    {
        await base.BeforeTestStart();
        
        _options = Host.Services.GetRequiredService<IOptions<MigrationOptions>>().Value;
        
        var persistence = Persistence.Instance.Apply(Sys);
        var snapshotActor = persistence.SnapshotStoreFor(_options.ToSnapshotStoreId);
        _snapshots = TestSnapshotStore.FromRef(snapshotActor);
        
        var journalActor = persistence.JournalFor(_options.ToJournalId);
        _journal = TestJournal.FromRef(journalActor);
    }

    protected override async Task AfterAllAsync()
    {
        await base.AfterAllAsync();
        await _sourceFixture.DisposeAsync();
        await _migrationFixture.DisposeAsync();
    }

    #endregion

    [Fact(DisplayName = "PersistenceWriterActor should write events to journal")]
    public async Task PersistenceWriterWriteEventTest()
    {
        var writer = Sys.ActorOf(Props.Create(() => new PersistenceWriterActor(_options!, "a")), "persistence-writer");
        
        writer.Tell(CreateEnvelopeFor(1, 1), TestActor);
        await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();
        writer.Tell(CreateEnvelopeFor(2, 2), TestActor);
        await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();
        writer.Tell(CreateEnvelopeFor(3, 3), TestActor);
        await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();

        var events = new List<int>();
        var reader = Sys.ReadJournalFor<InMemoryReadJournal>("akka.persistence.query.journal.inmem");
        await reader.CurrentEventsByPersistenceId("a", 0, long.MaxValue)
            .RunForeach(evt =>
            {
                events.Add((int)evt.Event);
            }, Sys.Materializer());

        events.Should().BeEquivalentTo([1, 2, 3], opt => opt.WithStrictOrdering());
    }
    
    [Fact(DisplayName = "PersistenceWriterActor should retry write on reject")]
    public async Task PersistenceWriterWriteEventRejectedTest()
    {
        await WithJournalWrite(behavior => behavior.SetInterceptorAsync(new RepeatJournalReject(2, NoopJournal.Instance)), async () =>
        {
            var writer = Sys.ActorOf(Props.Create(() => new PersistenceWriterActor(_options!, "a")), "persistence-writer");
        
            writer.Tell(CreateEnvelopeFor(1, 1), TestActor);
            await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();
            writer.Tell(CreateEnvelopeFor(2, 2), TestActor);
            await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();
            writer.Tell(CreateEnvelopeFor(3, 3), TestActor);
            await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();

            var events = new List<int>();
            var reader = Sys.ReadJournalFor<InMemoryReadJournal>("akka.persistence.query.journal.inmem");
            await reader.CurrentEventsByPersistenceId("a", 0, long.MaxValue)
                .RunForeach(evt =>
                {
                    events.Add((int)evt.Event);
                }, Sys.Materializer());

            events.Should().BeEquivalentTo([1, 2, 3], opt => opt.WithStrictOrdering());
        });
    }
    
    [Fact(DisplayName = "PersistenceWriterActor should stops self if reject failures exceeds max retries")]
    public async Task PersistenceWriterEventRejectedTotalFailureTest()
    {
        await WithJournalWrite(behavior => behavior.SetInterceptorAsync(new RepeatJournalReject(3, NoopJournal.Instance)), async () =>
        {
            var writer = Sys.ActorOf(Props.Create(() => new PersistenceWriterActor(_options!, "a")), "persistence-writer");

            var probe = CreateTestProbe();
            writer.Tell(CreateEnvelopeFor(1, 1), probe);
            probe!.ExpectMsg<PersistenceWriterProtocol.PersistFailed>();
        });
    }
    
    [Fact(DisplayName = "PersistenceWriterActor should stop self on persist fail")]
    public async Task PersistenceWriterWriteEventFailedTest()
    {
        await WithJournalWrite(behavior => behavior.SetInterceptorAsync(new RepeatJournalFail(1, NoopJournal.Instance)), async () =>
        {
            var writer = Sys.ActorOf(Props.Create(() => new PersistenceWriterActor(_options!, "a")), "persistence-writer");
            await WatchAsync(writer);
        
            var probe = CreateTestProbe();
            writer.Tell(CreateEnvelopeFor(1, 1), probe);
            await ExpectTerminatedAsync(writer);
            probe.ExpectMsg<PersistenceWriterProtocol.PersistFailed>();
        });
    }
    
    [Fact(DisplayName = "PersistenceWriterActor should write snapshots")]
    public async Task PersistenceWriterWriteSnapshotTest()
    {
        var writer = Sys.ActorOf(Props.Create(() => new PersistenceWriterActor(_options!, "a")), "persistence-writer");
        
        writer.Tell(CreateEnvelopeFor(1, 1), TestActor);
        await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();
        
        writer.Tell(new SelectedSnapshot(new SnapshotMetadata("a", 999), 6), TestActor);
        await ExpectMsgAsync<PersistenceWriterProtocol.SnapshotWriteCompleted>();
        
        var persistence = Persistence.Instance.Apply(Sys);
        var snapshot = persistence.SnapshotStoreFor(_options!.ToSnapshotStoreId);
        
        snapshot.Tell(new LoadSnapshot("a", SnapshotSelectionCriteria.Latest, long.MaxValue), TestActor);
        var result = await ExpectMsgAsync<LoadSnapshotResult>();
        result.Snapshot.Snapshot.Should().Be(6);
        result.Snapshot.Metadata.SequenceNr.Should().Be(1);
    }
    
    [Fact(DisplayName = "PersistenceWriterActor should retry snapshots when failed")]
    public async Task PersistenceWriterSnapshotFailTest()
    {
        await WithSnapshotSave(behavior => behavior.SetInterceptorAsync(new RepeatSnapshotFail(2, NoopSnapshot.Instance)), async () =>
        {
            var writer = Sys.ActorOf(Props.Create(() => new PersistenceWriterActor(_options!, "a")), "persistence-writer");
        
            writer.Tell(CreateEnvelopeFor(1, 1), TestActor);
            await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();
            
            writer.Tell(new SelectedSnapshot(new SnapshotMetadata("a", 999), 6), TestActor);
            await ExpectMsgAsync<PersistenceWriterProtocol.SnapshotWriteCompleted>();
            
            var persistence = Persistence.Instance.Apply(Sys);
            var snapshot = persistence.SnapshotStoreFor(_options!.ToSnapshotStoreId);
        
            snapshot.Tell(new LoadSnapshot("a", SnapshotSelectionCriteria.Latest, long.MaxValue), TestActor);
            var result = await ExpectMsgAsync<LoadSnapshotResult>();
            result.Snapshot.Snapshot.Should().Be(6);
            result.Snapshot.Metadata.SequenceNr.Should().Be(1);
        });
    }
    
    [Fact(DisplayName = "PersistenceWriterActor should stops self if snapshot failures exceeds max retries")]
    public async Task PersistenceWriterSnapshotTotalFailureTest()
    {
        await WithSnapshotSave(behavior => behavior.SetInterceptorAsync(new RepeatSnapshotFail(3, NoopSnapshot.Instance)), async () =>
        {
            var writer = Sys.ActorOf(Props.Create(() => new PersistenceWriterActor(_options!, "a")), "persistence-writer");
        
            writer.Tell(CreateEnvelopeFor(1, 1), TestActor);
            await ExpectMsgAsync<PersistenceWriterProtocol.PersistWriteCompleted>();
            
            var probe = CreateTestProbe();
            writer.Tell(new SelectedSnapshot(new SnapshotMetadata("a", 999), 6), probe);
            probe!.ExpectMsg<PersistenceWriterProtocol.SnapshotFailed>();
        });
    }

    
    #region Helper methods

    private static EventEnvelope CreateEnvelopeFor(object message, long offset)
    {
        return new EventEnvelope(Offset.Sequence(offset), "a", offset, message, DateTime.UtcNow.Ticks, []);
    }
    
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