using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UniRx;
using UnityEngine.UI;

public class NetworkScript : MonoBehaviour
{
    private string _host;
    private int _port = 9000;
    private UdpClient _client;
    
    private UdpClient _udpClient;
    private Subject<string> _subject = new Subject<string>();
    
    [SerializeField] private Text message;
    [SerializeField] private InputField peerAddress;

    void Start() {
        _udpClient = new UdpClient(9000);
        _udpClient.BeginReceive(OnReceived, _udpClient);

        _subject
            .ObserveOnMainThread()
            .Subscribe(msg => {
                message.text = msg;
            }).AddTo(this);
    }

    private void OnReceived(System.IAsyncResult result) {
        UdpClient getUdp = (UdpClient) result.AsyncState;
        IPEndPoint ipEnd = null;

        byte[] getByte = getUdp.EndReceive(result, ref ipEnd);

        var message = Encoding.UTF8.GetString(getByte);
        _subject.OnNext(message);

        getUdp.BeginReceive(OnReceived, getUdp);
    }

    private void OnDestroy() {
        _udpClient.Close();
    }
}