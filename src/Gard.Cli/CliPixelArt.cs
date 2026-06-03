using System.Text;

namespace Gard.Cli;

public static class CliPixelArt
{
    public static string Header(CliTerminal term)
    {
        if (!term.Rich) return GardCliInfo.VersionString;
        var sb = new StringBuilder();
        sb.AppendLine($"{term.Cyan("gard")} {term.Dim("v" + GardCliInfo.VersionNumber)}");
        sb.AppendLine(term.Green("  #####    ###    ####   ####    ") + term.Purple("        /\\"));
        sb.AppendLine(term.Green(" ##       ## ##   ##  ## ##  ##  ") + term.Purple("   ____/  \\___"));
        sb.AppendLine(term.Cyan(" ##  ###  #####   ####   ##  ##  ") + term.Purple("  /_  rocket _/"));
        sb.AppendLine(term.Cyan(" ##   ##  ## ##   ## ##  ##  ##  ") + term.Purple("    /_/\\_\\"));
        sb.AppendLine(term.Cyan("  #####   ## ##   ##  ## ####    ") + term.Purple("   ///  \\\\\\"));
        sb.AppendLine(term.Dim("LSP/1.2 Network Speed Client"));
        return sb.ToString().TrimEnd();
    }

    public static string ScanGlyph(CliTerminal term)
    {
        if (!term.Rich) return "";
        return term.Purple("   (((  )))\n") +
               term.Purple("  ((  ||  ))\n") +
               term.Green("      ||\n") +
               term.Green("     /__\\");
    }

    public static string TrendGlyph(CliTerminal term)
    {
        if (!term.Rich) return "";
        return term.Green("[[") + term.Cyan("====") + term.Purple(">>>>");
    }
}
