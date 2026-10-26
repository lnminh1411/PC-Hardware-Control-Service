using Microsoft.Extensions.Hosting;

namespace PcHwCtl.Service;

public sealed class FanBackgroundService : BackgroundService
{
    private readonly EngineHost _host;

    public FanBackgroundService(EngineHost host)
    {
        _host = host;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _host.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _host.ShutdownAll();
        await base.StopAsync(cancellationToken);
    }
}
