using NpgsqlTypes;

namespace GankedTV.Api.Data.Entities;

public class Game
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public required string Tag { get; set; }
    public string? CoverUrl { get; set; }

    // External ID from IGDB (igdb.com) — lets us fetch cover art, release
    // date, genres, etc. from their API without owning that metadata.
    public int? IgdbId { get; set; }

    // The IGDB image_id of the cover we last mirrored. Lets the catalog sync re-download a
    // cover only when it actually changed (and tells a placeholder — which has null here —
    // apart from real art). Null for seeded placeholders and games never linked to IGDB.
    public string? CoverImageId { get; set; }

    // True only for rows the IGDB importer created. Gates display-name refresh so the curated
    // seed rows (incl. ones the importer adopted by name) are never renamed by an upstream change.
    public bool IgdbManaged { get; set; }

    // Postgres-managed `tsvector` (GENERATED ALWAYS AS … STORED) over `name`. Powers the
    // long-query branch of /search?type=games via plainto_tsquery + ts_rank_cd.
    public NpgsqlTsVector SearchVector { get; private set; } = null!;
}
