using System.Net.Sockets;

namespace Sefirah.Services.Socket;

public interface ITcpServerProvider
{
    void OnConnected(ServerSession session);
    void OnDisconnected(ServerSession session);
    void OnError(ServerSession session, SocketError error);
    void OnServerError(SocketError error);
    void OnReceived(ServerSession session, byte[] buffer, long offset, long size);
}
