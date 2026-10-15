using Gard.Core.Protocol;

namespace Gard.Core.Measurement;

/// <summary>
/// Límites que el host aplica a un <c>test_start</c> recibido. El peer remoto no
/// está autenticado (pairing aún no implementado), así que todo parámetro que
/// dimensiona sockets, hilos, buffers o tiempo debe acotarse antes de usarse.
/// El CLI aplica los mismos límites del lado cliente.
/// </summary>
public static class TestLimits
{
    /// <summary>La spec sugiere 1/2/4/8; gard acepta hasta 32 por compat con su CLI.</summary>
    public const int MaxStreams = 32;
    public const double MaxDurationS = 3600;
    public const double MaxWarmupS = 60;
    public const double MaxGapS = 60;
    public const int MinTcpPayloadSize = 1;
    /// <summary>Muy por debajo de <see cref="FrameCodec.MaxFrameSize"/>: acota la RAM por stream.</summary>
    public const int MaxTcpPayloadSize = 4 * 1024 * 1024;

    /// <summary>
    /// Margen sobre la duración anunciada para esperar <c>test_end</c> del cliente
    /// (handshake UDP, HELLO, scheduling).
    /// </summary>
    public const double TestEndGraceS = 30;

    /// <summary>Timeout (ms) para que el host espere <c>test_end</c> de un test ya validado.</summary>
    public static int TestEndTimeoutMs(TestStartBody parms)
    {
        var totalS = parms.DurationS + parms.WarmupS + TestEndGraceS;
        return (int)Math.Min(int.MaxValue, totalS * 1000);
    }
}

public static class TestStartValidator
{
    /// <summary>
    /// Devuelve <c>null</c> si el host puede ejecutar el test, o la excepción que
    /// describe el rechazo. <paramref name="negotiatedCaps"/> <c>null</c> omite el
    /// chequeo de <c>udp_data_plane</c> (llamadores que no conocen el handshake).
    /// </summary>
    public static LandspeedException? Validate(TestStartBody parms, Capabilities? negotiatedCaps)
    {
        if (parms.Direction is not (TestDirection.Up or TestDirection.Down or TestDirection.Bidir))
            return LandspeedException.TestRejected($"direction inválida ({(int)parms.Direction})");
        if (parms.BidirMode is { } mode && mode is not (BidirMode.Simultaneous or BidirMode.Sequential))
            return LandspeedException.TestRejected($"bidir_mode inválido ({(int)mode})");
        if (parms.Transport is { } tr && tr is not (TestTransport.Tcp or TestTransport.Udp))
            return LandspeedException.TestRejected($"transport inválido ({(int)tr})");

        if (parms.Streams is < 1 or > TestLimits.MaxStreams)
            return LandspeedException.TestRejected($"streams debe estar entre 1 y {TestLimits.MaxStreams} (recibido {parms.Streams})");
        if (!InRange(parms.DurationS, TestLimits.MaxDurationS) || parms.DurationS <= 0)
            return LandspeedException.TestRejected($"duration_s debe estar en (0, {TestLimits.MaxDurationS}]");
        if (!InRange(parms.WarmupS, TestLimits.MaxWarmupS))
            return LandspeedException.TestRejected($"warmup_s debe estar en [0, {TestLimits.MaxWarmupS}]");
        if (parms.GapS is { } gap && !InRange(gap, TestLimits.MaxGapS))
            return LandspeedException.TestRejected($"gap_s debe estar en [0, {TestLimits.MaxGapS}]");

        if (parms.Transport == TestTransport.Udp)
        {
            if (negotiatedCaps is { } caps && !caps.HasFlag(Capabilities.UdpDataPlane))
                return LandspeedException.UdpNotNegotiated();
            if (parms.Direction == TestDirection.Bidir && parms.BidirMode == BidirMode.Sequential)
                return LandspeedException.TestRejected("UDP no soporta bidir secuencial");
            if (parms.PayloadSize is < UdpDataPlane.MinPayloadSize or > UdpDataPlane.MaxPayloadSize)
                return LandspeedException.PayloadOutOfRange(
                    parms.PayloadSize, UdpDataPlane.MinPayloadSize, UdpDataPlane.MaxPayloadSize);
        }
        else if (parms.PayloadSize is < TestLimits.MinTcpPayloadSize or > TestLimits.MaxTcpPayloadSize)
        {
            return LandspeedException.PayloadOutOfRange(
                parms.PayloadSize, TestLimits.MinTcpPayloadSize, TestLimits.MaxTcpPayloadSize);
        }

        return null;
    }

    private static bool InRange(double value, double max) =>
        double.IsFinite(value) && value >= 0 && value <= max;
}
