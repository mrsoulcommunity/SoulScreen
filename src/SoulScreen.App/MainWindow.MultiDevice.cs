using System.Windows;
using System.Windows.Controls;
using SoulScreen.AirPlay;
using SoulScreen.App.Controls;
using SoulScreen.App.Logic;
using SoulScreen.Core.Sources;

namespace SoulScreen.App;

/// <summary>
/// The multi-device grid. When <see cref="AppSettings.EnableMultiDevice"/> is on, every
/// concurrent AirPlay session the receiver holds is offered its own tile: the receiver
/// exposes each session as an <see cref="IMirrorSource"/> via its session-source provider,
/// and this partial keeps a <see cref="TileHost"/> per source inside <see cref="TileGrid"/>.
/// <para>
/// The single-phone path is untouched: with the toggle off, or while only one phone is
/// connected, the main <see cref="VideoHost"/> and its pipeline behave exactly as before.
/// The grid is the receiver's second face, not its replacement.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>The tile for each source, keyed by the source so a changed list reuses the
    /// controls that survive it rather than rebuilding the grid.</summary>
    private readonly Dictionary<IMirrorSource, TileHost> _tiles = [];

    /// <summary>True while the grid is what is being shown instead of the single VideoHost.</summary>
    private bool _gridActive;

    /// <summary>Called from <see cref="StartReceiverAsync"/> once the receiver exists: hands
    /// it the tile budget before <c>StartAsync</c> can accept sessions.</summary>
    private void InitialiseMultiDevice(AirPlayReceiver receiver)
    {
        receiver.MaximumSessions = MultiDeviceWanted ? _settings.MaxMirroredTiles : 1;
    }

    /// <summary>Whether the grid is on. Read when a receiver starts, so changing the toggle
    /// takes effect from the next receiver start rather than mid-session.</summary>
    private bool MultiDeviceWanted => _settings.EnableMultiDevice;

    /// <summary>Reads the receiver's latest session list onto the grid. Called from the
    /// half-second metrics tick, which is also what catches a session that ended without a
    /// state event to subscribe to.</summary>
    private void RefreshMultiDeviceGrid()
    {
        if (_receiver is null || _demo is not null)
        {
            if (_gridActive) CollapseMultiDeviceGrid();
            return;
        }

        var sources = _receiver.SessionSources.Sources;

        // From two concurrent senders up the grid takes over; with zero or one the
        // single-session path owns the window and the grid folds away.
        if (sources.Count >= 2)
        {
            ShowMultiDeviceGrid(sources);
            return;
        }

        if (_gridActive) CollapseMultiDeviceGrid();
    }

    /// <summary>Builds or reuses one tile per source, lays the grid out for the count, and
    /// applies this PC's privacy decision to each tile's phone.</summary>
    private void ShowMultiDeviceGrid(IReadOnlyList<AirPlaySessionSource> sources)
    {
        TileGrid.Visibility = Visibility.Visible;
        VideoHost.Visibility = Visibility.Collapsed;
        _gridActive = true;

        // Retire tiles whose source is gone, reusing the rest in source order.
        var keep = new HashSet<IMirrorSource>(sources);
        foreach (var (source, tile) in _tiles)
        {
            if (keep.Contains(source)) continue;
            _tiles.Remove(source);
            TileGrid.Children.Remove(tile);
            tile.Source = null;
        }

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (!_tiles.TryGetValue(source, out var tile))
            {
                tile = new TileHost(_settings) { Source = source };
                tile.ApprovalDecided += (t, allow) => OnTileApprovalDecided(t, allow);
                tile.ScreenshotSaved += (_, path) =>
                {
                    _sessionTally.AddScreenshot();
                    _log.Info($"tile screenshot saved: {path}");
                };
                tile.RecordingFinished += (_, finished) =>
                {
                    _sessionTally.AddRecording(finished.Bytes);
                    _log.Info($"tile recording finished: {finished.Path} ({finished.Bytes} bytes)");
                };
                tile.TileSwapToFullRequested += (_, _) => ShowToast("Swap to full is coming to the grid later", "");
                _tiles[source] = tile;
                TileGrid.Children.Add(tile);
            }

            tile.TileIndex = i;
            ApplyTileTrust(tile, source.Device);
        }

        ArrangeMultiDeviceGrid(sources.Count);
        UpdateTray();
    }

    /// <summary>Applies the same DeviceTrust decision the single path would to this tile's
    /// phone. The tile never decides for itself; the host's lists are the one truth.</summary>
    private void ApplyTileTrust(TileHost tile, SourceDeviceInfo? device)
    {
        var decision = DecideFor(device);
        tile.SetApproval(decision, device);

        if (decision == ConnectDecision.Block && tile.Source is AirPlaySessionSource source)
        {
            _log.Info($"{device?.Name ?? "the device"} is blocked; its session is dropped");
            _ = source.StopAsync();
        }
    }

    /// <summary>The tile's Allow/Block buttons. Allow remembers the phone; block adds it to
    /// the blocked list and drops the session - the same bookkeeping the single path does.</summary>
    private void OnTileApprovalDecided(object? sender, bool allow)
    {
        if (sender is not TileHost tile || tile.Source?.Device is not { } known) return;

        if (allow)
        {
            DeviceTrust.Remove(_settings.BlockedDevices, known.Name, known.Model);
            DeviceTrust.Add(_settings.AllowedDevices, known.Name, known.Model);
        }
        else
        {
            DeviceTrust.Remove(_settings.AllowedDevices, known.Name, known.Model);
            DeviceTrust.Add(_settings.BlockedDevices, known.Name, known.Model);
        }
        _settings.Save();
        RefreshDeviceRuleLists();

        ShowToast(allow ? $"{known.Name} may mirror" : $"{known.Name} is blocked", "");
        if (!allow && tile.Source is AirPlaySessionSource sessionSource)
            _ = sessionSource.StopAsync();
        ApplyTileTrust(tile, known);
        UpdateTray();
    }

    /// <summary>The grid's row/column shape for a count: 2 is side by side or stacked by the
    /// window's aspect, 3 is one large plus two small, 4 is two by two.</summary>
    private void ArrangeMultiDeviceGrid(int count)
    {
        TileGrid.RowDefinitions.Clear();
        TileGrid.ColumnDefinitions.Clear();

        switch (count)
        {
            case 2:
            {
                var ordered = _tiles.Values.ToList();
                if (ActualWidth >= ActualHeight)
                {
                    TileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    TileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (var i = 0; i < ordered.Count; i++) Grid.SetColumn(ordered[i], i);
                }
                else
                {
                    TileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    TileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    for (var i = 0; i < ordered.Count; i++) Grid.SetRow(ordered[i], i);
                }
                break;
            }
            case 3:
            {
                // Presenter: a large tile left, two stacked right.
                TileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                TileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                TileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                TileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var ordered = _tiles.Values.ToList();
                if (ordered.Count > 0)
                {
                    Grid.SetColumn(ordered[0], 0);
                    Grid.SetRowSpan(ordered[0], 2);
                }
                for (var i = 1; i < ordered.Count && i <= 2; i++)
                {
                    Grid.SetColumn(ordered[i], 1);
                    Grid.SetRow(ordered[i], i - 1);
                }
                break;
            }
            default: // 4, and anything unexpected: two by two
            {
                for (var i = 0; i < 2; i++)
                {
                    TileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    TileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }
                var index = 0;
                foreach (var tile in _tiles.Values)
                {
                    Grid.SetRow(tile, index / 2);
                    Grid.SetColumn(tile, index % 2);
                    index++;
                    if (index >= 4) break;
                }
                break;
            }
        }
    }

    /// <summary>Folds the grid away: tiles are cleared and the single path takes the window
    /// back - either the one surviving phone, or the idle panel.</summary>
    private void CollapseMultiDeviceGrid()
    {
        if (!_gridActive) return;
        _gridActive = false;
        TileGrid.Visibility = Visibility.Collapsed;
        foreach (var (_, tile) in _tiles) tile.Source = null;
        _tiles.Clear();
        TileGrid.Children.Clear();

        if (_receiver is null || _shuttingDown) return;

        // A live single session that was being tiled goes back onto the main picture; the
        // ordinary state machinery owns everything else.
        if (_receiver.State == MirrorSourceState.Streaming && ActiveSource?.Device is not null)
        {
            if (VideoHost.Visibility != Visibility.Visible) ShowVideo();
        }
        else if (IdlePanel.Visibility != Visibility.Visible && ApprovalPanel.Visibility != Visibility.Visible)
        {
            SetIdleState("Waiting for your iPhone", "This PC is advertising itself on your network.", MirrorSourceState.Ready);
        }
    }

    /// <summary>Called from <see cref="StopReceiverAsync"/>: the tiles go with the receiver
    /// that fed them.</summary>
    private void DisposeMultiDevice()
    {
        _gridActive = false;
        foreach (var (_, tile) in _tiles) _ = tile.DisposeAsync();
        _tiles.Clear();
        TileGrid.Children.Clear();
        TileGrid.Visibility = Visibility.Collapsed;
    }
}
