using System.Security.Claims;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Auth.Jwt;

public interface IJwtService
{
    string Issue(User user);
    ClaimsPrincipal? Validate(string token);
}
