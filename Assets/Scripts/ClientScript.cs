using UnityEngine;
using System.Net.Sockets;
using System.Text;

public class ClientExample : MonoBehaviour
{
    private string host = "10.0.2.188";
    private int port = 9000;
    private UdpClient client;

    void Start() {
        client = new UdpClient();
        client.Connect(host, port);
    }

    void Update() {
        if (Input.GetKeyDown(KeyCode.S)) {
            var message = Encoding.UTF8.GetBytes("接続されました");
            client.Send(message, message.Length);
        }
    }

    private void OnDestroy() {
        client.Close();
    }
}