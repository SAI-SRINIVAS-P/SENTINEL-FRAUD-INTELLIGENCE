using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Fraud.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace Fraud.Api.Auth;

public sealed class JwtTokenService(IConfiguration configuration, UserManager<IdentityUser> userManager)
{
    public async Task<AuthResponse> CreateAsync(IdentityUser user)
    {
        var issuer = configuration["Jwt:Issuer"] ?? "FraudRiskPlatform";
        var audience = configuration["Jwt:Audience"] ?? "FraudRiskDashboard";
        var key = configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
        if (Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Jwt:Key must be at least 32 bytes long.");
        var expires = DateTimeOffset.UtcNow.AddHours(4);
        var roles = (await userManager.GetRolesAsync(user)).ToArray();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Email ?? user.UserName ?? "analyst")
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow, expires.UtcDateTime, credentials);
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), user.Email ?? string.Empty, roles, expires);
    }
}
