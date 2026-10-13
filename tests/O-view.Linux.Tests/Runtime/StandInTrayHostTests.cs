using OView.Linux.Tray;
using Tmds.DBus.Protocol;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// The stand-in notification area, exercised over a real session bus.
///
/// <para>These run only in the <c>linux-runtime</c> job, which provides the bus
/// (<c>dbus-run-session</c>) and sets <c>OVIEW_LINUX_RUNTIME=1</c>. Everywhere else —
/// Windows, and the ordinary test job — there is no bus to talk to, so each test returns
/// early, the same way <see cref="LinuxRuntimeEnvironmentTests"/> does.</para>
///
/// <para><b>This is the first test anywhere that watches a tray host appear and disappear.</b>
/// <see cref="SniHostProbe"/> shipped with no test at all, because nothing in the suite
/// could put a watcher on a bus to probe; that is what the stand-in host is for.</para>
/// </summary>
[Trait("Category", "LinuxRuntime")]
public class StandInTrayHostTests
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// How long any one step may take. Generous because this is a shared CI runner, not a
    /// measurement: nothing here is timing a round trip, only refusing to hang for ever.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A 22 px icon, the size KDE asks items for. Opaque amber, so the bytes are recognisable.</summary>
    private const int IconSize = 22;
    private static readonly byte[] AmberPixel = [0xFF, 0xD9, 0x8E, 0x00];   // A, R, G, B

    [Fact]
    public async Task TheProbeReadsPresentWhileAHostRunsAndAbsentOnceItIsGone()
    {
        if (!InRuntimeJob) return;

        var host = await StandInTrayHost.StartAsync();
        try
        {
            Assert.Equal(TrayHostState.Present, await SniHostProbe.CheckAsync());
        }
        finally
        {
            await host.DisposeAsync();
        }

        // The bus reassigns ownership when it notices the connection has gone, which is not
        // the same instant as Dispose returning — so poll rather than read once.
        Assert.Equal(TrayHostState.Absent, await ProbeUntilAsync(TrayHostState.Absent));
    }

    [Fact]
    public async Task AnItemThatRegistersIsRecordedUnderTheBusNameItCameFrom()
    {
        if (!InRuntimeJob) return;

        await using var host = await StandInTrayHost.StartAsync();
        await using var item = await StandInTrayItem.StartAsync();

        var registered = await host.WaitForItemAsync(Patience);

        Assert.Equal(item.BusName, registered.BusName);
        Assert.Equal(item.BusName, registered.Service);
        Assert.Equal("/StatusNotifierItem", registered.ObjectPath);
        Assert.Equal([registered], host.Items);
    }

    [Fact]
    public async Task TheIconAnItemPublishedCanBeReadBackThroughTheHost()
    {
        if (!InRuntimeJob) return;

        await using var host = await StandInTrayHost.StartAsync();
        await using var item = await StandInTrayItem.StartAsync();

        var registered = await host.WaitForItemAsync(Patience);
        var pixmaps = await host.ReadIconPixmapAsync(registered);

        Assert.NotNull(pixmaps);
        var pixmap = Assert.Single(pixmaps);
        Assert.Equal(IconSize, pixmap.Width);
        Assert.Equal(IconSize, pixmap.Height);
        Assert.Equal(IconSize * IconSize * 4, pixmap.Argb32.Length);
        Assert.Equal(AmberPixel, pixmap.Argb32[..4]);
    }

    [Fact]
    public async Task TheWatcherPropertiesAnswerOverTheBus()
    {
        if (!InRuntimeJob) return;

        await using var host = await StandInTrayHost.StartAsync();
        await using var item = await StandInTrayItem.StartAsync();
        await host.WaitForItemAsync(Patience);

        // Read them as an item would — over the bus — rather than off the host object, which
        // would prove only that the C# property works.
        Assert.True(await ReadWatcherPropertyAsync("IsStatusNotifierHostRegistered", v => v.GetBool()));
        Assert.Equal(
            [item.BusName],
            await ReadWatcherPropertyAsync("RegisteredStatusNotifierItems", v => v.GetArray<string>()));
    }

    /// <summary>Polls the probe until it reports <paramref name="wanted"/>, or gives up and returns what it last saw.</summary>
    private static async Task<TrayHostState> ProbeUntilAsync(TrayHostState wanted)
    {
        var deadline = DateTime.UtcNow + Patience;
        TrayHostState state;

        do
        {
            state = await SniHostProbe.CheckAsync();
            if (state == wanted)
            {
                return state;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        return state;
    }

    private static async Task<T> ReadWatcherPropertyAsync<T>(string property, Func<VariantValue, T> read)
    {
        var connection = DBusConnection.Session;
        await connection.ConnectAsync();

        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: StandInTrayHost.WatcherService,
            path: "/StatusNotifierWatcher",
            @interface: "org.freedesktop.DBus.Properties",
            member: "Get",
            signature: "ss",
            flags: MessageFlags.None);
        writer.WriteString(StandInTrayHost.WatcherService);
        writer.WriteString(property);

        var value = await connection
            .CallMethodAsync(writer.CreateMessage(), static (m, _) => m.GetBodyReader().ReadVariantValue())
            .WaitAsync(Patience);

        return read(value.Type == VariantValueType.Variant ? value.GetVariantValue() : value);
    }

    /// <summary>
    /// Test code standing in for an app with a tray icon: it publishes a
    /// <c>StatusNotifierItem</c> carrying one pixmap and registers itself with whatever owns
    /// the watcher name.
    ///
    /// <para>Deliberately not the real head, which is not started here. This exists so the
    /// host can be tested without one, which is the only order that works: a stand-in host
    /// nobody has registered with has not been shown to accept a registration.</para>
    /// </summary>
    private sealed class StandInTrayItem : IAsyncDisposable
    {
        private const string ItemPath = "/StatusNotifierItem";
        private const string ItemInterface = "org.kde.StatusNotifierItem";

        private readonly DBusConnection _connection;

        private StandInTrayItem(DBusConnection connection, string busName)
        {
            _connection = connection;
            BusName = busName;
        }

        /// <summary>The name the bus gave this connection, and the one it registered under.</summary>
        public string BusName { get; }

        public static async Task<StandInTrayItem> StartAsync()
        {
            var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
                ?? throw new InvalidOperationException(
                    "DBUS_SESSION_BUS_ADDRESS is not set: there is no session bus to publish on.");

            var connection = new DBusConnection(address);

            try
            {
                await connection.ConnectAsync();

                var busName = connection.UniqueName
                    ?? throw new InvalidOperationException("the bus gave this connection no name");

                var item = new StandInTrayItem(connection, busName);
                connection.AddMethodHandler(new ItemObject());

                var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: StandInTrayHost.WatcherService,
                    path: "/StatusNotifierWatcher",
                    @interface: StandInTrayHost.WatcherService,
                    member: "RegisterStatusNotifierItem",
                    signature: "s",
                    flags: MessageFlags.None);
                writer.WriteString(item.BusName);

                await connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(Patience);
                return item;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            _connection.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class ItemObject : IPathMethodHandler
        {
            public string Path => ItemPath;

            public bool HandlesChildPaths => false;

            public ValueTask HandleMethodAsync(MethodContext context)
            {
                var request = context.Request;

                if (request.InterfaceAsString != "org.freedesktop.DBus.Properties"
                    || request.MemberAsString != "Get")
                {
                    context.ReplyUnknownMethodError();
                    return ValueTask.CompletedTask;
                }

                var reader = request.GetBodyReader();
                reader.ReadString();                       // interface — only one is published here
                if (reader.ReadString() != "IconPixmap")
                {
                    context.ReplyError(
                        "org.freedesktop.DBus.Error.UnknownProperty",
                        $"{ItemInterface} publishes only IconPixmap here");
                    return ValueTask.CompletedTask;
                }

                // a(iiay) inside a variant: width, height, and ARGB32 in network byte order.
                // Written by hand because no VariantValue factory builds an array of structs.
                var writer = context.CreateReplyWriter("v");
                writer.WriteSignature("a(iiay)");

                var array = writer.WriteArrayStart(DBusType.Struct);
                writer.WriteStructureStart();
                writer.WriteInt32(IconSize);
                writer.WriteInt32(IconSize);
                writer.WriteArray(Pixels());
                writer.WriteArrayEnd(array);

                context.Reply(writer.CreateMessage());
                return ValueTask.CompletedTask;
            }

            private static byte[] Pixels()
            {
                var pixels = new byte[IconSize * IconSize * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    AmberPixel.CopyTo(pixels, i);
                }

                return pixels;
            }
        }
    }
}
