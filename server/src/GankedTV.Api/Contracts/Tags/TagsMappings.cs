using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Tags;

public static class TagsMappings
{
    public static TagSummary ToSummary(this Tag tag, int clipCount = 0) =>
        new(tag.Id, tag.Slug, tag.Name, clipCount);

    public static TagDetail ToDetail(this Tag tag, int clipCount) =>
        new(tag.Id, tag.Slug, tag.Name, clipCount);
}
