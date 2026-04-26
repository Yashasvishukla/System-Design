using System.ComponentModel.DataAnnotations;

namespace RateLimiter.RateLimiter.Core.Models;

/// <summary>
/// Defines a rate limit rule for an endpoint.
/// </summary>
public class EndpointRateLimitRule
{
    public Guid RuleId { get; set; }
    
    [Required]
    [RegularExpression(@"^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS):/[\w/\-]*$")]
    public string EndpointPattern { get; set; } = string.Empty;
    
    [Range(1, 1000)]
    public int Capacity { get; set; }

    [Range(1, 1000)]
    public int RefillRate { get; set; }
    
    [Range(1, 1000)]
    public TimeSpan RefillInterval { get; set; }
    
    public bool IsEnabled { get; set; }
    
    public string Description { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; }

    public void Validate()
    {
        if (Capacity <= 0)
        {
            throw new ArgumentException("Capacity must be greater than 0.", nameof(Capacity));
        }
        
        if (RefillRate <= 0)
        {
            throw new ArgumentException("RefillRate must be greater than 0.", nameof(RefillRate));
        }

        if (RefillInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("RefillInterval must be greater than 0.", nameof(RefillInterval));
        }

        if (RefillRate > Capacity)
        {
            throw new ArgumentException("RefillRate must be less than or equal to Capacity.", nameof(RefillRate));
        }
    }
    
    
    public double GetRatePerSecond() => RefillRate / RefillInterval.TotalSeconds;
    
    public override string ToString() => $"{EndpointPattern} - {Capacity} requests per {RefillInterval.TotalSeconds} seconds";
}