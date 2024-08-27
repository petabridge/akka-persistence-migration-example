using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Journal;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.Query;

namespace Akka.Persistence.Migration.Actors;

public class PersistenceWriterActor: ReceivePersistenceWithRetryActor<object, object>
{
    private IActorRef? _sender;
    
    public PersistenceWriterActor(MigrationOptions options, string persistenceId)
    {
        PersistenceId = persistenceId;

        JournalPluginId = options.ToJournalId;
        SnapshotPluginId = options.ToSnapshotStoreId;
        MaxRetries = options.MaxRetries;
        RetryInterval = options.RetryInterval;
        Become(Idle);
    }
    
    public override string PersistenceId { get; }
    protected override int MaxRetries { get; }
    protected override TimeSpan RetryInterval { get; }
    
    private void Idle()
    {
        Command<EventEnvelope>(env =>
        {
            var msg = env.Tags.Length > 0 ? new Tagged(env.Event, env.Tags) : env.Event;
            HandlePersist(msg, Sender);
        });
        
        Command<SelectedSnapshot>(snapshot =>
        {
            HandleSaveSnapshot(snapshot.Snapshot, Sender);
        });
        
        CommandRetry();
        
        CommandAny(HandleUnhandled);
    }
    
    private void Writing()
    {
        CommandRetry();
        
        CommandAny(HandleUnhandled);
    }
    
    private void HandlePersist(object evt, IActorRef sender)
    {
        _sender = sender;
        Become(Writing);
        
        Log.Debug($"Migrating event {evt}");
        PersistWithRetry(evt, _ =>
        {
            Log.Debug($"Event {evt} migrated");
            _sender.Tell(PersistenceWriterProtocol.PersistWriteCompleted.Instance);
            _sender = null;
        
            Become(Idle);
        });
    }
    
    private void HandleSaveSnapshot(object snapshot, IActorRef sender)
    {
        _sender = sender;
        Become(Writing);
        
        SaveSnapshotWithRetry(snapshot);
    }
    
    private void HandleUnhandled(object msg)
    {
        Log.Error($"Illegal out of band command detected: {msg}");
        Unhandled(msg);
    }
    
    protected override void OnPersistFailure(Exception cause)
    {
        _sender.Tell(new PersistenceWriterProtocol.PersistFailed(cause));
    }
    
    protected override void OnSaveSnapshotSuccess(SaveSnapshotSuccess success)
    {
        _sender.Tell(PersistenceWriterProtocol.SnapshotWriteCompleted.Instance);
        _sender = null;
        
        Become(Idle);
    }

    protected override void OnSaveSnapshotFailure(Exception cause)
    {
        _sender.Tell(new PersistenceWriterProtocol.SnapshotFailed(cause));
    }
}
