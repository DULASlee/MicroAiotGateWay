namespace IoTHunter.Management.Infrastructure.Options;

public sealed class AuthOptions
{
    public string DefaultAdminUsername { get; set; } = "admin";
    public string DefaultAdminPassword { get; set; } = "admin123";
    public string JwtSecret { get; set; } = "aB3xK9mP2qR7tW5vY1zC4eF6hJ8lN0pQ3sU9wX2yZ5aB8cD1eF4gH7Z9iJ6kLmNo";
    public int TokenExpirationHours { get; set; } = 8;
}
