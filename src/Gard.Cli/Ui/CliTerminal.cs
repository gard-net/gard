using System.Text;

namespace Gard.Cli;

/// <param name="Plain">Salida lineal sin layout rico (<c>--plain</c>).</param>
/// <param name="NoColor">Layout rico pero sin ANSI (<c>--no-color</c>).</param>
/// <param name="Style"><c>rich</c> o <c>plain</c> (<c>--style</c>).</param>
/// <param name="Ascii">Sólo ASCII aunque el terminal soporte Unicode (<c>--ascii</c>).</param>
public sealed record CliStyleOptions(bool Plain, bool NoColor, string Style, bool Ascii = false)
{
    public static CliStyleOptions Default { get; } = new(false, false, "rich");
    public static CliStyleOptions PlainText { get; } = new(true, true, "plain");
}

/// <summary>
/// Capacidades del terminal resueltas una vez: layout rico, color, Unicode,
/// ancho y si se puede redibujar una línea en vivo (stderr interactivo).
///
/// Detección (en orden): flags del usuario, <c>NO_COLOR</c>/<c>FORCE_COLOR</c>,
/// <c>TERM=dumb</c>, <c>CI</c>, redirección de stdout/stderr, locale UTF-8 y, en
/// Windows, si la consola acepta secuencias VT. <c>forceInteractive</c> es el
/// modo determinista de tests: ignora todo el entorno.
/// </summary>
public sealed class CliTerminal
{
    public const int DefaultWidth = 80;

    public CliTerminal(
        CliStyleOptions options,
        bool? forceInteractive = null,
        int? forceWidth = null,
        bool? forceUnicode = null)
    {
        var honorEnvironment = forceInteractive is null;
        var interactive = forceInteractive ?? !Console.IsOutputRedirected;
        var dumb = honorEnvironment && string.Equals(Env("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
        var ci = honorEnvironment && !string.IsNullOrEmpty(Env("CI"));
        var envNoColor = honorEnvironment && !string.IsNullOrEmpty(Env("NO_COLOR"));
        var envForceColor = honorEnvironment && Env("FORCE_COLOR") is { Length: > 0 } fc && fc != "0";

        Plain = options.Plain || options.Style == "plain";
        Rich = !Plain && options.Style == "rich" && interactive && !dumb && !ci;
        var vt = !honorEnvironment || WindowsConsole.EnableVirtualTerminal();
        Color = !options.NoColor && !envNoColor && vt && (Rich || envForceColor);
        Unicode = !options.Ascii && (forceUnicode ?? (!honorEnvironment || DetectUnicode()));
        Width = Math.Clamp(forceWidth ?? (honorEnvironment ? DetectWidth() : DefaultWidth), 40, 200);
        Live = Rich && (forceInteractive ?? !Console.IsErrorRedirected);
        Symbols = Unicode ? CliSymbols.Unicode : CliSymbols.Ascii;
    }

    public bool Plain { get; }
    /// <summary>Layout rico (paneles, tablas alineadas) sobre un TTY.</summary>
    public bool Rich { get; }
    public bool Color { get; }
    public bool Unicode { get; }
    /// <summary>Columnas disponibles, acotado a [40, 200].</summary>
    public int Width { get; }
    /// <summary>Se puede redibujar una línea de progreso en stderr.</summary>
    public bool Live { get; }
    public CliSymbols Symbols { get; }

    public string Paint(string text, string ansi) => Color ? $"{ansi}{text}{CliTheme.Reset}" : text;
    public string Cyan(string text) => Paint(text, CliTheme.Cyan);
    public string Green(string text) => Paint(text, CliTheme.Green);
    public string Yellow(string text) => Paint(text, CliTheme.Yellow);
    public string Red(string text) => Paint(text, CliTheme.Red);
    public string Purple(string text) => Paint(text, CliTheme.Purple);
    public string Dim(string text) => Paint(text, CliTheme.Dim);
    public string Bold(string text) => Paint(text, CliTheme.Bold);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static int DetectWidth()
    {
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > 0) return Console.WindowWidth;
        }
        catch (IOException) { }
        catch (PlatformNotSupportedException) { }
        return int.TryParse(Env("COLUMNS"), out var cols) && cols > 0 ? cols : DefaultWidth;
    }

    /// <summary>
    /// Windows: se pasa la consola a UTF-8 (y se restaura al salir), como hace el
    /// propio CLI de dotnet. Unix: locale UTF-8; sin locale se asume UTF-8 sólo en
    /// macOS. La consola de texto de Linux (<c>TERM=linux</c>) va en ASCII.
    /// </summary>
    private static bool DetectUnicode()
    {
        if (OperatingSystem.IsWindows()) return WindowsConsole.TryUseUtf8Output();
        if (string.Equals(Env("TERM"), "linux", StringComparison.Ordinal)) return false;
        var locale = Env("LC_ALL") is { Length: > 0 } all ? all
            : Env("LC_CTYPE") is { Length: > 0 } ctype ? ctype
            : Env("LANG");
        if (string.IsNullOrEmpty(locale)) return OperatingSystem.IsMacOS();
        return locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
            || locale.Contains("utf8", StringComparison.OrdinalIgnoreCase);
    }
}

public static class CliTheme
{
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Dim = "\u001b[2m";
    public const string Cyan = "\u001b[36m";
    public const string Green = "\u001b[32m";
    public const string Yellow = "\u001b[33m";
    public const string Red = "\u001b[31m";
    public const string Purple = "\u001b[35m";

    /// <summary>Vuelve al inicio de la línea y la borra (para progreso en vivo).</summary>
    public const string ClearLine = "\r\u001b[2K";
}

/// <summary>Juego de glifos con equivalente ASCII para terminales sin Unicode.</summary>
public sealed record CliSymbols(
    string TopLeft, string TopRight, string BottomLeft, string BottomRight,
    string Horizontal, string Vertical, string Rule,
    char BarFull, char BarEmpty, string Spark,
    string Ok, string Warn, string Fail, string Bullet, string Dot,
    string Up, string Down, string Arrow, string Ellipsis,
    string[] Spinner, string Pointer = ">")
{
    public static CliSymbols Unicode { get; } = new(
        "╭", "╮", "╰", "╯", "─", "│", "─",
        '█', '░', "▁▂▃▄▅▆▇█",
        "✓", "!", "✗", "•", "·",
        "↑", "↓", "→", "…",
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"], "▸");

    public static CliSymbols Ascii { get; } = new(
        "+", "+", "+", "+", "-", "|", "-",
        '#', '.', "_.-=*#",
        "ok", "!", "x", "*", "-",
        "^", "v", "->", "...",
        ["|", "/", "-", "\\"]);
}

/// <summary>Ajustes de consola específicos de Windows; no-op en otros SO.</summary>
internal static class WindowsConsole
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    private static bool? _vt;
    private static readonly object Gate = new();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    /// <summary>
    /// Activa secuencias ANSI en conhost (Windows 10+). Si falla (consola legacy),
    /// el CLI apaga el color en vez de imprimir escapes crudos.
    /// </summary>
    public static bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return true;
        lock (Gate)
        {
            if (_vt is { } cached) return cached;
            var ok = true;
            foreach (var std in new[] { StdOutputHandle, StdErrorHandle })
            {
                var h = GetStdHandle(std);
                if (h == 0 || h == -1 || !GetConsoleMode(h, out var mode)) continue; // redirigido
                if ((mode & EnableVirtualTerminalProcessing) == 0
                    && !SetConsoleMode(h, mode | EnableVirtualTerminalProcessing))
                {
                    ok = false;
                }
            }
            _vt = ok;
            return ok;
        }
    }

    public static bool TryUseUtf8Output()
    {
        try
        {
            if (Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage) return true;
            var previous = Console.OutputEncoding;
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Console.OutputEncoding = previous; } catch { }
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
