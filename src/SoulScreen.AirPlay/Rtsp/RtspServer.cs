using System.Net;
using System.Net.Sockets;
using System.Text;
using SoulScreen.Core.Buffers;
using SoulScreen.Core.Logging;

namespace SoulScreen.AirPlay.Rtsp;

public interface IRtspRequestHandler
{
    /// <summary>Handles one request. <paramref name="connectionState"/> is whatever the
    /// handler stored for this TCP connection via <see cref="RtspConnectionContext"/>.</summary>
    Task<RtspResponse> HandleAsync(RtspRequest request, RtspConnectionContext context, CancellationToken cancellationToken);

    /// <summary>Called once when a connection closes so per-session resources can be released.</summary>
    void OnConnectionClosed(RtspConnectionContext context);
}

/// <summary>Per-connection scratch space handed to the request handler.</summary>
public sealed class RtspConnectionContext(IPEndPoint remoteEndPoint, IPAddress localAddress)
{
    public IPEndPoint RemoteEndPoint { get; } = remoteEndPoint;

    /// <summary>Local address the sender reached us on - needed when we hand out data
    /// ports, because a multi-homed PC must answer on the same subnet.</summary>
    public IPAddress LocalAddress { get; } = localAddress;

    /// <summary>Handler-owned session object; the AirPlay handler stores its session here.</summary>
    public object? Session { get; set; }

    private Action? _close;

    /// <summary>Installed by the server: cancels the connection's read loop, which closes
    /// the socket and runs the normal end-of-connection cleanup.</summary>
    internal void AttachCloser(Action close) => _close = close;

    /// <summary>
    /// Asks the server to drop this connection. The sender sees its control channel close
    /// and ends the session on its side, exactly as if the receiver had gone away.
    /// </summary>
    public void RequestClose() => _close?.Invoke();

    public override string ToString() => RemoteEndPoint.ToString();
}

/// <summary>
/// TCP listener for the AirPlay control channel. Deliberately tolerant: it accepts both
/// RTSP/1.0 and HTTP/1.1 framing, keeps connections alive, and isolates one misbehaving
/// sender from the rest.
/// </summary>
public sealed class RtspServer(IRtspRequestHandler handler, string serverName = "AirTunes/220.68") : IAsyncDisposable
{
    private readonly ILogger _log = Log.For("rtsp");
    private readonly List<Task> _connections = [];
    private readonly object _connectionsLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>Log every request line and response status.</summary>
    public bool Trace { get; set; }

    public int Port { get; private set; }

    public Task StartAsync(ushort port, CancellationToken cancellationToken = default)
    {
        if (_listener is not null) return Task.CompletedTask;

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Could not listen on TCP port {port}. Another AirPlay receiver may already be running.", ex);
        }

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        _log.Info($"control channel listening on {Port}");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener!;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log.Debug($"accept failed: {ex.SocketErrorCode}");
                continue;
            }

            var task = Task.Run(() => ServeConnectionAsync(client, token), CancellationToken.None);
            lock (_connectionsLock)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken serverToken)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
        var context = new RtspConnectionContext(remote, local);
        _log.Info($"connection from {remote}");

        // One token per connection, so a single sender can be dropped - the "disconnect"
        // action in the app - without taking the listener or any other connection with it.
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var token = connectionCts.Token;
        context.AttachCloser(() =>
        {
            try { connectionCts.Cancel(); }
            catch (ObjectDisposedException) { /* the connection already ended */ }
        });

        var requestCount = 0;

        try
        {
            // Mirroring is latency-sensitive and the control channel carries small
            // messages, so Nagle would only add delay.
            client.NoDelay = true;
            using var stream = client.GetStream();
            var reader = new RtspStreamReader(stream);

            while (!token.IsCancellationRequested)
            {
                var request = await reader.ReadRequestAsync(token).ConfigureAwait(false);
                if (request is null) break; // clean close

                requestCount++;
                if (Trace)
                {
                    _log.Debug($"<- {request}");
                    foreach (var (key, value) in request.Headers) _log.Trace($"     {key}: {value}");
                    if (request.Body.Length > 0) _log.Trace($"     body:\n{Hex.Dump(request.Body, 192)}");
                }

                RtspResponse response;
                try
                {
                    response = await handler.HandleAsync(request, context, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"handler threw on {request.Method} {request.Path}", ex);
                    response = RtspResponse.Status(500, "Internal Server Error");
                }

                if (Trace) _log.Debug($"-> {response}");

                var bytes = response.Serialize(request.Protocol, request.CSeq, serverName);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* the phone closed the socket, which is how sessions normally end */ }
        catch (Exception ex)
        {
            _log.Warn($"connection {remote} failed", ex);
        }
        finally
        {
            try { handler.OnConnectionClosed(context); }
            catch (Exception ex) { _log.Warn("connection teardown failed", ex); }
            client.Dispose();

            if (requestCount == 0)
            {
                // A sender that opens a socket and says nothing is doing a reachability
                // probe, not starting a session. Worth calling out: it looks identical to a
                // failed session in a summary log, and the two need very different fixes.
                _log.Info($"connection from {remote} closed without sending a request (reachability probe)");
            }
            else
            {
                _log.Info($"connection from {remote} closed after {requestCount} request(s)");
            }
        }
    }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;

        await cts.CancelAsync().ConfigureAwait(false);
        var listener = Interlocked.Exchange(ref _listener, null);
        listener?.Stop();

        Task[] pending;
        lock (_connectionsLock) pending = [.. _connections];
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { /* connections that will not drain are abandoned with the listener */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _acceptLoop = null;
        cts.Dispose();
        _log.Info("control channel stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>Incremental request reader: header block first, then exactly Content-Length bytes.</summary>
internal sealed class RtspStreamReader(Stream stream)
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private byte[] _buffer = new byte[8192];
    private int _length;
    private int _consumed;

    public async Task<RtspRequest?> ReadRequestAsync(CancellationToken token)
    {
        var headerEnd = await ReadUntilHeaderEndAsync(token).ConfigureAwait(false);
        if (headerEnd < 0) return null;

        var headerText = Encoding.UTF8.GetString(_buffer, _consumed, headerEnd - _consumed);
        _consumed = headerEnd + 4; // skip the terminating CRLFCRLF

        var request = ParseHead(headerText);

        var contentLength = 0;
        if (request.Headers.TryGetValue("Content-Length", out var value) &&
            int.TryParse(value, out var parsed) && parsed > 0)
        {
            if (parsed > MaxBodyBytes)
                throw new InvalidDataException($"Refusing a {parsed} byte RTSP body.");
            contentLength = parsed;
        }

        if (contentLength > 0)
            request.Body = await ReadExactlyAsync(contentLength, token).ConfigureAwait(false);

        return request;
    }

    private static RtspRequest ParseHead(string headerText)
    {
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 3)
            throw new InvalidDataException($"Malformed request line: '{lines[0]}'");

        var request = new RtspRequest
        {
            Method = requestLine[0].ToUpperInvariant(),
            Uri = requestLine[1],
            Protocol = requestLine[2],
        };

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            request.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return request;
    }

    /// <summary>Returns the offset of the CRLFCRLF that ends the header block, or -1 on a clean close.</summary>
    private async Task<int> ReadUntilHeaderEndAsync(CancellationToken token)
    {
        // Tracked relative to _consumed, not as an absolute index: FillAsync may compact the
        // buffer and shift everything down, which would leave an absolute cursor pointing
        // past the terminator and hang the connection waiting for bytes already received.
        var scanned = 0;

        while (true)
        {
            var index = IndexOfHeaderEnd(_consumed + Math.Max(0, scanned - 3));
            if (index >= 0) return index;

            scanned = _length - _consumed;

            if (scanned > MaxHeaderBytes)
                throw new InvalidDataException("RTSP header block is implausibly large.");

            if (!await FillAsync(token).ConfigureAwait(false))
                return -1;
        }
    }

    private int IndexOfHeaderEnd(int from)
    {
        var span = _buffer.AsSpan(0, _length);
        for (var i = Math.Max(from, _consumed); i + 3 < _length; i++)
            if (span[i] == '\r' && span[i + 1] == '\n' && span[i + 2] == '\r' && span[i + 3] == '\n')
                return i;
        return -1;
    }

    /// <summary>Bytes received but not yet parsed into a request.</summary>
    public int PendingByteCount => _length - _consumed;

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken token)
    {
        while (_length - _consumed < count)
            if (!await FillAsync(token).ConfigureAwait(false))
                throw new EndOfStreamException($"Connection closed with {count - (_length - _consumed)} body bytes outstanding.");

        var body = _buffer.AsSpan(_consumed, count).ToArray();
        _consumed += count;
        return body;
    }

    /// <summary>Reads more bytes, compacting or growing the buffer first. Returns false at EOF.</summary>
    private async Task<bool> FillAsync(CancellationToken token)
    {
        if (_consumed > 0 && _consumed == _length)
        {
            _consumed = 0;
            _length = 0;
        }
        else if (_length == _buffer.Length)
        {
            if (_consumed > 0)
            {
                Array.Copy(_buffer, _consumed, _buffer, 0, _length - _consumed);
                _length -= _consumed;
                _consumed = 0;
            }
            else
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }
        }

        var read = await stream.ReadAsync(_buffer.AsMemory(_length), token).ConfigureAwait(false);
        if (read == 0) return false;
        _length += read;
        return true;
    }

    /// <summary>Diagnostic helper for tracing unexpected framing.</summary>
    public string DumpPending() => Hex.Dump(_buffer.AsSpan(_consumed, _length - _consumed));
}
