using Microsoft.Extensions.Options;
using RateLimiter.RateLimiter.Core.Exception;
using RateLimiter.RateLimiter.Core.Interfaces;
using RateLimiter.RateLimiter.Core.Models;
using StackExchange.Redis;

namespace RateLimiter.Infrasturcture.Services;

public class RateLimitService: IRateLimitService, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RateLimitService> _logger;
    private readonly RateLimitConfiguration _configuration;
    private readonly LuaScript _tokenBucketScript;
    private bool _disposed;

    #region LuaScript

    private const string LuaScriptContent = @"
    
    local key = KEYS[1]
    local tokens_requested = tonumber(ARGV[1])
    local capacity = tonumber(ARGV[2])
    local refill_rate = tonumber(ARGV[3])
    local refill_interval_ms = tonumber(ARGV[4])
    local now = tonumber(ARGV[5])

    -- GET current bucket state
    local bucket = redis.call('HMGET', key, 'tokens', 'last_refill')
    local current_tokens = tonumber(bucket[1])
    local last_refill = tonumber(bucket[2])

    -- Initialize bucket if it doesn't exist
    if not current_tokens then
        current_tokens = capacity
        last_refill = now
    end
    
    -- Calculate refill time
    local time_elapsed_ms = now - last_refill
    local tokens_to_add = (time_elapsed_ms / refill_interval_ms) * refill_rate
    
    -- Add tokens to bucket
    current_tokens = math.min(capacity, current_tokens + tokens_to_add)


    local allowed = 0
    if current_tokens >= tokens_requested then
        current_tokens = current_tokens - tokens_requested
        allowed = 1
    end
    
    -- update the bucket state
    redis.call('HMSET', key, 
        'tokens', tostring(current_tokens),
        'last_refill', tostring(now)
        'capacity', tostring(capacity)
        'refill_rate', tostring(refill_rate)
        'refill_interval_ms', tostring(refill_interval_ms)
    
    -- set expiry
    redis.call('EXPIRE', key, 3600)

    -- calculate time until next token
    local time_until_next_token_ms = 0
    if allowed == 0 then
        local token_needed = tokens_requested - current_tokens
        time_until_next_token_ms = math.ceil(token_needed / refill_rate) * refill_interval_ms
    end
    
    return { allowed, current_tokens, capacity, time_until_next_token_ms }
    ";

    #endregion



    public RateLimitService(
        IConnectionMultiplexer redis,
        IOptions<RateLimitConfiguration> configuration,
        ILogger<RateLimitService> logger
    )
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration.Value ?? throw new ArgumentNullException(nameof(configuration));

        try
        {
            _configuration.Validate();
        }
        catch (Exception e)
        {
            throw new RateLimiterConfigurationException("Invalid rate limit configuration", e);
        }
        
        // Pre-compile the Lua script
        _tokenBucketScript = LuaScript.Prepare(LuaScriptContent);
        
        _logger.LogInformation("Redis Rate limit service initialized successfully with rule count: {RuleCount}.", _configuration.EndpointRules.Count);
    }
    
    public async Task<RateLimitResult> CheckRateLimitAsync(string method, string path, string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID cannot be null or empty.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("HTTP method cannot be null or empty.", nameof(method));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("HTTP path cannot be null or empty.", nameof(path));
        }

        try
        {
            // Find the matching rule for the endpoint
            var rule = FindMatchingRule(method, path);
            if (rule == null)
            {
                _logger.LogDebug("No Rate Limit rule found for {Method} {Path}", method, path);
                return RateLimitResult.Allowed(); // This is good way.
            }

            var redisKey = BuildRedisKey(method, path, clientId);

            // Execute the lua script with timeout
            var db = _redis.GetDatabase();
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_configuration.RedisTimeoutMs);

            var result = await db.ScriptEvaluateAsync(
                _tokenBucketScript,
                new 
                {
                    key = (RedisKey)redisKey,
                    tokensRequested = 1,
                    capacity = rule.Capacity,
                    refillRate = rule.RefillRate,
                    refillIntervalMs = (long)rule.RefillInterval.TotalMilliseconds,
                    currentTimestamp
                }).ConfigureAwait(false);


            // Parse result
            var isAllowed = (int)result![0] == 1;
            var remainingTokens = (int)result![1];
            var capacity = (int)result![2];
            var retryAfterMs = (long)result![3];
            RateLimitResult rateLimitResult;
            if (isAllowed)
            {
                rateLimitResult = RateLimitResult.AllowedWithLimit(
                    remainingTokens,
                    capacity,
                    currentTimestamp + (long)rule.RefillInterval.TotalMilliseconds,
                    rule.RuleId.ToString(),
                    redisKey
                );
            }
            else
            {
                rateLimitResult = RateLimitResult.Denied(remainingTokens, capacity,
                    TimeSpan.FromMilliseconds(retryAfterMs), rule.RuleId.ToString());
                rateLimitResult.RedisKey = redisKey;
            }

            return rateLimitResult;
        }
        catch (RedisException ex)
        {
            if (_configuration.FailOpen)
            {
                _logger.LogWarning("Redis connection failed, falling back to fail-open mode. Exception: {Exception}",
                    ex.Message);
                return RateLimitResult.Allowed();
            }

            throw new RateLimiterStorageException("Redis connection failed", ex);
        }
        catch (TimeoutException ex)
        {
            if (_configuration.FailOpen)
            {
                _logger.LogWarning("Redis operation timed out, falling back to fail-open mode. Exception: {Exception}",
                    ex.Message);
                return RateLimitResult.Allowed();
            }
            throw new RateLimiterStorageException("Redis operation timed out", ex);
        }
        catch (Exception e) when (e is not RateLimiterException)
        {
            if (_configuration.FailOpen)
            {
                _logger.LogWarning("Redis operation failed, falling back to fail-open mode. Exception: {Exception}",
                    e.Message);
                return RateLimitResult.Allowed();
            }
            throw new Exception("Exception has occured.", e);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.PingAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if(_disposed) return;
        _disposed = true;
    }


    private EndpointRateLimitRule? FindMatchingRule(string method, string path)
    {
        var pattern = $"{method.ToUpperInvariant()}:{path}";
        return _configuration.EndpointRules
            .Where(r => r.IsEnabled)
            .FirstOrDefault(r => r.EndpointPattern.Equals(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildRedisKey(string method, string path, string clientId)
    {
        return $"ratelimit:endpoint:{method.ToUpperInvariant()}:{path}:{clientId}";
    }
}