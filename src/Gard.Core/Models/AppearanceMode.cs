using System.Text.Json.Serialization;

namespace Gard.Core.Models;

/// <summary>
/// Preferencia de apariencia de la UI. La capa de presentación traduce a su
/// equivalente en cada plataforma; el Core sólo almacena el valor.
/// </summary>
public enum AppearanceMode
{
    /// <summary>Seguir la preferencia del sistema.</summary>
    [JsonStringEnumMemberName("system")] System,
    /// <summary>Forzar tema claro.</summary>
    [JsonStringEnumMemberName("light")] Light,
    /// <summary>Forzar tema oscuro.</summary>
    [JsonStringEnumMemberName("dark")] Dark,
}
