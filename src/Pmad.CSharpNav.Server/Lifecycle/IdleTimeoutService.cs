using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pmad.CSharpNav.Server.Lifecycle;

public sealed partial class IdleTimeoutService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IdleTimeoutService> _logger;
    private readonly TimeSpan _idleTimeout;
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

    public IdleTimeoutService(
        IHostApplicationLifetime lifetime,
        ILogger<IdleTimeoutService> logger,
        TimeSpan? idleTimeout = null)
    {
        _lifetime = lifetime;
        _logger = logger;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);
    }

    public void RecordActivity()
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            var lastTicks = Interlocked.Read(ref _lastActivityTicks);
            var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(lastTicks, TimeSpan.Zero);

            if (elapsed > _idleTimeout)
            {
                LogIdleTimeout(_logger, elapsed.TotalMinutes);
                _lifetime.StopApplication();
                break;
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Idle timeout reached ({ElapsedMinutes:F1} minutes without requests). Shutting down daemon.")]
    private static partial void LogIdleTimeout(ILogger logger, double elapsedMinutes);
}
