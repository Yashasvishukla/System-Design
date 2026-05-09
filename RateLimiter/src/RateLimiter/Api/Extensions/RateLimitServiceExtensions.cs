using RateLimiter.Infrasturcture.Services;
using RateLimiter.RateLimiter.Api.Middleware;
using RateLimiter.RateLimiter.Core.Interfaces;
using RateLimiter.RateLimiter.Core.Models;
using StackExchange.Redis;

namespace RateLimiter.RateLimiter.Api.Extensions;

public static class RateLimitServiceExtensions
{
    public static IServiceCollection AddRateLimitService(this IServiceCollection services, IConfiguration configuration)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }
        
        var rateLimitConfig = configuration
            .GetSection(RateLimitConfiguration.SectionName)
            .Get<RateLimitConfiguration>();
        
        if (rateLimitConfig == null)
        {
            throw new InvalidOperationException($"Configuration section '{RateLimitConfiguration.SectionName}' not found.");
        }
        
        var endpointRuleSection = configuration.GetSection($"{RateLimitConfiguration.SectionName}:EndpointRules");
        rateLimitConfig.EndpointRules = endpointRuleSection.Get<List<EndpointRateLimitRule>>() ?? new List<EndpointRateLimitRule>();
        
        services.Configure<RateLimitConfiguration>(configuration.GetSection(RateLimitConfiguration.SectionName));
        
        rateLimitConfig.Validate();
        
        

        if (rateLimitConfig.EnableRateLimiting)
        {
            // Register Redis Connection Multiplexer
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<IConnectionMultiplexer>>();
                try
                {
                    var options = ConfigurationOptions.Parse(rateLimitConfig.RedisConnectionString);
                    options.AbortOnConnectFail = false;
                    options.ConnectTimeout = rateLimitConfig.RedisTimeoutMs;
                    options.SyncTimeout = rateLimitConfig.RedisTimeoutMs;

                    var redis = ConnectionMultiplexer.Connect(options);
                    redis.ConnectionFailed += (sender, args) => logger.LogError(args.Exception, "Redis connection failed.");
                    redis.ConnectionRestored += (sender, args) => logger.LogInformation("Redis connection restored.");

                    return redis;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to connect to Redis");
                        
                    if (rateLimitConfig.FailOpen)
                    {
                        logger.LogWarning(
                            "Rate limiter configured to fail open - service will start without Redis");
                            
                        // Return a dummy multiplexer that will cause operations to fail gracefully
                        throw;
                    }
 
                    throw;
                }
            });
        }

        services.AddSingleton<IRateLimitService, RateLimitService>();
        services.AddSingleton<IClientIdentifier, ClientIdentifier>();
        return services;
    }


    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app)
    {
        if(app == null)
            throw new ArgumentNullException(nameof(app));
        app.UseMiddleware<RateLimitMiddleware>();
        return app;
    }

    public static IHealthChecksBuilder AddRateLimiterHealthCheck(this IHealthChecksBuilder builder,
        string name = "rate_limiter", TimeSpan? timeout = null)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        
        return builder.AddCheck<RateLimitHealthCheck>(name, tags: new[] { "rate_limiter", "redis" }, timeout: timeout);
    }
}