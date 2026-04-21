namespace Gard.Core.Handshake;

/// <summary>Interfaz para validar pairing codes del lado host.</summary>
public interface IPairingValidator
{
    /// <summary>
    /// Devuelve <c>true</c> si el código es obligatorio antes de aceptar
    /// <c>test_start</c>. Devuelve <c>false</c> si el peer ya está emparejado
    /// o si el host opera en modo abierto (tests/dev).
    /// </summary>
    bool RequiresPairing();

    /// <summary>
    /// Valida un código recibido. Sólo se llama si <see cref="RequiresPairing"/>
    /// devolvió <c>true</c>.
    /// </summary>
    bool Validate(string code);
}

/// <summary>Validador trivial que siempre acepta. Útil en tests y en primera sesión.</summary>
public sealed class AlwaysAcceptPairing : IPairingValidator
{
    public bool RequiresPairing() => false;
    public bool Validate(string code) => true;
}
