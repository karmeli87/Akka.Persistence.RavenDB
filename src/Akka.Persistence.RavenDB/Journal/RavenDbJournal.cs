﻿using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence.Journal;
using Akka.Persistence.RavenDb.Journal.Types;
using Akka.Persistence.RavenDb.Query;
using Nito.AsyncEx;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Akka.Persistence.RavenDb.Journal
{
    public class RavenDbJournal : AsyncWriteJournal, IWithUnboundedStash
    {
        private static readonly RavenDbPersistence Persistence = Context.System.WithExtension<RavenDbPersistence, RavenDbPersistenceProvider>();
        private readonly RavenDbJournalConfiguration _configuration;
        private readonly Akka.Serialization.Serialization _serialization;
        private readonly RavenDbStore _store;
        
        //requests for the highest sequence number may be made concurrently to writes executing for the same persistenceId.
        private readonly ConcurrentDictionary<string, AsyncReaderWriterLock> _lockPerActor = new ConcurrentDictionary<string, AsyncReaderWriterLock>();
        
        public RavenDbJournal() : this(RavenDbPersistence.Get(Context.System).JournalConfiguration)
        {
        }

        // This constructor is needed because config can come from both Akka.Persistence and Akka.Cluster.Sharding
        public RavenDbJournal(Config config) : this(new RavenDbJournalConfiguration(config))
        {
        }

        private RavenDbJournal(RavenDbJournalConfiguration configuration)
        {
            _configuration = configuration;
            _serialization = Context.System.Serialization;
            _store = new RavenDbStore(_configuration);
        }

        public async Task<object> Initialize()
        {
            if (_configuration.AutoInitialize)
            {
                return await _store.CreateDatabaseAsync();
            }

            return new Status.Success(NotUsed.Instance);
        }

        protected override void PreStart()
        {
            base.PreStart();

            // Call the Initialize method and pipe the result back to signal that
            // the database is ready to use, if it needs to be initialized
            Initialize().PipeTo(Self);

            // WaitingForInitialization receive handler will wait for a success/fail
            // result back from the Initialize method
            BecomeStacked(WaitingForInitialization);
        }

        public IStash Stash { get; set; }

        private bool WaitingForInitialization(object message)
        {
            switch (message)
            {
                // Database is already created or successfully created all needed databases
                case Status.Success _:
                    UnbecomeStacked();
                    // Unstash all messages received while we were initializing our database
                    Stash.UnstashAll();
                    break;

                case Status.Failure fail:
                    // Failed creating database. Log an error and stop the actor.
                    //_log.Error(fail.Cause, "Failure during {0} initialization.", Self);
                    Context.Stop(Self);
                    break;

                default:
                    // By default, stash all received messages while we're waiting for the
                    // Initialize method.
                    Stash.Stash();
                    break;
            }
            return true;
        }

        protected override bool ReceivePluginInternal(object message)
        {
            switch (message)
            {
                case RavenDbReadJournal.CreateIndexesMessage create:
                    var sender = Sender;
                    _store.EnsureIndexesCreated().PipeTo(sender);
                    return true;
                
                default:
                    return false;
            }
        }

        protected override void PostStop()
        {
            _store.Stop();
            _store.Dispose();
            base.PostStop();
        }

        public override async Task ReplayMessagesAsync(
            IActorContext context, 
            string persistenceId, 
            long fromSequenceNr, 
            long toSequenceNr, 
            long max, 
            Action<IPersistentRepresentation> recoveryCallback)
        {
            using var session = _store.Instance.OpenAsyncSession();
            using var cts = _store.GetCancellationTokenSource(useSaveChangesTimeout: false);
            session.Advanced.SessionInfo.SetContext(persistenceId);
            
            await using var results = await session.Advanced.StreamAsync<Types.Event>(startsWith: _store.GetEventPrefix(persistenceId), startAfter: _store.GetSequenceId(persistenceId, fromSequenceNr - 1), token: cts.Token);
            while (max > 0 && await results.MoveNextAsync())
            {
                var message = results.Current.Document;
                if (message.SequenceNr > toSequenceNr)
                    return;

                var persistent = Types.Event.Deserialize(_serialization, message, context.Sender);
                recoveryCallback(persistent);
                max--;
            }
        }

        public override async Task<long> ReadHighestSequenceNrAsync(string persistenceId, long fromSequenceNr)
        {
            using (await GetLocker(persistenceId).WriterLockAsync())
            {
                using var session = _store.Instance.OpenAsyncSession();
                using var cts = _store.GetCancellationTokenSource(useSaveChangesTimeout: true);
                session.Advanced.SessionInfo.SetContext(persistenceId);

                var metadata = await session.LoadAsync<Metadata>(_store.GetMetadataId(persistenceId), cts.Token);
                return metadata?.MaxSequenceNr ?? 0;

                // TODO read last event with the prefix of 'persistenceId' 
            }
        }

        protected override async Task<IImmutableList<Exception?>> WriteMessagesAsync(IEnumerable<AtomicWrite> messages)
        {
            var builder = ImmutableList.CreateBuilder<Exception?>();
            using var cts = _store.GetCancellationTokenSource(useSaveChangesTimeout: true);
            var writes = new Dictionary<string, Task>();
            var original = messages.ToList();

            // we can have multiple atomic writes with the _same_ actor :(
            foreach (var atomicWrites in original.GroupBy(m => m.PersistenceId))
            {
                var t = AtomicWriteForActor(atomicWrites, cts);
                writes.Add(atomicWrites.Key, t);
            }

            foreach (var atomicWrite in original)
            {
                try
                {
                    await writes[atomicWrite.PersistenceId]; // unwrap the exception if needed
                    builder.Add(null);
                }
                catch (Exception e)
                {
                    builder.Add(e);
                }
            }
           
            return builder.ToImmutable();
        }

        // we group multiple atomic writes for a given actor into a single atomic write
        private async Task AtomicWriteForActor(IGrouping<string, AtomicWrite> atomicWrites, CancellationTokenSource cts)
        {
            var persistenceId = atomicWrites.Key;
            using var _ = await GetLocker(persistenceId).ReaderLockAsync(cts.Token);
            using var session = _store.Instance.OpenAsyncSession();
            session.Advanced.SessionInfo.SetContext(persistenceId);
            // TODO stav: might need to set transaction mode here
            // session.Advanced.SetTransactionMode(mode);

            var highest = long.MinValue;
            var lowest = long.MaxValue;

            foreach (var atomicWrite in atomicWrites)
            {
                lowest = Math.Min(lowest, atomicWrite.LowestSequenceNr);
                highest = Math.Max(highest, atomicWrite.HighestSequenceNr);

                var payload = (IImmutableList<IPersistentRepresentation>)atomicWrite.Payload;

                foreach (var representation in payload)
                {
                    var id = _store.GetSequenceId(representation.PersistenceId, representation.SequenceNr);
                    var journalEvent = Types.Event.Serialize(_serialization, representation);
                    // events are immutable and should always be new 
                    await session.StoreAsync(journalEvent, changeVector: string.Empty, id, cts.Token);
                }
            }

            var metadataId = _store.GetMetadataId(persistenceId);
            session.Advanced.Defer(new PatchCommandData(metadataId, changeVector: null, patch: new PatchRequest
            {
                Script = Metadata.UpdateScript,
                Values = new Dictionary<string, object>
                {
                    [nameof(Metadata.MaxSequenceNr)] = highest,
                    ["check"] = lowest - 1,
                }
            }, new PatchRequest
            {
                Script = Metadata.CreateNewScript,
                Values = new Dictionary<string, object>
                {
                    [nameof(Metadata.PersistenceId)] = persistenceId,
                    [nameof(Metadata.MaxSequenceNr)] = highest,
                    ["collection"] = _store.EventsMetadataCollection,
                    ["type"] = _store.Instance.Conventions.FindClrTypeName(typeof(Metadata)),
                    ["collection2"] = _store.Instance.Conventions.FindCollectionName(typeof(ActorId)),
                    ["type2"] = _store.Instance.Conventions.FindClrTypeName(typeof(ActorId))
                }
            }));

            if (Persistence.QueryConfiguration.WaitForNonStale) // used for tests
                session.Advanced.WaitForIndexesAfterSaveChanges();
            
            await session.SaveChangesAsync(cts.Token);
        }

        protected override async Task DeleteMessagesToAsync(string persistenceId, long toSequenceNr)
        {
            var batch = 1024;
            int deleted;
            do
            {
                deleted = 0;
                using var cts = _store.GetCancellationTokenSource(useSaveChangesTimeout: false);
                using var session = _store.Instance.OpenAsyncSession();
                await using var results = await session.Advanced.StreamAsync<Types.Event>(startsWith: _store.GetEventPrefix(persistenceId), pageSize: batch, token: cts.Token);
                while (await results.MoveNextAsync())
                {
                    var current = results.Current.Document;
                    if (current.SequenceNr > toSequenceNr)
                        break;

                    deleted++;
                    session.Delete(current.Id);
                }
                await session.SaveChangesAsync(cts.Token);
            } while (deleted == batch);
        }

        private AsyncReaderWriterLock GetLocker(string persistenceId) => _lockPerActor.GetOrAdd(persistenceId, new AsyncReaderWriterLock());
    }
}
