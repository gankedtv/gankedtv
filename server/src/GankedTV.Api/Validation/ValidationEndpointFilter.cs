using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Problems;

namespace GankedTV.Api.Validation;

/// <summary>
/// Endpoint filter that runs DataAnnotations validation on the first argument of type
/// <typeparamref name="T"/> in the endpoint signature. On failure, short-circuits with
/// a 400 <c>ValidationProblemDetails</c> response whose <c>errors</c> dictionary is
/// keyed by property name.
/// </summary>
public sealed class ValidationEndpointFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var target = context.Arguments.OfType<T>().FirstOrDefault();
        if (target is null)
        {
            // A literal JSON `null` body deserializes to a null reference; short-circuit
            // with the same shape that ProblemResults.InvalidBody() produces so handler-side
            // defensive guards (if the filter is ever bypassed) return an identical envelope.
            return ProblemResults.InvalidBody();
        }

        var ctx = new ValidationContext(target);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(target, ctx, results, validateAllProperties: true))
        {
            return await next(context);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            var members = result.MemberNames.DefaultIfEmpty(string.Empty);
            foreach (var member in members)
            {
                var key = string.IsNullOrEmpty(member) ? "body" : member;
                if (!errors.TryGetValue(key, out var existing))
                {
                    errors[key] = [result.ErrorMessage ?? "Invalid value."];
                }
                else
                {
                    errors[key] = [.. existing, result.ErrorMessage ?? "Invalid value."];
                }
            }
        }

        return Results.ValidationProblem(errors);
    }
}

public static class ValidationEndpointFilterExtensions
{
    /// <summary>
    /// Attach DataAnnotations validation for the request body of type <typeparamref name="T"/>.
    /// </summary>
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter<ValidationEndpointFilter<T>>();
}
