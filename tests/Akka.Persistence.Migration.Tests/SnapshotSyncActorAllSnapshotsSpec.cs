using Akka.Actor;
using Akka.Configuration;
using Akka.DependencyInjection;
using Akka.Hosting;
using Akka.Persistence.Migration.Actors;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.Migration.Tests.Infrastructure;
using Akka.Persistence.Query.InMemory;
using Akka.Persistence.Sql.Hosting;
using Akka.Persistence.TestKit;
using Akka.TestKit;
using FluentAssertions;
using FluentAssertions.Extensions;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Akka.Persistence.Migration.Tests;

public class SnapshotSyncActorAllSnapshotsSpec : Akka.Hosting.TestKit.TestKit
{
    private readonly MsSqliteContainer _targetFixture;
    private TestProbe? _probe;
    private ITestSnapshotStore? _snapshots;
    private MigrationOptions? _options;

    public SnapshotSyncActorAllSnapshotsSpec(ITestOutputHelper output)
        : base(nameof(SnapshotSyncActorAllSnapshotsSpec), output)
    {
        _targetFixture = new MsSqliteContainer();
        if (!_targetFixture.InitializeAsync().Wait(5.Seconds()))
            throw new TestTimeoutException(5000);
    }

    #region Test hosting setup

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        base.ConfigureServices(context, services);
        
        services
            .AddOptions<MigrationOptions>()
            .Configure(opt =>
            {
                opt.FromJournalId = "akka.persistence.journal.test";
                opt.FromReadJournalId = "akka.persistence.query.journal.inmem";
                opt.FromSnapshotStoreId = "akka.persistence.snapshot-store.test";
                
                opt.ToJournalId = "akka.persistence.journal.sqlite-target";
                opt.ToSnapshotStoreId = "akka.persistence.snapshot-store.sqlite-target";
                
                opt.MigrateAllSnapshots = true;
                opt.RetryInterval = 200.Milliseconds();
                opt.MaxRetries = 3;
                
                opt.MigrationSqlOptions.ConnectionString = _targetFixture.ConnectionString;
                opt.MigrationSqlOptions.PluginId = "migration";
                opt.MigrationSqlOptions.ProviderName = ProviderName.SQLiteMS;
            });
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<MigrationOptions>>().Value;
        builder
            // Setup source persistence plugin
            .AddHocon(
                ConfigurationFactory.FromResource<TestSnapshotStore>("Akka.Persistence.TestKit.config.conf"), 
                HoconAddMode.Append)
            .AddHocon(InMemoryReadJournal.DefaultConfiguration(), HoconAddMode.Append)
            .AddHocon(
                "akka.persistence.query.journal.inmem.write-plugin = akka.persistence.journal.test", 
                HoconAddMode.Prepend)
            
            // Make sure that query "write-plugin" setting points to the correct write plugin
            .AddHocon(
                hocon: $"{options.FromReadJournalId}.write-plugin = {options.FromJournalId.ToHocon()}", 
                addMode: HoconAddMode.Prepend)

            // Setup target persistence plugin
            .WithSqlPersistence(
                connectionString: _targetFixture.ConnectionString,
                providerName: ProviderName.SQLiteMS,
                pluginIdentifier: "sqlite-target",
                autoInitialize: true,
                isDefaultPlugin: false)
            
            // Setup required migration related persistence plugins
            .WithSqlMigrationPersistence(provider)
            
            .WithActors((system, registry) =>
            {
                var testActor = system.ActorOf(TestPersistenceActor.Props(
                    "a",
                    "akka.persistence.journal.test",
                    "akka.persistence.snapshot-store.test"), "testActor");
                registry.Register<TestPersistenceActor>(testActor);
            });
    }

    protected override async Task BeforeTestStart()
    {
        await base.BeforeTestStart();
        
        _options = Host.Services.GetRequiredService<IOptions<MigrationOptions>>().Value;
        var persistence = Persistence.Instance.Apply(Sys);
        var snapshotActor = persistence.SnapshotStoreFor(_options.FromSnapshotStoreId);
        _snapshots = TestSnapshotStore.FromRef(snapshotActor);
        _probe = CreateTestProbe(Sys, "testProbe");
    }

    protected override async Task AfterAllAsync()
    {
        await base.AfterAllAsync();
        await _targetFixture.DisposeAsync();
    }

    #endregion

    [Fact(DisplayName = "SnapshotSyncActor should load all snapshots")]
    public async Task SnapshotSyncReadTest()
    {
        await Initialize(true);
        
        var syncActor = Sys.ActorOf(Props.Create(() => new SnapshotSyncActor(_options!, _probe!, "a")), "snapshot-sync-actor");

        var loaded = _probe!.ExpectMsg<SnapshotSyncProtocol.AllSnapshotsLoaded>();
        loaded.Count.Should().Be(10);
        
        syncActor.Tell(SnapshotSyncProtocol.GetCount.Instance, TestActor);
        var count = await ExpectMsgAsync<SnapshotSyncProtocol.SnapshotSyncCount>();
        count.Count.Should().Be(10);

        foreach (var i in Enumerable.Range(1, 10))
        {
            syncActor.Tell(SnapshotSyncProtocol.GetCurrent.Instance, TestActor);
            
            var snapshot = await ExpectMsgAsync<SnapshotSyncProtocol.CurrentSnapshotSync>();
            snapshot.Snapshot.Should().NotBeNull();
            snapshot.Snapshot!.Snapshot.Should().BeOfType<string>();
            snapshot.Snapshot.Snapshot.Should().Be(GenerateState(i));
            snapshot.Snapshot.Metadata.SequenceNr.Should().Be(i * 100);
            
            syncActor.Tell(SnapshotSyncProtocol.MoveNext.Instance, TestActor);
            await ExpectMsgAsync<Done>();
        }
    }
    
    [Fact(DisplayName = "SnapshotSyncActor should return null if no snapshots")]
    public async Task SnapshotSyncReadEmptyTest()
    {
        await Initialize(false);
        
        var syncActor = Sys.ActorOf(Props.Create(() => new SnapshotSyncActor(_options!, _probe!, "a")), "snapshot-sync-actor");

        var loaded = _probe!.ExpectMsg<SnapshotSyncProtocol.AllSnapshotsLoaded>();
        loaded.Count.Should().Be(0);
        
        syncActor.Tell(SnapshotSyncProtocol.GetCount.Instance, TestActor);
        var count = await ExpectMsgAsync<SnapshotSyncProtocol.SnapshotSyncCount>();
        count.Count.Should().Be(0);

        syncActor.Tell(SnapshotSyncProtocol.GetCurrent.Instance, TestActor);
        var snapshot = await ExpectMsgAsync<SnapshotSyncProtocol.CurrentSnapshotSync>();
        snapshot.Snapshot.Should().BeNull();
    }
    
    [Fact(DisplayName = "SnapshotSyncActor should persist the last loaded sequence on restart")]
    public async Task SnapshotSyncReadPersistTest()
    {
        await Initialize(true);
        
        var syncActor = Sys.ActorOf(Props.Create(() => new SnapshotSyncActor(_options!, _probe!, "a")), "snapshot-sync-actor");

        var loaded = _probe!.ExpectMsg<SnapshotSyncProtocol.AllSnapshotsLoaded>();
        loaded.Count.Should().Be(10);
        
        syncActor.Tell(SnapshotSyncProtocol.GetCount.Instance, TestActor);
        var count = await ExpectMsgAsync<SnapshotSyncProtocol.SnapshotSyncCount>();
        count.Count.Should().Be(10);

        foreach (var i in Enumerable.Range(1, 5))
        {
            syncActor.Tell(SnapshotSyncProtocol.GetCurrent.Instance, TestActor);
            
            var snapshot = await ExpectMsgAsync<SnapshotSyncProtocol.CurrentSnapshotSync>();
            snapshot.Snapshot.Should().NotBeNull();
            snapshot.Snapshot!.Snapshot.Should().BeOfType<string>();
            snapshot.Snapshot.Snapshot.Should().Be(GenerateState(i));
            snapshot.Snapshot.Metadata.SequenceNr.Should().Be(i * 100);
            
            syncActor.Tell(SnapshotSyncProtocol.MoveNext.Instance, TestActor);
            await ExpectMsgAsync<Done>();
        }

        await WatchAsync(syncActor);
        syncActor.Tell(PoisonPill.Instance);
        await ExpectTerminatedAsync(syncActor);
        
        syncActor = Sys.ActorOf(Props.Create(() => new SnapshotSyncActor(_options!, _probe!, "a")), "snapshot-sync-actor");
        
        loaded = _probe!.ExpectMsg<SnapshotSyncProtocol.AllSnapshotsLoaded>();
        loaded.Count.Should().Be(5);
        
        syncActor.Tell(SnapshotSyncProtocol.GetCount.Instance, TestActor);
        count = await ExpectMsgAsync<SnapshotSyncProtocol.SnapshotSyncCount>(10.Minutes());
        count.Count.Should().Be(5);

        foreach (var i in Enumerable.Range(6, 5))
        {
            syncActor.Tell(SnapshotSyncProtocol.GetCurrent.Instance, TestActor);
            
            var snapshot = await ExpectMsgAsync<SnapshotSyncProtocol.CurrentSnapshotSync>();
            snapshot.Snapshot.Should().NotBeNull();
            snapshot.Snapshot!.Snapshot.Should().BeOfType<string>();
            snapshot.Snapshot.Snapshot.Should().Be(GenerateState(i));
            snapshot.Snapshot.Metadata.SequenceNr.Should().Be(i * 100);
            
            syncActor.Tell(SnapshotSyncProtocol.MoveNext.Instance, TestActor);
            await ExpectMsgAsync<Done>();
        }
    }
    
    [Fact(DisplayName = "SnapshotSyncActor should retry snapshot read when it failed")]
    public async Task SnapshotSyncRetryTest()
    {
        await Initialize(true);
        
        await WithSnapshotLoad(
            behavior => behavior.SetInterceptorAsync(new RepeatFail(2, Noop.Instance)), 
            async () =>
            {
                var syncActor = Sys.ActorOf(Props.Create(() => new SnapshotSyncActor(_options!, _probe!, "a")), "snapshot-sync-actor");
                
                var loaded = _probe!.ExpectMsg<SnapshotSyncProtocol.AllSnapshotsLoaded>();
                loaded.Count.Should().Be(10);
                
                syncActor.Tell(SnapshotSyncProtocol.GetCount.Instance, TestActor);
                var count = await ExpectMsgAsync<SnapshotSyncProtocol.SnapshotSyncCount>();
                count.Count.Should().Be(10);
                
                foreach (var i in Enumerable.Range(1, 10))
                {
                    syncActor.Tell(SnapshotSyncProtocol.GetCurrent.Instance, TestActor);
            
                    var snapshot = await ExpectMsgAsync<SnapshotSyncProtocol.CurrentSnapshotSync>();
                    snapshot.Snapshot.Should().NotBeNull();
                    snapshot.Snapshot!.Snapshot.Should().BeOfType<string>();
                    snapshot.Snapshot.Snapshot.Should().Be(GenerateState(i));
                    snapshot.Snapshot.Metadata.SequenceNr.Should().Be(i * 100);
            
                    syncActor.Tell(SnapshotSyncProtocol.MoveNext.Instance, TestActor);
                    await ExpectMsgAsync<Done>();
                }
            });
    }
    
    [Fact(DisplayName = "SnapshotSyncActor should stops self if failures exceeds max retries")]
    public async Task SnapshotSyncTotalFailureTest()
    {
        await Initialize(true);
        
        await WithSnapshotLoad(
            behavior => behavior.SetInterceptorAsync(new RepeatFail(3, Noop.Instance)), 
            async () =>
            {
                var syncActor = Sys.ActorOf(Props.Create(() => new SnapshotSyncActor(_options!, _probe!, "a")), "snapshot-sync-actor");

                _probe!.ExpectMsg<LoadSnapshotFailed>();
                
                await WatchAsync(syncActor);
                await ExpectTerminatedAsync(syncActor);
            });
    }

    #region Helper methods

    private async Task Initialize(bool saveSnapshots)
    {
        var registry = Host.Services.GetRequiredService<IActorRegistry>();
        var testActor = await registry.GetAsync<TestPersistenceActor>();
        
        foreach (var i in Enumerable.Range(1, 1000))
        {
            testActor.Tell(i.ToString(), TestActor);
            ExpectMsg($"{i}-done");
            
            if (saveSnapshots && i % 100 == 0)
            {
                testActor.Tell(TestPersistenceActor.TakeSnapshot.Instance, TestActor);
                ExpectMsg($"{i}-saved");
            }
        }
    }

    private async Task WithSnapshotLoad(Func<SnapshotStoreLoadBehavior, Task> behaviorSelector, Func<Task> execution)
    {
        if (behaviorSelector == null) throw new ArgumentNullException(nameof(behaviorSelector));
        if (execution == null) throw new ArgumentNullException(nameof(execution));

        try
        {
            await behaviorSelector(_snapshots!.OnLoad);
            await execution();
        }
        finally
        {
            await _snapshots!.OnLoad.Pass();
        }
    }
    
    private static string GenerateState(int rounds)
        => "-" + string.Join("-", Enumerable.Range(1, rounds).Select(i => i * 100));

    private sealed class RepeatFail: ISnapshotStoreInterceptor
    {
        private readonly ISnapshotStoreInterceptor _next;
        private readonly int _failCount;
        private int _count;
        
        public RepeatFail(int failCount, ISnapshotStoreInterceptor next)
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
    
    private sealed class Noop : ISnapshotStoreInterceptor
    {
        public static readonly ISnapshotStoreInterceptor Instance = new Noop();

        public Task InterceptAsync(string persistenceId, SnapshotSelectionCriteria criteria) => Task.FromResult(true);
    }
    
    #endregion
}