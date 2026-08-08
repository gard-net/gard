using System.Text;

namespace Gard.Cli;

public sealed record CliTableColumn(string Header, int Width, bool Right = false);

public static class CliTable
{
    public static string Render(CliTerminal term, IReadOnlyList<CliTableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Border(columns));
        sb.Append("| ");
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append("  ");
            sb.Append(term.Cyan(Fit(columns[i].Header, columns[i].Width, columns[i].Right)));
        }
        sb.AppendLine(" |");
        sb.AppendLine(Border(columns, dashed: true));
        foreach (var row in rows)
        {
            sb.Append("| ");
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append("  ");
                var value = i < row.Count ? row[i] : "";
                sb.Append(Fit(value, columns[i].Width, columns[i].Right));
            }
            sb.AppendLine(" |");
        }
        sb.Append(Border(columns));
        return sb.ToString();
    }

    public static string Gauge(double ratio, int width = 24)
    {
        if (!double.IsFinite(ratio)) ratio = 0;
        ratio = Math.Clamp(ratio, 0, 1);
        var fill = (int)Math.Round(ratio * width);
        return new string('█', fill) + new string('░', width - fill);
    }

    public static string Bar(double ratio, int width = 24)
        => "[" + Gauge(ratio, width) + "]";

    private static string Border(IReadOnlyList<CliTableColumn> columns, bool dashed = false)
    {
        var width = columns.Sum(c => c.Width) + Math.Max(0, columns.Count - 1) * 2 + 2;
        return "+" + new string(dashed ? '-' : '=', width) + "+";
    }

    private static string Fit(string text, int width, bool right)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > width) text = width <= 1 ? text[..width] : text[..(width - 1)] + ".";
        return right ? text.PadLeft(width) : text.PadRight(width);
    }
}
