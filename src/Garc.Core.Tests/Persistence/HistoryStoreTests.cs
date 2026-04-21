using Garc.Core.Models;
using Garc.Core.Persistence;
using Garc.Core.Protocol;

namespace Garc.Core.Tests.Persistence;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dir;

    public HistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "landspeed-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TestResult MakeResult(string id, DateTimeOffset startedAt, ulong meanBps = 100_000_000)
    {
        return new TestResult
        {
            Id = id,
            SessionId = "session-" + id,
            PeerName = "peer",
            PeerPlatform = PeerPlatform.Macos,
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(10),
            Direction = TestDirection.Down,
            Streams = 4,
            DurationS = 10,
            MeanBps = meanBps,
            PeakBps = meanBps + 1_000_000,
            PerStreamBps = new ulong[] { meanBps / 4, meanBps / 4, meanBps / 4, meanBps / 4 },
            PingMinMs = 1, PingAvgMs = 2, PingMaxMs = 5, PingP95Ms = 4,
            JitterMs = 0.3, LossPct = 0,
            PingSamples = 20,
        };
    }

    [Fact]
    public async Task LoadAll_OnEmptyDirectory_ReturnsEmpty()
    {
        var store = new HistoryStore(_dir);
        var all = await store.LoadAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task Append_PersistsAcrossInstances()
    {
        var s1 = new HistoryStore(_dir);
        var result = MakeResult("a", DateTimeOffset.UtcNow);
        await s1.AppendAsync(result);

        var s2 = new HistoryStore(_dir);
        var all = await s2.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("a", all[0].Id);
        Assert.Equal(result.MeanBps, all[0].MeanBps);
    }

    [Fact]
    public async Task Append_DedupsById_UpdatingEntry()
    {
        var store = new HistoryStore(_dir);
        var r1 = MakeResult("x", DateTimeOffset.UtcNow, meanBps: 100_000_000);
        var r2 = MakeResult("x", DateTimeOffset.UtcNow, meanBps: 250_000_000);

        await store.AppendAsync(r1);
        await store.AppendAsync(r2);
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal(250_000_000UL, all[0].MeanBps);
    }

    [Fact]
    public async Task LoadAll_IsSortedMostRecentFirst()
    {
        var store = new HistoryStore(_dir);
        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(MakeResult("old", now.AddMinutes(-10)));
        await store.AppendAsync(MakeResult("mid", now.AddMinutes(-5)));
        await store.AppendAsync(MakeResult("new", now));
        var all = await store.LoadAllAsync();
        Assert.Equal(new[] { "new", "mid", "old" }, all.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Delete_RemovesEntryById()
    {
        var store = new HistoryStore(_dir);
        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(MakeResult("keep", now));
        await store.AppendAsync(MakeResult("drop", now.AddSeconds(1)));

        await store.DeleteAsync("drop");
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("keep", all[0].Id);
    }

    [Fact]
    public async Task Delete_NonExistentId_NoOp()
    {
        var store = new HistoryStore(_dir);
        await store.AppendAsync(MakeResult("a", DateTimeOffset.UtcNow));
        await store.DeleteAsync("nonexistent");
        var all = await store.LoadAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task Clear_EmptiesHistory()
    {
        var store = new HistoryStore(_dir);
        await store.AppendAsync(MakeResult("a", DateTimeOffset.UtcNow));
        await store.AppendAsync(MakeResult("b", DateTimeOffset.UtcNow));
        await store.ClearAsync();
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Write_IsAtomic_NoLeftoverTmpFile()
    {
        var store = new HistoryStore(_dir);
        await store.AppendAsync(MakeResult("a", DateTimeOffset.UtcNow));
        var tmp = Path.Combine(_dir, ".history.json.tmp");
        Assert.False(File.Exists(tmp), "archivo temporal quedó colgado tras el rename");
        Assert.True(File.Exists(Path.Combine(_dir, HistoryStore.FileName)));
    }

    [Fact]
    public async Task Json_UsesSnakeCaseSchema()
    {
        var store = new HistoryStore(_dir);
        await store.AppendAsync(MakeResult("a", DateTimeOffset.UtcNow));
        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.Contains("\"schema_version\"", json);
        Assert.Contains("\"session_id\"", json);
        Assert.Contains("\"mean_bps\"", json);
        Assert.Contains("\"per_stream_bps\"", json);
        // Campos LSP/1.1 no poblados no deben aparecer.
        Assert.DoesNotContain("\"rtt_under_load_ms\"", json);
        Assert.DoesNotContain("\"intervals\"", json);
    }

    [Fact]
    public async Task ConcurrentAppends_AllPersist()
    {
        var store = new HistoryStore(_dir);
        var baseTime = DateTimeOffset.UtcNow;
        var tasks = Enumerable.Range(0, 20)
            .Select(i => store.AppendAsync(MakeResult("id-" + i, baseTime.AddSeconds(i))))
            .ToArray();
        await Task.WhenAll(tasks);
        var all = await store.LoadAllAsync();
        Assert.Equal(20, all.Count);
    }
}
