using System;
using UnityEngine;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine.UI;
using UniRx;

public class ConnectSc: MonoBehaviour
{
    public InputField peerIpInputField;
    public InputField messageInputField;
    
    public Button connectButton;
    public Button sendButton;
    
    public Text connectResponseText;
    public Text receiveText;
    
    private UdpClient _udpClient;
    private Thread _receiveThread;
    private IPEndPoint _peerEp;
    
    private Subject<string> _subject = new Subject<string>();

    private const string MyIpAddress = "10.0.2.188";
    // 10.0.2.67
    private const int Port = 9000;

    private void Start()
    {
        connectButton.onClick.AddListener(ConnectStart);
        sendButton.onClick.AddListener(SendMessage);
        
        _udpClient = new UdpClient(Port);
        _receiveThread = new Thread(new ThreadStart(ReceiveData))
        {
            IsBackground = true
        };
        _receiveThread.Start();

        _subject
            .ObserveOnMainThread()
            .Subscribe(msg =>
            {
                receiveText.text = msg;
            }).AddTo(this);
    }
    
    private void Update()
    {
        if (Input.GetKey(KeyCode.Escape))
        {
            _receiveThread.Abort();
            _udpClient.Close();
            Application.Quit();
        }
    }

    private void ConnectStart()
    {
        var peerIp = peerIpInputField.text;
        if (IPAddress.TryParse(peerIp, out var ipAddress))
        {
            connectResponseText.text = "接続されました。";
            _peerEp = new IPEndPoint(ipAddress, Port);
        }
        else
        {
            Debug.LogError("相手のIPアドレスは、無効なIPアドレスです。");
        }
    }

    private void ReceiveData()
    {
        while (true)
        {
            IPEndPoint ipEnd = null;
            byte[] getByte = _udpClient.Receive(ref ipEnd);
            var receivedMessage = Encoding.UTF8.GetString(getByte);
            _subject.OnNext(receivedMessage);
        }
    }

    private void SendMessage()
    {
        var messageToSend = Encoding.UTF8.GetBytes(messageInputField.text);
        try
        {
            _udpClient.Send(messageToSend, messageToSend.Length, _peerEp);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

    }
}
