﻿using Akka.Actor;
using Akka.Persistence.Query;
using Raven.Client.Documents.Changes;
using System.Threading.Channels;

namespace Akka.Persistence.RavenDb.Query.ContinuousQuery;

public class AllEvents : ContinuousQuery<TimeoutChange>
{
    private ChangeVectorOffset _offset;

    public AllEvents(RavenDbReadJournal ravendb, Channel<EventEnvelope> channel, ChangeVectorOffset offset) : base(ravendb, channel)
    {
        _offset = offset;
    }

    protected override IChangesObservable<TimeoutChange> Subscribe(IDatabaseChanges changes)
    {
        return new TimeoutObservable(Ravendb.Storage.QueryConfiguration.RefreshInterval);
    }

    protected override async Task Query()
    {
        using var session = Ravendb.Store.Instance.OpenAsyncSession();
        var q = session.Advanced.AsyncDocumentQuery<Journal.Types.Event>(nameof(Journal.EventsByTagAndChangeVector));
        q = _offset.ApplyOffset(q);
        using var cts = Ravendb.Store.GetCancellationTokenSource(useSaveChangesTimeout: false);
        await using var results = await session.Advanced.StreamAsync(q, cts.Token);
        while (await results.MoveNextAsync())
        {
            var @event = results.Current.Document;
            var persistent = Journal.Types.Event.Deserialize(Ravendb.Storage.Serialization, @event, ActorRefs.NoSender);
            _offset = new ChangeVectorOffset(results.Current.ChangeVector);
            var e = new EventEnvelope(_offset, @event.PersistenceId, @event.SequenceNr, persistent.Payload,
                @event.Timestamp, @event.Tags);
            await Channel.Writer.WriteAsync(e, cts.Token);
        }
    }
}