using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DurianRoute.Api.Data;
using DurianRoute.Shared;
using Microsoft.IdentityModel.Tokens;

namespace DurianRoute.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "DurianRoute";
    public string Audience { get; set; } = "DurianRoute.Client";
    public string Key { get; set; } = "";
    public int ExpiryHours { get; set; } = 12;

    public SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(Key));
}

public class TokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options)
{
    public LoginResponse Issue(AppUser user)
    {
        var o = options.Value;
        var expires = DateTime.UtcNow.AddHours(o.ExpiryHours);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            o.Issuer, o.Audience, claims,
            expires: expires,
            signingCredentials: new SigningCredentials(o.SigningKey(), SecurityAlgorithms.HmacSha256));

        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), user.UserName, [user.Role], expires);
    }
}
