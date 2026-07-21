using FluentAssertions;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Tests.Data;

public class ClipFailureReasonsTests
{
    [Theory]
    [InlineData(ClipFailureReasons.SourceUnavailable)]
    [InlineData(ClipFailureReasons.FetchFailed)]
    [InlineData(ClipFailureReasons.TranscodeFailed)]
    [InlineData(ClipFailureReasons.ThumbnailFailed)]
    [InlineData(null)]
    public void IsRetryable_InfraAndUnknownReasons_AreRetryable(string? reason)
    {
        ClipFailureReasons.IsRetryable(reason).Should().BeTrue();
    }

    [Theory]
    [InlineData(ClipFailureReasons.SourceTooLong)]
    [InlineData(ClipFailureReasons.SourceTooLarge)]
    [InlineData(ClipFailureReasons.TrimUnverifiable)]
    public void IsRetryable_ContentRejections_AreNotRetryable(string reason)
    {
        ClipFailureReasons.IsRetryable(reason).Should().BeFalse();
    }

    [Fact]
    public void NonRetryableReasons_AreExactlyTheContentRejections()
    {
        // The requeue query (ClipMediaJobStore) filters on this same set, so it must stay in lockstep
        // with IsRetryable: every listed reason is non-retryable, and nothing else is.
        ClipFailureReasons.NonRetryableReasons.Should().BeEquivalentTo(
            new[]
            {
                ClipFailureReasons.SourceTooLong,
                ClipFailureReasons.SourceTooLarge,
                ClipFailureReasons.TrimUnverifiable,
            });
        ClipFailureReasons.NonRetryableReasons.Should().OnlyContain(r => !ClipFailureReasons.IsRetryable(r));
    }
}
