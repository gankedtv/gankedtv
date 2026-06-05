using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GankedTV.Api.Tests.Validation;

public class ValidationEndpointFilterTests
{
    private sealed class RequiredModel
    {
        [Required(ErrorMessage = "Name required")]
        public string? Name { get; set; }
    }

    private sealed class MultiErrorModel : IValidatableObject
    {
        // Object-level errors: two with no member name (→ "body", second one appends) and one
        // with a named member (→ "Field"). Covers the empty/named member branches, the first-seen
        // vs. already-present key-aggregation branches, and the null-ErrorMessage fallback (both
        // body results omit a message, so the "Invalid value." default fires on add and append).
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield return new ValidationResult(errorMessage: null);
            yield return new ValidationResult("named error", new[] { "Field" });
            yield return new ValidationResult(errorMessage: null);
        }
    }

    private static EndpointFilterInvocationContext Context(object arg) =>
        EndpointFilterInvocationContext.Create(new DefaultHttpContext(), arg);

    [Fact]
    public async Task InvokeAsync_WhenValid_CallsNext()
    {
        var filter = new ValidationEndpointFilter<RequiredModel>();
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok("ok"));
        };

        var result = await filter.InvokeAsync(Context(new RequiredModel { Name = "valid" }), next);

        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok<string>>();
    }

    [Fact]
    public async Task InvokeAsync_WhenPropertyInvalid_ShortCircuitsKeyedByPropertyName()
    {
        var filter = new ValidationEndpointFilter<RequiredModel>();
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        var result = await filter.InvokeAsync(Context(new RequiredModel { Name = null }), next);

        nextCalled.Should().BeFalse();
        var problem = result.Should().BeOfType<ProblemHttpResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var details = problem.ProblemDetails.Should().BeOfType<HttpValidationProblemDetails>().Subject;
        details.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task InvokeAsync_AggregatesErrorsByKey()
    {
        var filter = new ValidationEndpointFilter<MultiErrorModel>();
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(Results.Ok());

        var result = await filter.InvokeAsync(Context(new MultiErrorModel()), next);

        var problem = result.Should().BeOfType<ProblemHttpResult>().Subject;
        var details = problem.ProblemDetails.Should().BeOfType<HttpValidationProblemDetails>().Subject;
        details.Errors["body"].Should().BeEquivalentTo("Invalid value.", "Invalid value.");
        details.Errors["Field"].Should().BeEquivalentTo("named error");
    }

    [Fact]
    public async Task InvokeAsync_WhenTargetMissing_ReturnsInvalidBody()
    {
        var filter = new ValidationEndpointFilter<RequiredModel>();
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(Results.Ok());

        // No RequiredModel argument (a literal JSON null body deserializes to null).
        var result = await filter.InvokeAsync(Context("unrelated-arg"), next);

        result.Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
