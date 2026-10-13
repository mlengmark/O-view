using Tmds.DBus.Protocol;

namespace OView.Linux.Tests.Runtime;

/// <summary>One pixmap an item published, as the item published it: ARGB32, network byte order.</summary>
/// <param name="Width">Pixels across, as the item declared it.</param>
/// <param name="Height">Pixels down, as the item declared it.</param>
/// <param name="Argb32">The raw bytes. Four per pixel, so a well-formed pixmap has Width × Height × 4 of them.</param>
public sealed record TrayIconPixmap(int Width, int Height, byte[] Argb32);

/// <summary>What one call to <c>RegisterStatusNotifierItem</c> left behind.</summary>
/// <param name="BusName">
/// Where the item actually lives. Taken from the message's sender, not from its argument:
/// the bus assigns the sender and an item cannot forge it, whereas the argument is whatever
/// the item chose to say.
/// </param>
/// <param name="Service">The argument as given — a bus name from most items, an object path from some.</param>
/// <param name="ObjectPath">
/// Where to ask the item about itself. The spec lets the argument be either a bus name or an
/// object path and real watchers accept both, so this is the argument when it looks like a
/// path and <c>/StatusNotifierItem</c> otherwise.
/// </param>
public sealed record RegisteredItem(string BusName, string Service, string ObjectPath);

/// <summary>
/// Test code standing in for a KDE notification area: it owns
/// <c>org.kde.StatusNotifierWatcher</c> on the session bus, accepts the items apps register
/// with it, and can ask one of them for its icon.
///
/// <para><b>Why a stand-in rather than a mock.</b> Every Linux tray defect that reached a
/// desktop — the first click deadlocking (#124), the menu taking tens of seconds (#125), the
/// click segfaulting on the D-Bus thread (#143) — needed a real bus and a real dispatcher
/// together, which is exactly what no in-process fake can give. So this talks the actual
/// protocol over the actual bus, and the <c>linux-runtime</c> job runs it under
/// <c>dbus-run-session</c>.</para>
///
/// <para><b>It is both the watcher and the host.</b> A notification area is one process
/// playing both parts, so <c>IsStatusNotifierHostRegistered</c> reads true from the moment
/// this starts. An item that waits for a host before publishing itself would otherwise wait
/// for ever.</para>
///
/// <para><b>Its own connection, not <see cref="DBusConnection.Session"/>.</b> That one is a
/// shared static the app's own code uses, and disposing it would leave the rest of the test
/// run without a bus. A separate connection is also what makes <see cref="DisposeAsync"/>
/// mean something: the name goes with it, which is how a test observes the difference
/// between a host being there and being gone.</para>
/// </summary>
public sealed class StandInTrayHost : IAsyncDisposable
{
    /// <summary>The well-known name a notification area owns, and that <c>SniHostProbe</c> looks for.</summary>
    public const string WatcherService = "org.kde.StatusNotifierWatcher";

    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>Where an item lives when its registration named a bus rather than a path.</summary>
    private const string DefaultItemPath = "/StatusNotifierItem";

    /// <summary>
    /// How long <see cref="ReadIconPixmapAsync"/> waits for an item to answer. Well under
    /// D-Bus's own 25 s reply timeout, so a test that is going to fail does so while the job
    /// still has time to upload what it captured.
    /// </summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private readonly DBusConnection _connection;
    private readonly Lock _gate = new();
    private readonly List<RegisteredItem> _items = [];
    private readonly List<string> _hosts = [];

    /// <summary>Completes as soon as the first item registers, so a test need not poll.</summary>
    private readonly TaskCompletionSource<RegisteredItem> _firstItem =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private StandInTrayHost(DBusConnection connection) => _connection = connection;

    /// <summary>
    /// Every item registered so far, oldest first. A snapshot — registrations arrive on the
    /// bus's own thread, so the list behind it keeps changing.
    /// </summary>
    public IReadOnlyList<RegisteredItem> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    /// <summary>
    /// What <c>IsStatusNotifierHostRegistered</c> reports. Always true — see the note on the
    /// class about this being the host as well as the watcher.
    /// </summary>
    public bool IsHostRegistered => true;

    /// <summary>Bus names that called <c>RegisterStatusNotifierHost</c>, oldest first.</summary>
    public IReadOnlyList<string> Hosts
    {
        get { lock (_gate) return [.. _hosts]; }
    }

    /// <summary>
    /// Connects to the session bus, exports the watcher object and takes the name. Throws if
    /// the name is already owned — a real notification area on the same bus would mean the
    /// test was measuring that one instead of this.
    /// </summary>
    public static async Task<StandInTrayHost> StartAsync()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
            ?? throw new InvalidOperationException(
                "DBUS_SESSION_BUS_ADDRESS is not set: there is no session bus to host on.");

        var connection = new DBusConnection(address);
        var host = new StandInTrayHost(connection);

        try
        {
            await connection.ConnectAsync();
            connection.AddMethodHandler(new WatcherObject(host));

            // The object is exported before the name is taken, so the first call to arrive
            // after an item notices the name cannot land on nothing.
            if (!await connection.TryRequestNameAsync(WatcherService, RequestNameOptions.None))
            {
                throw new InvalidOperationException(
                    $"{WatcherService} is already owned on this bus, so the stand-in host cannot take it.");
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return host;
    }

    /// <summary>
    /// The first item to register, waiting up to <paramref name="timeout"/> for one. Throws
    /// <see cref="TimeoutException"/> rather than returning null: a test that got here wanted
    /// an item, and "none arrived" is the failure, not a value to carry on with.
    /// </summary>
    public async Task<RegisteredItem> WaitForItemAsync(TimeSpan timeout) =>
        await _firstItem.Task.WaitAsync(timeout);

    /// <summary>
    /// Asks an item for its <c>IconPixmap</c> — the pixels a notification area would draw.
    /// Empty when the item publishes an icon name instead, which is equally valid under the
    /// spec; null when it could not be asked at all.
    /// </summary>
    public async Task<IReadOnlyList<TrayIconPixmap>?> ReadIconPixmapAsync(RegisteredItem item)
    {
        try
        {
            var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: item.BusName,
                path: item.ObjectPath,
                @interface: PropertiesInterface,
                member: "Get",
                signature: "ss",
                flags: MessageFlags.None);
            writer.WriteString(ItemInterface);
            writer.WriteString("IconPixmap");

            return await _connection
                .CallMethodAsync(writer.CreateMessage(), static (m, _) => ReadPixmaps(m))
                .WaitAsync(ReplyTimeout);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException
                                       or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops the name and the connection with it, which is what makes the host "gone" as far
    /// as anything else on the bus can tell.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        _firstItem.TrySetCanceled();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Unwraps the <c>a(iiay)</c> an item returns. The value arrives inside a variant, and
    /// some items nest it inside another, so unwrap until the array falls out rather than
    /// assuming a depth — the same shape <c>PortalThemeSource</c> deals with.
    /// </summary>
    private static IReadOnlyList<TrayIconPixmap> ReadPixmaps(Message message)
    {
        var value = message.GetBodyReader().ReadVariantValue();
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }

        if (value.Type != VariantValueType.Array)
        {
            return [];
        }

        var pixmaps = new List<TrayIconPixmap>(value.Count);
        for (var i = 0; i < value.Count; i++)
        {
            var entry = value.GetItem(i);
            if (entry.Type != VariantValueType.Struct || entry.Count != 3)
            {
                continue;
            }

            pixmaps.Add(new TrayIconPixmap(
                entry.GetItem(0).GetInt32(),
                entry.GetItem(1).GetInt32(),
                entry.GetItem(2).GetArray<byte>()));
        }

        return pixmaps;
    }

    private void Register(RegisteredItem item)
    {
        lock (_gate)
        {
            _items.Add(item);
        }

        _firstItem.TrySetResult(item);
    }

    private void RegisterHost(string service)
    {
        lock (_gate)
        {
            _hosts.Add(service);
        }
    }

    /// <summary>
    /// The exported <c>/StatusNotifierWatcher</c> object: the two Register methods, the three
    /// properties the spec defines, and introspection so a client that looks before it calls
    /// finds something.
    /// </summary>
    private sealed class WatcherObject(StandInTrayHost host) : IPathMethodHandler
    {
        public string Path => WatcherPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;

            switch (request.InterfaceAsString)
            {
                case WatcherService:
                    HandleWatcherCall(context, request);
                    break;

                case PropertiesInterface:
                    HandlePropertiesCall(context, request);
                    break;

                default:
                    context.ReplyUnknownMethodError();
                    break;
            }

            return ValueTask.CompletedTask;
        }

        private void HandleWatcherCall(MethodContext context, Message request)
        {
            switch (request.MemberAsString)
            {
                case "RegisterStatusNotifierItem":
                {
                    var service = request.GetBodyReader().ReadString();
                    var sender = request.SenderAsString
                        ?? throw new InvalidOperationException("a registration with no sender");

                    host.Register(new RegisteredItem(
                        BusName: sender,
                        Service: service,
                        ObjectPath: service.StartsWith('/') ? service : DefaultItemPath));

                    ReplyEmpty(context);

                    // A real notification area announces each item it took on. Avalonia does
                    // not wait for this, but an item that does would otherwise never believe
                    // its registration landed.
                    Announce("StatusNotifierItemRegistered", service);
                    break;
                }

                case "RegisterStatusNotifierHost":
                    host.RegisterHost(request.GetBodyReader().ReadString());
                    ReplyEmpty(context);
                    Announce("StatusNotifierHostRegistered", argument: null);
                    break;

                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private void HandlePropertiesCall(MethodContext context, Message request)
        {
            switch (request.MemberAsString)
            {
                case "Get":
                {
                    var reader = request.GetBodyReader();
                    reader.ReadString();                      // interface — only one is exported here
                    var property = reader.ReadString();

                    if (Property(property) is not { } value)
                    {
                        context.ReplyError(
                            "org.freedesktop.DBus.Error.UnknownProperty",
                            $"{WatcherService} has no property {property}");
                        break;
                    }

                    var writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(value);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "GetAll":
                {
                    var writer = context.CreateReplyWriter("a{sv}");
                    writer.WriteDictionary(new Dictionary<string, VariantValue>
                    {
                        ["RegisteredStatusNotifierItems"] = RegisteredServices(),
                        ["IsStatusNotifierHostRegistered"] = VariantValue.Bool(host.IsHostRegistered),
                        ["ProtocolVersion"] = VariantValue.Int32(0),
                    });
                    context.Reply(writer.CreateMessage());
                    break;
                }

                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private VariantValue? Property(string name) => name switch
        {
            "RegisteredStatusNotifierItems" => RegisteredServices(),
            "IsStatusNotifierHostRegistered" => VariantValue.Bool(host.IsHostRegistered),
            "ProtocolVersion" => VariantValue.Int32(0),
            _ => (VariantValue?)null,
        };

        /// <summary>
        /// The services as the items named themselves, which is what the property carries —
        /// <see cref="RegisteredItem.BusName"/> is for talking to one, not for publishing.
        /// </summary>
        private VariantValue RegisteredServices() =>
            VariantValue.Array(host.Items.Select(i => i.Service).ToArray());

        private static void ReplyEmpty(MethodContext context) =>
            context.Reply(context.CreateReplyWriter("").CreateMessage());

        private void Announce(string signal, string? argument)
        {
            var writer = host._connection.GetMessageWriter();
            writer.WriteSignalHeader(
                path: WatcherPath,
                @interface: WatcherService,
                member: signal,
                signature: argument is null ? "" : "s");

            if (argument is not null)
            {
                writer.WriteString(argument);
            }

            // Best effort by design: a signal nobody subscribed to is not a failure, and a
            // closed connection means the test is already over.
            host._connection.TrySendMessage(writer.CreateMessage());
        }
    }
}
