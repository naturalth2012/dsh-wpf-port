using Dsh.Client;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Pins the reconnect backoff contract (os/07 §4.3): exponential growth capped at the
/// max delay, reset on success. Pure and deterministic — no timers, no sockets.
/// </summary>
public class ReconnectPolicyTests
{
    [Fact]
    public void NextDelay_grows_exponentially_and_caps()
    {
        var p = new ReconnectPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay());  // 1s
        Assert.Equal(TimeSpan.FromSeconds(2), p.NextDelay());  // 2s
        Assert.Equal(TimeSpan.FromSeconds(4), p.NextDelay());  // 4s
        Assert.Equal(TimeSpan.FromSeconds(8), p.NextDelay());  // 8s
        Assert.Equal(TimeSpan.FromSeconds(15), p.NextDelay()); // capped
        Assert.Equal(TimeSpan.FromSeconds(15), p.NextDelay()); // stays capped
        Assert.Equal(6, p.Attempts);
    }

    [Fact]
    public void Reset_restarts_at_initial_delay()
    {
        var p = new ReconnectPolicy(initialDelay: TimeSpan.FromSeconds(1));
        p.NextDelay(); // 1s
        p.NextDelay(); // 2s
        p.NextDelay(); // 4s

        p.Reset();

        Assert.Equal(0, p.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay());
    }

    [Fact]
    public void Defaults_are_1s_to_15s()
    {
        var p = new ReconnectPolicy();
        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay());
        for (int i = 0; i < 10; i++) p.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(15), p.NextDelay());
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-2.0)]
    public void Multiplier_le_1_throws(double multiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(backoffMultiplier: multiplier));
    }
}
