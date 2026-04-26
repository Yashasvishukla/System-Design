using RateLimiter.RateLimiter.Core.Models;

namespace RateLimiter.RateLimiter.Core.Interfaces;

public interface IRateLimitService
{
    Task<RateLimitResult> CheckRateLimitAsync(
        string method,
        string path,
        string clientId,
        CancellationToken cancellationToken = default);
    
    
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}