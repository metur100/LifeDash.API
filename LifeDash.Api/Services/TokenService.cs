using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LifeDash.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace LifeDash.Api.Services;

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "lifedash";
    public string Audience { get; set; } = "lifedash";
    public int ExpiryHours { get; set; } = 72;
}

public class TokenService(JwtOptions options)
{
    public string Create(User user)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("name", user.DisplayName)
        };

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(options.ExpiryHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
