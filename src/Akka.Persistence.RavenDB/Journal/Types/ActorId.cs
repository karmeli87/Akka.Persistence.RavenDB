﻿namespace Akka.Persistence.RavenDb.Journal.Types;

// we need this to be an unmodified entity, so we can have a subscription for PersistenceIds() without duplicates
public class ActorId
{
    public string PersistenceId;
    public DateTime CreatedAt;
}
