using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.SignalR;
using System.Threading.RateLimiting;

namespace MareSynchronosServer.Hubs;

public sealed class ConcurrencyFilter : IHubFilter, IDisposable
{
    private ConcurrencyLimiter _limiter;
    private int _currentLimit = 0;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly MareMetrics _metrics;
    private readonly ILogger<ConcurrencyFilter> _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public ConcurrencyFilter(
        IConfigurationService<ServerConfiguration> config,
        MareMetrics metrics,
        ILogger<ConcurrencyFilter> logger)
    {
        _config = config;
        _metrics = metrics;
        _logger = logger;

        RecreateLimiter();

        _ = Task.Run(async () =>
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var stats = _limiter?.GetStatistics();
                    if (stats != null)
                    {
                        _metrics.SetGaugeTo(MetricsAPI.GaugeHubConcurrencyAvailable, stats.CurrentAvailablePermits);
                        _metrics.SetGaugeTo(MetricsAPI.GaugeHubConcurrencyQueued, stats.CurrentQueuedCount);
                    }
                }
                catch
                {
                    // Ignore metrics errors
                }
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
        });

        _logger.LogInformation("ConcurrencyFilter initialized with limit {Limit}", _currentLimit);
    }

    private void RecreateLimiter()
    {
        var newLimit = _config.GetValueOrDefault(nameof(ServerConfiguration.HubExecutionConcurrencyLimit), 50);

        if (newLimit == _currentLimit && _limiter is not null)
        {
            return;
        }

        var oldLimit = _currentLimit;
        _currentLimit = newLimit;
        _limiter?.Dispose();
        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = newLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = newLimit * 100,
        });

        if (oldLimit != 0)
        {
            _logger.LogInformation("ConcurrencyFilter limit changed from {OldLimit} to {NewLimit}", oldLimit, newLimit);
        }
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (string.Equals(invocationContext.HubMethodName, "CheckClientHealth", StringComparison.Ordinal))
        {
            return await next(invocationContext).ConfigureAwait(false);
        }

        var ct = invocationContext.Context.ConnectionAborted;
        RateLimitLease lease;

        try
        {
            lease = await _limiter.AcquireAsync(1, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        if (!lease.IsAcquired)
        {
            _logger.LogWarning("Concurrency limit exceeded for user {User}, method {Method}",
                invocationContext.Context.UserIdentifier,
                invocationContext.HubMethodName);

            _metrics.IncCounter(MetricsAPI.CounterHubConcurrencyRejected);
            throw new HubException("Server is busy. Please try again in a moment.");
        }

        using (lease)
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cts.Cancel();
        _limiter?.Dispose();
        _cts.Dispose();
    }
}
