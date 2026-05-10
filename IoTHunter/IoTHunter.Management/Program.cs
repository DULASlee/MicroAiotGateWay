using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IoTHunter.Management.Infrastructure.Options;
using IoTHunter.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<IoTGatewayOptions>(builder.Configuration.GetSection("IoTGateway"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<IoTGatewayOptions>>().Value);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "IoTHunter.Management",
        ValidAudience = "IoTHunter.WebUI",
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Auth:JwtSecret"]!))
    };
});
builder.Services.AddAuthorization();

builder.Services.AddHttpClient("IoTGateway", client =>
{
    var gatewayOptions = builder.Configuration.GetSection("IoTGateway").Get<IoTGatewayOptions>();
    client.BaseAddress = new Uri(gatewayOptions?.ConfigUrl ?? "http://iot-gateway.iothunter.svc.cluster.local/api/v1/config");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddControllers();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", service = "IoTHunter.Management" }));

app.Run();
