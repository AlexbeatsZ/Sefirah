using Sefirah.Data.Contracts;
using Sefirah.Services;
using Sefirah.Services.Socket;

if (RemoteActionPolicy.CanExecute(ProtocolCapabilities.Local))
    throw new InvalidOperationException("A Windows endpoint must never advertise remote-action controller authority.");

if (RemoteActionPolicy.CanExecute(Array.Empty<string>()))
    throw new InvalidOperationException("A peer without controller authority must not execute remote actions.");

if (!RemoteActionPolicy.CanExecute([ProtocolCapabilities.RemoteActionsControllerV1]))
    throw new InvalidOperationException("An explicit controller peer should retain remote-action support.");

if (!ConnectionHeartbeatPolicy.ShouldSendTo([ProtocolCapabilities.ConnectionHeartbeatV1]))
    throw new InvalidOperationException("A peer with heartbeat capability must receive proactive heartbeats.");

if (ConnectionHeartbeatPolicy.ShouldSendTo([ProtocolCapabilities.RemoteActionsControllerV1]))
    throw new InvalidOperationException("Remote-action authority must not substitute for heartbeat capability.");

await AssertControlFramesBypassBlockedApplicationQueue();
AssertApplicationQueueIsBounded();
AssertOversizedFramesAreRejected();

if (ConnectionCollisionPolicy.PreferredDirection("a", "b") != ConnectionDirection.Outgoing)
    throw new InvalidOperationException("The lower device ID must prefer its outgoing connection.");

if (ConnectionCollisionPolicy.PreferredDirection("b", "a") != ConnectionDirection.Incoming)
    throw new InvalidOperationException("The higher device ID must prefer its incoming connection.");

if (!ConnectionCollisionPolicy.ShouldAcceptCandidate("a", "b", null, ConnectionDirection.Incoming))
    throw new InvalidOperationException("A sole non-preferred connection must remain usable.");

if (!ConnectionCollisionPolicy.ShouldAcceptCandidate("a", "b", ConnectionDirection.Incoming, ConnectionDirection.Incoming))
    throw new InvalidOperationException("A newly authenticated same-direction connection must replace a stale one.");

if (!ConnectionCollisionPolicy.ShouldAcceptCandidate("a", "b", ConnectionDirection.Incoming, ConnectionDirection.Outgoing))
    throw new InvalidOperationException("A preferred candidate must replace a duplicate non-preferred connection.");

if (ConnectionCollisionPolicy.ShouldAcceptCandidate("a", "b", ConnectionDirection.Outgoing, ConnectionDirection.Incoming))
    throw new InvalidOperationException("A non-preferred duplicate must not replace the preferred connection.");

var authenticationGate = new ConnectionAuthenticationGate();
ConnectionDirection? installedDirection = null;
var startTogether = new Barrier(2);

Parallel.Invoke(
    () => TryInstall(ConnectionDirection.Incoming),
    () => TryInstall(ConnectionDirection.Outgoing));

if (installedDirection != ConnectionDirection.Outgoing)
    throw new InvalidOperationException("Concurrent authentication must converge on the preferred connection direction.");

void TryInstall(ConnectionDirection candidateDirection)
{
    startTogether.SignalAndWait();
    authenticationGate.Execute("peer", () =>
    {
        if (!ConnectionCollisionPolicy.ShouldAcceptCandidate(
                "a",
                "b",
                installedDirection,
                candidateDirection))
            return;

        Thread.Sleep(10);
        installedDirection = candidateDirection;
    });
}

Console.WriteLine("Remote action and connection collision safety regression: PASS");

static async Task AssertControlFramesBypassBlockedApplicationQueue()
{
    var sent = new List<string>();
    var firstSendStarted = new ManualResetEventSlim();
    var releaseFirstSend = new ManualResetEventSlim();
    var sendCount = 0;
    using var sender = new SerializedSocketSender(
        buffer =>
        {
            lock (sent)
                sent.Add(System.Text.Encoding.UTF8.GetString(buffer));
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstSendStarted.Set();
                releaseFirstSend.Wait(TimeSpan.FromSeconds(5));
            }
            return true;
        },
        () => true,
        () => 0,
        () => 0);

    if (!sender.TrySendApplication("app-1"u8.ToArray()))
        throw new InvalidOperationException("The first application frame should be accepted.");
    if (!firstSendStarted.Wait(TimeSpan.FromSeconds(5)))
        throw new InvalidOperationException("The sender worker did not start.");
    if (!sender.TrySendApplication("app-2"u8.ToArray()))
        throw new InvalidOperationException("The second application frame should be queued.");

    var control = Task.Run(() => sender.TrySendControl("heartbeat"u8.ToArray()));
    if (!await control.WaitAsync(TimeSpan.FromSeconds(1)) || !control.Result)
        throw new InvalidOperationException("The control frame should be accepted while an application send is blocked.");

    releaseFirstSend.Set();

    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (true)
    {
        lock (sent)
        {
            if (sent.Count == 3)
                break;
        }
        if (DateTime.UtcNow >= deadline)
            throw new InvalidOperationException("Queued application frame was not delivered.");
        await Task.Delay(10);
    }

    lock (sent)
    {
        if (!sent.SequenceEqual(["app-1", "heartbeat", "app-2"]))
            throw new InvalidOperationException($"Control priority was lost: {string.Join(",", sent)}");
    }
}

static void AssertOversizedFramesAreRejected()
{
    using var sender = new SerializedSocketSender(
        _ => true,
        () => true,
        () => 0,
        () => 0);

    if (sender.TrySendApplication(new byte[SerializedSocketSender.DefaultMaxFrameBytes + 1]))
        throw new InvalidOperationException("An oversized application frame must be rejected.");
    if (sender.TrySendControl(new byte[SerializedSocketSender.DefaultMaxControlFrameBytes + 1]))
        throw new InvalidOperationException("An oversized control frame must be rejected.");
}

static void AssertApplicationQueueIsBounded()
{
    var firstSendStarted = new ManualResetEventSlim();
    var releaseFirstSend = new ManualResetEventSlim();
    using var sender = new SerializedSocketSender(
        _ =>
        {
            firstSendStarted.Set();
            releaseFirstSend.Wait(TimeSpan.FromSeconds(5));
            return true;
        },
        () => true,
        () => 0,
        () => 0,
        maxQueuedFrames: 1,
        maxQueuedBytes: 16);

    if (!sender.TrySendApplication([1]))
        throw new InvalidOperationException("The active frame should be accepted.");
    if (!firstSendStarted.Wait(TimeSpan.FromSeconds(5)))
        throw new InvalidOperationException("The bounded sender worker did not start.");
    if (!sender.TrySendApplication([2]))
        throw new InvalidOperationException("One queued frame should fit the configured bound.");
    if (sender.TrySendApplication([3]))
        throw new InvalidOperationException("A frame beyond the configured queue bound must be rejected.");

    releaseFirstSend.Set();
}
