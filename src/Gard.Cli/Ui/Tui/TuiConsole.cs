using System.Text;
using System.Threading.Channels;

namespace Gard.Cli;

/// <summary>
/// Terminal en modo pantalla completa: alternate screen (al salir el scrollback
/// del usuario queda intacto), cursor oculto, teclas leídas en un hilo propio y
/// redibujo por diferencias (sólo las líneas que cambiaron, sin parpadeo).
/// <see cref="Dispose"/> restaura todo aunque la app termine por excepción.
/// </summary>
internal sealed class TuiConsole : IDisposable
{
    private const string Esc = "\u001b[";
    private readonly TextWriter _out;
    private readonly Channel<ConsoleKeyInfo> _keys = Channel.CreateUnbounded<ConsoleKeyInfo>();
    private string[] _previous = [];
    private int _width;
    private int _height;
    private bool _active;
    private bool _previousTreatCtrlC;

    public TuiConsole(TextWriter output)
    {
        _out = output;
    }

    public ChannelReader<ConsoleKeyInfo> Keys => _keys.Reader;

    public (int Width, int Height) Size()
    {
        try { return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight)); }
        catch { return (CliTerminal.DefaultWidth, 24); }
    }

    public void Enter()
    {
        _active = true;
        try
        {
            _previousTreatCtrlC = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true; // Ctrl+C llega como tecla: salida ordenada
        }
        catch (IOException) { }
        _out.Write($"{Esc}?1049h{Esc}?25l{Esc}2J{Esc}H");
        _out.Flush();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        var reader = new Thread(ReadKeys) { IsBackground = true, Name = "gard-tui-keys" };
        reader.Start();
    }

    private void ReadKeys()
    {
        while (_active)
        {
            try
            {
                var key = Console.ReadKey(intercept: true);
                _keys.Writer.TryWrite(key);
            }
            catch (InvalidOperationException) { break; } // stdin redirigido
            catch (IOException) { break; }
        }
    }

    /// <summary>Dibuja el frame; sólo reescribe las líneas que cambiaron desde el anterior.</summary>
    public void Draw(string[] lines, int width, int height)
    {
        var sb = new StringBuilder();
        if (width != _width || height != _height)
        {
            // Resize: todo el contenido anterior puede quedar desalineado.
            sb.Append($"{Esc}2J");
            _previous = [];
            _width = width;
            _height = height;
        }
        for (var i = 0; i < lines.Length && i < height; i++)
        {
            if (i < _previous.Length && _previous[i] == lines[i]) continue;
            sb.Append($"{Esc}{i + 1};1H").Append(lines[i]).Append(CliTheme.Reset);
        }
        if (sb.Length == 0) return;
        _out.Write(sb.ToString());
        _out.Flush();
        _previous = lines;
    }

    private void OnProcessExit(object? sender, EventArgs e) => Restore();

    private void Restore()
    {
        if (!_active) return;
        _active = false;
        try
        {
            _out.Write($"{CliTheme.Reset}{Esc}?25h{Esc}?1049l");
            _out.Flush();
        }
        catch { }
        try { Console.TreatControlCAsInput = _previousTreatCtrlC; } catch { }
    }

    public void Dispose()
    {
        Restore();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _keys.Writer.TryComplete();
    }
}
