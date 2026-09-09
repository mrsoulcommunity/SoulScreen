using System.Net;
using System.Net.Sockets;
using SoulScreen.AirPlay;
using SoulScreen.AirPlay.Discovery;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.AirPlay.Pairing;
using SoulScreen.Core.Logging;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

Log.MinimumLevel = args.Contains("--trace") ? LogLevel.Trace : LogLevel.Debug;
Log.Entry += entry =>
{
    var colour = entry.Level switch
    {
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Warn => ConsoleColor.Yellow,
        LogLevel.Info => ConsoleColor.Cyan,
        LogLevel.Trace => ConsoleColor.DarkGray,
        _ => ConsoleColor.Gray,
    };
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = colour;
    Console.WriteLine(entry.ToString());
    Console.ForegroundColor = previous;
};

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

switch (command)
{
    case "advertise":
        await AdvertiseAsync(stopping.Token);
        break;
    case "browse":
        await BrowseAsync(stopping.Token);
        break;
    case "fairplay":
        FairPlaySelfTest();
        break;
    case "serve":
        await ServeAsync(stopping.Token);
        break;
    default:
        Console.WriteLine("""
            SoulScreen protocol tool

              serve [--name <n>] [--port <p>] [--dump <dir>]
                                                    run the full AirPlay receiver
              advertise [--name <n>] [--port <p>]   publish over mDNS only, no control channel
              browse                                list AirPlay receivers visible on this network
              fairplay                              check the native FairPlay helper

            Options:
              --trace                               verbose protocol logging
              --no-audio                            decline the audio stream
            """);
        break;
}

return 0;

async Task AdvertiseAsync(CancellationToken token)
{
    var options = new AirPlayOptions
    {
        DeviceName = ValueOf("--name") ?? "SoulScreen",
        Port = ushort.Parse(ValueOf("--port") ?? "7000"),
    };

    var identity = DeviceIdentity.LoadOrCreate(options.StateDirectory);
    var (airplay, raop) = AirPlayAdvertisement.Build(options, identity);

    await using var responder = new MulticastDnsResponder { HostName = options.DeviceName };
    responder.Advertise(airplay);
    responder.Advertise(raop);
    await responder.StartAsync(token);

    Console.WriteLine();
    Console.WriteLine($"  Advertising \"{options.DeviceName}\" on port {options.Port}");
    Console.WriteLine($"  device id : {identity.DeviceId}");
    Console.WriteLine($"  public key: {identity.Ed25519PublicKeyHex}");
    Console.WriteLine($"  features  : {AirPlayFeaturePresets.Format(options.Features)}");
    Console.WriteLine();
    Console.WriteLine("  On the iPhone: Control Center -> Screen Mirroring. Ctrl+C to stop.");
    Console.WriteLine();

    try { await Task.Delay(Timeout.Infinite, token); }
    catch (OperationCanceledException) { }
    await responder.StopAsync();
}

/// <summary>
/// Runs the receiver headlessly and reports what arrives. With --dump the decrypted
/// elementary stream is written to disk, which is the fastest way to prove the protocol
/// side works: the file plays in ffplay or VLC without any renderer involved.
/// </summary>
async Task ServeAsync(CancellationToken token)
{
    var options = new AirPlayOptions
    {
        DeviceName = ValueOf("--name") ?? "SoulScreen",
        Port = ushort.Parse(ValueOf("--port") ?? "7000"),
        DumpDirectory = ValueOf("--dump"),
        EnableAudio = !args.Contains("--no-audio"),
        TraceProtocol = args.Contains("--trace"),
    };
    if (!options.EnableAudio) options.Features = AirPlayFeaturePresets.MirroringVideoOnly;

    await using var receiver = new AirPlayReceiver(options);

    var frames = 0L;
    var lastReport = DateTime.UtcNow;

    receiver.StateChanged += (_, e) =>
        Console.WriteLine($"  [state] {e.State}{(e.Device is { } d ? $" - {d}" : "")}{(e.Message is { } m ? $" ({m})" : "")}");

    receiver.VideoFormatChanged += (_, format) => Console.WriteLine($"  [video] {format}");

    receiver.VideoSampleReady += (_, sample) =>
    {
        frames++;
        var now = DateTime.UtcNow;
        if ((now - lastReport).TotalSeconds < 1) return;
        Console.WriteLine($"  [video] {frames} frames, last {sample.Length} bytes{(sample.IsKeyFrame ? " (key)" : "")}");
        lastReport = now;
    };

    receiver.AudioFormatChanged += (_, format) => Console.WriteLine($"  [audio] {format}");

    await receiver.StartAsync(token);

    Console.WriteLine();
    Console.WriteLine($"  SoulScreen receiver \"{receiver.AdvertisedName}\" on port {options.Port}");
    Console.WriteLine($"  device id : {receiver.Identity.DeviceId}");
    Console.WriteLine($"  FairPlay  : {(NativeFairPlay.IsAvailable ? NativeFairPlay.Version : "MISSING - mirroring will fail")}");
    if (options.DumpDirectory is not null) Console.WriteLine($"  dumping to: {options.DumpDirectory}");
    Console.WriteLine();
    Console.WriteLine("  iPhone: Control Center -> Screen Mirroring -> " + receiver.AdvertisedName);
    Console.WriteLine("  Ctrl+C to stop.");
    Console.WriteLine();

    try { await Task.Delay(Timeout.Infinite, token); }
    catch (OperationCanceledException) { }

    Console.WriteLine($"\n  {frames} frames received.");
    await receiver.StopAsync(CancellationToken.None);
}

/// <summary>Sends one PTR query for _airplay._tcp.local and prints whatever answers,
/// which is the quickest way to confirm the responder is reachable from this network.</summary>
async Task BrowseAsync(CancellationToken token)
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

    var query = new DnsMessage();
    query.Questions.Add(new DnsQuestion("_airplay._tcp.local", DnsRecordType.Ptr, WantsUnicastReply: true));
    query.Questions.Add(new DnsQuestion("_raop._tcp.local", DnsRecordType.Ptr, WantsUnicastReply: true));

    await socket.SendToAsync(query.ToArray(), SocketFlags.None,
        new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), token);
    Console.WriteLine("Query sent, listening for 5 seconds...\n");

    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
    deadline.CancelAfter(TimeSpan.FromSeconds(5));

    var buffer = new byte[9000];
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        while (!deadline.IsCancellationRequested)
        {
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), deadline.Token);
            DnsMessage message;
            try { message = DnsMessage.Parse(buffer.AsSpan(0, result.ReceivedBytes)); }
            catch (Exception) { continue; }

            foreach (var record in message.Answers.Concat(message.Additionals))
            {
                if (record is not RawRecord raw || raw.Type != DnsRecordType.Ptr) continue;
                var label = $"{raw.Name} <- {result.RemoteEndPoint}";
                if (seen.Add(label)) Console.WriteLine($"  {label}");
            }
        }
    }
    catch (OperationCanceledException) { }

    if (seen.Count == 0) Console.WriteLine("  (nothing answered)");
}

/// <summary>Exercises the native helper end to end so a broken build is caught here
/// rather than halfway through a mirroring handshake.</summary>
void FairPlaySelfTest()
{
    Console.WriteLine();
    if (!NativeFairPlay.IsAvailable)
    {
        Console.WriteLine($"  FairPlay helper NOT available: {NativeFairPlay.UnavailableReason}");
        Console.WriteLine("  Build it with: pwsh tools/build-fairplay.ps1");
        Console.WriteLine();
        return;
    }

    Console.WriteLine($"  helper : {NativeFairPlay.Version}");

    var failures = 0;
    for (byte mode = 0; mode < 4; mode++)
    {
        using var session = new FairPlaySession();

        // The opener iOS sends, with the last byte selecting one of four challenge modes.
        byte[] request = [0x46, 0x50, 0x4c, 0x59, 0x03, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x04, 0x02, 0x00, mode, 0x00];
        var reply = session.Setup(request);

        // Every reply starts with the FPLY magic, an 0x82 length marker, and echoes the mode.
        var ok = reply.Length == 142
                 && reply[0] == 0x46 && reply[1] == 0x50 && reply[2] == 0x4c && reply[3] == 0x59
                 && reply[11] == 0x82
                 && reply[13] == mode;

        Console.WriteLine($"  mode {mode}: {(ok ? "ok " : "FAIL")} {Convert.ToHexString(reply.AsSpan(0, 16))}...");
        if (!ok) failures++;
    }

    using (var session = new FairPlaySession())
    {
        byte[] setupRequest = [0x46, 0x50, 0x4c, 0x59, 0x03, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x04, 0x02, 0x00, 0x00, 0x00];
        session.Setup(setupRequest);

        // Round two is normally 164 bytes of sender-supplied material; the reply echoes
        // 20 bytes from it after a fixed header, so a synthetic message still proves the
        // plumbing works.
        var handshakeRequest = new byte[164];
        handshakeRequest[0] = 0x46; handshakeRequest[1] = 0x50; handshakeRequest[2] = 0x4c; handshakeRequest[3] = 0x59;
        handshakeRequest[4] = 0x03; handshakeRequest[5] = 0x01; handshakeRequest[6] = 0x03;
        for (var i = 144; i < 164; i++) handshakeRequest[i] = (byte)(i - 144);

        var reply = session.Handshake(handshakeRequest);
        var echoed = reply.AsSpan(12, 20).ToArray();
        var expected = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var ok = reply.Length == 32 && reply[11] == 0x14 && echoed.SequenceEqual(expected);
        Console.WriteLine($"  round2: {(ok ? "ok " : "FAIL")} {Convert.ToHexString(reply)}");
        if (!ok) failures++;

        // The key unwrap needs real sender material, so only check that it runs and
        // produces 16 bytes rather than throwing.
        try
        {
            var key = session.DecryptKey(new byte[72]);
            Console.WriteLine($"  unwrap: ok  produced {key.Length} bytes");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  unwrap: FAIL {ex.Message}");
            failures++;
        }
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "  FairPlay helper is working." : $"  {failures} check(s) failed.");
    Console.WriteLine();
}

string? ValueOf(string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
