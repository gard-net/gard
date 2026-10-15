using System.Text;

namespace Gard.Cli;

/// <summary>
/// Medición, recorte y padding de texto ignorando secuencias ANSI SGR
/// (<c>ESC [ … m</c>). <c>string.Length</c> cuenta los escapes y rompe columnas.
/// </summary>
public static class CliText
{
    public static int VisibleLength(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b')
            {
                while (i < text.Length && text[i] != 'm') i++;
                continue;
            }
            count++;
        }
        return count;
    }

    /// <summary>Quita las secuencias ANSI SGR.</summary>
    public static string StripAnsi(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b')
            {
                while (i < text.Length && text[i] != 'm') i++;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Recorta a <paramref name="width"/> columnas visibles terminando en <c>…</c>.
    /// Conserva los escapes y agrega un reset si hubo color, para que no se
    /// "derrame" al resto de la salida.
    /// </summary>
    public static string TruncateVisible(string text, int width, string ellipsis = "…")
    {
        var sb = new StringBuilder();
        var count = 0;
        var sawAnsi = false;
        for (var i = 0; i < text.Length && count < width - ellipsis.Length; i++)
        {
            if (text[i] == '\u001b')
            {
                sawAnsi = true;
                while (i < text.Length)
                {
                    sb.Append(text[i]);
                    if (text[i] == 'm') break;
                    i++;
                }
                continue;
            }
            sb.Append(text[i]);
            count++;
        }
        if (sawAnsi) sb.Append(CliTheme.Reset);
        sb.Append(ellipsis);
        return sb.ToString();
    }

    /// <summary>Recorta si hace falta y rellena con espacios hasta <paramref name="width"/> columnas visibles.</summary>
    public static string Fit(string text, int width, bool right = false, string ellipsis = "…")
    {
        if (VisibleLength(text) > width) text = TruncateVisible(text, width, ellipsis);
        var pad = new string(' ', Math.Max(0, width - VisibleLength(text)));
        return right ? pad + text : text + pad;
    }
}
