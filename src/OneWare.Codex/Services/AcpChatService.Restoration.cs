using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private bool _delegationsRetiring;

    // Called under the owning conversation's background lock. One atomic file per handle
    // avoids replacing another chat view's unrelated saved children or the session's options.
    private void SaveDelegationLocked(Delegation run, bool retiring = false)
    {
        if (run.Conversation.SessionId is not { } session) return;
        try
        {
            var conversation = run.Conversation;
            _profile.SaveDelegation(new AcpSavedDelegation(1, run.OwnerSession, run.RootTurn.Directory,
                conversation.Id, session, conversation.Definition, conversation.Description, conversation.Catalog,
                conversation.Skills, conversation.Ancestors, conversation.Parent?.Id, run.Number,
                retiring && run.Active ? "interrupted" : BackgroundStatus(run), run.Answer, run.Error));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Emit(new ChatErrorEvent("Could not save a delegated conversation for reopening: " + error.Message));
        }
    }

    private void SaveDelegation(Delegation run)
    {
        lock (_backgroundSync)
            if (!_delegationsRetiring && run.Epoch == _conversationEpoch && run.OwnerSession == _session &&
                _delegatedAgents.TryGetValue(run.Conversation.Id, out var current) && ReferenceEquals(current.Run, run))
                SaveDelegationLocked(run);
    }

    private void PreserveDelegationsBeforeRetirement()
    {
        lock (_backgroundSync)
        {
            if (_delegationsRetiring) return;
            foreach (var child in _delegatedAgents.Values) SaveDelegationLocked(child.Run, retiring: true);
            _delegationsRetiring = true;
        }
    }

    private void RestoreDelegatedConversations()
    {
        if (_delegation != null) return;
        var incomplete = false;
        lock (_backgroundSync)
        {
            _delegationsRetiring = false;
            try
            {
                var loaded = _profile.LoadDelegations(_session!, _cwd!);
                incomplete = loaded.Incomplete;
                var pending = loaded.Records.ToList();
                var sessions = new HashSet<string>(StringComparer.Ordinal);
                // Build parents before descendants; cycles and orphaned records stay on disk.
                for (var depth = 1; depth <= 4; depth++)
                foreach (var record in pending.Where(x => x.Ancestors.Length == depth).ToArray())
                {
                    DelegatedConversation? parent = null;
                    if (depth == 1 && record.ParentId != null || depth > 1 &&
                        (record.ParentId == null || !_delegatedAgents.TryGetValue(record.ParentId, out parent) ||
                         !record.Ancestors[..^1].SequenceEqual(parent.Ancestors)) || !sessions.Add(record.SessionId))
                        continue;
                    var conversation = new DelegatedConversation(record.Id, record.Definition, record.Description,
                        record.Catalog, record.Skills, record.Ancestors, parent) { SessionId = record.SessionId };
                    var turn = new Prompt(_connection!, _generation, _session!, _cwd!, true, [], [],
                        record.Definition.Model ?? _selection.Model, _toolsRevision, CancellationToken.None) { Finished = true };
                    var cancellation = new CancellationTokenSource();
                    if (record.State == "cancelled") cancellation.Cancel();
                    var interrupted = record.State is "running" or "interrupted";
                    var run = new Delegation(this, turn, conversation, record.Turn, parent?.Run, false,
                        _conversationEpoch, cancellation)
                    {
                        Active = false, Answer = record.Answer, Error = interrupted
                            ? "The task was interrupted. Send a follow-up to continue its saved conversation." : record.Error,
                        Interrupted = interrupted, DeliveryQueued = 1, ResultObserved = 1
                    };
                    conversation.Run = run;
                    run.Completion.TrySetResult(true);
                    cancellation.Dispose();
                    _delegatedAgents.Add(conversation.Id, conversation);
                    pending.Remove(record);
                }
                incomplete |= pending.Count != 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
            }
        }
        if (incomplete)
            Emit(new ChatErrorEvent("Some delegated conversations could not be restored. Their saved records were kept; " +
                                   "no replacement agents were started."));
        // Opening history never starts inference, replays cards, re-counts usage or re-delivers old results.
    }
}
