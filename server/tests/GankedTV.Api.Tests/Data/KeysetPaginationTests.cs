using FluentAssertions;
using GankedTV.Api.Pagination;

namespace GankedTV.Api.Tests.Data;

public class KeysetPaginationTests
{
    private sealed record Row(DateTimeOffset CreatedAt, Guid Id);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Row MakeRow(int minutesAfterT0, byte idByte = 0) =>
        new(T0.AddMinutes(minutesAfterT0), new Guid(idByte, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]));

    [Fact]
    public void WhereKeysetBefore_ReturnsOnlyOlderRows()
    {
        var rows = new[] { MakeRow(0), MakeRow(1), MakeRow(2) }.AsQueryable();
        var cursor = MakeRow(1);

        var result = rows.WhereKeysetBefore(r => r.CreatedAt, r => r.Id, cursor.CreatedAt, cursor.Id).ToList();

        result.Should().ContainSingle().Which.CreatedAt.Should().Be(T0);
    }

    [Fact]
    public void WhereKeysetBefore_TiedTimestamp_BreaksTieOnId()
    {
        var older = MakeRow(0, idByte: 1);
        var newer = MakeRow(0, idByte: 9);
        var rows = new[] { older, newer }.AsQueryable();

        var result = rows.WhereKeysetBefore(r => r.CreatedAt, r => r.Id, newer.CreatedAt, newer.Id).ToList();

        result.Should().ContainSingle().Which.Id.Should().Be(older.Id);
    }

    [Fact]
    public void WhereKeysetAfter_ReturnsOnlyNewerRows()
    {
        var rows = new[] { MakeRow(0), MakeRow(1), MakeRow(2) }.AsQueryable();
        var cursor = MakeRow(1);

        var result = rows.WhereKeysetAfter(r => r.CreatedAt, r => r.Id, cursor.CreatedAt, cursor.Id).ToList();

        result.Should().ContainSingle().Which.CreatedAt.Should().Be(T0.AddMinutes(2));
    }

    [Fact]
    public void WhereKeysetAfter_TiedTimestamp_BreaksTieOnId()
    {
        var lower = MakeRow(0, idByte: 1);
        var higher = MakeRow(0, idByte: 9);
        var rows = new[] { lower, higher }.AsQueryable();

        var result = rows.WhereKeysetAfter(r => r.CreatedAt, r => r.Id, lower.CreatedAt, lower.Id).ToList();

        result.Should().ContainSingle().Which.Id.Should().Be(higher.Id);
    }

    [Fact]
    public void TakePage_MoreRowsThanLimit_TrimsAndBuildsCursor()
    {
        var rows = new List<Row> { MakeRow(2), MakeRow(1), MakeRow(0) };

        var (page, nextCursor) = KeysetPagination.TakePage(rows, 2, r => r.CreatedAt, r => r.Id);

        page.Should().HaveCount(2);
        nextCursor.Should().NotBeNull();
        KeysetCursor.TryParse(nextCursor, out var createdAt, out var id).Should().BeTrue();
        createdAt.Should().Be(page[^1].CreatedAt);
        id.Should().Be(page[^1].Id);
    }

    [Fact]
    public void TakePage_ExactLimit_NoCursor()
    {
        var rows = new List<Row> { MakeRow(1), MakeRow(0) };

        var (page, nextCursor) = KeysetPagination.TakePage(rows, 2, r => r.CreatedAt, r => r.Id);

        page.Should().HaveCount(2);
        nextCursor.Should().BeNull();
    }

    [Fact]
    public void TakePage_Empty_NoCursor()
    {
        var (page, nextCursor) = KeysetPagination.TakePage(new List<Row>(), 5, r => r.CreatedAt, r => r.Id);

        page.Should().BeEmpty();
        nextCursor.Should().BeNull();
    }
}
