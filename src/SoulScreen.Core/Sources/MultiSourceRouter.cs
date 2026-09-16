using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SoulScreen.Core.Media;

namespace SoulScreen.Core.Sources;

/// <summary>
/// Aggregates multiple <see cref="IMirrorSource"/> instances (one per mirroring tile) behind a
/// single <see cref="IMirrorSource"/> facade, so the rest of the application — the UI, the
/// recording pipeline, the session summary — continues to work without knowing how many phones
/// are connected.
///
/// <para>
/// Each tile owns its own pairing, FairPlay, H.264 decoder, audio output, and recording
/// pipeline. A failure on one tile does not affect the others: sessions are fully isolated at
/// the <see cref="AirPlaySession"/> level, and <see cref="MultiSourceRouter"/> propagates
/// each session's events independently.
/// </para>
/// </summary>
public sealed class MultiSourceRouter : IMirrorSource
{
    private readonly ObservableCollection<IMirrorSource> _sources = [];
    private readonly object _gate = new();
    private readonly Dictionary<IMirrorSource, CancellationTokenSource> _sourceCts = [];

    public MultiSourceRouter()
    {
        _sources.CollectionChanged += OnCollectionChanged;
    }

    // --------------------------------------------------------------------- IMirrorSource

    public string Id => "multi";

    public string DisplayName => "Multi-device";

    public MirrorSourceState State
    {
        get
        {
            lock (_gate)
            {
                if (_sources.Count == 0) return MirrorSourceState.Stopped;
                var worst = MirrorSourceState.Stopped;
                foreach (var s in _sources)
                    if (s.State > worst) worst = s.State;
                return worst;
            }
        }
    }

    public SourceDeviceInfo? Device
    {
        get
        {
            lock (_gate)
            {
                // Primary device = the first connected one; null if none.
                foreach (var s in _sources)
                    if (s.Device is { } d) return d;
                return null;
            }
        }
    }

    public event EventHandler<MirrorSourceStateChangedEventArgs>? StateChanged;
    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;
    public event EventHandler<AudioFormat>? AudioFormatChanged;
    public event EventHandler<MediaSample>? AudioSampleReady;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? combined;
        lock (_gate)
        {
            combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            foreach (var s in _sources)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(combined.Token);
                _sourceCts[s] = cts;
                _ = StartSourceAsync(s, cts.Token);
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task StartSourceAsync(IMirrorSource source, CancellationToken token)
    {
        try { await source.StartAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception)
        {
            // Faulted sources are tolerated; the UI reflects the degraded state.
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        List<IMirrorSource> snapshot;
        lock (_gate)
        {
            snapshot = [.. _sources];
            foreach (var cts in _sourceCts.Values) cts.Cancel();
            _sourceCts.Clear();
        }

        await Task.WhenAll(snapshot.Select(s => s.StopAsync(cancellationToken)))
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
    }

    // --------------------------------------------------------------------- Source management

    /// <summary>All currently connected sources in tile order (0..N-1).</summary>
    public IReadOnlyList<IMirrorSource> Sources
    {
        get
        {
            lock (_gate) return [.. _sources];
        }
    }

    /// <summary>Raised after a source is added or removed.</summary>
    public event NotifyCollectionChangedEventHandler? SourcesChanged;

    /// <summary>Adds <paramref name="source"/> as the next tile. Returns the tile index.</summary>
    public int AddSource(IMirrorSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            var index = _sources.Count;
            _sources.Add(source);
            WireSource(source);
            return index;
        }
    }

    /// <summary>Removes the source at <paramref name="index"/>.</summary>
    public void RemoveSourceAt(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _sources.Count) return;
            var source = _sources[index];
            UnwireSource(source);
            _sources.RemoveAt(index);
            if (_sourceCts.TryGetValue(source, out var cts)) { cts.Cancel(); _sourceCts.Remove(source); }
            _ = source.DisposeAsync();
        }
    }

    /// <summary>Swaps the positions of two tiles.</summary>
    public void SwapTiles(int indexA, int indexB)
    {
        lock (_gate)
        {
            if (indexA < 0 || indexA >= _sources.Count) return;
            if (indexB < 0 || indexB >= _sources.Count) return;
            (_sources[indexA], _sources[indexB]) = (_sources[indexB], _sources[indexA]);
            RaiseSourcesChanged(NotifyCollectionChangedAction.Reset);
        }
    }

    // --------------------------------------------------------------------- private

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaiseSourcesChanged(e.Action);
    }

    private void RaiseSourcesChanged(NotifyCollectionChangedAction action)
    {
        SourcesChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(action));
    }

    private void WireSource(IMirrorSource source)
    {
        source.StateChanged += OnSourceStateChanged;
        source.VideoFormatChanged += OnSourceVideoFormatChanged;
        source.VideoSampleReady += OnSourceVideoSampleReady;
        source.AudioFormatChanged += OnSourceAudioFormatChanged;
        source.AudioSampleReady += OnSourceAudioSampleReady;
    }

    private void UnwireSource(IMirrorSource source)
    {
        source.StateChanged -= OnSourceStateChanged;
        source.VideoFormatChanged -= OnSourceVideoFormatChanged;
        source.VideoSampleReady -= OnSourceVideoSampleReady;
        source.AudioFormatChanged -= OnSourceAudioFormatChanged;
        source.AudioSampleReady -= OnSourceAudioSampleReady;
    }

    private void OnSourceStateChanged(object? sender, MirrorSourceStateChangedEventArgs e)
    {
        StateChanged?.Invoke(this, e);
    }

    private void OnSourceVideoFormatChanged(object? sender, VideoFormat e)
    {
        VideoFormatChanged?.Invoke(this, e);
    }

    private void OnSourceVideoSampleReady(object? sender, MediaSample e)
    {
        VideoSampleReady?.Invoke(this, e);
    }

    private void OnSourceAudioFormatChanged(object? sender, AudioFormat e)
    {
        AudioFormatChanged?.Invoke(this, e);
    }

    private void OnSourceAudioSampleReady(object? sender, MediaSample e)
    {
        AudioSampleReady?.Invoke(this, e);
    }

    // IAsyncDisposable: stops all sources and releases event handlers.
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var s in _sources) UnwireSource(s);
            _sources.Clear();
        }
    }

    /// <summary>Synchronous convenience wrapper over <see cref="DisposeAsync"/>, for callers
    /// (tests, teardown paths) that are not already in an async context.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
