namespace GankedTV.Api.Tests.TestSupport;

// The integration suite is split into several collections so xUnit can run them in
// parallel (collections are its unit of parallelism; tests within one still serialize).
// Each collection gets its own PostgresFixture instance and therefore its own database
// cloned from the migrated template — no cross-collection data bleed, and ResetAsync only
// truncates the collection's own database. Grouping is by domain so related write
// patterns stay serialized; sizes are roughly balanced so no collection dominates the
// wall clock. Tests that mutate process-global env vars must live in PostgresServices.

[CollectionDefinition("PostgresClips")]
public sealed class PostgresClipsCollection : ICollectionFixture<PostgresFixture>
{
}

[CollectionDefinition("PostgresAuth")]
public sealed class PostgresAuthCollection : ICollectionFixture<PostgresFixture>
{
}

[CollectionDefinition("PostgresSocial")]
public sealed class PostgresSocialCollection : ICollectionFixture<PostgresFixture>
{
}

[CollectionDefinition("PostgresAdmin")]
public sealed class PostgresAdminCollection : ICollectionFixture<PostgresFixture>
{
}

[CollectionDefinition("PostgresDiscovery")]
public sealed class PostgresDiscoveryCollection : ICollectionFixture<PostgresFixture>
{
}

[CollectionDefinition("PostgresServices")]
public sealed class PostgresServicesCollection : ICollectionFixture<PostgresFixture>
{
}
