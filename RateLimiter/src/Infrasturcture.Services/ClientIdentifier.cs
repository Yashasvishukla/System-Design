using System.Security.Claims;
using Microsoft.Extensions.Options;
using RateLimiter.RateLimiter.Core.Interfaces;
using RateLimiter.RateLimiter.Core.Models;

namespace RateLimiter.Infrasturcture.Services;

public class ClientIdentifier: IClientIdentifier
{
    // Client Identification Strategy
    private readonly ClientIdentificationStrategy _strategy;
    private readonly ILogger<ClientIdentifier> _logger;

    public ClientIdentifier(
            IOptions<RateLimitConfiguration> configuration,
            ILogger<ClientIdentifier> logger
        )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _strategy = configuration.Value.ClientIdentification ?? throw new ArgumentNullException(nameof(configuration));
    }
    public string GetClientId(HttpContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return _strategy.Source switch
        {
            ClientIdSource.UserId => GetUserIdFromClaim(context),
            ClientIdSource.ApiKey => GetApiFromHeader(context),
            ClientIdSource.IpAddress => GetIpAddress(context),
            _ => throw new InvalidOperationException($"Invalid ClientIdentificationStrategy source: {_strategy.Source}")
        };
    }

    // TODO: understand the Principal and Claims Properly.
    private string GetUserIdFromClaim(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            _logger.LogDebug("User is not authenticated, falling back to IP address");
            return $"anonymous:{GetIpAddress(context)}";
        }

        var userId = context.User.FindFirst(_strategy.UserIdClaimType)?.Value;
        // Fallback to common claim types
        userId ??= context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? context.User.FindFirst("sub")?.Value
                   ?? context.User.FindFirst("userId")?.Value;

        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogDebug("User ID not found in claims, falling back to IP address");
            return $"anonymous:{GetIpAddress(context)}";
        }
        
        _logger.LogDebug("User ID found in claims: {UserId}", userId);
        return $"user:{userId}";

    }

    private string GetApiFromHeader(HttpContext context)
    {
        var headerName = _strategy.HeaderName ?? "X-API-Key";
        if (!context.Request.Headers.TryGetValue(headerName, out var apiKeyValues))
        {
            _logger.LogDebug("API key not found in request headers, falling back to IP address");
            return $"anonymous:{GetIpAddress(context)}";
        }
        
        var apiKey = apiKeyValues.FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogDebug("API key not found in request headers, falling back to IP address");
            return $"anonymous:{GetIpAddress(context)}";
        }

        // IMP: Hashing the API key for security reasons.
        var hashApiKey = HashApi(apiKey);
        _logger.LogDebug("API key found in request headers: {ApiKey}", apiKey);
        return $"api:{hashApiKey}";
    }

    private string GetIpAddress(HttpContext context)
    {
        string? ipAddress = string.Empty;

        if (_strategy.UseForwardedHeaders)
        {
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
                ipAddress = ips.FirstOrDefault()?.Trim();
            }
            
            
            ipAddress ??= context.Request.Headers["X-Real-IP"].FirstOrDefault();
        }
        
        // Fallback to direct IP address
        ipAddress ??= context.Connection.RemoteIpAddress?.ToString();

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            _logger.LogDebug("IP address found: {IpAddress}", ipAddress);
            return "unknown";
        }

        return $"ip:{ipAddress}";
    }


    private static string HashApi(string apiKey)
    {
        var prefix = apiKey.Length > 8 ? apiKey.Substring(0, 8) : apiKey;
        var hash = apiKey.GetHashCode().ToString("x8");
        return $"{prefix}....{hash}";
    }
}