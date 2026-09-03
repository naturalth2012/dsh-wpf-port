using System.Collections;

namespace Dsh.App;

/// <summary>
/// Shared "is this collection empty?" rule behind the right-panel empty-state placeholders
/// (Jobs / Todo / Queue / Settings).
///
/// Lives in <c>Dsh.App</c> — not in the WPF layer — for two reasons:
///  1. <c>Dsh.App.Tests</c> does not reference the WPF assemblies, so anything testable must sit
///     here (the same reason <see cref="TrajectoryLedger"/> lives in this layer).
///  2. The rule is pure presentation logic with no WPF dependency; only the <c>Visibility</c>
///     mapping belongs to the shell.
/// </summary>
public static class EmptyState
{
    /// <summary>
    /// True when the placeholder should be shown, i.e. the collection is definitely empty.
    /// <para>
    /// Deliberately FAIL-SAFE: an unrecognised value returns <c>false</c> (not empty). If a
    /// binding ever breaks, we show an empty panel rather than hiding real content — a missing
    /// hint is acceptable, invisibly dropping data is not.
    /// </para>
    /// </summary>
    public static bool IsEmpty(object? value)
    {
        int count = value switch
        {
            null => 0,                    // unresolved binding ⇒ treat as empty
            int i => i,
            ICollection c => c.Count,
            _ => -1,                      // unknown shape ⇒ assume non-empty
        };
        return count == 0;
    }

    /// <summary>
    /// Positive counterpart of <see cref="IsEmpty"/>: true when the collection has at least one
    /// item. Used by UI that should hide itself when there is nothing to show (e.g. the
    /// produced-files bar at the bottom of the right panel).
    /// <para>
    /// Fails the same way as <see cref="IsEmpty"/>: an unrecognised value returns <c>false</c>
    /// (treated as empty). When we have no idea whether the collection has items, hiding the
    /// panel is safer than flashing an empty one — a missing bar is cheap to debug, a bar that
    /// "doesn't show files" looks like the broken view bug from the screenshot in 2026-08-31.
    /// </para>
    /// </summary>
    public static bool HasAny(object? value)
    {
        int count = value switch
        {
            null => 0,
            int i => i,
            ICollection c => c.Count,
            _ => 0,                       // unknown shape ⇒ treat as empty (see note above)
        };
        return count > 0;
    }
}
