using OView.Core.Models;
using OView.Core.Providers;

namespace OView.TestSupport;

/// <summary>A provider that always answers with the snapshot it was given: the composite's answer on a given machine.</summary>
public sealed class FixedProvider(UsageSnapshot snapshot) : IUsageProvider
{
    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => snapshot;
}

/// <summary>
/// A provider that throws on every read, to exercise the failure paths: a locked file by
/// default, or whatever <paramref name="exception"/> a test wants reported.
/// </summary>
public sealed class ThrowingProvider(Exception? exception = null) : IUsageProvider
{
    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => throw exception ?? new IOException("locked");
}
