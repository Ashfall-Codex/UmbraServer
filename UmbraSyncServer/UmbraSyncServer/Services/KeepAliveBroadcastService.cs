using UmbraSync.API.SignalR;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.SignalR;

namespace MareSynchronosServer.Services;

public sealed class KeepAliveBroadcastService : IHostedService, IDisposable
{
    private readonly ILogger<KeepAliveBroadcastService> _logger;
    private readonly IHubContext<MareHub, IMareHub> _hubContext;
    private readonly IConfigurationService<ServerConfiguration> _configService;
    private Timer? _timer;
    private int _running;
    private int _tickCount;
    private byte[] _paddingPayload = Array.Empty<byte>();
    private TimeSpan _interval = TimeSpan.FromSeconds(5);

    public KeepAliveBroadcastService(ILogger<KeepAliveBroadcastService> logger,
        IHubContext<MareHub, IMareHub> hubContext,
        IConfigurationService<ServerConfiguration> configService)
    {
        _logger = logger;
        _hubContext = hubContext;
        _configService = configService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ApplyConfiguration();
        _logger.LogInformation(
            "KeepAliveBroadcastService started (per-user push every {Sec}s, padding={Bytes}B)",
            _interval.TotalSeconds, _paddingPayload.Length);
        _timer = new Timer(OnTick, null, _interval, _interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void ApplyConfiguration()
    {
        int paddingBytes = Math.Max(0, _configService.GetValueOrDefault(
            nameof(ServerConfiguration.KeepAlivePaddingBytes), 2048));
        int intervalSec = Math.Max(1, _configService.GetValueOrDefault(
            nameof(ServerConfiguration.KeepAliveIntervalSeconds), 5));

        _paddingPayload = paddingBytes == 0 ? Array.Empty<byte>() : new byte[paddingBytes];
        _interval = TimeSpan.FromSeconds(intervalSec);
    }

    private void OnTick(object? state)
    {
        // Re-entrancy guard: skip a tick if the previous one is still running (e.g. backplane hiccup).
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        try
        {
            var connections = MareHub.GetActiveUserConnections();
            int tickNum = Interlocked.Increment(ref _tickCount);

            if (connections.Count == 0)
            {
                if (tickNum % 720 == 1) // ~1 fois par heure quand idle
                    _logger.LogInformation("KeepAlive tick #{N}: no active connections", tickNum);
                return;
            }

            byte[] padding = _paddingPayload;
            int dispatched = 0;
            int failed = 0;
            foreach (var (uid, connectionId) in connections)
            {
                try
                {
                    // Fire-and-forget: we don't await; SignalR queues the send, and a stale
                    // connection just returns silently (kernel TCP will mark it dead later).
                    _ = _hubContext.Clients.Client(connectionId).Client_KeepAlive(padding);
                    dispatched++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "KeepAlive dispatch failed for {UID}/{ConnId}", uid, connectionId);
                }
            }

            // Information seulement si on a des échecs OU 1 fois toutes les ~5 min en
            // résumé. Les ticks normaux passent en Trace pour ne pas polluer journalctl.
            if (failed > 0)
            {
                _logger.LogWarning(
                    "KeepAlive tick #{N}: dispatched={Count} failed={Failed} totalConns={Total}",
                    tickNum, dispatched, failed, connections.Count);
            }
            else if (tickNum % 60 == 1) // ~5 min à 5s d'intervalle
            {
                _logger.LogInformation(
                    "KeepAlive tick #{N}: dispatched={Count} totalConns={Total} payloadBytes={Bytes}",
                    tickNum, dispatched, connections.Count, padding.Length);
            }
            else
            {
                _logger.LogTrace(
                    "KeepAlive tick #{N}: dispatched={Count} totalConns={Total}",
                    tickNum, dispatched, connections.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KeepAlive tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
