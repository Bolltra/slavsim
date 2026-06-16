using System;

namespace Mx43Sim.Core.Updates;

/// <summary>
/// Pure version-comparison helpers shared between the GUI's auto-update
/// check and the test suite. Kept in Core (no WinForms / GUI deps) so
/// it can be exercised on Linux.
/// </summary>
public static class VersionUtils
{
    /// <summary>
    /// True if <paramref name="latest"/> (e.g. "v0.2.0" or "0.2.0") is
    /// strictly newer than <paramref name="current"/>. Pre-release tags
    /// (containing '-') are ignored.
    /// </summary>
    public static bool IsNewer(string latest, string current)
    {
        if (TryParse(StripV(latest), out var l) && TryParse(StripV(current), out var c))
        {
            if (l.Major != c.Major) return l.Major > c.Major;
            if (l.Minor != c.Minor) return l.Minor > c.Minor;
            return l.Patch > c.Patch;
        }
        return false;
    }

    private static string StripV(string v) => v.TrimStart('v', 'V').Trim();

    private static bool TryParse(string v, out (int Major, int Minor, int Patch) r)
    {
        r = default;
        var parts = v.Split('.', '-', '+');
        if (parts.Length < 1) return false;
        if (!int.TryParse(parts[0], out r.Major)) return false;
        if (parts.Length > 1 && int.TryParse(parts[1], out var m)) r.Minor = m;
        if (parts.Length > 2 && int.TryParse(parts[2], out var p)) r.Patch = p;
        return true;
    }
}
