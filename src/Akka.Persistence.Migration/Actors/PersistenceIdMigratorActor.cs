using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.MongoDb.Query;
using Akka.Persistence.Query;
using Akka.Streams;
using Akka.Streams.Dsl;
using LanguageExt.ClassInstances;

namespace Akka.Persistence.Migration.Actors;

public class PersistenceIdMigratorActor: ReceivePersistentActor
{
    private readonly MigrationOptions _options;
    private readonly string _persistenceId;
    private readonly IActorRef _migrationTracker;
    private readonly ILoggingAdapter _log;
    
    private IActorRef? _writer;
    private IActorRef? _snapshotSync;
    private IActorRef? _streamActor;

    private long _migratedCount;
    private long _persistCount;
    private long _lastSequenceNr = -1;
    private SelectedSnapshot? _currentSnapshot;
    private EventEnvelope? _currentEvent;
    
    public PersistenceIdMigratorActor(MigrationOptions options, IActorRef migrationTracker, string persistenceId)
    {
        _options = options;
        _persistenceId = persistenceId;
        _migrationTracker = migrationTracker;
        _log = Context.GetLogger();
        
        PersistenceId = $"{_persistenceId}-migrator";
        JournalPluginId = _options.MigrationSqlOptions.JournalId;
        SnapshotPluginId = _options.MigrationSqlOptions.SnapshotStoreId;
        
        Recover<SnapshotOffer>(offer => _lastSequenceNr = (long) offer.Snapshot);
        Recover<long>(state =>
        {
            _persistCount++;
            _lastSequenceNr = state;
        });
    }
    
    private void Initializing()
    {
        Command<SnapshotSyncProtocol.AllSnapshotsLoaded>(_ =>
        {
            // start persistence writer
            _writer = Context.ActorOf(Props.Create(() => new PersistenceWriterActor(_options, _persistenceId)), $"{_persistenceId}-writer");
            
            // Ask the SnapshotSync for the last or next highest snapshot
            _snapshotSync!
                .Ask<SnapshotSyncProtocol.CurrentSnapshot>(SnapshotSyncProtocol.GetCurrent.Instance, _options.AskTimeout)
                .PipeTo(Self, failure: ex => new PersistenceIdMigratorProtocol.CommandFailure(ex));
        });
        
        Command<PersistenceIdMigratorProtocol.CommandFailure>(fail =>
        {
            _log.Error(fail.Cause, "Failed to fetch the most recent snapshot");
            _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationFailed(fail.Cause, _persistenceId));
        });
        
        Command<SnapshotSyncProtocol.CurrentSnapshot>(snapshot =>
        {
            // Store the current snapshot, can be null if there are none
            _currentSnapshot = snapshot.Snapshot;
            
            // start event query
            var queryJournal = Context.System.ReadJournalFor<MongoDbReadJournal>(_options.FromReadJournalId);
            queryJournal
                .CurrentEventsByPersistenceId(_persistenceId, _lastSequenceNr + 1, long.MaxValue)
                .To(Sink.ActorRefWithAck<EventEnvelope>(
                    actorRef: Self,
                    onInitMessage: PersistenceIdMigratorProtocol.Initialize.Instance,
                    ackMessage: PersistenceIdMigratorProtocol.EventPersisted.Instance,
                    onCompleteMessage: PersistenceIdMigratorProtocol.EventPlaybackCompleted.Instance,
                    onFailureMessage: ex => new PersistenceIdMigratorProtocol.EventPlaybackFailed(ex)))
                .Run(Context.System.Materializer());
        });
        
        // Event query started
        Command<PersistenceIdMigratorProtocol.Initialize>(_ =>
        {
            // store a ref to the event query stream actor
            _streamActor = Sender;
            
            // Ack the event query stream
            _streamActor.Tell(PersistenceIdMigratorProtocol.EventPersisted.Instance, Self);
            
            // Start migration
            Become(Migrating);
        });
    }
    
    private void Migrating()
    {
        Command<EventEnvelope>(env =>
        {
            _currentEvent = env;
            _writer.Tell(env);
        });
        
        Command<PersistenceWriterProtocol.PersistWriteCompleted>(_ =>
        {
            Persist(_currentEvent!.SequenceNr, _ =>
            {
                _migratedCount++;
                _persistCount++;
                _lastSequenceNr = _currentEvent.SequenceNr;
                _currentEvent = null;
                if (_currentSnapshot is not null && _currentSnapshot.Metadata.SequenceNr == _lastSequenceNr)
                {
                    _writer.Tell(_currentSnapshot);
                }
                else
                {
                    if (_persistCount % _options.SnapshotEvery == 0)
                        SaveSnapshot(_lastSequenceNr);
                    else
                        _streamActor!.Tell(PersistenceIdMigratorProtocol.EventPersisted.Instance, Self);
                }
            });
        });
        
        Command<PersistenceWriterProtocol.SnapshotWriteCompleted>(_ =>
        {
            _currentSnapshot = null;
            _snapshotSync!
                .Ask<SnapshotSyncProtocol.MoveDone>(SnapshotSyncProtocol.MoveNext.Instance, _options.AskTimeout)
                .PipeTo(Self, failure:ex => new PersistenceIdMigratorProtocol.CommandFailure(ex));
        });
        
        Command<SnapshotSyncProtocol.MoveDone>(_ =>
        {
            _snapshotSync!
                .Ask<SnapshotSyncProtocol.CurrentSnapshot>(SnapshotSyncProtocol.GetCurrent.Instance, _options.AskTimeout)
                .PipeTo(Self, failure: ex => new PersistenceIdMigratorProtocol.CommandFailure(ex));
        });
        
        Command<SnapshotSyncProtocol.CurrentSnapshot>(snap =>
        {
            _currentSnapshot = snap.Snapshot;
            if (_persistCount == _options.SnapshotEvery)
                SaveSnapshot(_lastSequenceNr);
            else
                _streamActor!.Tell(PersistenceIdMigratorProtocol.EventPersisted.Instance, Self);
        });
        
        Command<SaveSnapshotSuccess>(msg =>
        {
            DeleteMessages(msg.Metadata.SequenceNr);
            _streamActor!.Tell(PersistenceIdMigratorProtocol.EventPersisted.Instance, Self);
        });
        
        Command<DeleteMessagesSuccess>(_ => { /* no-op */ });
        
        Command<DeleteMessagesFailure>(_ => { /* no-op */ });
        
        Command<PersistenceIdMigratorProtocol.EventPlaybackCompleted>(_ =>
        {
            _log.Info($"{_persistenceId}: Event playback completed. {_migratedCount} events migrated.");
            _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationCompleted(_persistenceId));
        });
        
        Command<SaveSnapshotFailure>(fail =>
        {
            _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationFailed(fail.Cause, _persistenceId));
        });
        
        Command<PersistenceIdMigratorProtocol.CommandFailure>(fail =>
        {
            _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationFailed(fail.Cause, _persistenceId));
        });
        
        Command<PersistenceIdMigratorProtocol.EventPlaybackFailed>(fail =>
        {
            _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationFailed(fail.Cause, _persistenceId));
        });
        
        Command<MigrationActorProtocol.PersistenceIdMigrationFailed>(msg =>
        {
            _migrationTracker.Tell(msg);
        });
    }
    
    public override string PersistenceId { get; }
    
    protected override void OnReplaySuccess()
    {
        base.OnReplaySuccess();
        
        // switch state to initializing
        Become(Initializing);
        
        // start snapshot sync actor
        _snapshotSync = Context.ActorOf(Props.Create(() => new SnapshotSyncActor(_options, Self, _persistenceId)), $"{_persistenceId}-snapshot-sync");
    }
    
    protected override void OnPersistFailure(Exception cause, object @event, long sequenceNr)
    {
        base.OnPersistFailure(cause, @event, sequenceNr);
        _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationFailed(cause, _persistenceId));
    }
    
    protected override void OnPersistRejected(Exception cause, object @event, long sequenceNr)
    {
        base.OnPersistRejected(cause, @event, sequenceNr);
        _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationFailed(cause, _persistenceId));
    }
    
    protected override void OnRecoveryFailure(Exception reason, object? message = null)
    {
        base.OnRecoveryFailure(reason, message);
        _migrationTracker.Tell(new MigrationActorProtocol.PersistenceIdMigrationFailed(reason, _persistenceId));
    }
}
