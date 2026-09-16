using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Workbench.Options;

namespace Workbench.Services;

internal sealed class WorkbenchHealthReportCache
{
    private readonly IServiceProvider _services;
    private readonly TimeSpan _duration;
    private readonly object _lock = new();
    private HealthReport? _report;
    private DateTimeOffset _expiresAt;
    private Task<HealthReport?>? _refresh;
    private bool _hasValue;

    public WorkbenchHealthReportCache(IServiceProvider services, IOptions<WorkbenchOptions> options)
    {
        _services = services;
        _duration = options.Value.MetricsSampleInterval;
    }

    public Task<HealthReport?> GetAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_hasValue && DateTimeOffset.UtcNow < _expiresAt)
                return Task.FromResult(_report);

            Task<HealthReport?> refresh;
            if (_refresh is null)
            {
                var completion = new TaskCompletionSource<HealthReport?>(TaskCreationOptions.RunContinuationsAsynchronously);
                refresh = completion.Task;
                _refresh = refresh;
                _ = RefreshAsync(completion);
            }
            else
            {
                refresh = _refresh;
            }

            return refresh.WaitAsync(cancellationToken);
        }
    }

    private async Task RefreshAsync(TaskCompletionSource<HealthReport?> completion)
    {
        try
        {
            var service = _services.GetService<HealthCheckService>();
            var report = service is null
                ? null
                : await service.CheckHealthAsync(CancellationToken.None);

            lock (_lock)
            {
                _report = report;
                _expiresAt = DateTimeOffset.UtcNow + _duration;
                _hasValue = true;
            }

            completion.TrySetResult(report);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_refresh, completion.Task))
                    _refresh = null;
            }
        }
    }
}
