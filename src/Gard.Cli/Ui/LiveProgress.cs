using System.Globalization;
using Gard.Core.Measurement;

namespace Gard.Cli;

/// <summary>
/// Línea de progreso en stderr mientras corre una medición:
/// <c>⠹ measuring  ████████░░░░  4.2/10s  912.4 Mb/s  ▃▅▆▇▆▇</c>.
///
/// En un TTY con layout rico se redibuja en el lugar (~12 fps) y se borra al
/// terminar, así stdout queda sólo con el resultado. Sin TTY (logs, CI) imprime
/// una línea por fase y nada más: la salida sigue siendo legible en un archivo.
/// </summary>
public sealed class LiveProgress : IAsyncDisposable
{
    private readonly CliTerminal _term;
    private readonly TextWriter _err;
    private readonly double _totalS;
    private readonly object _gate = new();
    private readonly List<double> _history = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _loop;
    private readonly bool _enabled;
    private string _phase = "starting";
    private double _elapsedS;
    private ulong _currentBps;
    private int _frame;
    private bool _drawn;

    /// <param name="totalS">Duración esperada de la fase de medición (para la barra).</param>
    /// <param name="enabled"><c>false</c> ⇒ no escribe nada (JSON/CSV, <c>--quiet</c>).</param>
    public LiveProgress(CliTerminal term, TextWriter err, double totalS, bool enabled = true)
    {
        _term = term;
        _err = err;
        _totalS = totalS;
        _enabled = enabled;
        if (_enabled && term.Live) _loop = Task.Run(LoopAsync);
    }

    /// <summary>Throughput instantáneo recibido (bps), en orden, para el sparkline final.</summary>
    public IReadOnlyList<double> History
    {
        get { lock (_gate) return _history.ToArray(); }
    }

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            if (_phase == phase) return;
            _phase = phase;
        }
        if (_enabled && !_term.Live) _err.WriteLine($"{_term.Symbols.Arrow} {phase}");
    }

    /// <summary>Callback para <c>MeasurementSession.RunClientAsync(onProgress:)</c>.</summary>
    public void OnMeasurement(MeasurementProgress progress)
    {
        switch (progress)
        {
            case PingPhaseDoneProgress:
                SetPhase("measuring");
                break;
            case ThroughputTickProgress tick:
                lock (_gate)
                {
                    _phase = "measuring";
                    _elapsedS = tick.ElapsedS;
                    _currentBps = tick.InstantaneousBps;
                    _history.Add(tick.InstantaneousBps);
                }
                break;
            case FinishedProgress:
                SetPhase("collecting results");
                break;
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Draw();
                await Task.Delay(80, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Draw()
    {
        string line;
        lock (_gate)
        {
            line = Compose();
            _drawn = true;
        }
        Redraw(_term, _err, line);
    }

    /// <summary>
    /// Reescribe la línea actual de <paramref name="err"/>. Con color (⇒ VT
    /// disponible) usa borrar-línea; sin él, retorno de carro y relleno a lo ancho
    /// para no imprimir escapes crudos en consolas legacy.
    /// </summary>
    public static void Redraw(CliTerminal term, TextWriter err, string line)
    {
        var prefix = term.Color ? CliTheme.ClearLine : "\r";
        var pad = term.Color ? "" : new string(' ', Math.Max(0, term.Width - 1 - CliText.VisibleLength(line)));
        err.Write(prefix + line + pad);
        err.Flush();
    }

    public static void Clear(CliTerminal term, TextWriter err)
    {
        err.Write(term.Color ? CliTheme.ClearLine : "\r" + new string(' ', term.Width - 1) + "\r");
        err.Flush();
    }

    internal string Compose()
    {
        var s = _term.Symbols;
        var spinner = _term.Cyan(s.Spinner[_frame++ % s.Spinner.Length]);
        var parts = new List<string> { spinner, _phase };
        if (_phase == "measuring" && _totalS > 0)
        {
            var ratio = Math.Clamp(_elapsedS / _totalS, 0, 1);
            parts.Add(_term.Cyan(CliTable.Gauge(ratio, 16, s)));
            parts.Add(_term.Dim($"{_elapsedS.ToString("F1", CultureInfo.InvariantCulture)}/{_totalS.ToString("0.#", CultureInfo.InvariantCulture)}s"));
            parts.Add(_term.Bold(CliHumanFormatter.FormatBps(_currentBps)));
            var sparkWidth = Math.Max(0, _term.Width - 60);
            if (sparkWidth > 4) parts.Add(_term.Purple(CliTable.Sparkline(_history, Math.Min(sparkWidth, 24), s)));
        }
        var text = string.Join("  ", parts);
        return CliText.VisibleLength(text) < _term.Width
            ? text
            : CliText.TruncateVisible(text, _term.Width - 1, s.Ellipsis);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        if (_drawn) Clear(_term, _err);
        _cts.Dispose();
    }
}
