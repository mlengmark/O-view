using OView.App.Platform;

namespace OView.TestSupport;

/// <summary>
/// An in-memory stand-in for <see cref="IPanelPositionStore"/>, so the placement rule's edge
/// cases (an unplugged display, a corrupt file's real-world equivalent of "nothing stored")
/// are exercised without touching a file.
/// </summary>
public sealed class FakePanelPositionStore : IPanelPositionStore
{
    private readonly Dictionary<string, PanelPlace> _places = [];

    public int SaveCalls { get; private set; }

    public PanelPlace? Read(string displayId) =>
        _places.TryGetValue(displayId, out var place) ? place : null;

    public void Save(string displayId, PanelPlace place)
    {
        _places[displayId] = place;
        SaveCalls++;
    }

    public void Forget(string displayId) => _places.Remove(displayId);
}
