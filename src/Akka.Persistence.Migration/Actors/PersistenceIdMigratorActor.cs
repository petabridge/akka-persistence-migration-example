using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Persistence.Journal;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.MongoDb.Query;
using Akka.Persistence.Query;
using Akka.Streams;
using Akka.Streams.Dsl;

namespace Akka.Persistence.Migration.Actors;

public class PersistenceIdMigratorActor: ReceivePersistentActor
{
    private readonly string _persistenceId;
    private readonly IActorRef _migrationTracker;
    private PersistenceIdMigratorState _state = new (NoOffset.Instance, NoOffset.Instance);
    private readonly ILoggingAdapter _log;
    private EventEnvelope _currentEvent;

    public PersistenceIdMigratorActor(IActorRef migrationTracker, string persistenceId)
    {
        _persistenceId = persistenceId;
        PersistenceId = $"{_persistenceId}-migrator";
        _migrationTracker = migrationTracker;
        _log = Context.GetLogger();
        
        Command<EventEnvelope>(env =>
        {
        });
        
        Command<MigrationCompleted>(msg =>
        {
            _migrationTracker.Tell(msg);
            Context.Stop(Self);
        });
        
        Command<EventPlaybackFailed>(fail =>
        {
            
        });
        
        Command<MigrationFailed>(msg =>
        {
            _migrationTracker.Tell(msg);
            Context.Stop(Self);
        });
        
    }

    private void Recovering()
    {
        Recover<SnapshotOffer>(offer => _state = (PersistenceIdMigratorState) offer.Snapshot);
        Recover<RecoveryCompleted>(_ =>
        {
            
        });
        Recover<PersistenceIdMigratorState>(state => _state = state);
    }

    private void Migrating()
    {
        
    }
    
    public override string PersistenceId { get; }

    protected override void PreStart()
    {
        var queryJournal = Context.System.ReadJournalFor<MongoDbReadJournal>("akka.persistence.query.mongodb");
        queryJournal
            .CurrentEventsByPersistenceId(PersistenceId, 0, long.MaxValue)
            .RunWith(
                sink: Sink.ActorRefWithAck<EventEnvelope>(
                    actorRef: Self,
                    onInitMessage: Initialize.Instance,
                    ackMessage: EventPersisted.Instance,
                    onCompleteMessage: EventPlaybackCompleted.Instance,
                    onFailureMessage: ex => new EventPlaybackFailed(ex)), 
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
    
    public sealed record PersistenceIdMigratorState(Offset EventOffset, Offset SnapshotOffset);

    public sealed class Initialize
    {
        public static readonly Initialize Instance = new();
        private Initialize() { }
    }
    public sealed record EventPersisted
    {
        public static readonly EventPersisted Instance = new();
        private EventPersisted() { }
    }

    public sealed record EventPlaybackCompleted
    {
        public static readonly EventPlaybackCompleted Instance = new();
        private EventPlaybackCompleted() { }
    }
    public sealed record EventPlaybackFailed(Exception Cause);    
}
