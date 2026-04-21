namespace Gard.Core.Protocol;

/// <summary>
/// Enum canónico de mensajes de control con su id de correlación. Cada variante
/// es un record sellado para permitir pattern matching exhaustivo.
/// </summary>
public abstract record ControlMessage(ulong Id)
{
    /// <summary>Discriminador <c>t</c> tal y como aparece en el JSON.</summary>
    public abstract string T { get; }
}

public sealed record HelloMessage(ulong Id, HelloBody Body) : ControlMessage(Id)
{ public override string T => "hello"; }

public sealed record HelloAckMessage(ulong Id, HelloAckBody Body) : ControlMessage(Id)
{ public override string T => "hello_ack"; }

public sealed record PairRequestMessage(ulong Id, PairRequestBody Body) : ControlMessage(Id)
{ public override string T => "pair_request"; }

public sealed record PairAckMessage(ulong Id, PairAckBody Body) : ControlMessage(Id)
{ public override string T => "pair_ack"; }

public sealed record ClockSyncMessage(ulong Id, ClockSyncBody Body) : ControlMessage(Id)
{ public override string T => "clock_sync"; }

public sealed record ClockSyncAckMessage(ulong Id, ClockSyncAckBody Body) : ControlMessage(Id)
{ public override string T => "clock_sync_ack"; }

public sealed record PingMessage(ulong Id, PingBody Body) : ControlMessage(Id)
{ public override string T => "ping"; }

public sealed record PongMessage(ulong Id, PongBody Body) : ControlMessage(Id)
{ public override string T => "pong"; }

public sealed record TestStartMessage(ulong Id, TestStartBody Body) : ControlMessage(Id)
{ public override string T => "test_start"; }

public sealed record TestStartAckMessage(ulong Id, TestStartAckBody Body) : ControlMessage(Id)
{ public override string T => "test_start_ack"; }

public sealed record TestTickMessage(ulong Id, TestTickBody Body) : ControlMessage(Id)
{ public override string T => "test_tick"; }

public sealed record TestEndMessage(ulong Id, TestEndBody Body) : ControlMessage(Id)
{ public override string T => "test_end"; }

public sealed record ResultMessage(ulong Id, ResultBody Body) : ControlMessage(Id)
{ public override string T => "result"; }

public sealed record GoodbyeMessage(ulong Id, GoodbyeBody Body) : ControlMessage(Id)
{ public override string T => "goodbye"; }

public sealed record ErrorMessage(ulong Id, ErrorBody Body) : ControlMessage(Id)
{ public override string T => "error"; }
