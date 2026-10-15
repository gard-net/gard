namespace Gard.Cli;

/// <summary>
/// Lo que todo comando necesita: capacidades del terminal, stdout/stderr y la
/// cancelación por Ctrl+C. stdout lleva sólo el resultado; progreso, avisos y
/// errores van a stderr para no ensuciar pipes (<c>--json | jq</c>).
/// </summary>
public sealed class CliContext : IDisposable
{
    private readonly CancellationTokenSource _interrupt = new();
    private int _interruptCount;

    public CliContext(CliTerminal term, TextWriter? stdout = null, TextWriter? stderr = null, bool hookConsole = true)
    {
        Term = term;
        Out = stdout ?? Console.Out;
        Err = stderr ?? Console.Error;
        if (hookConsole)
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
    }

    public CliTerminal Term { get; }
    public TextWriter Out { get; }
    public TextWriter Err { get; }

    /// <summary>Se cancela con el primer Ctrl+C (o SIGTERM); el segundo Ctrl+C mata el proceso.</summary>
    public CancellationToken Interrupted => _interrupt.Token;

    public void Warn(string message) => Err.WriteLine($"{Term.Yellow("warning:")} {message}");

    public void Error(string message) => Err.WriteLine($"{Term.Red("error:")} {message}");

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (Interlocked.Increment(ref _interruptCount) > 1) return; // segundo Ctrl+C: salida inmediata
        e.Cancel = true;
        try { _interrupt.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try { _interrupt.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _interrupt.Dispose();
    }
}
