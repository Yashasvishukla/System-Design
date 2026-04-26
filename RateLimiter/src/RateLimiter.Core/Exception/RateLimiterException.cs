namespace RateLimiter.RateLimiter.Core.Exception;
using System;
public class RateLimiterException: Exception
{
    public RateLimiterException(string message) : base(message)
    {
        
    }

    public RateLimiterException(string message, Exception innerException) :
        base(message, innerException)
    {
        
    }
}



public class RateLimiterConfigurationException: RateLimiterException
{
    public RateLimiterConfigurationException(string message) : base(message)
    {
        
    }

    public RateLimiterConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
        
    }
}

public class RateLimiterStorageException: RateLimiterException
{
    public RateLimiterStorageException(string message) : base(message)
    {
        
    }

    public RateLimiterStorageException(string message, Exception innerException) : base(message, innerException)
    {
        
    }
}