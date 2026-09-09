using System.Reflection;
using System.Runtime.InteropServices;
using SoulScreen.Core.Logging;

namespace SoulScreen.AirPlay.FairPlay;

/// <summary>
/// Thrown when a mirroring session needs FairPlay but the native helper is not installed.
/// </summary>
public sealed class FairPlayUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Bridge to <c>soulscreen_fairplay.dll</c>, the native helper that answers Apple's
/// FairPlay SAP handshake and unwraps the AES key from SETUP.
/// <para>
/// The implementation lives outside this assembly on purpose - see tools/build-fairplay.ps1
/// for why. Everything here is written so that a missing DLL degrades to a clear message
/// rather than a crash: discovery, pairing and the USB transport keep working.
/// </para>
/// </summary>
public static class NativeFairPlay
{
    private const string LibraryName = "soulscreen_fairplay";

    private static readonly ILogger Log_ = Log.For("fairplay");
    private static readonly object InitLock = new();
    private static bool _resolverInstalled;
    private static bool? _available;
    private static string? _unavailableReason;

    /// <summary>Whether the helper could be loaded. Cached after the first probe.</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureProbed();
            return _available!.Value;
        }
    }

    /// <summary>Human-readable explanation when <see cref="IsAvailable"/> is false.</summary>
    public static string? UnavailableReason
    {
        get
        {
            EnsureProbed();
            return _unavailableReason;
        }
    }

    /// <summary>Version banner reported by the native helper, when it loaded.</summary>
    public static string? Version { get; private set; }

    public static void ThrowIfUnavailable()
    {
        if (!IsAvailable)
            throw new FairPlayUnavailableException(
                $"FairPlay support is not installed. {_unavailableReason} " +
                "Run 'pwsh tools/build-fairplay.ps1' to build it, then restart SoulScreen.");
    }

    private static void EnsureProbed()
    {
        if (_available.HasValue) return;
        lock (InitLock)
        {
            if (_available.HasValue) return;
            InstallResolver();
            try
            {
                Version = Marshal.PtrToStringAnsi(ss_fairplay_version());
                _available = true;
                Log_.Info($"native helper loaded: {Version}");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _available = false;
                _unavailableReason = ex switch
                {
                    DllNotFoundException => "soulscreen_fairplay.dll was not found.",
                    BadImageFormatException => "soulscreen_fairplay.dll is built for the wrong architecture (SoulScreen needs x64).",
                    _ => "soulscreen_fairplay.dll is missing an expected export.",
                };
                Log_.Warn($"native helper unavailable: {_unavailableReason}");
            }
        }
    }

    /// <summary>
    /// Teaches the runtime where to find the helper: next to the executable, in a native/
    /// subdirectory, or in the repository's native/ folder when running from a build tree.
    /// </summary>
    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        NativeLibrary.SetDllImportResolver(typeof(NativeFairPlay).Assembly, (name, assembly, path) =>
        {
            if (name != LibraryName) return IntPtr.Zero;

            foreach (var candidate in CandidatePaths())
            {
                if (!File.Exists(candidate)) continue;
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    Log_.Debug($"loaded {candidate}");
                    return handle;
                }
            }
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> CandidatePaths()
    {
        const string fileName = LibraryName + ".dll";

        var appDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(appDirectory, fileName);
        yield return Path.Combine(appDirectory, "native", fileName);

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, fileName);
            yield return Path.Combine(assemblyDirectory, "native", fileName);
        }

        // Walk up out of bin/Debug/net8.0 to find the repository's native/ directory.
        var probe = new DirectoryInfo(appDirectory);
        for (var depth = 0; depth < 6 && probe is not null; depth++, probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "native", fileName);
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ss_fairplay_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ss_fairplay_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ss_fairplay_destroy(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ss_fairplay_setup(IntPtr handle, byte[] request, int requestLength, byte[] response, int responseLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ss_fairplay_handshake(IntPtr handle, byte[] request, int requestLength, byte[] response, int responseLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ss_fairplay_decrypt(IntPtr handle, byte[] input, int inputLength, byte[] output, int outputLength);
}

/// <summary>
/// One FairPlay exchange, scoped to a single mirroring session.
/// <para>
/// The sender drives it in two POSTs to /fp-setup: a 16-byte opener that picks a mode and
/// gets a 142-byte challenge back, then a 164-byte message that gets a 32-byte reply and
/// leaves the session able to unwrap the stream key from SETUP.
/// </para>
/// </summary>
public sealed class FairPlaySession : IDisposable
{
    public const int SetupRequestLength = 16;
    public const int SetupResponseLength = 142;
    public const int HandshakeRequestLength = 164;
    public const int HandshakeResponseLength = 32;
    public const int EncryptedKeyLength = 72;
    public const int AesKeyLength = 16;

    private readonly ILogger _log = Log.For("fairplay");
    private IntPtr _handle;

    public FairPlaySession()
    {
        NativeFairPlay.ThrowIfUnavailable();
        _handle = NativeFairPlay.ss_fairplay_create();
        if (_handle == IntPtr.Zero)
            throw new FairPlayUnavailableException("The FairPlay helper could not allocate a session.");
    }

    /// <summary>True once <see cref="Handshake"/> has run and a key can be unwrapped.</summary>
    public bool IsHandshakeComplete { get; private set; }

    /// <summary>Handles a /fp-setup body, choosing the round from its length.</summary>
    public byte[] HandleRequest(ReadOnlySpan<byte> body) => body.Length switch
    {
        SetupRequestLength => Setup(body),
        HandshakeRequestLength => Handshake(body),
        _ => throw new InvalidDataException(
            $"Unexpected fp-setup body of {body.Length} bytes; expected {SetupRequestLength} or {HandshakeRequestLength}."),
    };

    public byte[] Setup(ReadOnlySpan<byte> request)
    {
        EnsureAlive();
        if (request.Length < SetupRequestLength)
            throw new InvalidDataException($"fp-setup round 1 needs {SetupRequestLength} bytes, got {request.Length}.");

        var response = new byte[SetupResponseLength];
        var result = NativeFairPlay.ss_fairplay_setup(_handle, request.ToArray(), request.Length, response, response.Length);
        if (result != 0)
            throw new InvalidDataException($"FairPlay setup rejected the request (code {result}); the sender may be using an unsupported FairPlay version.");

        IsHandshakeComplete = false;
        _log.Debug($"fp-setup round 1 answered (mode {request[14]})");
        return response;
    }

    public byte[] Handshake(ReadOnlySpan<byte> request)
    {
        EnsureAlive();
        if (request.Length < HandshakeRequestLength)
            throw new InvalidDataException($"fp-setup round 2 needs {HandshakeRequestLength} bytes, got {request.Length}.");

        var response = new byte[HandshakeResponseLength];
        var result = NativeFairPlay.ss_fairplay_handshake(_handle, request.ToArray(), request.Length, response, response.Length);
        if (result != 0)
            throw new InvalidDataException($"FairPlay handshake rejected the request (code {result}).");

        IsHandshakeComplete = true;
        _log.Debug("fp-setup round 2 answered");
        return response;
    }

    /// <summary>Unwraps the 72-byte "ekey" from SETUP into the 16-byte AES key that
    /// protects the mirroring and audio streams.</summary>
    public byte[] DecryptKey(ReadOnlySpan<byte> encryptedKey)
    {
        EnsureAlive();
        if (!IsHandshakeComplete)
            throw new InvalidOperationException("SETUP carried a key before the FairPlay handshake completed.");
        if (encryptedKey.Length < EncryptedKeyLength)
            throw new InvalidDataException($"The encrypted key is {encryptedKey.Length} bytes; FairPlay expects {EncryptedKeyLength}.");

        var key = new byte[AesKeyLength];
        var result = NativeFairPlay.ss_fairplay_decrypt(_handle, encryptedKey.ToArray(), encryptedKey.Length, key, key.Length);
        if (result != 0)
            throw new InvalidDataException($"FairPlay could not unwrap the stream key (code {result}).");

        _log.Debug("stream key unwrapped");
        return key;
    }

    private void EnsureAlive()
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(FairPlaySession));
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) NativeFairPlay.ss_fairplay_destroy(handle);
    }
}
