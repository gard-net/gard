using System.Text;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

/// <summary>Un peer en la lista de la TUI (descubierto por mDNS o agregado a mano).</summary>
public sealed class TuiPeer
{
    public required string Name { get; set; }
    public required string Host { get; set; }
    public required int Port { get; set; }
    public PeerPlatform Platform { get; set; } = PeerPlatform.Unknown;
    public Capabilities Caps { get; set; }
    /// <summary>Último connect TCP en ms; <c>null</c> = sin respuesta o aún sin sondear.</summary>
    public double? ConnectMs { get; set; }
    public bool Probed { get; set; }
    public bool Manual { get; init; }

    public string Key => $"{Host}:{Port}";
}

/// <summary>Medición en curso.</summary>
public sealed class TuiRun
{
    public required string PeerName { get; init; }
    public required string Mode { get; init; }
    public required double TotalS { get; init; }
    public string Phase { get; set; } = "starting";
    public double ElapsedS { get; set; }
    public ulong CurrentBps { get; set; }
    public List<double> History { get; } = new();
}

public sealed record TuiHistoryEntry(
    DateTime At, string Peer, string Mode, ulong DownBps, ulong UpBps,
    double P95Ms, double LossPct, LinkQuality? Quality, string? Error);

public enum TuiInputKind { None, AddHost, PairingCode }

/// <summary>Lo que la app debe ejecutar tras una tecla (el estado no hace I/O).</summary>
public enum TuiAction { None, Quit, RunTest, CancelTest, Rescan }

/// <summary>
/// Estado completo de la TUI y su reacción a teclas. Puro: no toca consola ni
/// red, así la vista y la navegación se testean sin terminal. Todas las
/// mutaciones deben hacerse bajo <see cref="Gate"/> (lo usan el loop de UI, la
/// medición en curso y el discovery).
/// </summary>
public sealed class TuiState
{
    public static readonly int[] StreamChoices = [1, 2, 4, 8];
    public static readonly double[] DurationChoices = [5, 10, 30];
    public static readonly ulong[] BitrateChoices = [10_000_000, 50_000_000, 100_000_000, 500_000_000, 0];

    public object Gate { get; } = new();

    public List<TuiPeer> Peers { get; } = new();
    public string? SelectedKey { get; private set; }

    public TestDirection Direction { get; set; } = TestDirection.Down;
    public TestTransport Transport { get; set; } = TestTransport.Tcp;
    public int Streams { get; set; } = 4;
    public double DurationS { get; set; } = 10;
    /// <summary>Ritmo UDP por stream; 0 = sin límite (satura el enlace, con cuidado).</summary>
    public ulong BitrateBps { get; set; } = 50_000_000;

    public bool Watch { get; set; }
    public double WatchIntervalS { get; set; } = 5;
    public DateTime? NextWatchRunAt { get; set; }

    public TuiRun? Running { get; set; }
    public TestResult? LastResult { get; set; }
    public string? LastResultPeer { get; set; }
    public List<TuiHistoryEntry> History { get; } = new();

    public string? Status { get; set; }
    public bool StatusIsError { get; set; }
    public bool ShowHelp { get; set; }
    public int SpinnerFrame { get; set; }

    public TuiInputKind Input { get; private set; }
    public string InputPrompt { get; private set; } = "";
    public StringBuilder InputText { get; } = new();
    private Action<string?>? _inputDone;

    public TuiPeer? Selected => Peers.FirstOrDefault(p => p.Key == SelectedKey) ?? Peers.FirstOrDefault();

    public int SelectedIndex => Selected is { } s ? Peers.IndexOf(s) : -1;

    /// <summary>Agrega o actualiza un peer; mantiene la selección por clave al reordenar.</summary>
    public TuiPeer Upsert(TuiPeer peer)
    {
        var existing = Peers.FirstOrDefault(p => p.Key == peer.Key);
        if (existing is null)
        {
            Peers.Add(peer);
            existing = peer;
        }
        else
        {
            if (!string.IsNullOrEmpty(peer.Name) && !existing.Manual) existing.Name = peer.Name;
            if (peer.Platform != PeerPlatform.Unknown) existing.Platform = peer.Platform;
            if (peer.Caps != Capabilities.None) existing.Caps = peer.Caps;
        }
        SelectedKey ??= existing.Key;
        Sort();
        return existing;
    }

    public void Select(string key)
    {
        if (Peers.Any(p => p.Key == key)) SelectedKey = key;
    }

    /// <summary>Vivos primero (por latencia), luego sin sondear, luego sin respuesta; desempate por nombre.</summary>
    public void Sort()
    {
        Peers.Sort((a, b) =>
        {
            static int Rank(TuiPeer p) => p.ConnectMs is not null ? 0 : !p.Probed ? 1 : 2;
            var r = Rank(a).CompareTo(Rank(b));
            return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    public string ModeLabel(string dot = "·")
    {
        var sep = $" {dot} ";
        var mode = $"{Direction.ToString().ToLowerInvariant()}{sep}{Transport.ToString().ToLowerInvariant()}{sep}" +
                   $"{EffectiveStreams()} stream{(EffectiveStreams() == 1 ? "" : "s")}{sep}{DurationS:0}s";
        if (Transport == TestTransport.Udp)
            mode += BitrateBps == 0 ? $"{sep}unlimited" : $"{sep}{CliHumanFormatter.FormatBps(BitrateBps)}/stream";
        return mode;
    }

    /// <summary>Forma compacta para el historial: <c>down/tcp/4</c>.</summary>
    public string ShortModeLabel() =>
        $"{Direction.ToString().ToLowerInvariant()}/{Transport.ToString().ToLowerInvariant()}/{EffectiveStreams()}";

    /// <summary>UDP bidir necesita ≥ 2 streams (§12.6); se ajusta en vez de fallar.</summary>
    public int EffectiveStreams() =>
        Transport == TestTransport.Udp && Direction == TestDirection.Bidir ? Math.Max(2, Streams) : Streams;

    public TestCommandOptions BuildOptions(TuiPeer peer, string? pairCode) => new()
    {
        Host = peer.Host,
        Port = peer.Port,
        Format = "human",
        Label = "",
        PairCode = pairCode,
        Quiet = true,
        Parameters = new TestParameters
        {
            Direction = Direction,
            DurationS = DurationS,
            Streams = EffectiveStreams(),
            PayloadSize = Transport == TestTransport.Udp ? UdpDataPlane.DefaultPayloadSize : 65_536,
            WarmupS = Transport == TestTransport.Udp ? 0 : 1,
            BidirMode = Direction == TestDirection.Bidir ? BidirMode.Simultaneous : null,
            Transport = Transport,
            TargetBitrateBps = Transport == TestTransport.Udp ? BitrateBps : 0,
        },
    };

    public void BeginInput(TuiInputKind kind, string prompt, Action<string?> done)
    {
        Input = kind;
        InputPrompt = prompt;
        InputText.Clear();
        _inputDone = done;
    }

    private void EndInput(string? value)
    {
        var done = _inputDone;
        Input = TuiInputKind.None;
        _inputDone = null;
        InputText.Clear();
        done?.Invoke(value);
    }

    /// <summary>Cierra un prompt abierto como si se hubiera cancelado (p. ej. el test se canceló).</summary>
    public void CancelInput()
    {
        if (Input != TuiInputKind.None) EndInput(null);
    }

    public void SetStatus(string message, bool error = false)
    {
        Status = message;
        StatusIsError = error;
    }

    public void CompleteRun(TestResult result, string peerName)
    {
        Running = null;
        LastResult = result;
        LastResultPeer = peerName;
        var down = result.ThroughputDown?.MeanBps ?? (result.Direction == TestDirection.Down ? result.MeanBps : 0);
        var up = result.ThroughputUp?.MeanBps ?? (result.Direction == TestDirection.Up ? result.MeanBps : 0);
        AddHistory(new TuiHistoryEntry(DateTime.Now, peerName, ShortMode(result), down, up,
            result.PingP95Ms, result.LossPct, CliQuality.Grade(result), null));
        SetStatus($"done · {CliHumanFormatter.FormatBps(result.MeanBps)} mean");
        ScheduleWatch();
    }

    public void FailRun(string peerName, string error, bool cancelled = false)
    {
        var mode = Running?.Mode ?? ShortModeLabel();
        Running = null;
        AddHistory(new TuiHistoryEntry(DateTime.Now, peerName, mode, 0, 0, 0, 0, null, cancelled ? "cancelled" : error));
        SetStatus(cancelled ? "cancelled" : error, error: !cancelled);
        if (cancelled) Watch = false;
        ScheduleWatch();
    }

    private void ScheduleWatch() =>
        NextWatchRunAt = Watch ? DateTime.UtcNow.AddSeconds(WatchIntervalS) : null;

    private void AddHistory(TuiHistoryEntry entry)
    {
        History.Insert(0, entry);
        if (History.Count > 50) History.RemoveAt(History.Count - 1);
    }

    private static string ShortMode(TestResult r)
    {
        var transport = r.Udp is null ? "tcp" : "udp";
        return $"{r.Direction.ToString().ToLowerInvariant()}/{transport}/{r.Streams}";
    }

    /// <summary>Procesa una tecla. Devuelve la acción de I/O que la app debe ejecutar.</summary>
    public TuiAction HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return TuiAction.Quit;

        if (Input != TuiInputKind.None) return HandleInputKey(key);

        // '?' por carácter: en Unix .NET no lo reporta como ConsoleKey.Oem2.
        if (key.KeyChar == '?' && !ShowHelp)
        {
            ShowHelp = true;
            return TuiAction.None;
        }

        if (ShowHelp)
        {
            ShowHelp = false; // cualquier tecla cierra la ayuda
            return key.Key is ConsoleKey.Q ? TuiAction.Quit : TuiAction.None;
        }

        switch (key.Key)
        {
            case ConsoleKey.Q:
                return TuiAction.Quit;
            case ConsoleKey.Escape:
                return Running is not null ? TuiAction.CancelTest : TuiAction.None;
            case ConsoleKey.UpArrow or ConsoleKey.K:
                Move(-1);
                return TuiAction.None;
            case ConsoleKey.DownArrow or ConsoleKey.J:
                Move(+1);
                return TuiAction.None;
            case ConsoleKey.Enter or ConsoleKey.Spacebar:
                if (Running is not null) return TuiAction.None;
                if (Selected is null)
                {
                    SetStatus("no peer selected: wait for discovery or press 'a' to add a host", error: true);
                    return TuiAction.None;
                }
                return TuiAction.RunTest;
            case ConsoleKey.X:
                return Running is not null ? TuiAction.CancelTest : TuiAction.None;
            case ConsoleKey.R:
                return TuiAction.Rescan;
            case ConsoleKey.H:
                ShowHelp = true;
                return TuiAction.None;
            case ConsoleKey.A:
                BeginInput(TuiInputKind.AddHost, "add host (ip[:port])", value =>
                {
                    if (string.IsNullOrWhiteSpace(value)) return;
                    if (TryParseHost(value.Trim(), out var host, out var port))
                    {
                        var peer = Upsert(new TuiPeer { Name = host, Host = host, Port = port, Manual = true });
                        Select(peer.Key);
                        SetStatus($"added {peer.Key}");
                    }
                    else
                    {
                        SetStatus($"invalid host '{value.Trim()}'", error: true);
                    }
                });
                return TuiAction.None;
        }

        if (Running is not null) return TuiAction.None; // la config no cambia a mitad de un test

        switch (key.KeyChar)
        {
            case 'd':
                Direction = Direction switch
                {
                    TestDirection.Down => TestDirection.Up,
                    TestDirection.Up => TestDirection.Bidir,
                    _ => TestDirection.Down,
                };
                break;
            case 'u':
                Transport = Transport == TestTransport.Tcp ? TestTransport.Udp : TestTransport.Tcp;
                break;
            case 's':
                Streams = Next(StreamChoices, Streams);
                break;
            case 't':
                DurationS = Next(DurationChoices, DurationS);
                break;
            case 'b':
                BitrateBps = Next(BitrateChoices, BitrateBps);
                if (Transport == TestTransport.Tcp) SetStatus("bitrate applies to UDP tests (press 'u')");
                break;
            case 'w':
                Watch = !Watch;
                NextWatchRunAt = null;
                SetStatus(Watch ? $"watch on: a test every {WatchIntervalS:0}s after each run" : "watch off");
                return Watch && Selected is not null ? TuiAction.RunTest : TuiAction.None;
        }
        return TuiAction.None;
    }

    private TuiAction HandleInputKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                EndInput(InputText.ToString());
                break;
            case ConsoleKey.Escape:
                EndInput(null);
                break;
            case ConsoleKey.Backspace:
                if (InputText.Length > 0) InputText.Length--;
                break;
            default:
                if (!char.IsControl(key.KeyChar) && InputText.Length < 64) InputText.Append(key.KeyChar);
                break;
        }
        return TuiAction.None;
    }

    private void Move(int delta)
    {
        if (Peers.Count == 0) return;
        var i = Math.Clamp(Math.Max(0, SelectedIndex) + delta, 0, Peers.Count - 1);
        SelectedKey = Peers[i].Key;
    }

    private static T Next<T>(T[] choices, T current) where T : IEquatable<T>
    {
        var i = Array.FindIndex(choices, c => c.Equals(current));
        return choices[(i + 1) % choices.Length];
    }

    /// <summary><c>10.0.0.2</c>, <c>10.0.0.2:9000</c>, <c>host.local</c>, <c>[fe80::1]:7737</c>.</summary>
    public static bool TryParseHost(string text, out string host, out int port)
    {
        host = text;
        port = Gard.Core.Discovery.DiscoveryConstants.DefaultPort;
        if (text.Length == 0 || text.Contains(' ')) return false;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            return rest.Length == 0 || (rest.StartsWith(':') && int.TryParse(rest[1..], out port) && port is > 0 and < 65536);
        }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon) // un solo ':' ⇒ host:port (IPv6 sin corchetes no lleva puerto)
        {
            host = text[..colon];
            return int.TryParse(text[(colon + 1)..], out port) && port is > 0 and < 65536;
        }
        return true;
    }
}
