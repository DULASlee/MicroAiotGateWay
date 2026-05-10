using IoTHunter.WebUI.Services;
using IoTHunter.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddScoped<AuthTokenHolder>();

builder.Services.AddHttpClient("Management", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiEndpoints:ManagementBaseUrl"]!);
});

builder.Services.AddScoped<ManagementApiClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("Management");
    return new ManagementApiClient(http, sp.GetRequiredService<AuthTokenHolder>());
});

builder.Services.AddHttpClient<BackendProcessorApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiEndpoints:BackendProcessorBaseUrl"]!);
});

builder.Services.AddHttpClient<SimulatorApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiEndpoints:DeviceSimulatorBaseUrl"]!);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
