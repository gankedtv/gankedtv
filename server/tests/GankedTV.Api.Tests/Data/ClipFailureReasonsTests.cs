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
    public void IsRetryable_ContentRejections_AreNotRetryable(string reason)
    {
        ClipFailureReasons.IsRetryable(reason).Should().BeFalse();
    }
}
