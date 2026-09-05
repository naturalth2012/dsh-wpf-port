using Dsh.Client;

namespace Dsh.Client.Tests;

/// <summary>
/// Pure-logic tests for the reconnect backoff (the class deliberately owns no sockets and no
/// UI state so these are cheap): initial delay, exponential growth, max cap, reset, and
/// constructor validation.
/// </summary>
public class ReconnectPolicyTests
{
    [Fact]
    public void First_delay_is_initial_delay()
    {
        var policy = new ReconnectPolicy(initialDelay: TimeSpan.FromMilliseconds(100), maxDelay: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.NextDelay());
        Assert.Equal(1, policy.Attempts);
    }

    [Fact]
    public void Delay_grows_exponentially()
    {
        var policy = new ReconnectPolicy(initialDelay: TimeSpan.FromMilliseconds(100), maxDelay: TimeSpan.FromSeconds(10));

        policy.NextDelay(); // 100

        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.NextDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(400), policy.NextDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(800), policy.NextDelay());
    }

    [Fact]
    public void Delay_caps_at_max()
    {
        var policy = new ReconnectPolicy(initialDelay: TimeSpan.FromMilliseconds(100), maxDelay: TimeSpan.FromMilliseconds(500));

        policy.NextDelay(); // 100
        policy.NextDelay(); // 200
        policy.NextDelay(); // 400

        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.NextDelay()); // 800 → capped
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.NextDelay());
    }

    [Fact]
    public void Reset_returns_to_initial_delay()
    {
        var policy = new ReconnectPolicy(initialDelay: TimeSpan.FromMilliseconds(100), maxDelay: TimeSpan.FromSeconds(1));

        policy.NextDelay();
        policy.NextDelay();
        policy.Reset();

        Assert.Equal(0, policy.Attempts);
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.NextDelay());
    }

    [Fact]
    public void Constructor_rejects_multiplier_leq_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(backoffMultiplier: 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(backoffMultiplier: 0.5));
    }
}
