using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Journal;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.MongoDb.Query;
using Akka.Persistence.Query;
using Akka.Streams;
using Akka.Streams.Dsl;

namespace Akka.Persistence.Migration.Actors;

public class PersistenceIdMigratorActor: ReceivePersistentActor
{
    private readonly IActorRef _migrationTracker;
    private SnapshotOffer? _snapshot;
    private readonly List<object> _recovered = new();
    private readonly ILoggingAdapter _log;

    public PersistenceIdMigratorActor(string persistenceId, IActorRef migrationTracker)
    {
        PersistenceId = persistenceId;
        _migrationTracker = migrationTracker;
        _log = Context.GetLogger();
        
        Recover<SnapshotOffer>(offer => _snapshot = offer);
        Recover<RecoveryCompleted>(_ => { });
        
        Command<EventEnvelope>(env =>
        {
            var msg = env.Tags.Length > 0 ? new Tagged(env.Event, env.Tags) : env.Event;
            _log.Info($"Migrating event {env.Event} to {msg}");
            Persist(msg, _ => { });
        });
        
        Command<MigrationCompleted>(msg =>
        {
            _migrationTracker.Tell(msg);
            Context.Stop(Self);
        });
        
        Command<MigrationFailed>(msg =>
        {
            _migrationTracker.Tell(msg);
            Context.Stop(Self);
        });
        
        Recover<object>(obj => _recovered.Add(obj));
    }
    
    public override string PersistenceId { get; }

    protected override void PreStart()
    {
        var queryJournal = Context.System.ReadJournalFor<MongoDbReadJournal>("akka.persistence.query.mongodb");
        queryJournal
            .CurrentEventsByPersistenceId(PersistenceId, 0, long.MaxValue)
            .RunWith(
                sink: Sink.ActorRef<EventEnvelope>(
                    actorRef: Self,
                    onCompleteMessage: new MigrationCompleted(PersistenceId),
                    onFailureMessage: ex => new MigrationFailed(ex, PersistenceId)), 
                materializer: Context.System.Materializer());
    }

    protected override void OnPersistFailure(Exception cause, object @event, long sequenceNr)
    {
        base.OnPersistFailure(cause, @event, sequenceNr);
        _migrationTracker.Tell(new MigrationFailed(cause, PersistenceId));
        Context.Stop(Self);
    }

    protected override void OnPersistRejected(Exception cause, object @event, long sequenceNr)
    {
        base.OnPersistRejected(cause, @event, sequenceNr);
        _migrationTracker.Tell(new MigrationFailed(cause, PersistenceId));
        Context.Stop(Self);
    }

    protected override void OnRecoveryFailure(Exception reason, object? message = null)
    {
        base.OnRecoveryFailure(reason, message);
        _migrationTracker.Tell(new MigrationFailed(reason, PersistenceId));
        Context.Stop(Self);
    }
}