using Fraud.Api.Auth;
using Fraud.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Fraud.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn,
    JwtTokenService tokens, ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var existing = await users.FindByEmailAsync(request.Email);
        if (existing is not null) return Conflict(new { message = "An account with this email already exists." });
        var user = new IdentityUser { UserName = request.Email.Trim(), Email = request.Email.Trim(), EmailConfirmed = true };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded) return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            ["identity"] = created.Errors.Select(x => x.Description).ToArray()
        }));
        await users.AddToRoleAsync(user, "Analyst");
        logger.LogInformation("Created analyst account {UserId}", user.Id);
        return StatusCode(StatusCodes.Status201Created, await tokens.CreateAsync(user));
    }

    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Keep failure text the same for unknown users and bad passwords.
            return Unauthorized(new { message = "Invalid email or password." });
        }
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) return Unauthorized(new { message = "Invalid email or password." });
        return Ok(await tokens.CreateAsync(user));
    }
}
