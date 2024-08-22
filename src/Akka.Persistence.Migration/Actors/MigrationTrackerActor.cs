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
    private readonly ILoggingAdapter _log;
    private readonly string _targetJournalId;
    private readonly Queue<string> _persistenceIds;
    
    public MigrationTrackerActor(MigrationOptions options)
    {
        _log = Context.GetLogger();
        _persistenceIds = new Queue<string>();
        
        _targetJournalId = options.FromJournalId;
        
        Become(Initializing);
    }
    
    private void Initializing()
    {
        Receive<string>(persistenceId => _persistenceIds.Enqueue(persistenceId));

        Receive<Initialized>(_ =>
        {
            _log.Info($"Initialization completed. Persistence IDs: [{string.Join(", ", _persistenceIds)}]");
            Become(Active);
            MigrateNextPersistenceId();
        });
        
        Receive<InitializationFailed>(fail =>
        {
            _log.Error(fail.Cause, "Initialization failed");
            Context.Stop(Self);
        });
    }
    
    private void Active()
    {
        Receive<MigrationCompleted>(msg =>
        {
            _log.Info($"Migration of {msg.PersistenceId} completed");
        });
        
        Receive<MigrationFailed>(msg =>
        {
            _log.Error(msg.Cause, $"Migration failed. Failed persistence ID: {msg.PersistenceId}");
            Context.Stop(Self);
        });
        
        Receive<Terminated>(_ =>
        {
            MigrateNextPersistenceId();
        });
    }
    
    private void MigrateNextPersistenceId()
    {
        if (_persistenceIds.Count == 0)
        {
            _log.Info("All persistence IDs have been migrated");
            Context.Stop(Self);
            return;
        }
        
        var persistenceId = _persistenceIds.Dequeue();
        _log.Info($"Migrating persistence ID: {persistenceId}");
        var migrator = Context.ActorOf(
            props: Props.Create(() => new PersistenceIdMigratorActor(Self, persistenceId)),
            name: $"{persistenceId}-migrator");
        Context.Watch(migrator);
    }
    
    protected override void PreStart()
    {
        var queryJournal = Context.System.ReadJournalFor<MongoDbReadJournal>(_targetJournalId);
        queryJournal.CurrentPersistenceIds()
            .RunWith(
                sink: Sink.ActorRef<string>(Self, Initialized.Instance, ex => new InitializationFailed(ex)), 
                materializer: Context.System.Materializer());
    }
}