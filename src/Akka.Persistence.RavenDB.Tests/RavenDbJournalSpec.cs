using Akka.Persistence.TCK.Journal;
using Raven.Client.Documents;

namespace Akka.Persistence.RavenDB.Tests
{
    public class RavenDbJournalSpec : JournalSpec, IClassFixture<RavenDbFixture>
    {
        private readonly IDocumentStore _store;
        protected override bool SupportsRejectingNonSerializableObjects { get; } = false;

        public RavenDbJournalSpec(RavenDbFixture database) 
            : base(database.CreateSpecConfigAndStore(out var store), "RavenDbJournalSpec")
        {
            _store = store;
            Initialize();
        }

        protected override void Dispose(bool disposing)
        {
            _store.Dispose();
            base.Dispose(disposing);
        }
    }
}