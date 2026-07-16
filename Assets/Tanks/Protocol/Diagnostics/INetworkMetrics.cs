namespace Tanks.Net;

// Metric emission from network thread.
// Not thread safe.
public interface INetworkMetrics
{
    public void PacketSent(int length);
    public void PacketReceived(int length);
    public void PingSent(int length);
    public void PingReceived(int length);
    public void PongSent(int length);
    public void PongReceived(int length);
    public void PingPongSent();
    public void PingPongReceived();
    public void InputSent();
    public void InputReceived();
    public void StateHashSent();
    public void StateHashReceived();
    public void AdvantageSent();
    public void AdvantageReceived();
    public void DisconnectSent();
    public void DisconnectReceived();

    public void RxDropped();
    public void RxRentFailed();
    public void RxEnqueueFailed();
    public void RxDequeueFailed();
    public void RxReturnFailed();

    public void TxDropped();
    public void TxRentFailed();
    public void TxEnqueueFailed();
    public void TxDequeueFailed();
    public void TxReturnFailed();

    public void ConnectionStarted();
    public void ConnectionAccepted();
    public void ConnectionEstablished();
    public void ConnectionDisconnected();

    public void TransportSendFailed();
    public void TransportSend();
    public void TransportReceiveFailed();
    public void TransportReceive();
}