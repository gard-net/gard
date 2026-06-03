namespace Gard.Cli;

public static class CliHelp
{
    public static string General() =>
        $"""
        {GardCliInfo.VersionString}

        Measure local network quality between devices you own.

        Usage:
          gard <command> [options]
          gard help <command>
          gard --version

        Commands:
          scan    Discover LSP peers on the local network.
          host    Listen for incoming tests and advertise over mDNS.
          test    Measure throughput, latency, jitter and packet loss.

        Examples:
          gard scan --seconds 5
          gard host --name office-mac --port 7737
          gard test --host 192.168.1.50 --direction down --duration 10 --streams 4
          gard test --host 192.168.1.50 --transport udp --direction up --payload 1200 --bitrate 200M
          gard test --host 192.168.1.50 --format csv --label lab-run-01

        Common options:
          -h, --help       Show help.
          -v, --version    Show version.

        Output formats:
          human            Default terminal report.
          json             Single JSON result on stdout.
          csv              Single CSV row on stdout.

        Use 'gard help test' for measurement options.
        """;

    public static string ForCommand(string command) => command switch
    {
        "scan" => Scan(),
        "host" => Host(),
        "test" => Test(),
        _ => General(),
    };

    public static string Scan() =>
        $"""
        {GardCliInfo.VersionString}

        Usage:
          gard scan [--seconds N]

        Discover LSP peers advertised as _landspeed._tcp on the local network.

        Options:
          --seconds N      Scan duration in seconds. Default: 10.
          -h, --help       Show this help.

        Examples:
          gard scan
          gard scan --seconds 5
        """;

    public static string Host() =>
        $"""
        {GardCliInfo.VersionString}

        Usage:
          gard host [--name NAME] [--port N] [--dynamic-port]

        Start a Gard host so other peers can measure against this machine.

        Options:
          --name NAME      Advertised device name. Default: local host name.
          --port N         TCP control port. Default: 7737.
          --dynamic-port   Ask the OS for an available port.
          -h, --help       Show this help.

        Examples:
          gard host
          gard host --name office-mac --port 7737
          gard host --dynamic-port
        """;

    public static string Test() =>
        $"""
        {GardCliInfo.VersionString}

        Usage:
          gard test --host <ip-or-name> [options]
          gard test <ip-or-name> [options]
          gard test --csv-header

        Measure a peer over TCP or UDP using LSP/1.2.

        Options:
          --host HOST      Peer IP address or DNS name.
          --port N         TCP control port. Default: 7737.
          --direction DIR  up, down or bidir. Default: down.
          --streams N      Parallel streams, 1..32. Default: 1.
          --duration S     Measurement duration in seconds. Default: 10.
          --warmup S       Warmup before measuring. Default: 1.
          --payload BYTES  Payload size. TCP min: 512. UDP range: 64..65507.
          --transport T    tcp or udp. Default: tcp.
          --bitrate BPS    UDP target bitrate per stream, e.g. 200M or 1.5G.
          --bidir-mode M   simultaneous or sequential. UDP supports simultaneous only.
          --gap S          Gap between sequential bidirectional phases. Default: 0.5.
          --format F       human, json or csv. Default: human.
          --label ID       Label included in CSV output.
          --csv-header     Print the CSV header and exit.
          -h, --help       Show this help.

        Examples:
          gard test --host 192.168.1.50 --direction down --duration 10 --streams 4
          gard test 192.168.1.50 --transport udp --direction up --payload 1200 --bitrate 200M
          gard test --host 192.168.1.50 --format json
          gard test --host 192.168.1.50 --format csv --label lab-run-01
        """;
}
