using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Gard.Core.Protocol;

namespace Gard.Core.Handshake;

/// <summary>Lo que el host sabe del peer al momento de decidir el pairing.</summary>
/// <param name="Address">IP remota de la conexión de control; <c>null</c> si se desconoce.</param>
public sealed record PairingPeer(IPAddress? Address, string DeviceName, PeerPlatform Platform);

public enum PairingVerdict
{
    Accepted,
    InvalidCode,
    /// <summary>Demasiados intentos fallidos recientes; el código ni se evaluó.</summary>
    RateLimited,
}

/// <summary>Política de pairing del lado host (spec §7).</summary>
public interface IPairingValidator
{
    /// <summary><c>true</c> si este peer debe enviar <c>pair_request</c> antes de seguir.</summary>
    bool RequiresPairing(PairingPeer peer);

    /// <summary>Evalúa un código recibido. Sólo se llama si <see cref="RequiresPairing"/> fue <c>true</c>.</summary>
    PairingVerdict Validate(PairingPeer peer, string code);
}

/// <summary>Modo abierto: no exige pairing. Útil en tests y labs de confianza.</summary>
public sealed class AlwaysAcceptPairing : IPairingValidator
{
    public bool RequiresPairing(PairingPeer peer) => false;
    public PairingVerdict Validate(PairingPeer peer, string code) => PairingVerdict.Accepted;
}

/// <summary>
/// Exige un código numérico de 6 dígitos en cada sesión. No recuerda peers: LSP/1
/// no tiene identidad estable de peer (<c>device_name</c> e IP son falsificables),
/// así que "ya emparejado" no se puede verificar sin TLS/certificados.
///
/// Fuerza bruta: 10⁶ códigos frente a 5 fallos/minuto por origen (spec §7) y un
/// tope global que acota el ataque desde muchas IPs (o muchas IPv6 del mismo /64).
/// La comparación es de tiempo constante.
/// </summary>
public sealed class CodePairingValidator : IPairingValidator
{
    public const int CodeLength = 6;
    public const int MaxFailuresPerSourcePerWindow = 5;
    public const int MaxFailuresGlobalPerWindow = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const int SweepThreshold = 1024;

    private readonly byte[] _code;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failuresBySource = new();
    private readonly Queue<DateTimeOffset> _failuresGlobal = new();

    public CodePairingValidator(string code, TimeProvider? time = null)
    {
        if (!IsValidCode(code))
            throw new ArgumentException($"el código de pairing debe tener {CodeLength} dígitos", nameof(code));
        Code = code;
        _code = Encoding.ASCII.GetBytes(code);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Código vigente, para mostrarlo al usuario del host.</summary>
    public string Code { get; }

    /// <summary>Código aleatorio criptográfico de 6 dígitos (incluye ceros a la izquierda).</summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static bool IsValidCode(string? code) =>
        code is { Length: CodeLength } && code.All(char.IsAsciiDigit);

    public bool RequiresPairing(PairingPeer peer) => true;

    public PairingVerdict Validate(PairingPeer peer, string code)
    {
        var now = _time.GetUtcNow();
        var key = SourceKey(peer.Address);
        lock (_gate)
        {
            if (!_failuresBySource.TryGetValue(key, out var bySource))
            {
                if (_failuresBySource.Count >= SweepThreshold) Sweep(now);
                bySource = new Queue<DateTimeOffset>();
                _failuresBySource[key] = bySource;
            }
            Prune(bySource, now);
            Prune(_failuresGlobal, now);
            if (bySource.Count >= MaxFailuresPerSourcePerWindow || _failuresGlobal.Count >= MaxFailuresGlobalPerWindow)
                return PairingVerdict.RateLimited;

            // FixedTimeEquals devuelve false (sin cortocircuito por contenido) si
            // los largos difieren; el largo del código es público de todos modos.
            var candidate = Encoding.ASCII.GetBytes(code ?? "");
            if (CryptographicOperations.FixedTimeEquals(candidate, _code))
                return PairingVerdict.Accepted;

            bySource.Enqueue(now);
            _failuresGlobal.Enqueue(now);
            return PairingVerdict.InvalidCode;
        }
    }

    /// <summary>
    /// Agrupa por IPv4 o por prefijo /64 en IPv6: rotar la interface-id dentro
    /// del mismo /64 (SLAAC, privacy extensions) no da intentos nuevos.
    /// </summary>
    internal static string SourceKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        return "v6:" + Convert.ToHexString(bytes, 0, 8);
    }

    private static void Prune(Queue<DateTimeOffset> q, DateTimeOffset now)
    {
        while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (k, q) in _failuresBySource.ToArray())
        {
            Prune(q, now);
            if (q.Count == 0) _failuresBySource.Remove(k);
        }
    }
}
