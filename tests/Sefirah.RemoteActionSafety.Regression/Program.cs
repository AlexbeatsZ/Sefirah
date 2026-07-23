using Sefirah.Data.Contracts;
using Sefirah.Services;

if (RemoteActionPolicy.CanExecute(ProtocolCapabilities.Local))
    throw new InvalidOperationException("A Windows endpoint must never advertise remote-action controller authority.");

if (RemoteActionPolicy.CanExecute(Array.Empty<string>()))
    throw new InvalidOperationException("A peer without controller authority must not execute remote actions.");

if (!RemoteActionPolicy.CanExecute([ProtocolCapabilities.RemoteActionsControllerV1]))
    throw new InvalidOperationException("An explicit controller peer should retain remote-action support.");

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
