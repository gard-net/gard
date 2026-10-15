using Gard.Cli;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Cli;

public class TuiStateTests
{
    private static ConsoleKeyInfo Key(char c) => new(c, CharToKey(c), false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k, char c = '\0', bool ctrl = false) => new(c, k, false, false, ctrl);

    private static ConsoleKey CharToKey(char c) => c switch
    {
        >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
        '?' => ConsoleKey.Oem2,
        _ => ConsoleKey.NoName,
    };

    private static TuiState WithPeers(params (string name, double? ms)[] peers)
    {
        var s = new TuiState();
        var i = 1;
        foreach (var (name, ms) in peers)
        {
            var p = s.Upsert(new TuiPeer { Name = name, Host = $"10.0.0.{i++}", Port = 7737 });
            p.ConnectMs = ms;
            p.Probed = true;
        }
        s.Sort();
        return s;
    }

    [Fact]
    public void Peers_SortReachableFirstAndKeepSelectionByKey()
    {
        var s = WithPeers(("zeta", 1.0), ("alpha", null), ("mac", 0.5));
        Assert.Equal(["mac", "zeta", "alpha"], s.Peers.Select(p => p.Name));

        s.Select("10.0.0.1"); // zeta
        s.Peers.Single(p => p.Name == "zeta").ConnectMs = 0.1;
        s.Sort();
        Assert.Equal("zeta", s.Selected!.Name);
    }

    [Fact]
    public void Navigation_MovesAndClamps()
    {
        var s = WithPeers(("a", 1.0), ("b", 2.0), ("c", 3.0));
        Assert.Equal("a", s.Selected!.Name);
        s.HandleKey(Key(ConsoleKey.DownArrow));
        s.HandleKey(Key('j'));
        s.HandleKey(Key(ConsoleKey.DownArrow));
        Assert.Equal("c", s.Selected!.Name);
        s.HandleKey(Key(ConsoleKey.UpArrow));
        Assert.Equal("b", s.Selected!.Name);
    }

    [Fact]
    public void ConfigKeys_CycleAndBuildOptions()
    {
        var s = WithPeers(("mac", 1.0));
        s.HandleKey(Key('d')); // up
        s.HandleKey(Key('u')); // udp
        s.HandleKey(Key('s')); // 4 → 8
        s.HandleKey(Key('t')); // 10 → 30
        Assert.Equal(TestDirection.Up, s.Direction);
        Assert.Equal(TestTransport.Udp, s.Transport);
        Assert.Equal(8, s.Streams);
        Assert.Equal(30, s.DurationS);

        var o = s.BuildOptions(s.Selected!, "123456");
        Assert.Equal("10.0.0.1", o.Host);
        Assert.Equal(TestTransport.Udp, o.Parameters.Transport);
        Assert.Equal(UdpDataPlane.DefaultPayloadSize, o.Parameters.PayloadSize);
        Assert.Equal(50_000_000UL, o.Parameters.TargetBitrateBps);
        Assert.Equal("123456", o.PairCode);
    }

    [Fact]
    public void UdpBidir_BumpsToTwoStreams()
    {
        var s = WithPeers(("mac", 1.0));
        s.Streams = 1;
        s.Transport = TestTransport.Udp;
        s.Direction = TestDirection.Bidir;
        Assert.Equal(2, s.BuildOptions(s.Selected!, null).Parameters.Streams);
    }

    [Fact]
    public void Enter_RunsOnlyWithPeerAndWhenIdle()
    {
        var empty = new TuiState();
        Assert.Equal(TuiAction.None, empty.HandleKey(Key(ConsoleKey.Enter)));
        Assert.True(empty.StatusIsError);

        var s = WithPeers(("mac", 1.0));
        Assert.Equal(TuiAction.RunTest, s.HandleKey(Key(ConsoleKey.Enter)));
        s.Running = new TuiRun { PeerName = "mac", Mode = "down", TotalS = 10 };
        Assert.Equal(TuiAction.None, s.HandleKey(Key(ConsoleKey.Enter)));
        Assert.Equal(TuiAction.CancelTest, s.HandleKey(Key(ConsoleKey.Escape)));
        // La config no cambia a mitad de un test.
        s.HandleKey(Key('d'));
        Assert.Equal(TestDirection.Down, s.Direction);
    }

    [Fact]
    public void AddHost_ViaInputMode()
    {
        var s = new TuiState();
        s.HandleKey(Key('a'));
        Assert.Equal(TuiInputKind.AddHost, s.Input);
        foreach (var c in "192.168.1.9:9000") s.HandleKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        s.HandleKey(Key(ConsoleKey.Backspace));
        s.HandleKey(new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false));
        s.HandleKey(Key(ConsoleKey.Enter));

        Assert.Equal(TuiInputKind.None, s.Input);
        Assert.Equal("192.168.1.9:9001", s.Selected!.Key);
        Assert.True(s.Selected.Manual);
    }

    [Fact]
    public void AddHost_InvalidInput_ShowsError()
    {
        var s = new TuiState();
        s.HandleKey(Key('a'));
        foreach (var c in "host:notaport") s.HandleKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        s.HandleKey(Key(ConsoleKey.Enter));
        Assert.Empty(s.Peers);
        Assert.True(s.StatusIsError);
    }

    [Fact]
    public void InputMode_SwallowsCommandKeys()
    {
        var s = WithPeers(("mac", 1.0));
        s.HandleKey(Key('a'));
        Assert.Equal(TuiAction.None, s.HandleKey(Key('q'))); // 'q' es texto, no salir
        Assert.Equal("q", s.InputText.ToString());
        s.HandleKey(Key(ConsoleKey.Escape));
        Assert.Equal(TuiAction.Quit, s.HandleKey(Key('q')));
    }

    [Fact]
    public void QuestionMark_OpensHelp_EvenWithoutOem2Key()
    {
        // En Linux, .NET entrega '?' con ConsoleKey distinto de Oem2.
        var s = new TuiState();
        s.HandleKey(new ConsoleKeyInfo('?', ConsoleKey.NoName, true, false, false));
        Assert.True(s.ShowHelp);
        s.HandleKey(Key('j'));
        Assert.False(s.ShowHelp);
    }

    [Fact]
    public void FailedRun_HistoryUsesCompactMode()
    {
        var s = WithPeers(("mac", 1.0));
        s.FailRun("mac", "connection refused");
        Assert.Equal("down/tcp/4", s.History[0].Mode);
    }

    [Fact]
    public void CtrlC_AlwaysQuits()
    {
        var s = new TuiState();
        s.HandleKey(Key('a'));
        Assert.Equal(TuiAction.Quit, s.HandleKey(Key(ConsoleKey.C, '\u0003', ctrl: true)));
    }

    [Fact]
    public void Watch_SchedulesNextRunAfterCompletion()
    {
        var s = WithPeers(("mac", 1.0));
        Assert.Equal(TuiAction.RunTest, s.HandleKey(Key('w')));
        s.Running = new TuiRun { PeerName = "mac", Mode = "down", TotalS = 1 };
        s.CompleteRun(Result(), "mac");
        Assert.Null(s.Running);
        Assert.NotNull(s.NextWatchRunAt);
        Assert.Single(s.History);
        Assert.Equal(900_000_000UL, s.History[0].DownBps);

        // Cancelar apaga el watch.
        s.Running = new TuiRun { PeerName = "mac", Mode = "down", TotalS = 1 };
        s.FailRun("mac", "cancelled", cancelled: true);
        Assert.False(s.Watch);
        Assert.Null(s.NextWatchRunAt);
    }

    [Theory]
    [InlineData("10.0.0.2", "10.0.0.2", 7737, true)]
    [InlineData("10.0.0.2:9000", "10.0.0.2", 9000, true)]
    [InlineData("mac.local", "mac.local", 7737, true)]
    [InlineData("[fe80::1]:7000", "fe80::1", 7000, true)]
    [InlineData("fe80::1", "fe80::1", 7737, true)]
    [InlineData("host:99999", "host", 0, false)]
    [InlineData("two words", "", 0, false)]
    public void TryParseHost(string input, string host, int port, bool ok)
    {
        Assert.Equal(ok, TuiState.TryParseHost(input, out var h, out var p));
        if (ok)
        {
            Assert.Equal(host, h);
            Assert.Equal(port, p);
        }
    }

    internal static TestResult Result() => new()
    {
        SessionId = "s", PeerName = "mac", PeerPlatform = PeerPlatform.Macos,
        StartedAt = DateTimeOffset.UnixEpoch, EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(10),
        Direction = TestDirection.Down, Streams = 4, DurationS = 10,
        MeanBps = 900_000_000, PeakBps = 950_000_000, PerStreamBps = [],
        PingMinMs = 0.3, PingAvgMs = 0.6, PingMaxMs = 2, PingP95Ms = 1.1,
        JitterMs = 0.2, LossPct = 0, PingSamples = 100,
    };
}

public class TuiViewTests
{
    private static CliTerminal Term(bool unicode = true, bool color = true) =>
        new(new CliStyleOptions(false, !color, "rich"), forceInteractive: true, forceUnicode: unicode);

    private static TuiState Busy()
    {
        var s = new TuiState();
        var mac = s.Upsert(new TuiPeer { Name = "Mac", Host = "192.168.8.102", Port = 7737, Platform = PeerPlatform.Macos, Caps = Capabilities.DefaultV1 });
        mac.ConnectMs = 2.1;
        mac.Probed = true;
        s.Upsert(new TuiPeer { Name = "iPhone", Host = "192.168.8.146", Port = 7737 }).Probed = true;
        s.LastResult = TuiStateTests.Result();
        s.LastResultPeer = "Mac";
        s.History.Add(new TuiHistoryEntry(DateTime.Now, "Mac", "down/tcp/4", 900_000_000, 0, 1.1, 0, LinkQuality.Excellent, null));
        s.History.Add(new TuiHistoryEntry(DateTime.Now, "iPhone", "down/tcp/4", 0, 0, 0, 0, null, "connection refused"));
        s.SetStatus("done");
        return s;
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    [InlineData(64, 16)]
    [InlineData(200, 60)]
    public void Render_FillsExactlyTheScreen(int width, int height)
    {
        foreach (var unicode in new[] { true, false })
        {
            var lines = TuiView.Render(Busy(), Term(unicode), width, height);
            Assert.Equal(height, lines.Length);
            Assert.All(lines, l => Assert.Equal(width, CliText.VisibleLength(l)));
        }
    }

    [Fact]
    public void Render_ShowsPeersResultAndHistory()
    {
        var text = string.Join("\n", TuiView.Render(Busy(), Term(color: false), 100, 30));

        Assert.Contains("Peers (2)", text);
        Assert.Contains("▸ Mac", text);
        Assert.Contains("2.1 ms", text);
        Assert.Contains("no answer", text);
        Assert.Contains("900.00 Mb/s", text);
        Assert.Contains("excellent", text);
        Assert.Contains("History", text);
        Assert.Contains("connection refused", text);
        Assert.Contains("enter run", text);
    }

    [Fact]
    public void Render_RunningTest_ShowsLiveProgress()
    {
        var s = Busy();
        s.Running = new TuiRun { PeerName = "Mac", Mode = "down/tcp/4", TotalS = 10, Phase = "measuring", ElapsedS = 4, CurrentBps = 812_000_000 };
        s.Running.History.AddRange([100, 300, 800, 812]);
        var text = string.Join("\n", TuiView.Render(s, Term(color: false), 100, 30));

        Assert.Contains("measuring", text);
        Assert.Contains("4.0/10s", text);
        Assert.Contains("812.00 Mb/s", text);
        Assert.Contains("esc cancel", text);
    }

    [Fact]
    public void Render_InputMode_ShowsPromptInFooter()
    {
        var s = Busy();
        s.BeginInput(TuiInputKind.PairingCode, "pairing code for Mac", _ => { });
        s.InputText.Append("482");
        var lines = TuiView.Render(s, Term(color: false), 100, 30);
        Assert.Contains("pairing code for Mac: 482", lines[^1]);
    }

    [Fact]
    public void Render_Help_ReplacesTestPanel()
    {
        var s = Busy();
        s.ShowHelp = true;
        var text = string.Join("\n", TuiView.Render(s, Term(color: false), 100, 30));
        Assert.Contains("Help", text);
        Assert.Contains("udp rate per stream", text);
    }

    [Fact]
    public void Render_TooSmall_ExplainsInsteadOfBreaking()
    {
        var lines = TuiView.Render(Busy(), Term(), 40, 10);
        Assert.Equal(10, lines.Length);
        Assert.Contains(lines, l => l.Contains("terminal too small"));
    }

    [Fact]
    public void Render_Ascii_HasNoUnicode()
    {
        var s = Busy();
        s.Running = new TuiRun { PeerName = "Mac", Mode = "down", TotalS = 10, Phase = "measuring", ElapsedS = 2, CurrentBps = 1 };
        s.Running.History.AddRange([1, 2, 3]);
        var text = string.Join("", TuiView.Render(s, Term(unicode: false, color: false), 100, 30));
        Assert.All(text, c => Assert.True(c < 128, $"no-ASCII U+{(int)c:X4}"));
    }

    [Fact]
    public void Render_EmptyState_GuidesTheUser()
    {
        var text = string.Join("\n", TuiView.Render(new TuiState(), Term(color: false), 90, 24));
        Assert.Contains("searching via mDNS", text);
        Assert.Contains("press 'a' to add a host", text);
    }
}
