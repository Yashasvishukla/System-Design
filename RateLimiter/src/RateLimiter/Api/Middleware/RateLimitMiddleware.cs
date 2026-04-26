using System.Net;
using Microsoft.Extensions.Options;
using RateLimiter.RateLimiter.Core.Exception;
using RateLimiter.RateLimiter.Core.Interfaces;
using RateLimiter.RateLimiter.Core.Models;

namespace RateLimiter.RateLimiter.Api.Middleware;

/// <summary>
/// Middleware for enforcing rate limits.
/// </summary>
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly RateLimitConfiguration _config;

    public RateLimitMiddleware(
        RequestDelegate next,
        ILogger<RateLimitMiddleware> logger,
        IOptions<RateLimitConfiguration> config
        )
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config.Value ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();
    }

    public async Task InvokeAsync(HttpContext context,
        IRateLimitService rateLimiter,
        IClientIdentifier clientIdentifier)
    {
        if (!_config.EnableRateLimiting)
        {
            await _next(context);
            return;
        }

        try
        {
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? "/";
            var clientId = clientIdentifier.GetClientId(context);

            _logger.LogDebug("Processing request: {Method} {Path} for client: {ClientId}", method, path, clientId);

            var result = await rateLimiter.CheckRateLimitAsync(method, path, clientId);

            // Add the information to the header
            foreach (var header in result.ToHeaders())
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            if (!result.IsAllowed)
            {
                await HandleRateLimitExceeded(context, result);
                return;
            }

            await _next(context);
        }
        catch (RateLimiterException ex)
        {
            if (_config.FailOpen)
            {
                _logger.LogWarning("Rate limit exceeded, falling back to fail-open mode. Exception: {Exception}",
                    ex.Message);
                await _next(context);
            }
            else
            {
                await HandleRateLimitExceeded(context);
            }
        }
        catch (Exception e)
        {
            if (_config.FailOpen)
            {
                _logger.LogWarning("Rate limit exceeded, falling back to fail-open mode. Exception: {Exception}",
                    e.Message);
                await _next(context);
            }

            {
                throw;
            }
        }
    }
    
    public async Task HandleRateLimitExceeded(HttpContext context, RateLimitResult result)
    {
        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        context.Response.ContentType = "application/json";
        var errorResponse = new
        {
            error = "rate_limit_exceeded",
            message = "Too many requests. Please retry later.",
            details = new
            {
                limit = result.Capacity,
                remaining = result.RemainingTokens,
                reset = result.ResetTimestamp,
                retryAfter = result.RetryAfter?.TotalSeconds
            }
        };

        await context.Response.WriteAsJsonAsync(errorResponse);
    }

    public async Task HandleRateLimitExceeded(HttpContext context)
    {
        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        context.Response.ContentType = "application/json";
        var errorResponse = new
        {
            error = "rate_limiter_unavailable",
            message = "Rate limiting service is temporarily unavailable."
        };
        await context.Response.WriteAsJsonAsync(errorResponse);
    }
}