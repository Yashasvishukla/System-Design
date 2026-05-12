using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RateLimiter.RateLimiter.Core.Interfaces;
using RateLimiter.RateLimiter.Core.Models;

namespace RateLimiter.RateLimiter.Api.Extensions;

public class RateLimitHealthCheck: IHealthCheck
{
    private readonly IRateLimitService _rateLimiter;
    private readonly RateLimitConfiguration _config;

    public RateLimitHealthCheck(IRateLimitService rateLimiter, IOptions<RateLimitConfiguration> config)
    {
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _config = config.Value ?? throw new ArgumentNullException(nameof(config));
    }
    
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_config.EnableRateLimiting)
        {
            return HealthCheckResult.Healthy("Rate limiting is disabled.");
        }

        try
        {
            var isHealthy = await _rateLimiter.IsHealthyAsync(cancellationToken);

            if (isHealthy)
            {
                return HealthCheckResult.Healthy("Rate limiting is healthy.");
            }

            if (_config.FailOpen)
            {
                return HealthCheckResult.Degraded("Rate limiter unhealthy but configured to fail open");
            }
            
            return HealthCheckResult.Unhealthy("Rate limiter unhealthy.");
        }
        catch (Exception e)
        {
            if (_config.FailOpen)
            {
                return HealthCheckResult.Degraded("Rate limiter unhealthy but configured to fail open");
            }
            
            return HealthCheckResult.Unhealthy("Rate limiter unhealthy.", e);
        }
    }
}