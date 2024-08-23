using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.MongoDb.Query;
using Akka.Persistence.Query;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.Options;

namespace Akka.Persistence.Migration.Actors;

public class MigrationTrackerActor: ReceiveActor
{
    private readonly MigrationOptions _options;
    private readonly ILoggingAdapter _log;
    private readonly Queue<string> _persistenceIds;
    private int _runningMigrations;
    private IActorRef? _watcher;
    
    public MigrationTrackerActor(MigrationOptions options)
    {
        _log = Context.GetLogger();
        _persistenceIds = new Queue<string>();
        _options = options;

        Become(Initializing);
    }
    
    private void Initializing()
    {
        Receive<string>(persistenceId => _persistenceIds.Enqueue(persistenceId));

        Receive<MigrationActorProtocol.Initialized>(_ =>
        {
            _log.Info($"Initialization completed. Persistence IDs loaded: {_persistenceIds.Count}");
            Become(Active);
            MigrateNextBatch();
        });
        
        Receive<MigrationActorProtocol.InitializationFailed>(fail =>
        {
            _log.Error(fail.Cause, "Initialization failed");
            Context.Stop(Self);
        });

        Receive<MigrationActorProtocol.NotifyWhenCompleted>(_ =>
        {
            _watcher = Sender;
        });
    }
    
    private void Active()
    {
        Receive<MigrationActorProtocol.NotifyWhenCompleted>(_ =>
        {
            _watcher = Sender;
        });
        
        Receive<MigrationActorProtocol.PersistenceIdMigrationCompleted>(msg =>
        {
            _log.Info($"Migration of {msg.PersistenceId} completed");
            _runningMigrations--;
            MigrateNextBatch();
        });
        
        Receive<MigrationActorProtocol.PersistenceIdMigrationFailed>(msg =>
        {
            _log.Error(msg.Cause, $"Migration failed. Failed persistence ID: {msg.PersistenceId}");
            _watcher?.Tell(new MigrationActorProtocol.MigrationFailed(msg.Cause, msg.PersistenceId));
        });
    }

    private void MigrateNextBatch()
    {
        if (_runningMigrations == 0 && _persistenceIds.Count == 0)
        {
            _log.Info("All persistence IDs have been migrated");
            _watcher?.Tell(MigrationActorProtocol.MigrationCompleted.Instance);
            return;
        }
        
        while (_runningMigrations < _options.MaxParallelMigrations)
        {
            if (_persistenceIds.Count == 0)
                break;
            MigrateNextPersistenceId();
        }
    }
    
    private void MigrateNextPersistenceId()
    {
        _runningMigrations++;
        var persistenceId = _persistenceIds.Dequeue();
        _log.Info($"Migrating persistence ID: {persistenceId}");
        Context.ActorOf(
            props: Props.Create(() => new PersistenceIdMigratorActor(_options, Self, persistenceId)),
            name: $"{persistenceId}-migrator");
    }
    
    protected override void PreStart()
    {
        var queryJournal = Context.System.ReadJournalFor<MongoDbReadJournal>(_options.FromReadJournalId);
        queryJournal.CurrentPersistenceIds()
            .RunWith(
                sink: Sink.ActorRef<string>(Self, MigrationActorProtocol.Initialized.Instance, ex => new MigrationActorProtocol.InitializationFailed(ex)), 
                materializer: Context.System.Materializer());
    }
}