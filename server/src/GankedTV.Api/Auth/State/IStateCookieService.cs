namespace GankedTV.Api.Auth.State;

public interface IStateCookieService
{
    string IssueState(string? returnTo);
    StateValidationResult ValidateState(string stateParam, string? cookieValue);
}

public readonly record struct StateValidationResult(bool Ok, string? ReturnTo)
{
    public static StateValidationResult Invalid => new(false, null);
    public static StateValidationResult Valid(string? returnTo) => new(true, returnTo);
}
