namespace RateLimiter.RateLimiter.Core.Models;

/// <summary>
/// Result of a rate limit operation.
/// </summary>
public class RateLimitResult
{
    public bool IsAllowed { get; set; }
    public int RemainingTokens { get; set; }
    public int Capacity { get; set; }
    public long ResetTimestamp { get; set; } // Unix timestamp
    public TimeSpan? RetryAfter { get; set; }
    public string? AppliedRuleId { get; set; }
    
    public string? RedisKey { get; set; }
    
    public bool IsRateLimited { get; set; }
    
    public IReadOnlyDictionary<string, string> ToHeaders()
    {
        if (!IsRateLimited)
        {
            return new Dictionary<string, string>();
        }
        
        var headers = new Dictionary<string, string>
        {
            ["X-RateLimit-Limit"] = Capacity.ToString(),
            ["X-RateLimit-Remaining"] = Math.Max(0, RemainingTokens).ToString(),
            ["X-RateLimit-Reset"] = ResetTimestamp.ToString() // Unix timestamp (seconds)
        };

        if (RetryAfter.HasValue)
        {
            headers["Retry-After"] = ((int)Math.Ceiling(RetryAfter.Value.TotalSeconds)).ToString();
        }
        return headers;
    }

    /// <summary>
    /// Create a result indicating the request is allowed.
    /// </summary>
    /// <returns></returns>
    public static RateLimitResult Allowed()
    {
        // TODO: Revisit this section for better handling of rate limits
        return new RateLimitResult
        {
            IsAllowed = true,
            IsRateLimited = false,
            RemainingTokens = 0,       
            Capacity = 0,
            ResetTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    public static RateLimitResult Denied(
        int remainingTokens,
        int capacity,
        TimeSpan retryAfter,
        string? appliedRuleId = null)
    {
        return new RateLimitResult
        {
            IsAllowed = false,
            RemainingTokens = remainingTokens,
            ResetTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RetryAfter = retryAfter,
            AppliedRuleId = appliedRuleId,
        };
    }
    
    /// <summary>
    /// Creates a result indicating the request is allowed with rate limiting applied.
    /// </summary>
    public static RateLimitResult AllowedWithLimit(
        int remainingTokens, 
        int capacity, 
        long resetTimestamp, 
        string? appliedRuleId = null,
        string? redisKey = null)
    {
        return new RateLimitResult
        {
            IsAllowed = true,
            IsRateLimited = true,  // Rate limiting was applied
            RemainingTokens = remainingTokens,
            Capacity = capacity,
            ResetTimestamp = resetTimestamp,
            AppliedRuleId = appliedRuleId,
            RedisKey = redisKey
        };
    }

    
    public override string ToString() => $"Allowed: {IsAllowed}, Remaining: {RemainingTokens}, Capacity: {Capacity}, Reset: {ResetTimestamp}";
}