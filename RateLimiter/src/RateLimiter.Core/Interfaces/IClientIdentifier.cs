namespace RateLimiter.RateLimiter.Core.Interfaces;

public interface IClientIdentifier
{
    string GetClientId(HttpContext context);
}