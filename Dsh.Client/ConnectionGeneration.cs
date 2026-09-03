namespace Dsh.Client;

/// <summary>
/// A monotonically-increasing connection generation, mirroring the Web client's
/// <c>connectionGeneration</c>: each reconnect increments it, and any downstream frame or
/// pending state tagged with an older generation is discarded. Provides the ownership
/// test the client uses to drop stale pushes after a reconnect.
/// </summary>
public sealed class ConnectionGeneration
{
    private int _current;

    /// <summary>Current generation value; increments by one per reconnect.</summary>
    public int Current => _current;

    /// <summary>Bump to a new generation (on reconnect); returns the new value.</summary>
    public int Advance() => ++_current;

    /// <summary>True when <paramref name="value"/> is the live (current) generation.</summary>
    public bool IsCurrent(int value) => value == _current;
}
