using Avalonia;

namespace OView.Linux.Panel;

/// <summary>
/// Getting the window manager to actually put the panel where letting go of it says it belongs:
/// the settled place is asked for, read back, and asked once more when the answer was no.
///
/// <para><b>Why asking once is not enough.</b> An X11 move is run by the window manager, which
/// holds the pointer until the button comes up, and the positions it reports stop arriving when
/// the pointer stops, not when the button does. So the moment a drop becomes observable
/// (<see cref="PanelDrag"/>) can still be inside the manager's own grab, where a position request
/// is discarded without a word: no move, and no position change to notice the refusal by.
/// Maximilian's fourth VM run is three of those in a row, "place saved, settled at 307,56 dip"
/// with the panel left hanging off the bottom of the screen, and the refusal only surfacing
/// against the <i>next</i> gesture's position change. Reading the position back turns that
/// silence into an answer, and one more request is enough, because by then the button is up.</para>
///
/// <para><b>Twice, and then it says so.</b> <see cref="MostRequests"/> is the whole of the retry:
/// a manager that has refused two requests a grace apart is one that will not be told, and a
/// panel being asked to move forever would be worse than one left where the user dropped it. The
/// log names that outcome rather than going quiet about it, a refusal nobody can read is how
/// this reached a VM instead of a test.</para>
///
/// <para>Driven through three callbacks rather than holding the window, for the reason
/// <see cref="PanelDrag"/> gives: Avalonia has no window without a windowing subsystem and CI has
/// none, so a stand-in window manager in a test is the only way any of this is provable.</para>
/// </summary>
public sealed class PanelMoveSettling(
    Func<PixelPoint> observe, Action<PixelPoint> request, Action<string>? log = null)
{
    /// <summary>
    /// How many times one settled place is asked for before this reports the refusal and stops.
    /// Two: the first can land inside the manager's grab, and a second a grace later cannot.
    /// </summary>
    public const int MostRequests = 2;

    /// <summary>The place asked for, until it has been read back. Null when nothing is owed.</summary>
    private PixelPoint? _asked;

    private int _requests;

    /// <summary>Whether a request is still waiting to be read back.</summary>
    public bool AwaitingAnswer => _asked is not null;

    /// <summary>Asks for <paramref name="place"/>, which begins a fresh settling.</summary>
    public void Ask(PixelPoint place)
    {
        _requests = 0;
        Request(place);
    }

    /// <summary>
    /// Forgets a request still waiting on an answer, because a new move has made it stale, the
    /// panel is about to be somewhere else entirely and the old place is not where it belongs.
    /// </summary>
    public void Abandon() => _asked = null;

    /// <summary>
    /// Reads the panel's position back, says what the window manager did with the request, and
    /// answers whether another read-back is owed, which is exactly when the request was refused
    /// and there is one left to spend on it.
    /// </summary>
    public bool ReadBack()
    {
        if (_asked is not { } asked)
        {
            return false;
        }

        var at = observe();
        var honoured = at == asked;

        // Word for word what a keyboard move writes, because a reader comparing a drop with a
        // nudge in the same log is comparing the same question.
        log?.Invoke(
            $"panel position -> {at.X},{at.Y} (requested {asked.X},{asked.Y}; "
            + $"{(honoured ? "honoured" : "OVERRIDDEN by the window manager")})");

        if (honoured)
        {
            _asked = null;
            return false;
        }

        if (_requests >= MostRequests)
        {
            _asked = null;
            log?.Invoke(
                $"panel left at {at.X},{at.Y}, {asked.X},{asked.Y} was asked for {_requests} times "
                + "and refused; the next open will use it instead");
            return false;
        }

        Request(asked);
        return true;
    }

    private void Request(PixelPoint place)
    {
        _asked = place;
        _requests++;
        request(place);
    }
}
