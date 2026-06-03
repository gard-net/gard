using System.Security.Cryptography;
using Gard.Core.Discovery;
using Gard.Core.Protocol;
using Gard.Core.Transport;
using Gard.Core.Utils;

namespace Gard.Core.Handshake;

/// <summary>Handshake como cliente (inicia <c>hello</c>).</summary>
public static class ClientHandshake
{
    /// <summary>
    /// Ejecuta el handshake sobre un transporte ya abierto.
    /// </summary>
    /// <param name="providePairingCode">Invocado si el host responde
    /// <c>requires_pairing = true</c>. Si es <c>null</c> en ese caso, se
    /// lanza <c>LandspeedException.PairingRequired</c>.</param>
    public static async Task<HandshakeOutcome> PerformAsync(
        IFrameTransport transport,
        DeviceIdentity identity,
        Func<CancellationToken, Task<string>>? providePairingCode = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new ControlStream(transport, cancellationToken);

        // --- hello ---
        var helloId = NextMessageId();
        await transport.SendControlAsync(new HelloMessage(helloId, new HelloBody
        {
            ProtocolVersion = ProtocolVersion.Current,
            AppVersion = identity.AppVersion,
            Platform = identity.Platform,
            DeviceName = identity.Name,
            Caps = identity.Caps,
            Nonce = GenerateNonceBase64(),
        }), cancellationToken).ConfigureAwait(false);

        var (_, ack) = await stream.ExpectAsync<HelloAckBody>(msg =>
            msg is HelloAckMessage h ? (h.Id, h.Body) : null).ConfigureAwait(false);

        if (!ack.ProtocolVersion.IsCompatibleWith(ProtocolVersion.Current))
        {
            throw LandspeedException.IncompatibleProtocolVersion(ack.ProtocolVersion.ToString());
        }

        var negotiated = identity.Caps.Intersection(ack.Caps);

        // --- pairing (si el host lo pide) ---
        if (ack.RequiresPairing)
        {
            if (providePairingCode is null)
            {
                throw LandspeedException.PairingRequired();
            }
            var code = await providePairingCode(cancellationToken).ConfigureAwait(false);
            var pairId = NextMessageId();
            await transport.SendControlAsync(
                new PairRequestMessage(pairId, new PairRequestBody { PairingCode = code }),
                cancellationToken).ConfigureAwait(false);
            var (_, pairAck) = await stream.ExpectAsync<PairAckBody>(msg =>
                msg is PairAckMessage p ? (p.Id, p.Body) : null).ConfigureAwait(false);
            if (!pairAck.Ok)
            {
                throw LandspeedException.PairingInvalid();
            }
        }

        // --- clock_sync (sólo si ambos lo soportan) ---
        long offsetNs = 0;
        ulong rttNs = 0;
        if (negotiated.HasFlag(Capabilities.ClockSync))
        {
            var t0 = MonotonicClock.NowNs();
            var syncId = NextMessageId();
            await transport.SendControlAsync(
                new ClockSyncMessage(syncId, new ClockSyncBody { T0Ns = t0 }),
                cancellationToken).ConfigureAwait(false);
            var (_, syncAck) = await stream.ExpectAsync<ClockSyncAckBody>(msg =>
                msg is ClockSyncAckMessage s ? (s.Id, s.Body) : null).ConfigureAwait(false);
            var t3 = MonotonicClock.NowNs();
            // NTP: offset = ((t1 - t0) + (t2 - t3)) / 2 ; rtt = (t3 - t0) - (t2 - t1)
            unchecked
            {
                var t0i = (long)syncAck.T0Ns;
                var t1i = (long)syncAck.T1Ns;
                var t2i = (long)syncAck.T2Ns;
                var t3i = (long)t3;
                offsetNs = ((t1i - t0i) + (t2i - t3i)) / 2;
                var rttSigned = (t3i - t0i) - (t2i - t1i);
                rttNs = rttSigned > 0 ? (ulong)rttSigned : 0;
            }
        }

        return new HandshakeOutcome
        {
            SessionId = ack.SessionId,
            NegotiatedCaps = negotiated,
            PeerVersion = ack.ProtocolVersion,
            PeerPlatform = ack.Platform ?? PeerPlatform.Linux,
            ClockOffsetNs = offsetNs,
            ClockSyncRttNs = rttNs,
        };
    }

    private static ulong NextMessageId() => (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue);

    private static string GenerateNonceBase64()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}

/// <summary>Handshake como host (acepta <c>hello</c>).</summary>
public static class HostHandshake
{
    public static async Task<HandshakeOutcome> AcceptAsync(
        IFrameTransport transport,
        DeviceIdentity identity,
        string? sessionId = null,
        IPairingValidator? pairing = null,
        CancellationToken cancellationToken = default)
    {
        pairing ??= new AlwaysAcceptPairing();
        sessionId ??= Guid.NewGuid().ToString();
        await using var stream = new ControlStream(transport, cancellationToken);

        // --- hello ---
        var (helloId, hello) = await stream.ExpectAsync<HelloBody>(msg =>
            msg is HelloMessage h ? (h.Id, h.Body) : null).ConfigureAwait(false);

        if (!hello.ProtocolVersion.IsCompatibleWith(ProtocolVersion.Current))
        {
            var err = LandspeedException.IncompatibleProtocolVersion(hello.ProtocolVersion.ToString());
            await TrySendErrorAsync(transport, err, cancellationToken).ConfigureAwait(false);
            throw err;
        }

        var negotiated = identity.Caps.Intersection(hello.Caps);
        var requires = pairing.RequiresPairing();

        await transport.SendControlAsync(new HelloAckMessage(helloId, new HelloAckBody
        {
            ProtocolVersion = ProtocolVersion.Current,
            SessionId = sessionId,
            ServerTimeNs = MonotonicClock.NowNs(),
            Platform = identity.Platform,
            Caps = identity.Caps,
            RequiresPairing = requires,
        }), cancellationToken).ConfigureAwait(false);

        // --- pair_request (si aplica) ---
        if (requires)
        {
            var (pairId, pairReq) = await stream.ExpectAsync<PairRequestBody>(msg =>
                msg is PairRequestMessage p ? (p.Id, p.Body) : null).ConfigureAwait(false);
            var ok = pairing.Validate(pairReq.PairingCode);
            await transport.SendControlAsync(new PairAckMessage(pairId, new PairAckBody
            {
                Ok = ok,
                Reason = ok ? null : "pairing_code inválido",
            }), cancellationToken).ConfigureAwait(false);
            if (!ok) throw LandspeedException.PairingInvalid();
        }

        // --- clock_sync (si aplica) ---
        if (negotiated.HasFlag(Capabilities.ClockSync))
        {
            var (syncId, sync) = await stream.ExpectAsync<ClockSyncBody>(msg =>
                msg is ClockSyncMessage s ? (s.Id, s.Body) : null).ConfigureAwait(false);
            var t1 = MonotonicClock.NowNs();
            var t2 = MonotonicClock.NowNs();
            await transport.SendControlAsync(new ClockSyncAckMessage(syncId, new ClockSyncAckBody
            {
                T0Ns = sync.T0Ns, T1Ns = t1, T2Ns = t2,
            }), cancellationToken).ConfigureAwait(false);
        }

        return new HandshakeOutcome
        {
            SessionId = sessionId,
            NegotiatedCaps = negotiated,
            PeerVersion = hello.ProtocolVersion,
            PeerPlatform = hello.Platform,
            ClockOffsetNs = 0,
            ClockSyncRttNs = 0,
        };
    }

    private static async Task TrySendErrorAsync(IFrameTransport transport, LandspeedException err, CancellationToken ct)
    {
        if (err.WireCode is not int code) return;
        try
        {
            await transport.SendControlAsync(
                new ErrorMessage(0, new ErrorBody { Code = code, Message = err.Message }),
                ct).ConfigureAwait(false);
        }
        catch { /* best effort */ }
    }
}
