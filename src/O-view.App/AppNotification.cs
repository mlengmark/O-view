namespace OView.App;

/// <summary>
/// A request to tell the user something, raised by the engine and rendered by whichever
/// head is attached — a balloon tip on Windows, a freedesktop notification on Linux.
///
/// <para>Deliberately not a balloon tip in the engine's vocabulary: the decision of
/// <em>whether</em> to notify is accounting logic and belongs here, while <em>how</em> it
/// appears is the platform's business.</para>
/// </summary>
public sealed record AppNotification(string Title, string Message);
