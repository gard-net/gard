using System.Globalization;
using Gard.Core.Discovery;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

public sealed record ScanOptions(int Seconds);

public sealed record HostOptions(string Name, int Port, bool DynamicPort);

public sealed record InfoOptions(string Host, int Port);

public sealed record WatchOptions(string Host, int Port, double IntervalS, TestCommandOptions Test);

public sealed record TestCommandOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Format { get; init; }
    public required string Label { get; init; }
    public required TestParameters Parameters { get; init; }
}

public sealed class CliOptionException(string message) : ArgumentException(message);

public static class CliOptions
{
    private static readonly string[] GlobalValueOptions = ["--style"];
    private static readonly string[] GlobalFlags = ["--plain", "--no-color"];

    public static CliStyleOptions ParseGlobalStyle(string[] args)
    {
        var style = GetStringOpt(args, "--style", "rich").ToLowerInvariant();
        if (style is not ("rich" or "plain")) throw new CliOptionException("--style must be rich or plain");
        return new CliStyleOptions(HasFlag(args, "--plain"), HasFlag(args, "--no-color"), style);
    }

    public static string[] StripGlobalOptions(string[] args)
    {
        var kept = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (GlobalFlags.Contains(args[i])) continue;
            if (GlobalValueOptions.Contains(args[i]))
            {
                if (i == args.Length - 1 || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new CliOptionException($"{args[i]} requires a value");
                i++;
                continue;
            }
            kept.Add(args[i]);
        }
        return kept.ToArray();
    }

    public static ScanOptions ParseScan(string[] args)
    {
        var seconds = GetIntOpt(args, "--seconds", 10);
        RequireRange(seconds, 1, 86_400, "--seconds");
        return new ScanOptions(seconds);
    }

    public static HostOptions ParseHost(string[] args, string defaultHostName)
    {
        var name = GetStringOpt(args, "--name", defaultHostName);
        var dynamicPort = HasFlag(args, "--dynamic-port");
        var port = dynamicPort ? 0 : GetIntOpt(args, "--port", DiscoveryConstants.DefaultPort);
        if (!dynamicPort) RequirePort(port, "--port");
        return new HostOptions(name, port, dynamicPort);
    }

    public static TestCommandOptions ParseTest(string[] args)
    {
        var host = GetStringOpt(args, "--host", "");
        if (string.IsNullOrWhiteSpace(host) && args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            host = args[0];
        if (string.IsNullOrWhiteSpace(host)) throw new CliOptionException("missing --host <ip-or-name>");

        var port = GetIntOpt(args, "--port", DiscoveryConstants.DefaultPort);
        RequirePort(port, "--port");

        var dirStr = GetStringOpt(args, "--direction", GetStringOpt(args, "--dir", "down")).ToLowerInvariant();
        var streams = GetIntOpt(args, "--streams", 1);
        var duration = GetDoubleOpt(args, "--duration", 10);
        var warmup = GetDoubleOpt(args, "--warmup", 1);
        var payload = GetIntOpt(args, "--payload", 65_536);
        var bidirMStr = GetStringOpt(args, "--bidir-mode", "simultaneous").ToLowerInvariant();
        var gap = GetDoubleOpt(args, "--gap", 0.5);
        var fmtStr = GetStringOpt(args, "--format", "human").ToLowerInvariant();
        var label = GetStringOpt(args, "--label", "");
        var transStr = GetStringOpt(args, "--transport", "tcp").ToLowerInvariant();
        var bitrate = ParseBitrate(GetStringOpt(args, "--bitrate", "0"));

        var direction = dirStr switch
        {
            "up" => TestDirection.Up,
            "down" => TestDirection.Down,
            "bidir" => TestDirection.Bidir,
            _ => throw new CliOptionException($"--direction must be up, down or bidir (received: {dirStr})"),
        };
        var transport = transStr switch
        {
            "tcp" => TestTransport.Tcp,
            "udp" => TestTransport.Udp,
            _ => throw new CliOptionException($"--transport must be tcp or udp (received: {transStr})"),
        };
        var bidirMode = bidirMStr switch
        {
            "simultaneous" => BidirMode.Simultaneous,
            "sequential" => BidirMode.Sequential,
            _ => throw new CliOptionException("--bidir-mode must be sequential or simultaneous"),
        };

        if (fmtStr is not ("human" or "json" or "csv"))
            throw new CliOptionException($"--format must be human, json or csv (received: {fmtStr})");
        RequireRange(streams, 1, 32, "--streams");
        RequireFinitePositive(duration, "--duration");
        RequireFiniteNonNegative(warmup, "--warmup");
        RequireFiniteNonNegative(gap, "--gap");
        if (transport == TestTransport.Udp)
        {
            RequireRange(payload, UdpDataPlane.MinPayloadSize, UdpDataPlane.MaxPayloadSize, "--payload");
            if (direction == TestDirection.Bidir && bidirMode == BidirMode.Sequential)
                throw new CliOptionException("UDP does not support --bidir-mode sequential; use simultaneous");
        }
        else
        {
            RequireRange(payload, 512, int.MaxValue, "--payload");
        }

        return new TestCommandOptions
        {
            Host = host,
            Port = port,
            Format = fmtStr,
            Label = label,
            Parameters = new TestParameters
            {
                Direction = direction,
                DurationS = duration,
                Streams = streams,
                PayloadSize = payload,
                WarmupS = warmup,
                BidirMode = direction == TestDirection.Bidir ? bidirMode : null,
                GapS = direction == TestDirection.Bidir && bidirMode == BidirMode.Sequential ? gap : null,
                Transport = transport,
                TargetBitrateBps = bitrate,
            },
        };
    }

    public static InfoOptions ParseInfo(string[] args)
    {
        var host = GetStringOpt(args, "--host", "");
        if (string.IsNullOrWhiteSpace(host) && args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            host = args[0];
        if (string.IsNullOrWhiteSpace(host)) throw new CliOptionException("missing <host>");
        var port = GetIntOpt(args, "--port", DiscoveryConstants.DefaultPort);
        RequirePort(port, "--port");
        return new InfoOptions(host, port);
    }

    public static WatchOptions ParseWatch(string[] args)
    {
        var interval = GetDoubleOpt(args, "--interval", 5);
        RequireFinitePositive(interval, "--interval");

        var testArgs = args.Contains("--format")
            ? args
            : [.. args, "--format", "human"];
        if (!args.Contains("--duration")) testArgs = [.. testArgs, "--duration", "3"];
        if (!args.Contains("--warmup")) testArgs = [.. testArgs, "--warmup", "0"];

        var test = ParseTest(testArgs);
        return new WatchOptions(test.Host, test.Port, interval, test);
    }

    public static bool HasFlag(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i] == name) return true;
        return false;
    }

    public static bool HasHelp(string[] args) => HasFlag(args, "-h") || HasFlag(args, "--help");

    public static string GetStringOpt(string[] args, string name, string def)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i == args.Length - 1 || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new CliOptionException($"{name} requires a value");
            return args[i + 1];
        }
        return def;
    }

    public static int GetIntOpt(string[] args, string name, int def)
    {
        var raw = GetStringOpt(args, name, def.ToString(CultureInfo.InvariantCulture));
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        throw new CliOptionException($"{name} must be a valid integer (received: {raw})");
    }

    public static double GetDoubleOpt(string[] args, string name, double def)
    {
        var raw = GetStringOpt(args, name, def.ToString(CultureInfo.InvariantCulture));
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        throw new CliOptionException($"{name} must be a valid number (received: {raw})");
    }

    public static ulong ParseBitrate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var original = value;
        var s = value.Trim().ToLowerInvariant();
        s = s.Replace("bps", "").Replace("b/s", "").Trim();
        double mult = 1;
        if (s.EndsWith("k", StringComparison.Ordinal)) { mult = 1_000; s = s[..^1]; }
        else if (s.EndsWith("m", StringComparison.Ordinal)) { mult = 1_000_000; s = s[..^1]; }
        else if (s.EndsWith("g", StringComparison.Ordinal)) { mult = 1_000_000_000; s = s[..^1]; }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
            !double.IsFinite(v) || v < 0)
            throw new CliOptionException($"invalid --bitrate: {original}");

        var bps = v * mult;
        if (bps > ulong.MaxValue)
            throw new CliOptionException($"--bitrate is out of range: {original}");
        return (ulong)Math.Round(bps);
    }

    private static void RequirePort(int value, string name) => RequireRange(value, 1, 65_535, name);

    private static void RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new CliOptionException($"{name} must be between {min} and {max}");
    }

    private static void RequireFinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new CliOptionException($"{name} must be > 0");
    }

    private static void RequireFiniteNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new CliOptionException($"{name} must be >= 0");
    }
}
