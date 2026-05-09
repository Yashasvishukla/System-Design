using Microsoft.Extensions.Options;
using RateLimiter.RateLimiter.Core.Exception;
using RateLimiter.RateLimiter.Core.Interfaces;
using RateLimiter.RateLimiter.Core.Models;
using StackExchange.Redis;

namespace RateLimiter.Infrasturcture.Services;

public class RateLimitService : IRateLimitService, IDisposable
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<RateLimitService> _logger;
        private readonly RateLimitConfiguration _config;
        private bool _disposed;

        // Lua script for atomic token bucket operation
        // KEYS[1] = Redis key for the bucket
        // ARGV[1] = tokens_requested (usually 1)
        // ARGV[2] = capacity
        // ARGV[3] = refill_rate
        // ARGV[4] = refill_interval_ms
        // ARGV[5] = current_timestamp_ms
        private const string LuaScriptContent = @"
            local key = KEYS[1]
            local tokens_requested = tonumber(ARGV[1])
            local capacity = tonumber(ARGV[2])
            local refill_rate = tonumber(ARGV[3])
            local refill_interval_ms = tonumber(ARGV[4])
            local now = tonumber(ARGV[5])

            -- Get current bucket state
            local bucket = redis.call('HMGET', key, 'tokens', 'last_refill')
            local current_tokens = tonumber(bucket[1])
            local last_refill = tonumber(bucket[2])

            -- Initialize if bucket doesn't exist
            if not current_tokens then
                current_tokens = capacity
                last_refill = now
            end

            -- Calculate refill
            local time_elapsed_ms = now - last_refill
            local tokens_to_add = (time_elapsed_ms / refill_interval_ms) * refill_rate

            -- Add tokens (capped at capacity)
            current_tokens = math.min(capacity, current_tokens + tokens_to_add)

            -- Attempt to consume tokens
            local allowed = 0
            if current_tokens >= tokens_requested then
                current_tokens = current_tokens - tokens_requested
                allowed = 1
            end

            -- Update bucket state (ensure all values are properly stringified for Redis)
            redis.call('HSET', key, 
                'tokens', tostring(current_tokens),
                'last_refill', tostring(math.floor(now)),
                'capacity', tostring(capacity),
                'refill_rate', tostring(refill_rate),
                'refill_interval_ms', tostring(refill_interval_ms)
            )

            -- Set expiry (cleanup inactive buckets after 1 hour)
            redis.call('EXPIRE', key, 3600)

            -- Calculate time until next token (for retry-after)
            local time_until_next_token_ms = 0
            if allowed == 0 then
                local tokens_needed = tokens_requested - current_tokens
                time_until_next_token_ms = math.ceil((tokens_needed / refill_rate) * refill_interval_ms)
            end

            -- Return: [allowed, remaining_tokens, capacity, retry_after_ms]
            return {allowed, math.floor(current_tokens), capacity, time_until_next_token_ms}
        ";

        public RateLimitService(
            IConnectionMultiplexer redis,
            IOptions<RateLimitConfiguration> config,
            ILogger<RateLimitService> logger)
        {
            _redis = redis ?? throw new ArgumentNullException(nameof(redis));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Validate configuration on startup
            try
            {
                _config.Validate();
            }
            catch (Exception ex)
            {
                throw new RateLimiterConfigurationException("Invalid rate limiter configuration", ex);
            }

            _logger.LogInformation(
                "RedisRateLimitService initialized with {RuleCount} rules",
                _config.EndpointRules.Count);
        }

        /// <inheritdoc />
        public async Task<RateLimitResult> CheckRateLimitAsync(
            string method,
            string path,
            string clientId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method cannot be null or empty", nameof(method));

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));

            try
            {
                // Find matching rule for this endpoint
                var rule = FindMatchingRule(method, path);

                if (rule == null)
                {
                    _logger.LogDebug(
                        "No rate limit rule found for {Method} {Path}",
                        method, path);

                    return RateLimitResult.Allowed();
                }

                // Build Redis key
                var redisKey = BuildRedisKey(method, path, clientId);

                _logger.LogDebug(
                    "Checking rate limit: key={RedisKey}, rule={RuleId}",
                    redisKey, rule.RuleId);

                // Execute Lua script with timeout
                var db = _redis.GetDatabase();
                var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_config.RedisTimeoutMs);

                // Prepare parameters for Lua script
                var keys = new RedisKey[] { redisKey };
                var values = new RedisValue[]
                {
                    1,  // tokensRequested
                    rule.Capacity,
                    rule.RefillRate,
                    (long)rule.RefillInterval.TotalMilliseconds,
                    currentTimestamp
                };

                var result = await db.ScriptEvaluateAsync(
                    LuaScriptContent,
                    keys,
                    values).ConfigureAwait(false);

                // Parse result: [allowed, remaining_tokens, capacity, retry_after_ms]
                var resultValues = (RedisValue[])result!;
                var isAllowed = (int)resultValues[0] == 1;
                var remainingTokens = (int)resultValues[1];
                var capacity = (int)resultValues[2];
                var retryAfterMs = (long)resultValues[3];

                RateLimitResult rateLimitResult;
                
                if (isAllowed)
                {
                    rateLimitResult = RateLimitResult.AllowedWithLimit(
                        remainingTokens,
                        capacity,
                        currentTimestamp + (long)rule.RefillInterval.TotalMilliseconds,
                        rule.RuleId,
                        redisKey
                    );
                }
                else
                {
                    rateLimitResult = RateLimitResult.Denied(
                        remainingTokens,
                        capacity,
                        TimeSpan.FromMilliseconds(retryAfterMs),
                        rule.RuleId
                    );
                    rateLimitResult.RedisKey = redisKey;
                }

                if (isAllowed)
                {
                    _logger.LogDebug(
                        "Request allowed: {Method} {Path} for {ClientId} - Remaining: {Remaining}/{Capacity}",
                        method, path, clientId, remainingTokens, capacity);
                }
                else
                {
                    _logger.LogWarning(
                        "Request rate limited: {Method} {Path} for {ClientId} - RetryAfter: {RetryAfter}ms",
                        method, path, clientId, retryAfterMs);
                }

                return rateLimitResult;
            }
            catch (RedisException ex)
            {
                _logger.LogError(ex,
                    "Redis error during rate limit check for {Method} {Path}",
                    method, path);

                if (_config.FailOpen)
                {
                    _logger.LogWarning("Failing open - allowing request due to Redis error");
                    return RateLimitResult.Allowed();
                }

                throw new RateLimiterStorageException("Redis operation failed", ex);
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex,
                    "Timeout during rate limit check for {Method} {Path}",
                    method, path);

                if (_config.FailOpen)
                {
                    _logger.LogWarning("Failing open - allowing request due to timeout");
                    return RateLimitResult.Allowed();
                }

                throw new RateLimiterStorageException("Rate limit check timed out", ex);
            }
            catch (Exception ex) when (ex is not RateLimiterException)
            {
                _logger.LogError(ex,
                    "Unexpected error during rate limit check for {Method} {Path}",
                    method, path);

                if (_config.FailOpen)
                {
                    _logger.LogWarning("Failing open - allowing request due to unexpected error");
                    return RateLimitResult.Allowed();
                }

                throw new RateLimiterException("Rate limit check failed", ex);
            }
        }

        /// <inheritdoc />
        public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var db = _redis.GetDatabase();
                await db.PingAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                return false;
            }
        }

        /// <summary>
        /// Finds the matching rate limit rule for the given endpoint.
        /// </summary>
        private EndpointRateLimitRule? FindMatchingRule(string method, string path)
        {
            var pattern = $"{method.ToUpperInvariant()}:{path}";

            return _config.EndpointRules
                .Where(r => r.IsEnabled)
                .FirstOrDefault(r => r.EndpointPattern.Equals(pattern, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Builds the Redis key for a given endpoint and client.
        /// </summary>
        private static string BuildRedisKey(string method, string path, string clientId)
        {
            return $"ratelimit:endpoint:{method.ToUpperInvariant()}:{path}:{clientId}";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _logger.LogInformation("RedisRateLimitService disposed");
        }
    }
