namespace OView.Core.Models;

/// <summary>
/// Edge-triggered threshold detection: notify exactly once when session usage
/// crosses from below the threshold to at-or-above it, re-arming when it drops
/// below (a window reset) or goes unknown. Level-triggered logic would fire on
/// every poll while above threshold — one notification per crossing is the
/// contract.
/// </summary>
public sealed class ThresholdWatcher(int thresholdPercent)
{
    private bool _above;

    /// <summary>True exactly when this sample crosses the threshold upward.</summary>
    public bool ShouldNotify(int? sessionPercent)
    {
        if (sessionPercent is not { } p || p < thresholdPercent)
        {
            _above = false;   // re-arm: below threshold, or data went unknown
            return false;
        }

        if (_above)
        {
            return false;     // still above — already notified for this crossing
        }

        _above = true;
        return true;
    }
}
