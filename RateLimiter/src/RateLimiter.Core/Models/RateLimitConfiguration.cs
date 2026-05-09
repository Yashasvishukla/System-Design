using System.ComponentModel.DataAnnotations;

namespace RateLimiter.RateLimiter.Core.Models;

/// <summary>
/// Root configuration for rate limiting.
/// </summary>
public class RateLimitConfiguration
{
    public const string SectionName = "RateLimiting";
    
    [Required]
    public string RedisConnectionString { get; set; } = string.Empty;

    [Required]
    public List<EndpointRateLimitRule> EndpointRules { get; set; } = new();

    [Required]
    public ClientIdentificationStrategy ClientIdentification { get; set; } = new();
    
    public bool EnableRateLimiting { get; set; } = true;
    
    public bool FailOpen { get; set; } = false;

    public int RedisTimeoutMs { get; set; } = 1000;
    
    // Validate config

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RedisConnectionString))
            throw new ArgumentException("RedisConnectionString must be set.", nameof(RedisConnectionString));
        
        if (EndpointRules.Count == 0)
            throw new ArgumentException("At least one endpoint rule must be defined.", nameof(EndpointRules));
        
        // Check for duplicate rule IDs
        var duplicateRuleIds = EndpointRules
            .GroupBy(r => r.RuleId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if(duplicateRuleIds.Any())
            throw new ArgumentException($"Duplicate rule IDs found: {string.Join(", ", duplicateRuleIds)}", nameof(EndpointRules));
        
        // Check for duplicate Endpoint Patterns
        var duplicatePatterns = EndpointRules
            .Where(r => r.IsEnabled)
            .GroupBy(r => r.EndpointPattern, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if(duplicatePatterns.Any())
            throw new ArgumentException($"Duplicate endpoint patterns found: {string.Join(", ", duplicatePatterns)}", nameof(EndpointRules));


        foreach (var rule in EndpointRules)
        {
            rule.Validate();
        }
        
        ClientIdentification.Validate();
            
    }

}


public class ClientIdentificationStrategy
{
    public ClientIdSource Source { get; set; } = ClientIdSource.IpAddress;
    
    public string? HeaderName { get; set; } = "X-API-Key";
    
    public string UserIdClaimType { get; set; } = "sub";

    public bool UseForwardedHeaders { get; set; } = true;


    public void Validate()
    {
        if (Source == ClientIdSource.ApiKey && string.IsNullOrWhiteSpace(HeaderName))
            throw new ArgumentException("HeaderName must be set when Source is ApiKey.", nameof(HeaderName));
        if (Source == ClientIdSource.UserId && string.IsNullOrWhiteSpace(UserIdClaimType))
            throw new ArgumentException("UserIdClaimType must be set when Source is UserId.", nameof(UserIdClaimType));
    }
}


/// <summary>
/// Source for client identification.
/// </summary>
public enum ClientIdSource
{
    /// <summary>
    /// Identify by authenticated user ID from claims.
    /// </summary>
    UserId,
 
    /// <summary>
    /// Identify by API key from the request header.
    /// </summary>
    ApiKey,
 
    /// <summary>
    /// Identify by IP address.
    /// </summary>
    IpAddress
}