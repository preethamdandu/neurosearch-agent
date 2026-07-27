using Xunit;
using NeuroSearch.Core;

namespace NeuroSearch.Tests;

/// <summary>
/// OWASP Security Validation Tests
/// Tests input validation, rate limiting, and SSRF protection
/// </summary>
public class SecurityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidateSearchQuery_Should_Reject_Empty_Input(string? emptyInput)
    {
        // Act
        var result = InputValidator.ValidateSearchQuery(emptyInput);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("cannot be empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("'; DROP TABLE users; --", "SQL")]
    [InlineData("1=1 UNION SELECT * FROM passwords", "SQL")]
    [InlineData("admin'--", "SQL")]
    [InlineData("SELECT * FROM users WHERE username='admin' OR '1'='1'", "SQL")]
    public void ValidateSearchQuery_Should_Detect_SQL_Injection(string maliciousInput, string expectedPattern)
    {
        // Act
        var result = InputValidator.ValidateSearchQuery(maliciousInput);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(expectedPattern, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>", "script")]
    [InlineData("javascript:void(0)", "script")]
    [InlineData("<iframe src='evil.com'>", "script")]
    [InlineData("onerror=alert('xss')", "script")]
    [InlineData("eval(malicious_code)", "script")]
    public void ValidateSearchQuery_Should_Detect_XSS_Patterns(string maliciousInput, string expectedPattern)
    {
        // Act
        var result = InputValidator.ValidateSearchQuery(maliciousInput);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(expectedPattern, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSearchQuery_Should_Enforce_Max_Length()
    {
        // Arrange
        var tooLongQuery = new string('a', 501); // Max is 500

        // Act
        var result = InputValidator.ValidateSearchQuery(tooLongQuery);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("exceeds maximum length", result.ErrorMessage);
    }

    [Theory]
    [InlineData("http://localhost/admin")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://localhost:6333")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://172.31.255.255")]
    public void ValidateUrl_Should_Block_SSRF_Attacks(string internalUrl)
    {
        // Act
        var result = InputValidator.ValidateUrl(internalUrl);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("SSRF protection", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ValidateUrl_Should_Reject_Empty_Urls(string? emptyUrl)
    {
        // Act
        var result = InputValidator.ValidateUrl(emptyUrl);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("cannot be empty", result.ErrorMessage);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void ValidateUrl_Should_Only_Allow_HTTP_HTTPS(string invalidScheme)
    {
        // Act
        var result = InputValidator.ValidateUrl(invalidScheme);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("http", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("http://")]
    [InlineData("https://")]
    [InlineData("http://.com")]
    public void ValidateUrl_Should_Reject_Malformed_Urls(string malformedUrl)
    {
        // Act
        var result = InputValidator.ValidateUrl(malformedUrl);

        // Assert
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(-1, 1, 10, "value")]
    [InlineData(0, 1, 10, "value")]
    [InlineData(11, 1, 10, "value")]
    [InlineData(100, 1, 10, "value")]
    public void ValidateNumericRange_Should_Reject_Out_Of_Range(int value, int min, int max, string fieldName)
    {
        // Act
        var result = InputValidator.ValidateNumericRange(value, min, max, fieldName);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("must be between", result.ErrorMessage);
    }

    [Theory]
    [InlineData(1, 1, 10)]
    [InlineData(5, 1, 10)]
    [InlineData(10, 1, 10)]
    public void ValidateNumericRange_Should_Accept_Valid_Values(int value, int min, int max)
    {
        // Act
        var result = InputValidator.ValidateNumericRange(value, min, max, "test");

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(value.ToString(), result.Value);
    }

    [Fact]
    public void RateLimiter_Should_Allow_Burst_Then_Throttle()
    {
        // Arrange
        var limiter = new RateLimiter(requestsPerMinute: 5, burstSize: 2);
        var key = "test_burst";

        // Act
        var result1 = limiter.AllowRequest(key);
        var result2 = limiter.AllowRequest(key);
        var result3 = limiter.AllowRequest(key);

        // Assert
        Assert.True(result1.IsAllowed, "First request should be allowed (burst)");
        Assert.True(result2.IsAllowed, "Second request should be allowed (burst)");
        Assert.False(result3.IsAllowed, "Third request should be rate limited");
        Assert.True(result3.RetryAfterSeconds > 0, "Should provide retry-after value");
    }

    [Fact]
    public void RateLimiter_Should_Be_Thread_Safe()
    {
        // Arrange
        var limiter = new RateLimiter(requestsPerMinute: 100, burstSize: 10);
        var key = "concurrent_test";
        var allowedCount = 0;
        var tasks = new List<Task>();

        // Act - 20 concurrent requests
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var result = limiter.AllowRequest(key);
                if (result.IsAllowed)
                {
                    Interlocked.Increment(ref allowedCount);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        Assert.Equal(10, allowedCount); // Only burst size should succeed
    }

    [Fact]
    public async Task RateLimiter_Should_Refill_Over_Time()
    {
        // Arrange
        var limiter = new RateLimiter(requestsPerMinute: 60, burstSize: 1); // 1 per second
        var key = "refill_test";

        // Act
        var result1 = limiter.AllowRequest(key);
        Assert.True(result1.IsAllowed);

        var result2 = limiter.AllowRequest(key);
        Assert.False(result2.IsAllowed);

        // Wait for refill
        await Task.Delay(1100); // 1.1 seconds

        var result3 = limiter.AllowRequest(key);

        // Assert
        Assert.True(result3.IsAllowed, "Should allow after refill period");
    }

    [Fact]
    public void ValidateSearchQuery_Should_Sanitize_Valid_Input()
    {
        // Arrange
        var input = "  normal   search  \0  query  ";

        // Act
        var result = InputValidator.ValidateSearchQuery(input);

        // Assert
        Assert.True(result.IsValid);
        // Use char overload: Assert.DoesNotContain("\0", string) can false-positive on some xUnit versions
        Assert.DoesNotContain('\0', result.Value);
        Assert.Equal("normal search query", result.Value); // Whitespace normalized
    }

    [Fact]
    public void ValidateUrl_Should_Accept_Valid_HTTPS_Urls()
    {
        // Arrange
        var validUrls = new[]
        {
            "https://example.com",
            "https://www.example.com/path",
            "https://api.example.com:8080/endpoint?param=value",
            "http://example.co.uk/article#section"
        };

        // Act & Assert
        foreach (var url in validUrls)
        {
            var result = InputValidator.ValidateUrl(url);
            Assert.True(result.IsValid, $"URL should be valid: {url}");
        }
    }
}
