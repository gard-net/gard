using System.Text;

namespace Gard.Cli;

/// <param name="Width">Ancho máximo de la columna; la tabla usa el mínimo necesario.</param>
public sealed record CliTableColumn(string Header, int Width, bool Right = false);

/// <summary>
/// Tabla sin bordes al estilo de <c>gh</c>/<c>kubectl</c>: encabezado, una regla
/// y filas alineadas. Cada columna mide lo que necesita su contenido (hasta su
/// <see cref="CliTableColumn.Width"/>) y si no entra en el terminal se recorta la
/// columna izquierda más ancha. Mide ancho visible: las celdas pueden traer color.
/// </summary>
public static class CliTable
{
    private const string Gap = "  ";

    public static string Render(CliTerminal term, IReadOnlyList<CliTableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = MeasureColumns(columns, rows, term.Width);
        var sb = new StringBuilder();

        sb.AppendLine(term.Bold(JoinRow(columns, widths, i => columns[i].Header, term)));
        sb.AppendLine(term.Dim(string.Join(Gap, widths.Select(w => Repeat(term.Symbols.Rule, w)))));
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var line = JoinRow(columns, widths, i => i < row.Count ? row[i] : "", term);
            if (r < rows.Count - 1) sb.AppendLine(line);
            else sb.Append(line);
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>Una fila suelta con los mismos anchos (para tablas que crecen, como watch).</summary>
    public static string RenderRow(CliTerminal term, IReadOnlyList<CliTableColumn> columns, IReadOnlyList<string> row)
        => JoinRow(columns, columns.Select(c => c.Width).ToArray(), i => i < row.Count ? row[i] : "", term);

    /// <summary>Encabezado + regla con los anchos declarados (para <see cref="RenderRow"/>).</summary>
    public static string RenderHeader(CliTerminal term, IReadOnlyList<CliTableColumn> columns)
    {
        var widths = columns.Select(c => c.Width).ToArray();
        return term.Bold(JoinRow(columns, widths, i => columns[i].Header, term)) + Environment.NewLine
            + term.Dim(string.Join(Gap, widths.Select(w => Repeat(term.Symbols.Rule, w))));
    }

    public static string Gauge(double ratio, int width = 24) => Gauge(ratio, width, CliSymbols.Unicode);

    public static string Gauge(double ratio, int width, CliSymbols symbols)
    {
        if (!double.IsFinite(ratio)) ratio = 0;
        ratio = Math.Clamp(ratio, 0, 1);
        var fill = (int)Math.Round(ratio * width);
        return new string(symbols.BarFull, fill) + new string(symbols.BarEmpty, width - fill);
    }

    /// <summary>
    /// Sparkline de <paramref name="width"/> celdas, re-muestreando <paramref name="values"/>
    /// y escalando entre mínimo y máximo (muestra la variación, no el valor absoluto).
    /// </summary>
    public static string Sparkline(IReadOnlyList<double> values, int width, CliSymbols symbols)
    {
        if (values.Count == 0 || width <= 0) return "";
        var levels = symbols.Spark;
        var min = values.Min();
        var range = values.Max() - min;
        var cells = Math.Min(width, values.Count);
        var sb = new StringBuilder(cells);
        for (var c = 0; c < cells; c++)
        {
            // Promedio del tramo que cae en esta celda.
            var from = c * values.Count / cells;
            var to = Math.Max(from + 1, (c + 1) * values.Count / cells);
            var avg = 0.0;
            for (var i = from; i < to; i++) avg += values[i];
            avg /= to - from;
            var level = range <= 0 ? levels.Length / 2 : (int)Math.Round((avg - min) / range * (levels.Length - 1));
            sb.Append(levels[Math.Clamp(level, 0, levels.Length - 1)]);
        }
        return sb.ToString();
    }

    private static int[] MeasureColumns(IReadOnlyList<CliTableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows, int available)
    {
        var widths = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var content = rows.Select(r => i < r.Count ? CliText.VisibleLength(Clean(r[i])) : 0).DefaultIfEmpty(0).Max();
            widths[i] = Math.Min(columns[i].Width, Math.Max(columns[i].Header.Length, content));
        }

        var total = widths.Sum() + Gap.Length * Math.Max(0, columns.Count - 1);
        while (total > available)
        {
            var candidate = -1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].Right || widths[i] <= Math.Max(4, columns[i].Header.Length)) continue;
                if (candidate < 0 || widths[i] > widths[candidate]) candidate = i;
            }
            if (candidate < 0) break;
            widths[candidate]--;
            total--;
        }
        return widths;
    }

    private static string JoinRow(IReadOnlyList<CliTableColumn> columns, int[] widths, Func<int, string> cell, CliTerminal term)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(Gap);
            var text = CliText.Fit(Clean(cell(i)), widths[i], columns[i].Right, term.Symbols.Ellipsis);
            // La última columna alineada a la izquierda no necesita relleno a la derecha.
            sb.Append(i == columns.Count - 1 && !columns[i].Right ? text.TrimEnd() : text);
        }
        return sb.ToString();
    }

    private static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static string Repeat(string s, int count)
    {
        var sb = new StringBuilder(s.Length * count);
        for (var i = 0; i < count; i++) sb.Append(s);
        return sb.ToString();
    }
}
