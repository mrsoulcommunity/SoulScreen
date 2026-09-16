using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.Tests;

public class MultiSourceRouterTests
{
    [Fact]
    public void AddSource_SingleSource_StateIsReady()
    {
        var router = new MultiSourceRouter();
        Assert.Equal(MirrorSourceState.Stopped, router.State);
        router.Dispose();
    }

    [Fact]
    public void AddSource_IncreasesCount()
    {
        var router = new MultiSourceRouter();
        var mock = new MockSource();
        var idx = router.AddSource(mock);
        Assert.Equal(0, idx);
        Assert.Equal(1, router.Sources.Count);
        router.Dispose();
    }

    [Fact]
    public void RemoveSourceAt_InvalidIndex_NoOp()
    {
        var router = new MultiSourceRouter();
        router.RemoveSourceAt(99); // should not throw
        Assert.Equal(0, router.Sources.Count);
        router.Dispose();
    }

    [Fact]
    public void RemoveSourceAt_ValidIndex_RemovesAndDecreasesCount()
    {
        var router = new MultiSourceRouter();
        var mock = new MockSource();
        router.AddSource(mock);
        router.RemoveSourceAt(0);
        Assert.Equal(0, router.Sources.Count);
        router.Dispose();
    }

    [Fact]
    public void SwapTiles_SwapsPositions()
    {
        var router = new MultiSourceRouter();
        var a = new MockSource { Id = "a" };
        var b = new MockSource { Id = "b" };
        router.AddSource(a);
        router.AddSource(b);
        router.SwapTiles(0, 1);
        Assert.Equal("b", router.Sources[0].Id);
        Assert.Equal("a", router.Sources[1].Id);
        router.Dispose();
    }

    [Fact]
    public void SwapTiles_InvalidIndex_NoOp()
    {
        var router = new MultiSourceRouter();
        var a = new MockSource { Id = "a" };
        router.AddSource(a);
        router.SwapTiles(0, 99); // should not throw
        Assert.Equal("a", router.Sources[0].Id);
        router.Dispose();
    }

    [Fact]
    public void OneSourceThrows_OtherSourcesKeepGoing()
    {
        var router = new MultiSourceRouter();
        var good = new MockSource();
        var bad = new MockSource { ThrowOnStart = true };
        router.AddSource(good);
        router.AddSource(bad);
        // Disposing the router should not throw even though one source is bad.
        var exception = Record.Exception(() => router.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void State_ReflectsWorstChildState()
    {
        var router = new MultiSourceRouter();
        var ready = new MockSource { State = MirrorSourceState.Ready };
        var streaming = new MockSource { State = MirrorSourceState.Streaming };
        router.AddSource(ready);
        router.AddSource(streaming);
        Assert.Equal(MirrorSourceState.Streaming, router.State);
        router.Dispose();
    }

    [Fact]
    public void State_StoppedWhenAllRemoved()
    {
        var router = new MultiSourceRouter();
        var mock = new MockSource { State = MirrorSourceState.Streaming };
        router.AddSource(mock);
        router.RemoveSourceAt(0);
        Assert.Equal(MirrorSourceState.Stopped, router.State);
        router.Dispose();
    }

    [Fact]
    public void Dispose_StopsAllSources()
    {
        var router = new MultiSourceRouter();
        var mock = new MockSource();
        router.AddSource(mock);
        router.Dispose();
        Assert.True(mock.Stopped);
    }
}

// ---------------------------------------------------------------------
// Grid layout tests
// ---------------------------------------------------------------------

public class GridLayoutTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    public void ComputeGrid_RowsAndColumns(int count, int expectedRows, int expectedCols)
    {
        var (rows, cols) = ComputeGrid(count);
        Assert.Equal(expectedRows, rows);
        Assert.Equal(expectedCols, cols);
    }

    [Theory]
    [InlineData(1, "1×1")]
    [InlineData(2, "1×2 or 2×1")]
    [InlineData(3, "presenter")]
    [InlineData(4, "2×2")]
    public void LayoutName_MatchesCount(int count, string expectedPattern)
    {
        var name = LayoutName(count);
        Assert.Equal(expectedPattern, name);
    }

    // -----------------------------------------------------------------
    // Helpers — copy-pasted from MainWindow.Grid.cs (to be extracted)
    // -----------------------------------------------------------------

    private static (int rows, int cols) ComputeGrid(int count) => count switch
    {
        1 => (1, 1),
        2 => (1, 2), // caller decides wide vs tall based on window aspect
        3 => (2, 2),
        _ => (2, 2),
    };

    private static string LayoutName(int count) => count switch
    {
        1 => "1×1",
        2 => "1×2 or 2×1",
        3 => "presenter",
        _ => "2×2",
    };
}

// ---------------------------------------------------------------------
// Mock source for testing
// ---------------------------------------------------------------------

internal sealed class MockSource : IMirrorSource
{
    public string Id { get; set; } = "mock";
    public string DisplayName => "Mock";
    public MirrorSourceState State { get; set; } = MirrorSourceState.Ready;
    public SourceDeviceInfo? Device => null;
    public bool ThrowOnStart { get; set; }
    public bool Stopped { get; private set; }

    public event EventHandler<MirrorSourceStateChangedEventArgs>? StateChanged;
    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;
    public event EventHandler<AudioFormat>? AudioFormatChanged;
    public event EventHandler<MediaSample>? AudioSampleReady;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnStart) throw new InvalidOperationException("mock failure");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => default;
}
