using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IoTHunter.Management.Infrastructure.Options;

namespace IoTHunter.Management.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly AuthOptions _authOptions;

    public AuthController(AuthOptions authOptions)
    {
        _authOptions = authOptions;
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (request.Username == _authOptions.DefaultAdminUsername &&
            request.Password == _authOptions.DefaultAdminPassword)
        {
            var token = GenerateJwtToken(request.Username);
            return Ok(new { token, username = request.Username });
        }

        return Unauthorized(new { message = "Invalid credentials" });
    }

    private string GenerateJwtToken(string username)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_authOptions.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim(ClaimTypes.Name, username) };

        var token = new JwtSecurityToken(
            issuer: "IoTHunter.Management",
            audience: "IoTHunter.WebUI",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_authOptions.TokenExpirationHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed record LoginRequest(string Username, string Password);
