using System.Collections.Concurrent;

namespace NeuroSearch.Core;

/// <summary>
/// Thread-safe rate limiter for API calls
/// Implements token bucket algorithm
/// </summary>
public class RateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private readonly int _requestsPerMinute;
    private readonly int _burstSize;

    public RateLimiter(int requestsPerMinute = 10, int burstSize = 5)
    {
        _requestsPerMinute = requestsPerMinute;
        _burstSize = burstSize;
    }

    /// <summary>
    /// Checks if request is allowed for given key (IP, user, endpoint)
    /// </summary>
    public RateLimitResult AllowRequest(string key)
    {
        var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(_requestsPerMinute, _burstSize));
        
        if (bucket.TryConsume())
        {
            return RateLimitResult.Allowed();
        }

        var retryAfter = bucket.GetRetryAfterSeconds();
        return RateLimitResult.RateLimited(retryAfter);
    }

    /// <summary>
    /// Resets rate limit for a specific key (for testing)
    /// </summary>
    public void Reset(string key)
    {
        _buckets.TryRemove(key, out _);
    }

    /// <summary>
    /// Token bucket implementation
    /// </summary>
    private class TokenBucket
    {
        private readonly int _capacity;
        private readonly double _refillRate; // tokens per second
        private double _tokens;
        private DateTime _lastRefill;
        private readonly object _lock = new();

        public TokenBucket(int requestsPerMinute, int burstSize)
        {
            _capacity = burstSize;
            _tokens = burstSize;
            _refillRate = requestsPerMinute / 60.0; // convert to per second
            _lastRefill = DateTime.UtcNow;
        }

        public bool TryConsume()
        {
            lock (_lock)
            {
                Refill();

                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return true;
                }

                return false;
            }
        }

        public int GetRetryAfterSeconds()
        {
            lock (_lock)
            {
                if (_tokens >= 1) return 0;
                
                // Calculate time until next token
                var tokensNeeded = 1 - _tokens;
                var secondsNeeded = tokensNeeded / _refillRate;
                return (int)Math.Ceiling(secondsNeeded);
            }
        }

        private void Refill()
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;

            if (elapsed > 0)
            {
                var tokensToAdd = elapsed * _refillRate;
                _tokens = Math.Min(_capacity, _tokens + tokensToAdd);
                _lastRefill = now;
            }
        }
    }
}

/// <summary>
/// Result of rate limit check
/// </summary>
public class RateLimitResult
{
    public bool IsAllowed { get; init; }
    public int RetryAfterSeconds { get; init; }

    public static RateLimitResult Allowed() =>
        new() { IsAllowed = true, RetryAfterSeconds = 0 };

    public static RateLimitResult RateLimited(int retryAfter) =>
        new() { IsAllowed = false, RetryAfterSeconds = retryAfter };
}
