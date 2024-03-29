﻿using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence.RavenDb.Journal;
using Akka.Persistence.RavenDb.Journal.Types;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.Http;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Commands;
using Raven.Client.ServerWide.Operations;
using Sparrow.Json;
using System.Security.Cryptography.X509Certificates;

namespace Akka.Persistence.RavenDb
{
    public class RavenDbStore : IDisposable
    {
        public readonly RavenDbConfiguration Configuration;
        public DocumentStore Instance => _instance.Value;
        private Lazy<DocumentStore> _instance;

        public RavenDbStore(RavenDbConfiguration configuration)
        {
            Configuration = configuration;
            _instance = new Lazy<DocumentStore>(GetStore);
        }

        private DocumentStore GetStore()
        {
            X509Certificate2 cert = null;
            if (string.IsNullOrEmpty(Configuration.CertificatePath) == false)
                cert = string.IsNullOrEmpty(Configuration.CertPassword) == false
                    ? new X509Certificate2(Configuration.CertificatePath, Configuration.CertPassword)
                    : new X509Certificate2(Configuration.CertificatePath);

            var store = new DocumentStore
            {
                Urls = Configuration.Urls,
                Conventions = Configuration.ToDocumentConventions(),
                Database = Configuration.Name,
                Certificate = cert
            };

            store.Conventions.LoadBalanceBehavior = LoadBalanceBehavior.UseSessionContext;
            store.Initialize();

            return store;
        }

        private readonly CancellationTokenSource _stopTokenSource = new CancellationTokenSource();

        public void Stop()
        {
            _stopTokenSource.Cancel();
        }

        public CancellationTokenSource GetCancellationTokenSource(bool useSaveChangesTimeout)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopTokenSource.Token);
            if (useSaveChangesTimeout)
                cts.CancelAfter(Configuration.SaveChangesTimeout);
            return cts;
        }

        public async Task<Status> CreateDatabaseAsync()
        {
            using var cts = GetCancellationTokenSource(useSaveChangesTimeout: false);
            var tries = 5;
            while (tries > 0)
            {
                try
                {
                    var record =
                        await Instance.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Configuration.Name),
                            token: cts.Token);
                    if (record != null)
                        return new Status.Success(NotUsed.Instance);

                    var res = await Instance.Maintenance.Server.SendAsync(
                        new CreateDatabaseOperation(new DatabaseRecord(Configuration.Name)), token: cts.Token);

                    using (var context = JsonOperationContext.ShortTermSingleUse())
                    {
                        await Instance.GetRequestExecutor(Configuration.Name)
                            .ExecuteAsync(new WaitForRaftIndexCommand(res.RaftCommandIndex), context, token: cts.Token);
                    }

                    return new Status.Success(NotUsed.Instance);
                }
                catch (ConcurrencyException e)
                {
                    if (e.Message.Contains("exists") == false)
                    {
                        return new Status.Failure(e);
                    }

                    // The database already exists
                    return new Status.Success(NotUsed.Instance);
                }
                catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
                {
                    return new Status.Success(NotUsed.Instance);
                }
                catch (DatabaseDisabledException e)
                {
                    //will retry on this
                }
                catch (Exception e)
                {
                    return new Status.Failure(e);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
                tries--;
            }

            return new Status.Failure(new Exception($"Failed to create database after 5 tries"));
        }

        public async Task<Status> EnsureIndexesCreated()
        {
            using var cts = GetCancellationTokenSource(false);

            var startTime = DateTime.Now;
            while (DateTime.Now - TimeSpan.FromSeconds(15) < startTime)
            {
                if (cts.IsCancellationRequested)
                    return new Status.Success(NotUsed.Instance);

                try
                {
                    var db = await Instance.Maintenance.Server.SendAsync(
                        new GetDatabaseRecordOperation(Configuration.Name), cts.Token);
                    if (db != null)
                    {
                        var res1 = await Instance.Maintenance.SendAsync(new GetIndexNamesOperation(0, int.MaxValue),
                            cts.Token);
                        if (res1.Contains(nameof(EventsByTagAndChangeVector)) &&
                            res1.Contains(nameof(ActorsByChangeVector)))
                        {
                            return new Status.Success(NotUsed.Instance);
                        }

                        await new EventsByTagAndChangeVector().ExecuteAsync(Instance, token: cts.Token);
                        await new ActorsByChangeVector().ExecuteAsync(Instance, token: cts.Token);

                        return new Status.Success(NotUsed.Instance);
                    }
                }
                catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
                {
                    //TODO stav: is cancelled due to Akka - we don't need the indexes anymore. Or due to timeout - need to return failure
                    return new Status.Success(NotUsed.Instance);
                }
                catch (DatabaseDisabledException e)
                {
                    //database locked, try again
                }
                catch (Exception e)
                {
                    return new Status.Failure(new Exception($"Failed to create indexes.", e));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            return new Status.Failure(new Exception($"Waited too long for indexes to be created."));
        }

        public string GetMetadataId(string persistenceId) => $"{EventsMetadataCollection}/{persistenceId}";

        public string GetEventPrefix(string persistenceId) => $"{EventsCollection}/{persistenceId}/";

        public string GetSequenceId(string persistenceId, long sequenceNr)
        {
            if (sequenceNr <= 0)
                sequenceNr = 0;

            return $"{GetEventPrefix(persistenceId)}{sequenceNr.ToLeadingZerosFormat()}";
        }

        public string EventsCollection => Instance.Conventions.FindCollectionName(typeof(Journal.Types.Event));
        public string EventsMetadataCollection => Instance.Conventions.FindCollectionName(typeof(Metadata));
        
        public void Dispose()
        {
            _stopTokenSource.Cancel();
            _stopTokenSource.Dispose();
            Instance?.Dispose();
        }
    }

    public class RavenDbPersistence : IExtension
    {
        public RavenDbJournalConfiguration JournalConfiguration;
        public RavenDbQueryConfiguration QueryConfiguration;
        public RavenDbSnapshotConfiguration SnapshotConfiguration;

        public readonly Akka.Serialization.Serialization Serialization;

        public RavenDbPersistence(ExtendedActorSystem system)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));

            JournalConfiguration = new RavenDbJournalConfiguration(system.Settings.Config.GetConfig(RavenDbJournalConfiguration.Identifier));
            QueryConfiguration = new RavenDbQueryConfiguration(system.Settings.Config.GetConfig(RavenDbQueryConfiguration.Identifier));
            SnapshotConfiguration = new RavenDbSnapshotConfiguration(system.Settings.Config.GetConfig(RavenDbSnapshotConfiguration.Identifier));

            Serialization = system.Serialization;
        }

        public static RavenDbPersistence Get(ActorSystem system)
        {
            return system.WithExtension<RavenDbPersistence, RavenDbPersistenceProvider>();
        }

        public static Config DefaultConfiguration()
        {
            return ConfigurationFactory.FromResource<RavenDbPersistence>("Akka.Persistence.RavenDb.reference.conf");
        }
    }
}
