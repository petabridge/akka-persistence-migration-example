using System.Collections.Immutable;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Journal;

namespace Akka.Persistence.Migration.Tests.Infrastructure;

public class TestPersistenceActor : UntypedPersistentActor
{
    public static Props Props(string persistenceId, string journalId, string snapshotId) 
        => Actor.Props.Create(() => new TestPersistenceActor(persistenceId, journalId, snapshotId));

    public sealed class DeleteCommand
    {
        public DeleteCommand(long toSequenceNr)
        {
            ToSequenceNr = toSequenceNr;
        }

        public long ToSequenceNr { get; }
    }
    
    public sealed class TakeSnapshot
    {
        public static readonly TakeSnapshot Instance = new();
        private TakeSnapshot() { }
    }

    public TestPersistenceActor(string persistenceId, string journalId, string snapshotId)
    {
        PersistenceId = persistenceId;
        JournalPluginId = journalId;
        SnapshotPluginId = snapshotId;
    }

    private string _state = string.Empty;
    private string _current = string.Empty;
    public override string PersistenceId { get; }

    protected override void OnRecover(object message)
    {
    }

    protected override void OnCommand(object message)
    {
        switch (message)
        {
            case TakeSnapshot:
                _state += $"-{_current}";
                SaveSnapshot(_state);
                Become(WhileSavingSnapshot(Sender));
                break;
            
            case DeleteCommand delete:
                DeleteMessages(delete.ToSequenceNr);
                Become(WhileDeleting(Sender)); // need to wait for delete ACK to return
                break;
            
            case string cmd:
                var sender = Sender;
                Persist(cmd, e =>
                {
                    _current = e;
                    sender.Tell($"{e}-done");
                });
                break;
        }
    }

    private Receive WhileSavingSnapshot(IActorRef originalSender) => 
        message =>
        {
            switch (message)
            {
                case SaveSnapshotSuccess success:
                    originalSender.Tell($"{success.Metadata.SequenceNr}-saved");
                    Become(OnCommand);
                    Stash.UnstashAll();
                    break;
                
                case SaveSnapshotFailure failure:
                    Log.Error(failure.Cause, "Failed to save snapshot. sequence number [{0}].", failure.Metadata.SequenceNr);
                    originalSender.Tell($"{failure.Metadata.SequenceNr}-save-failed");
                    Become(OnCommand);
                    Stash.UnstashAll();
                    break;
                
                default:
                    Stash.Stash();
                    break;
            }

            return true;
        };

    private Receive WhileDeleting(IActorRef originalSender)
    {
        return message =>
        {
            switch (message)
            {
                case DeleteMessagesSuccess success:
                    originalSender.Tell($"{success.ToSequenceNr}-deleted");
                    Become(OnCommand);
                    Stash.UnstashAll();
                    break;
                case DeleteMessagesFailure failure:
                    Log.Error(failure.Cause, "Failed to delete messages to sequence number [{0}].", failure.ToSequenceNr);
                    originalSender.Tell($"{failure.ToSequenceNr}-deleted-failed");
                    Become(OnCommand);
                    Stash.UnstashAll();
                    break;
                default:
                    Stash.Stash();
                    break;
            }

            return true;
        };
    }
}

public class ColorFruitTagger : IWriteEventAdapter
{
    public static IImmutableSet<string> Colors { get; } = ImmutableHashSet.Create("green", "black", "blue");
    public static IImmutableSet<string> Fruits { get; } = ImmutableHashSet.Create("apple", "banana");

    public string Manifest(object evt) => string.Empty;

    public object ToJournal(object evt)
    {
        if (evt is string s)
        {
            var colorTags = Colors.Aggregate(ImmutableHashSet<string>.Empty, (acc, color) => s.Contains(color) ? acc.Add(color) : acc);
            var fruitTags = Fruits.Aggregate(ImmutableHashSet<string>.Empty, (acc, color) => s.Contains(color) ? acc.Add(color) : acc);
            var tags = colorTags.Union(fruitTags);
            return tags.IsEmpty
                ? evt
                : new Tagged(evt, tags);
        }

        return evt;
    }
}