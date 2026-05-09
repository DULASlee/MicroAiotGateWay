using Microsoft.AspNetCore.SignalR;

namespace BackendProcessor.Hubs;

public sealed class MonitoringHub : Hub
{
    public async Task SendMetrics(object metrics)
    {
        await Clients.All.SendAsync("MetricsUpdate", metrics);
    }
}
