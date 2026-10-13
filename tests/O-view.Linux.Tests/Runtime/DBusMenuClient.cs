using System.Diagnostics;
using System.Text;
using Tmds.DBus.Protocol;

namespace OView.Linux.Tests.Runtime;

/// <summary>One row of a menu, as <c>com.canonical.dbusmenu</c> described it.</summary>
/// <param name="Id">The item id the menu gave it, which is what a click would be sent against.</param>
/// <param name="Depth">0 for a row of the menu itself, 1 for a row of a submenu, and so on.</param>
/// <param name="Label">The text a person reads, with the mnemonic markers taken out.</param>
/// <param name="Enabled">Whether it could be clicked. Absent from the properties means true.</param>
/// <param name="Visible">Whether it would be drawn at all. Absent from the properties means true.</param>
/// <param name="Kind">The <c>type</c> property — <c>separator</c> for a divider, <c>standard</c> otherwise.</param>
public sealed record DBusMenuRow(int Id, int Depth, string Label, bool Enabled, bool Visible, string Kind)
{
    /// <summary>
    /// The label without its mnemonic markers: an underscore marks the letter after it as the
    /// keyboard shortcut and is not drawn, and a doubled one is a literal underscore. Rows are
    /// matched on this rather than on the raw property, so a test says what the person reads —
    /// "Exit O-view" — and keeps saying it if the head ever marks up a shortcut.
    /// </summary>
    public static string Readable(string label)
    {
        var text = new StringBuilder(label.Length);

        for (var i = 0; i < label.Length; i++)
        {
            if (label[i] != '_')
            {
                text.Append(label[i]);
                continue;
            }

            if (i + 1 < label.Length && label[i + 1] == '_')
            {
                text.Append('_');
                i++;
            }
        }

        return text.ToString();
    }

    public override string ToString()
    {
        var indent = new string(' ', Depth * 2);
        var state = (Enabled ? "" : ", disabled") + (Visible ? "" : ", hidden");

        return Kind == "separator"
            ? $"{indent}──── (#{Id}, separator{state})"
            : $"{indent}{(Label.Length == 0 ? "(no label)" : Label)} (#{Id}{state})";
    }
}

/// <summary>One of the round trips opening a menu takes, and how long it took.</summary>
public sealed record DBusMenuStep(string What, TimeSpan Took);

/// <summary>
/// A menu as a panel has just read it: where it lives, the revision it is at, every row in it,
/// and what each step of reading it cost.
/// </summary>
/// <param name="Path">The object path the item named in its <c>Menu</c> property.</param>
/// <param name="Revision">The layout revision the item replied with.</param>
/// <param name="Rows">Every row, depth first, in the order the menu listed them.</param>
/// <param name="Steps">The round trips, in the order they were made.</param>
public sealed record DBusMenu(
    string Path, uint Revision, IReadOnlyList<DBusMenuRow> Rows, IReadOnlyList<DBusMenuStep> Steps)
{
    /// <summary>How long the whole menu took to open — the duration this slice exists to bound.</summary>
    public TimeSpan Took => Steps.Aggregate(TimeSpan.Zero, (total, step) => total + step.Took);

    /// <summary>
    /// Whether a row with this label would be drawn. Hidden rows do not count: a row a person
    /// cannot see is not one they can click, and the Linux head keeps a row in the menu with
    /// <c>IsVisible</c> false until it has something to offer.
    /// </summary>
    public bool HasRow(string label) =>
        Rows.Any(row => row.Visible && row.Label == DBusMenuRow.Readable(label));

    /// <summary>
    /// Which of <paramref name="labels"/> a person would not find in this menu, each said the
    /// way it is wrong — missing outright, or there but hidden. The difference is the whole
    /// diagnosis: nothing built the row, against the row being built and suppressed.
    /// </summary>
    public IReadOnlyList<string> RowsMissing(IEnumerable<string> labels) =>
    [
        .. labels
            .Where(label => !HasRow(label))
            .Select(label => Rows.Any(row => row.Label == DBusMenuRow.Readable(label))
                ? $"\"{label}\" is in the menu but hidden"
                : $"\"{label}\" is not in the menu at all"),
    ];

    /// <summary>The menu as a reader can take it in, for a test's output and for a failure message.</summary>
    public string Describe()
    {
        var report = new StringBuilder();
        report.AppendLine($"menu        : {Path} (revision {Revision})");

        foreach (var step in Steps)
        {
            report.AppendLine($"{step.What,-40}: {step.Took.TotalMilliseconds:0} ms");
        }

        report.AppendLine($"{"all of it",-40}: {Took.TotalMilliseconds:0} ms");
        report.AppendLine($"──── its {Rows.Count} rows ────");

        if (Rows.Count == 0)
        {
            report.AppendLine("(none — the menu is empty)");
        }
        else
        {
            foreach (var row in Rows)
            {
                report.AppendLine(row.ToString());
            }
        }

        return report.ToString();
    }
}

/// <summary>
/// Test code standing in for the half of a panel that opens a menu: it asks a registered tray
/// item where its menu lives, tells the item the menu is about to be shown, and fetches the
/// layout over <c>com.canonical.dbusmenu</c> — the three calls a notification area makes, in the
/// order it makes them.
///
/// <para><b>Why this exists.</b> The menu on this head took tens of seconds to appear (issue
/// #125), and nothing in the repository could see it: the rows are built in-process and look
/// perfect there, while the delay is in answering the bus. So this talks the protocol a panel
/// talks, over the real session bus, against the shipped binary.</para>
///
/// <para><b>Everything is on one budget.</b> <see cref="OpenAsync"/> takes the time a person
/// will wait and spends it across all three calls, so a menu that arrives only because the
/// object path came back quickly cannot pass. Running out names the call that was outstanding
/// and what the earlier ones cost, because "the menu was slow" is not a report anyone can
/// act on.</para>
///
/// <para><b>Its own connection, not <see cref="DBusConnection.Session"/>.</b> The same reason
/// <see cref="StandInTrayHost"/> keeps one: that one is a shared static the app's own code uses,
/// and disposing it would leave the rest of the run without a bus. A panel is also a different
/// process from the notification area's registry in no desktop at all — but it is a different
/// connection here, which is what makes the item answer the way it would a stranger.</para>
/// </summary>
public sealed class DBusMenuClient : IDisposable
{
    /// <summary>The interface a menu is read over, which is not the tray item's own.</summary>
    public const string MenuInterface = "com.canonical.dbusmenu";

    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>The whole menu, from its root. Both are what libdbusmenu itself asks for.</summary>
    private const int RootId = 0;
    private const int EveryDepth = -1;

    private readonly DBusConnection _connection;

    private DBusMenuClient(DBusConnection connection) => _connection = connection;

    /// <summary>
    /// Connects to the session bus the job set up. Throws rather than returning null when there
    /// is none: a caller that got here wanted to open a menu over it.
    /// </summary>
    public static async Task<DBusMenuClient> ConnectAsync()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
            ?? throw new InvalidOperationException(
                "DBUS_SESSION_BUS_ADDRESS is not set: there is no session bus to open a menu over.");

        var connection = new DBusConnection(address);

        try
        {
            await connection.ConnectAsync();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new DBusMenuClient(connection);
    }

    /// <summary>
    /// Opens the item's menu the way a panel does, within <paramref name="within"/> for all of
    /// it together, and returns what came back.
    /// </summary>
    /// <param name="item">A tray item that has registered with a notification area.</param>
    /// <param name="within">
    /// The whole budget. Spent across the three calls, so the one that runs out is reported with
    /// what the others had already used.
    /// </param>
    /// <exception cref="TimeoutException">The budget ran out, with the calls so far in the message.</exception>
    /// <exception cref="InvalidOperationException">The item named no menu to open.</exception>
    public async Task<DBusMenu> OpenAsync(RegisteredItem item, TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        var steps = new List<DBusMenuStep>();

        // Where the menu is. A panel cannot skip this: the object path is the item's to choose,
        // and Avalonia's is generated per tray icon (/net/avaloniaui/dbusmenu/…).
        var path = await StepAsync(
            "reading the item's menu object path", () => ReadMenuPathAsync(item));

        // What a panel says before it draws: the item's chance to bring the rows up to date.
        // Answered, not obeyed — nothing here acts on the "needs updating" it replies with,
        // because the fetch below asks for the layout as it stands either way.
        await StepAsync(
            "telling the item its menu is about to be shown", () => AboutToShowAsync(item, path));

        var layout = await StepAsync(
            "fetching the menu layout", () => GetLayoutAsync(item, path));

        return new DBusMenu(path, layout.Revision, layout.Rows, steps);

        async Task<T> StepAsync<T>(string what, Func<Task<T>> call)
        {
            var started = clock.Elapsed;
            var left = within - started;

            if (left <= TimeSpan.Zero)
            {
                throw OutOfTime(what, started);
            }

            try
            {
                var result = await call().WaitAsync(left);
                steps.Add(new DBusMenuStep(what, clock.Elapsed - started));
                return result;
            }
            catch (TimeoutException)
            {
                throw OutOfTime(what, started);
            }
        }

        TimeoutException OutOfTime(string what, TimeSpan started)
        {
            var spent = steps.Count == 0
                ? "nothing had come back yet"
                : string.Join(", ", steps.Select(s => $"{s.What} took {s.Took.TotalMilliseconds:0} ms"));

            return new TimeoutException(
                $"the menu did not open within {within.TotalSeconds:0.#} s: {what} was still "
                + $"outstanding {started.TotalMilliseconds:0} ms in, and {spent}.");
        }
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// The item's <c>Menu</c> property — an object path under the spec, and read as a string
    /// because that is all it is used for here. A panel with nothing to open is not a slow menu
    /// but a missing one, so an empty or root path is rejected here rather than fetched.
    /// </summary>
    private async Task<string> ReadMenuPathAsync(RegisteredItem item)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            // The sender the bus assigned, as everywhere else: a registration may name either a
            // bus name or a path, and only the sender is guaranteed to be addressable.
            destination: item.BusName,
            path: item.ObjectPath,
            @interface: PropertiesInterface,
            member: "Get",
            signature: "ss",
            flags: MessageFlags.None);
        writer.WriteString(ItemInterface);
        writer.WriteString("Menu");

        var path = await _connection.CallMethodAsync(
            writer.CreateMessage(), static (m, _) => ReadPath(m));

        return path is { Length: > 1 } && path.StartsWith('/')
            ? path
            : throw new InvalidOperationException(
                $"{item.BusName} published a tray icon but named no menu to open: its "
                + $"{ItemInterface}.Menu property is \"{path}\".");
    }

    /// <summary>
    /// <c>AboutToShow</c>, as a panel calls it on the way to drawing. An error reply is let
    /// through: an item that does not implement it is within the spec, and this call is here to
    /// be made and waited on, not to be the thing under test. A timeout is not let through —
    /// that is the menu failing to open, which is the whole subject.
    /// </summary>
    private async Task<bool> AboutToShowAsync(RegisteredItem item, string menuPath)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: item.BusName,
            path: menuPath,
            @interface: MenuInterface,
            member: "AboutToShow",
            signature: "i",
            flags: MessageFlags.None);
        writer.WriteInt32(RootId);

        try
        {
            return await _connection.CallMethodAsync(
                writer.CreateMessage(), static (m, _) => m.GetBodyReader().ReadBool());
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>GetLayout(0, -1, [])</c> — the whole menu, every property — and the rows it replies
    /// with. The same call libdbusmenu makes, so an item that is slow to answer it is slow for a
    /// person in front of a panel.
    /// </summary>
    private Task<(uint Revision, IReadOnlyList<DBusMenuRow> Rows)> GetLayoutAsync(
        RegisteredItem item, string menuPath)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: item.BusName,
            path: menuPath,
            @interface: MenuInterface,
            member: "GetLayout",
            signature: "iias",
            flags: MessageFlags.None);
        writer.WriteInt32(RootId);
        writer.WriteInt32(EveryDepth);
        writer.WriteArray(Array.Empty<string>());

        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) => ReadLayout(m));
    }

    /// <summary>
    /// The object path out of a <c>Get</c> reply. Unwrapped until something that is not a
    /// variant falls out, and accepted as a plain string too, the same way
    /// <see cref="StandInTrayHost"/> reads a pixmap: items nest the value to differing depths,
    /// and some type it as a string.
    /// </summary>
    private static string ReadPath(Message message)
    {
        var value = Unwrap(message.GetBodyReader().ReadVariantValue());

        return value.Type switch
        {
            VariantValueType.ObjectPath => value.GetObjectPathAsString(),
            VariantValueType.String => value.GetString(),
            _ => "",
        };
    }

    /// <summary>
    /// The <c>u(ia{sv}av)</c> a <c>GetLayout</c> reply carries: the revision, then the root item
    /// with every row nested inside it. The root itself is not a row — it is the menu — so what
    /// comes back is its children, flattened depth first with the depth kept.
    /// </summary>
    private static (uint Revision, IReadOnlyList<DBusMenuRow> Rows) ReadLayout(Message message)
    {
        var reader = message.GetBodyReader();
        var revision = reader.ReadUInt32();

        // The layout is a struct, which is 8-byte aligned and so does not start where the
        // revision ended.
        reader.AlignStruct();
        reader.ReadInt32();                                 // the root's own id, always 0
        reader.ReadDictionaryOfStringToVariantValue();      // and its properties, which no row shows

        var rows = new List<DBusMenuRow>();
        AddRows(reader.ReadArrayOfVariantValue(), depth: 0, rows);

        return (revision, rows);
    }

    /// <summary>
    /// Reads a menu's children into <paramref name="rows"/> and recurses into theirs, so a row
    /// inside a submenu is found as well. Anything that is not the struct the interface promises
    /// is skipped rather than throwing: a malformed entry is one row this cannot report, and the
    /// rows a test is looking for are more useful than an exception about a neighbour.
    /// </summary>
    private static void AddRows(IReadOnlyList<VariantValue> children, int depth, List<DBusMenuRow> rows)
    {
        foreach (var child in children)
        {
            var entry = Unwrap(child);
            if (entry.Type != VariantValueType.Struct || entry.Count != 3)
            {
                continue;
            }

            var id = Unwrap(entry.GetItem(0));
            var properties = ReadProperties(Unwrap(entry.GetItem(1)));

            rows.Add(new DBusMenuRow(
                Id: id.Type == VariantValueType.Int32 ? id.GetInt32() : 0,
                Depth: depth,
                Label: DBusMenuRow.Readable(Text(properties, "label")),
                Enabled: Flag(properties, "enabled"),
                Visible: Flag(properties, "visible"),
                Kind: Text(properties, "type") is { Length: > 0 } kind ? kind : "standard"));

            var grandchildren = Unwrap(entry.GetItem(2));
            if (grandchildren.Type != VariantValueType.Array)
            {
                continue;
            }

            var nested = new List<VariantValue>(grandchildren.Count);
            for (var i = 0; i < grandchildren.Count; i++)
            {
                nested.Add(grandchildren.GetItem(i));
            }

            AddRows(nested, depth + 1, rows);
        }
    }

    /// <summary>The <c>a{sv}</c> of one item's properties, with each value unwrapped once and for all.</summary>
    private static Dictionary<string, VariantValue> ReadProperties(VariantValue properties)
    {
        var read = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
        if (properties.Type != VariantValueType.Dictionary)
        {
            return read;
        }

        for (var i = 0; i < properties.Count; i++)
        {
            var entry = properties.GetDictionaryEntry(i);
            var name = Unwrap(entry.Key);

            if (name.Type == VariantValueType.String)
            {
                read[name.GetString()] = Unwrap(entry.Value);
            }
        }

        return read;
    }

    private static string Text(Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Type == VariantValueType.String
            ? value.GetString()
            : "";

    /// <summary>
    /// A boolean property, true when it is absent — which is what the interface says both
    /// <c>enabled</c> and <c>visible</c> default to, and why a row with no properties at all is
    /// still a row a person would see.
    /// </summary>
    private static bool Flag(Dictionary<string, VariantValue> properties, string name) =>
        !properties.TryGetValue(name, out var value)
        || value.Type != VariantValueType.Bool
        || value.GetBool();

    private static VariantValue Unwrap(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }

        return value;
    }
}
