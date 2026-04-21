namespace Garc.Core.Models;

/// <summary>
/// Preferencia de apariencia de la UI. La capa de presentación traduce a su
/// equivalente en cada plataforma; el Core sólo almacena el valor.
/// </summary>
public enum AppearanceMode
{
    /// <summary>Seguir la preferencia del sistema.</summary>
    System,
    /// <summary>Forzar tema claro.</summary>
    Light,
    /// <summary>Forzar tema oscuro.</summary>
    Dark,
}
