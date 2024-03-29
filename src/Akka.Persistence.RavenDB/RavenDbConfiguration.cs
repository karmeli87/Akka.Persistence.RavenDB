﻿using Akka.Configuration;
using Raven.Client.Documents.Conventions;
using System.Security.Cryptography.X509Certificates;

namespace Akka.Persistence.RavenDb
{
    public class RavenDbConfiguration
    {
        public readonly string Name;
        public readonly string[] Urls;
        public readonly string? CertificatePath;
        public readonly string? CertPassword;
        public readonly Version? HttpVersion;
        public readonly bool? DisableTcpCompression;
        public readonly TimeSpan SaveChangesTimeout;
        /// <summary>
        /// Flag determining whether the database should be automatically initialized.
        /// </summary>
        public bool AutoInitialize { get; private set; }

        //TODO stav: currently impossible to change timeout of stream (only save-changes timeout). Should add support for this?

        public RavenDbConfiguration(Config config)
        {
            Name = config.GetString("name") ?? throw new ArgumentException("name must be provided");
            Urls = config.GetStringList("urls")?.ToArray() ?? throw new ArgumentException("urls must be provided");
            CertificatePath = config.GetString("certificate-path");
            AutoInitialize = config.GetBoolean("auto-initialize", true);

            //TODO stav: DisposeCertificate in DocumentConventions?
            
            if (string.IsNullOrEmpty(CertificatePath) == false)
            {
                CertPassword = Environment.GetEnvironmentVariable("RAVEN_CERTIFICATE_PASSWORD");
            }

            var httpVersion = config.GetString("http-version");
            if (string.IsNullOrEmpty(httpVersion) == false)
            {
                //TODO stav: error gets swallowed in akka and doesn't bubble up
                HttpVersion = Version.Parse(httpVersion);
            }
            
            DisableTcpCompression = config.GetBoolean("disable-tcp-compression");
            SaveChangesTimeout = config.GetTimeSpan("save-changes-timeout", TimeSpan.FromSeconds(15));
        }

        public DocumentConventions ToDocumentConventions()
        {
            var conventions = new DocumentConventions();

            conventions.HttpVersion = HttpVersion ?? conventions.HttpVersion;
            conventions.DisableTcpCompression = DisableTcpCompression ?? conventions.DisableTcpCompression;
            
            return conventions;
        }
    }

    public class RavenDbJournalConfiguration : RavenDbConfiguration
    {
        public const string Identifier = "akka.persistence.journal.ravendb";
        public RavenDbJournalConfiguration(Config config) : base(config)
        {
            if (config == null)
                throw new ArgumentNullException("config",
                    "RavenDB journal settings cannot be initialized, because required HOCON section couldn't been found");
        }
    }

    public class RavenDbSnapshotConfiguration : RavenDbConfiguration
    {
        public const string Identifier = "akka.persistence.snapshot-store.ravendb";

        public RavenDbSnapshotConfiguration(Config config) : base(config)
        {
            if (config == null)
                throw new ArgumentNullException("config",
                    "RavenDB snapshot settings cannot be initialized, because required HOCON section couldn't been found");
        }
    }

    public class RavenDbQueryConfiguration
    {
        public const string Identifier = "akka.persistence.query.ravendb";

        public readonly TimeSpan RefreshInterval;
        public readonly int MaxBufferSize;
        public readonly bool WaitForNonStale;

        public RavenDbQueryConfiguration(Config config)
        {
            if (config == null)
                throw new ArgumentNullException("config",
                    "RavenDB query settings cannot be initialized, because required HOCON section couldn't been found");

            RefreshInterval = config.GetTimeSpan("refresh-interval", @default: TimeSpan.FromSeconds(3));
            MaxBufferSize = config.GetInt("max-buffer-size", @default: 64 * 1024);
            WaitForNonStale = config.GetBoolean("wait-for-non-stale");
        }
    }

}
