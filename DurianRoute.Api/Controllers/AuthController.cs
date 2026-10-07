using DurianRoute.Api.Auth;
using DurianRoute.Api.Data;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DurianRoute.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(DurianDbContext db, TokenService tokens) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == request.UserName, ct);
        if (user is null) return Unauthorized();

        var result = new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed) return Unauthorized();

        return tokens.Issue(user);
    }
}
