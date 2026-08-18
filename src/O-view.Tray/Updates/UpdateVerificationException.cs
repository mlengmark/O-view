namespace OView.Tray.Updates;

/// <summary>
/// Thrown when O-view cannot establish that a downloaded installer is the one the release
/// published — a missing or unusable <c>SHA256SUMS</c>, a digest that does not match, or an
/// asset URL pointing somewhere the app will not fetch from.
///
/// <para><b>It exists to be caught separately.</b> A failed download and a failed
/// verification look identical to a general <c>catch</c>, and the right response differs: a
/// network failure should send the user to the releases page to try again, whereas a
/// verification failure must not — routing someone to manually download the very asset that
/// just failed its check would hand them the file the check rejected. Telling them what was
/// observed, and stopping, is the correct behaviour (CLAUDE.md rule 6).</para>
/// </summary>
public sealed class UpdateVerificationException(string message) : Exception(message);
