using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;

namespace Akka.Persistence.Migration.Actors;

public class SnapshotSyncActor: ReceivePersistentActor, IWithTimers
{
    private readonly ILoggingAdapter _log;
    private readonly IActorRef _trackerActor;
    private readonly IActorRef _snapshotStore;
    private readonly Stack<SelectedSnapshot> _snapshots = new();
    private readonly string _persistenceId;
    
    private long _lastReadSequenceNr = -1;
    private long _currentSequenceNr = long.MaxValue;
    private readonly List<Exception> _exceptions = [];
    private readonly int _maxRetries;
    private readonly TimeSpan _retryInterval;
    private readonly bool _loadLatestOnly;
    
    public SnapshotSyncActor(
        MigrationOptions options,
        IActorRef trackerActor,
        string persistenceId)
    {
        _log = Context.GetLogger();
        _persistenceId = persistenceId;
        PersistenceId = $"{_persistenceId}-snapshot-sync";
        _trackerActor = trackerActor;
        
        JournalPluginId = options.MigrationSqlOptions.JournalId;
        SnapshotPluginId = options.MigrationSqlOptions.SnapshotStoreId;
        
        _maxRetries = options.MaxRetries;
        _retryInterval = options.RetryInterval;
        
        var persistence = Persistence.Instance.Apply(Context.System);
        _snapshotStore = persistence.SnapshotStoreFor(options.FromSnapshotStoreId);
        
        _loadLatestOnly = !options.MigrateAllSnapshots;
        if(_loadLatestOnly)
            Become(Initializing);
        else
            Become(InitializingAllSnapshots);
    }
    
    public override string PersistenceId { get; }
    public ITimerScheduler Timers { get; set; } = null!;
    
    private void InitializingAllSnapshots()
    {
        Command<LoadSnapshotResult>(msg =>
        {
            _exceptions.Clear();
            
            var snapshot = msg.Snapshot;
            if(snapshot is not null && snapshot.Metadata.SequenceNr > _lastReadSequenceNr)
            {
                _log.Info($"Snapshot load. Sequence number: {snapshot.Metadata.SequenceNr}");
                _snapshots.Push(snapshot);
                _currentSequenceNr = snapshot.Metadata.SequenceNr;
                LoadNextSnapshot( _currentSequenceNr - 1 );
            }
            else
            {
                _log.Info("All snapshots loaded");
                _trackerActor.Tell(new SnapshotSyncProtocol.AllSnapshotsLoaded(_snapshots.Count));
                Stash.UnstashAll();
                Become(Active);
            }
        });
        
        CommonHandlers();
    }
    
    private void Initializing()
    {
        Command<LoadSnapshotResult>(msg =>
        {
            _exceptions.Clear();
            
            var snapshot = msg.Snapshot;
            if(snapshot is not null)
            {
                _log.Info($"Snapshot load. Sequence number: {snapshot.Metadata.SequenceNr}");
                _snapshots.Push(snapshot);
            }
            
            _log.Info("Last snapshot loaded");
            _trackerActor.Tell(new SnapshotSyncProtocol.AllSnapshotsLoaded(_snapshots.Count));
            Stash.UnstashAll();
            Become(Active);
        });
        
        CommonHandlers();
    }

    private void CommonHandlers()
    {
        Recover<SnapshotOffer>(offer => _lastReadSequenceNr = (long) offer.Snapshot);
        Recover<long>(seq => _lastReadSequenceNr = seq);
        
        Command<LoadSnapshotFailed>(fail => HandleFailure(fail.Cause));
        Command<SnapshotSyncProtocol.Retry>(_ => LoadNextSnapshot(_currentSequenceNr - 1));
        CommandAny(_ => Stash.Stash());
    }
    
    private void Active()
    {
        Command<SnapshotSyncProtocol.GetCount>(_ =>
        {
            Sender.Tell(new SnapshotSyncProtocol.SnapshotSyncCount(_snapshots.Count));
        });
        
        Command<SnapshotSyncProtocol.GetCurrent>(_ =>
        {
            Sender.Tell(
                _snapshots.Count == 0 
                    ? new SnapshotSyncProtocol.CurrentSnapshot(null) 
                    : new SnapshotSyncProtocol.CurrentSnapshot(_snapshots.Peek()));
        });
        
        Command<SnapshotSyncProtocol.MoveNext>(_ =>
        {
            if (!_snapshots.TryPop(out var snapshot))
            {
                Sender.Tell(SnapshotSyncProtocol.MoveDone.Instance);
                return;
            }
            
            var sender = Sender;
            Persist(snapshot.Metadata.SequenceNr, _ =>
            {
                sender.Tell(SnapshotSyncProtocol.MoveDone.Instance);
            });
        });
    }

    private void HandleFailure(Exception cause)
    {
        _exceptions.Add(cause);
        if (_exceptions.Count < _maxRetries)
        {
            _log.Info("Snapshot load failed ({0}/{1}), retrying in {2} seconds...", 
                _exceptions.Count, _maxRetries, _retryInterval.TotalSeconds);
                
            Timers.StartSingleTimer(
                key: SnapshotSyncProtocol.Retry.Instance, 
                msg: SnapshotSyncProtocol.Retry.Instance,
                timeout: _retryInterval);
            return;
        }

        try
        {
            throw new AggregateException(
                message: $"Snapshot sync load failed. Maximum retries exceeded: {_maxRetries} retries",
                innerExceptions: _exceptions);
        }
        catch (AggregateException ex)
        {
            _log.Error(ex, "Snapshot sync load failed. Maximum retries exceeded: {0} retries", _maxRetries);
            _trackerActor.Tell(new LoadSnapshotFailed(ex));
        }
        finally
        {
            Context.Stop(Self);
        }
    }
    
    protected override void OnReplaySuccess()
    {
        base.OnReplaySuccess();
        
        if (_loadLatestOnly)
        {
            if(_lastReadSequenceNr == -1)
                LoadNextSnapshot(long.MaxValue);
            else
            {
                _log.Info("Latest snapshot already processed, skipping snapshot load.");
                _trackerActor.Tell(new SnapshotSyncProtocol.AllSnapshotsLoaded(0));
                Become(Active);
            }
        }
        else
        {
            LoadNextSnapshot(long.MaxValue);
        }
    }
    
    private void LoadNextSnapshot(long toSeqNr)
    {
        _snapshotStore.Ask<ISnapshotResponse>(new LoadSnapshot(_persistenceId, SnapshotSelectionCriteria.Latest, toSeqNr))
            .PipeTo(Self, Self);
    }
}
