using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using UniRx;
using UnityEngine;
using UnityEngine.UI;

public class UdpScript : MonoBehaviour
{
    public Button startReceiveButton;
    public Button endReceiveButton;
    public Button sendButton;

    public Text peerFqdnText;
    public Text receiveTextLog;

    public InputField messageInputField;

    // C++のライブラリから呼び出す関数の宣言
    [DllImport("udp-lib")]
    private static extern void setCallback(CallbackDelegate debugCallback, CallbackDelegate receiveCallback,
        CallbackDelegate startCallback);

    [DllImport("udp-lib")]
    private static extern void sendUDPMessage(string fqdn, int port, string message);

    [DllImport("udp-lib")]
    private static extern void preReceiveUDPMessage(int port);

    [DllImport("udp-lib")]
    private static extern void receiveUDPMessage();
    [DllImport("udp-lib")]
    private static extern void socketClose();

    // コールバックのデリゲート型定義
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CallbackDelegate(string message);

    private Thread _receiveThread;
    private Subject<string> _subject = new Subject<string>();

    [SerializeField] private int port = 8000;
    [SerializeField] private string peerFqdn = null;
    private string _myFqdn;
    
    private static Subject<string> _staticMessageSubject = new Subject<string>();
    private static object _staticLock = new object();
    private static SynchronizationContext _mainThread;

    private List<string> _messageLog = new List<string>()
    {
        "aaa",
        "bbb",
    };

    private void Start()
    {
        _mainThread = SynchronizationContext.Current;
        
        receiveTextLog.text = "";
        peerFqdnText.text = peerFqdn;
        
        sendButton.onClick.AddListener(() => SendData(messageInputField.text));
        startReceiveButton.onClick.AddListener(StartReceiveLoop);
        endReceiveButton.onClick.AddListener(() =>
        {
            _receiveThread.Abort();
            socketClose();
            Debug.Log("停止ボタンによるスレッド停止。");
        });

        _staticMessageSubject
            .ObserveOnMainThread()
            .Subscribe(msg =>
            {
                _messageLog.Insert(0, $"{DateTime.Now}:{msg}");
                if (_messageLog.Count > 4)
                {
                    _messageLog.RemoveAt(_messageLog.Count - 1);
                }
                receiveTextLog.text = string.Join("\n", _messageLog);
            }).AddTo(this);

        // SetFqdn();
    }

    private void OnApplicationQuit()
    {
        // スレッド停止
        _receiveThread.Abort();
        socketClose();
        Debug.Log("強制終了によるスレッド停止。");
    }

    private void SetPort(string portInp)
    {
        port = int.Parse(portInp);
    }

    private void SetFqdn(string fqdn)
    {
        peerFqdn = fqdn;
    }

    private void StartReceiveLoop()
    {
        // コールバック関数の設定
        setCallback(DebugCallback, ReceiveData, StartCallback);

        preReceiveUDPMessage(port);
    }

    private void SendData(string message)
    {
        if (peerFqdn == null)
        {
            Debug.LogError("FQDNが設定されていません。");
            return;
        }

        // IPAddressでの接続
        if (IPAddress.TryParse(peerFqdn, out var ipAddress))
        {
            sendUDPMessage(peerFqdn, port, message);
            return;
        }

        // FQDNでの接続
        try
        {
            var ipAddresses = Dns.GetHostAddresses(peerFqdn);
            if (ipAddresses.Length > 0) Debug.LogError("ドメインからIPアドレスが解決できませんでした。");
            // FQDNを指定した時にipAddressesに何が入ってるか確認。ここに実（or仮想）IPが入ってる？FQDNは入ってない？
            sendUDPMessage(peerFqdn, port, message);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public void DebugCallback(string message)
    {
        Debug.Log(message);
        
    }

    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public void ReceiveData(string message)
    {
        Debug.Log("ReceiveData Start");
        Debug.Log("Called from C++, ReceiveData : " + message);
        
        lock (_staticLock)
        {
            _mainThread.Post(_ => _staticMessageSubject.OnNext(message), null);
        }
    }

    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public void StartCallback(string message)
    {
        Debug.Log(message);
        _receiveThread = new Thread(new ThreadStart(ReceiveLoop)) { IsBackground = true };
        _receiveThread.Start();
    }

    private void ReceiveLoop()
    {
        _staticMessageSubject.OnNext("受信を開始しました。");
        while (true)
        {
            receiveUDPMessage();
        }
    }
}