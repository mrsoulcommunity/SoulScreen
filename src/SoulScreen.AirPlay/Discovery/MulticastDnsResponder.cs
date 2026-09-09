using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SoulScreen.Core.Logging;

namespace SoulScreen.AirPlay.Discovery;

/// <summary>
/// A minimal, self-contained mDNS responder.
/// <para>
/// It exists instead of a NuGet package because AirPlay is unusually picky: the TXT
/// entries must be emitted verbatim, the SRV target must resolve on the same packet,
/// and the announcement cadence matters for how quickly the receiver appears in
/// Control Center. Owning the wire format keeps all of that debuggable.
/// </para>
/// <para>
/// It coexists with Apple's Bonjour service (installed by iTunes) because the socket
/// is opened with address reuse, so both can share UDP/5353.
/// </para>
/// </summary>
public sealed class MulticastDnsResponder : IAsyncDisposable
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndPoint = new(MulticastGroup, 5353);
    private const string ServiceEnumerationName = "_services._dns-sd._udp.local";

    private readonly ILogger _log = Log.For("mdns");
    private readonly List<ServiceProfile> _services = [];
    private readonly object _sendLock = new();
    private readonly Dictionary<int, IPAddress> _interfaceAddresses = [];

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _announceLoop;

    /// <summary>Hostname advertised in A records, without the ".local" suffix.</summary>
    public string HostName { get; set; } = "SoulScreen";

    public IReadOnlyList<ServiceProfile> Services => _services;

    public bool IsRunning => _socket is not null;

    public void Advertise(ServiceProfile service)
    {
        _services.Add(service);
        _log.Debug($"advertising {service}");
    }

    public void ClearServices() => _services.Clear();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is not null) return Task.CompletedTask;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false,
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            // Bind to the wildcard address: on Windows a socket bound to one interface
            // address does not reliably receive multicast.
            socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            // Hearing our own packets makes conflict detection and log-based debugging possible.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                "Could not bind UDP port 5353 for mDNS. Another responder may be holding it exclusively.", ex);
        }

        JoinAllInterfaces(socket);

        _socket = socket;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _announceLoop = Task.Run(() => AnnounceLoopAsync(_cts.Token), CancellationToken.None);
        _log.Info($"responder up on 5353 as {HostName}.local across {_interfaceAddresses.Count} interface(s)");
        return Task.CompletedTask;
    }

    private void JoinAllInterfaces(Socket socket)
    {
        _interfaceAddresses.Clear();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            IPv4InterfaceProperties ipv4;
            try { ipv4 = nic.GetIPProperties().GetIPv4Properties(); }
            catch (NetworkInformationException) { continue; }
            if (ipv4 is null) continue;

            var address = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (address is null) continue;

            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastGroup, address));
                _interfaceAddresses[ipv4.Index] = address;
                _log.Debug($"joined 224.0.0.251 on {nic.Name} ({address}) idx={ipv4.Index}");
            }
            catch (SocketException ex)
            {
                _log.Debug($"could not join multicast on {nic.Name}: {ex.SocketErrorCode}");
            }
        }

        if (_interfaceAddresses.Count == 0)
            _log.Warn("no multicast-capable IPv4 interface found; the receiver will not be discoverable");
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var socket = _socket!;
        var buffer = new byte[9000];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log.Debug($"receive failed: {ex.SocketErrorCode}");
                continue;
            }

            try
            {
                var message = DnsMessage.Parse(buffer.AsSpan(0, result.ReceivedBytes));
                if (message.IsQuery && message.Questions.Count > 0)
                    HandleQuery(message, (IPEndPoint)result.RemoteEndPoint, result.PacketInformation.Interface);
            }
            catch (Exception ex)
            {
                // A malformed packet from any device on the LAN must never take us down.
                _log.Trace($"ignoring malformed mDNS packet from {result.RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    private void HandleQuery(DnsMessage query, IPEndPoint remote, int interfaceIndex)
    {
        if (!_interfaceAddresses.TryGetValue(interfaceIndex, out var localAddress))
            localAddress = _interfaceAddresses.Values.FirstOrDefault() ?? IPAddress.Loopback;

        var response = DnsMessage.Response();
        var wantsUnicast = false;

        foreach (var question in query.Questions)
        {
            wantsUnicast |= question.WantsUnicastReply;
            AnswerQuestion(question, localAddress, response);
        }

        if (response.Answers.Count == 0) return;

        _log.Trace($"answering {query.Questions[0].Type} {query.Questions[0].Name} from {remote}");
        Send(response, wantsUnicast ? remote : MulticastEndPoint, localAddress);
    }

    private void AnswerQuestion(DnsQuestion question, IPAddress localAddress, DnsMessage response)
    {
        var name = question.Name.TrimEnd('.');
        var hostFqdn = HostName + ".local";

        // "Which service types exist here?" - answered with one PTR per distinct type.
        if (question.Type is DnsRecordType.Ptr or DnsRecordType.Any &&
            name.Equals(ServiceEnumerationName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var type in _services.Select(s => s.QualifiedServiceType).Distinct(StringComparer.OrdinalIgnoreCase))
                response.Answers.Add(new PtrRecord(ServiceEnumerationName, type));
            return;
        }

        foreach (var service in _services)
        {
            var matchesType = name.Equals(service.QualifiedServiceType, StringComparison.OrdinalIgnoreCase);
            var matchesInstance = name.Equals(service.FullName, StringComparison.OrdinalIgnoreCase);

            if (matchesType && question.Type is DnsRecordType.Ptr or DnsRecordType.Any)
            {
                response.Answers.Add(new PtrRecord(service.QualifiedServiceType, service.FullName));
                AddServiceDetails(service, localAddress, response.Additionals);
            }
            else if (matchesInstance && question.Type is DnsRecordType.Srv or DnsRecordType.Any)
            {
                response.Answers.Add(new SrvRecord(service.FullName, service.HostName, service.Port));
                AddHostRecords(localAddress, response.Additionals);
            }
            else if (matchesInstance && question.Type is DnsRecordType.Txt)
            {
                response.Answers.Add(new TxtRecord(service.FullName, service.TxtEntries));
            }
        }

        if (question.Type is DnsRecordType.A or DnsRecordType.Any &&
            name.Equals(hostFqdn, StringComparison.OrdinalIgnoreCase))
        {
            response.Answers.Add(new ARecord(hostFqdn, localAddress));
        }
    }

    private void AddServiceDetails(ServiceProfile service, IPAddress localAddress, List<DnsRecord> into)
    {
        into.Add(new SrvRecord(service.FullName, service.HostName, service.Port));
        into.Add(new TxtRecord(service.FullName, service.TxtEntries));
        AddHostRecords(localAddress, into);
    }

    private void AddHostRecords(IPAddress localAddress, List<DnsRecord> into)
    {
        var hostFqdn = HostName + ".local";
        if (into.OfType<ARecord>().Any(a => a.Name.Equals(hostFqdn, StringComparison.OrdinalIgnoreCase))) return;
        into.Add(new ARecord(hostFqdn, localAddress));
    }

    /// <summary>
    /// Announces on start and then re-announces periodically. iOS caches aggressively and
    /// a laptop that suspends its Wi-Fi radio can miss the initial burst entirely, so a
    /// slow heartbeat keeps the entry alive in Control Center.
    /// </summary>
    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        try
        {
            // RFC 6762 wants at least two announcements a second apart; three is what
            // Apple's own receivers send.
            for (var i = 0; i < 3 && !token.IsCancellationRequested; i++)
            {
                Announce();
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                Announce();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Sends an unsolicited response describing every advertised service.</summary>
    public void Announce()
    {
        foreach (var (_, localAddress) in _interfaceAddresses)
        {
            var response = DnsMessage.Response();
            foreach (var service in _services)
            {
                response.Answers.Add(new PtrRecord(service.QualifiedServiceType, service.FullName));
                response.Answers.Add(new SrvRecord(service.FullName, service.HostName, service.Port));
                response.Answers.Add(new TxtRecord(service.FullName, service.TxtEntries));
            }
            if (response.Answers.Count == 0) return;
            response.Answers.Add(new ARecord(HostName + ".local", localAddress));
            Send(response, MulticastEndPoint, localAddress);
        }
    }

    /// <summary>Withdraws the advertisement by repeating every record with a TTL of zero.</summary>
    private void SendGoodbye()
    {
        foreach (var (_, localAddress) in _interfaceAddresses)
        {
            var response = DnsMessage.Response();
            foreach (var service in _services)
            {
                response.Answers.Add(new PtrRecord(service.QualifiedServiceType, service.FullName, Ttl: 0));
                response.Answers.Add(new SrvRecord(service.FullName, service.HostName, service.Port, Ttl: 0));
                response.Answers.Add(new TxtRecord(service.FullName, service.TxtEntries, Ttl: 0));
            }
            if (response.Answers.Count == 0) return;
            response.Answers.Add(new ARecord(HostName + ".local", localAddress, Ttl: 0));
            Send(response, MulticastEndPoint, localAddress);
        }
    }

    private void Send(DnsMessage message, IPEndPoint destination, IPAddress viaInterface)
    {
        var socket = _socket;
        if (socket is null) return;
        try
        {
            var bytes = message.ToArray();
            // MulticastInterface is socket-wide state, so serialise the whole send.
            lock (_sendLock)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    viaInterface.GetAddressBytes());
                socket.SendTo(bytes, SocketFlags.None, destination);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException ex)
        {
            _log.Debug($"send to {destination} via {viaInterface} failed: {ex.SocketErrorCode}");
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn($"could not encode mDNS response: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;

        try { SendGoodbye(); } catch { /* best effort on the way out */ }

        await cts.CancelAsync().ConfigureAwait(false);
        var socket = Interlocked.Exchange(ref _socket, null);
        socket?.Dispose();

        foreach (var task in new[] { _receiveLoop, _announceLoop })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _receiveLoop = null;
        _announceLoop = null;
        cts.Dispose();
        _log.Info("responder stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
