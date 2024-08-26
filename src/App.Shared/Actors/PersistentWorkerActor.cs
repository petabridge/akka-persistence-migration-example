using Akka.Actor;
using Akka.Event;
using Akka.Persistence;

namespace App.Shared.Actors;

public class PersistentWorkerActor: ReceivePersistentActor
{
    private readonly int _actorIndex;
    private readonly List<int> _state = [];
    private int _lastData = -1;
    private IActorRef? _sender;
    private int _recoveryCount;
    private long _lastSeqNr;
    
    public PersistentWorkerActor(int index, int totalActors, int snapshotCount)
    {
        _actorIndex = index;

        var log = Context.GetLogger();
        
        Recover<SnapshotOffer>(offer =>
        {
            _lastSeqNr = offer.Metadata.SequenceNr;
            _state.AddRange((IEnumerable<int>)offer.Snapshot);
            if (_state.Any(i => i % totalActors != _actorIndex))
                throw new Exception("Illegal state detected during recovery");
            _lastData = _state.Last();
        });
        
        Recover<int>(i =>
        {
            if(i < _lastData)
                throw new Exception("Illegal event detected during recovery");
            _recoveryCount++;
            _lastSeqNr++;
            _lastData = i;
            _state.Add(i);
        });
        
        Recover<RecoveryCompleted>(_ =>
        {
            log.Info($"{PersistenceId}: Recovery completed. Events recovered: {_recoveryCount}. Total data in state: {_state.Count}. State Data: {string.Join(", ", _state)}");
        });
        
        Command<int>(msg =>
        {
            _sender = Sender;
            Persist(msg, i =>
            {
                log.Info($"Data persisted: {i}");
                _lastData = i;
                _lastSeqNr++;
                _state.Add(i);
                if(_state.Count % snapshotCount == 0)
                    SaveSnapshot(_state);
                else
                    _sender.Tell(i);
            });
        });
        
        Command<SaveSnapshotSuccess>(_ =>
        {
            log.Info($"Last persisted data snapshot: {_lastData}");
            _sender.Tell(_lastData);
        });
        
        Command<SaveSnapshotFailure>(_ => throw new Exception("Snapshot failed!"));

        Command<TerminateSelf>(_ => Context.Stop(Self));
        
        Command<ReportState>(_ => 
        {
            var checksum = _state.Aggregate(0, (accum, i) => i + accum);
            Sender.Tell(new ReportRow(PersistenceId, _lastSeqNr, checksum));
        });
    }
    
    public override string PersistenceId => $"persistent-worker-{_actorIndex:D2}";
    
}