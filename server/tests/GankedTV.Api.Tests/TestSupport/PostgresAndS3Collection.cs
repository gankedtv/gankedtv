namespace GankedTV.Api.Tests.TestSupport;

// Combines Postgres + S3 fixtures so end-to-end tests that need both DB and storage land
// in a single collection. Postgres-only tests stay on the Postgres* collections — adding the
// S3 container to their collection would slow the suite for no win.
[CollectionDefinition("PostgresAndS3")]
public sealed class PostgresAndS3Collection
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<S3Fixture>
{
}
