using System.Globalization;
using Tmds.DBus.Protocol;

namespace OView.Linux.Platform;

/// <summary>
/// Whether the desktop wants animations, read through the XDG desktop portal's Settings
/// interface, the same route <see cref="PortalThemeSource"/> takes for light and dark.
///
/// <para><b>Three keys, because no one key is on every desktop.</b> GNOME's
/// <c>org.gnome.desktop.interface enable-animations</c> is the switch behind its Accessibility
/// panel's "Reduce animation", and its portal publishes it. Plasma's portal publishes
/// <c>kdeglobals</c>, where an <c>AnimationDurationFactor</c> of 0 is animations off. And newer
/// portals add <c>org.freedesktop.appearance reduced-motion</c>, the cross-desktop answer, where
/// 1 asks for reduced motion. A key a portal does not have is simply not an answer.</para>
///
/// <para><b>Off only when the desktop says so.</b> Animations are off when any key that could be
/// read says off, and on otherwise: on before the first answer, on with no portal, on when a key
/// is missing or holds something unexpected. "On" is the toolkit's own default and the Windows
/// head's; anything else would be a guess about a setting nobody has read.</para>
///
/// <para><b>Reads never block the caller.</b> The panel asks at the start of every motion, on the
/// UI thread, where a D-Bus round trip deadlocks the process (issue #124, and
/// <see cref="PortalThemeSource"/> for the whole story). So the bus is asked only off that
/// thread, and <see cref="AnimationsEnabled"/> returns what is already known. Changes arrive with
/// the portal's <c>SettingChanged</c> signal, so switching animations off takes effect at the next
/// motion; where no signal can come, each read starts a refresh the next one sees.</para>
///
/// <para><b>Unverified on a real desktop.</b> The calls, the signal and the variant shapes are
/// written from the portal specification and the two desktops' published keys.</para>
/// </summary>
public sealed class PortalAnimationSetting
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string SettingsInterface = "org.freedesktop.portal.Settings";

    /// <summary>The keys asked for, each a namespace and a key as the portal names them.</summary>
    public static readonly IReadOnlyList<(string Namespace, string Key)> Keys =
    [
        ("org.freedesktop.appearance", "reduced-motion"),
        ("org.gnome.desktop.interface", "enable-animations"),
        ("org.kde.kdeglobals.KDE", "AnimationDurationFactor"),
    ];

    /// <summary>
    /// How long one lookup may take before the desktop counts as having no answer. A portal on the
    /// bus that cannot be activated otherwise holds a read for D-Bus's own 25 s.
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    /// <summary>What each key last said: true for animations on, false for off. Missing keys are absent.</summary>
    private readonly Dictionary<(string Namespace, string Key), bool> _answers = [];

    private readonly Lock _gate = new();

    /// <summary>1 while a refresh is in flight, so reads do not queue bus calls behind each other.</summary>
    private int _refreshing;

    private volatile bool _subscribed;

    /// <summary>True once changes are known to arrive as signals, so reads stop starting refreshes.</summary>
    private volatile bool _followingSignals;

    /// <summary>
    /// Whether animations are on, from what the desktop has said so far. Never touches the bus on
    /// the calling thread.
    /// </summary>
    public bool AnimationsEnabled
    {
        get
        {
            if (!_followingSignals)
            {
                BeginRefresh();
            }

            lock (_gate)
            {
                return Decide(_answers.Values);
            }
        }
    }

    /// <summary>
    /// The answer and where it came from, for the log: a report from a desktop where the panel
    /// did not animate has to say whether it was told not to.
    /// </summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_answers.Count == 0)
            {
                return "on (no animation setting read from the desktop)";
            }

            var said = string.Join(", ", _answers.Select(a => $"{a.Key.Namespace} {a.Key.Key}={(a.Value ? "on" : "off")}"));
            return $"{(Decide(_answers.Values) ? "on" : "off")} ({said})";
        }
    }

    /// <summary>Starts listening and asks the desktop, off the calling thread. Returns at once.</summary>
    public void Start() => BeginRefresh();

    /// <summary>
    /// Off when any answer is off, on otherwise, including when there is no answer at all.
    /// </summary>
    public static bool Decide(IEnumerable<bool> answers) => answers.All(on => on);

    /// <summary>
    /// What one key's value says about animations: true on, false off, null not an answer (a key
    /// this does not know, or a value of a shape it does not expect).
    ///
    /// <para>Takes the plain value rather than the bus's variant so the rule is testable without a
    /// bus. Plasma's factor may come as a number or as the text kdeglobals stores, so both are read.</para>
    /// </summary>
    public static bool? Interpret(string @namespace, string key, object? value) => (@namespace, key, value) switch
    {
        ("org.freedesktop.appearance", "reduced-motion", uint reduce) => reduce != 1,
        ("org.gnome.desktop.interface", "enable-animations", bool enabled) => enabled,
        ("org.kde.kdeglobals.KDE", "AnimationDurationFactor", var factor) => FactorOf(factor) is { } f ? f > 0 : null,
        _ => null,
    };

    /// <summary>
    /// What one <c>SettingChanged</c> signal, or one read, does to the answer. Public so the tests
    /// can raise one with no portal to send it.
    /// </summary>
    public void OnSettingChanged(string @namespace, string key, object? value)
    {
        if (!Keys.Contains((@namespace, key)))
        {
            return;
        }

        lock (_gate)
        {
            if (Interpret(@namespace, key, value) is { } on)
            {
                _answers[(@namespace, key)] = on;
            }
            else
            {
                _answers.Remove((@namespace, key));
            }
        }
    }

    private static double? FactorOf(object? value) => value switch
    {
        double d => d,
        int i => i,
        uint u => u,
        long l => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private void BeginRefresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!_subscribed)
                {
                    await SubscribeAsync().ConfigureAwait(false);
                }

                var answered = false;
                foreach (var (ns, key) in Keys)
                {
                    if (await ReadAsync(ns, key).ConfigureAwait(false) is { } read)
                    {
                        answered = true;
                        OnSettingChanged(ns, key, read.Value);
                    }
                }

                // A portal that answers a read is one that raises the signal, so later reads need
                // not ask again.
                if (answered && _subscribed)
                {
                    _followingSignals = true;
                }
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    private async Task SubscribeAsync()
    {
        try
        {
            var connection = DBusConnection.Session;
            await connection.ConnectAsync().ConfigureAwait(false);

            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = PortalService,
                Path = PortalPath,
                Interface = SettingsInterface,
                Member = "SettingChanged",
            };

            await connection.AddMatchAsync(
                    rule,
                    static (m, _) =>
                    {
                        var body = m.GetBodyReader();
                        return (Namespace: body.ReadString(), Key: body.ReadString(), Value: Plain(body.ReadVariantValue()));
                    },
                    notification =>
                    {
                        if (notification.HasValue)
                        {
                            var change = notification.Value;
                            OnSettingChanged(change.Namespace, change.Key, change.Value);
                            return;
                        }

                        // The connection is gone, so reads go back to asking.
                        _subscribed = false;
                        _followingSignals = false;
                    },
                    emitOnCapturedContext: false,
                    flags: ObserverFlags.EmitOnConnectionClosed)
                .AsTask()
                .WaitAsync(LookupTimeout)
                .ConfigureAwait(false);

            _subscribed = true;
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
        }
    }

    /// <summary>
    /// One key's value, wrapped so a key the portal answered with something unexpected is still
    /// told apart from one it did not answer at all. Null when the portal gave no answer.
    /// </summary>
    private static async Task<Answer?> ReadAsync(string @namespace, string key)
    {
        try
        {
            var connection = DBusConnection.Session;
            await connection.ConnectAsync().ConfigureAwait(false);

            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalService,
                path: PortalPath,
                @interface: SettingsInterface,
                member: "Read",
                signature: "ss",
                flags: MessageFlags.None);
            writer.WriteString(@namespace);
            writer.WriteString(key);

            var value = await connection.CallMethodAsync(
                    writer.CreateMessage(),
                    static (m, _) => Plain(m.GetBodyReader().ReadVariantValue()))
                .WaitAsync(LookupTimeout)
                .ConfigureAwait(false);

            return new Answer(value);
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            // Includes the portal's "no such key", which is how a desktop without this key says so.
            return null;
        }
    }

    /// <summary>
    /// The plain value inside a portal variant. <c>Read</c> nests it one variant deeper than the
    /// signal does, so variants are unwrapped until something else falls out.
    /// </summary>
    private static object? Plain(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }

        return value.Type switch
        {
            VariantValueType.Bool => value.GetBool(),
            VariantValueType.UInt32 => value.GetUInt32(),
            VariantValueType.Int32 => value.GetInt32(),
            VariantValueType.Double => value.GetDouble(),
            VariantValueType.String => value.GetString(),
            _ => null,
        };
    }

    private static bool IsBusFailure(Exception ex) =>
        ex is DBusExceptionBase or IOException or InvalidOperationException
            or TimeoutException or OperationCanceledException;

    private sealed record Answer(object? Value);
}
