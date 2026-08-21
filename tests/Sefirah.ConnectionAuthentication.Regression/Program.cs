using System.Collections.Concurrent;
using Sefirah.Services;

var buffer = new ConnectionAuthenticationBuffer<string>();
var connectionId = Guid.NewGuid();
var attempt = buffer.Start(connectionId);

Assert(buffer.TryDefer(connectionId, "device-info"), "DeviceInfo was not deferred during authentication.");
Assert(buffer.TryDefer(connectionId, "connection-ack"), "ConnectionAck was not deferred during authentication.");
Assert(
    buffer.Complete(connectionId, attempt).SequenceEqual(["device-info", "connection-ack"]),
    "Deferred frames were not released in arrival order.");
Assert(!buffer.TryDefer(connectionId, "after-auth"), "Frames remained deferred after authentication completed.");

var superseded = buffer.Start(connectionId);
Assert(buffer.TryDefer(connectionId, "stale"), "Initial attempt did not accept a deferred frame.");
var current = buffer.Start(connectionId);
Assert(superseded.CancellationToken.IsCancellationRequested, "Superseded authentication was not cancelled.");
Assert(buffer.Complete(connectionId, superseded).Count == 0, "Superseded authentication released stale frames.");
Assert(buffer.TryDefer(connectionId, "current"), "Replacement authentication did not accept a frame.");
Assert(buffer.Complete(connectionId, current).SequenceEqual(["current"]), "Replacement authentication lost its frame.");

for (var round = 0; round < 25; round++)
{
    var raceId = Guid.NewGuid();
    var raceAttempt = buffer.Start(raceId);
    var directlyRouted = new ConcurrentBag<int>();
    var sender = Task.Run(() => Parallel.For(0, 2_000, message =>
    {
        if (!buffer.TryDefer(raceId, message.ToString()))
            directlyRouted.Add(message);
    }));

    await Task.Yield();
    var released = buffer.Complete(raceId, raceAttempt).Select(int.Parse).ToArray();
    await sender;

    var delivered = released.Concat(directlyRouted).ToArray();
    Assert(delivered.Length == 2_000, $"Race round {round} lost or duplicated a frame.");
    Assert(delivered.Distinct().Count() == 2_000, $"Race round {round} delivered a frame more than once.");
}

var cancelledId = Guid.NewGuid();
var cancelled = buffer.Start(cancelledId);
Assert(buffer.TryDefer(cancelledId, "discard-me"), "Cancelled attempt did not accept a deferred frame.");
buffer.Cancel(cancelledId);
Assert(cancelled.CancellationToken.IsCancellationRequested, "Cancellation token was not signalled.");
Assert(buffer.Complete(cancelledId, cancelled).Count == 0, "Cancelled authentication released a frame.");

Console.WriteLine("Connection authentication regression: PASS");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
