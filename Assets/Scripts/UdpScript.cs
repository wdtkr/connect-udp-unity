using System;
using System.Collections;
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
    public Button startVideoButton;

    public Text peerFqdnText;
    public Text receiveTextLog;

    public InputField messageInputField;

    public GameObject stringTestPanel;
    public GameObject videoTestPanel;

    public RawImage myVideoCapture;
    public RawImage peerVideoCapture;
    
    private WebCamTexture _webCamTexture;
    
    private Color32[] _tmpBuffer;
    private SingleAssignmentDisposable _disposable = new SingleAssignmentDisposable();
    
    private Texture2D _textureTmp;

    // C++のライブラリから呼び出す関数の宣言
    [DllImport("udp-lib")]
    private static extern void setCallback(DebugCallbackDelegate debugCallback, CallbackDelegate receiveCallback,
        DebugCallbackDelegate startCallback);

    [DllImport("udp-lib")]
    private static extern void sendUDPMessage(string fqdn, int port, byte[] message);

    [DllImport("udp-lib")]
    private static extern void preReceiveUDPMessage(int port);

    [DllImport("udp-lib")]
    private static extern void receiveUDPMessage();

    [DllImport("udp-lib")]
    private static extern void socketClose();

    // コールバックのデリゲート型定義
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DebugCallbackDelegate(string message);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CallbackDelegate(byte[] data);

    private Thread _receiveThread;
    private Subject<string> _subject = new Subject<string>();

    [SerializeField] private int port = 8000;
    [SerializeField] private string peerFqdn = null;
    private string _myFqdn;

    private static Subject<string> _staticMessageSubject = new Subject<string>();
    private static object _staticLock = new object();
    private static SynchronizationContext _mainThread;

    private List<string> _messageLog = new List<string>() { };

    private void Start()
    {
        _mainThread = SynchronizationContext.Current;

        receiveTextLog.text = "";
        peerFqdnText.text = peerFqdn;

        sendButton.onClick.AddListener(() => SendData(System.Text.Encoding.UTF8.GetBytes(messageInputField.text)));
        startReceiveButton.onClick.AddListener(StartReceiveLoop);
        endReceiveButton.onClick.AddListener(() =>
        {
            _receiveThread.Abort();
            socketClose();
            Debug.Log("停止ボタンによるスレッド停止。");
        });
        
        startVideoButton.onClick.AddListener(() =>
        {
            stringTestPanel.SetActive(false);
            videoTestPanel.SetActive(true);
            
            StartCoroutine(StartVideoStream());
        });

        _staticMessageSubject.ObserveOnMainThread()
            .Subscribe(msg =>
            {
                _messageLog.Insert(0, $"{DateTime.Now}:{msg}");
                if (_messageLog.Count > 4)
                {
                    _messageLog.RemoveAt(_messageLog.Count - 1);
                }

                receiveTextLog.text = string.Join("\n", _messageLog);
            })
            .AddTo(this);

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

    private void SendData(byte[] data)
    {
        if (peerFqdn == null)
        {
            Debug.LogError("FQDNが設定されていません。");
            return;
        }

        // IPAddressでの接続
        if (IPAddress.TryParse(peerFqdn, out var ipAddress))
        {
            sendUDPMessage(peerFqdn, port, data);
            return;
        }

        // FQDNでの接続
        try
        {
            var ipAddresses = Dns.GetHostAddresses(peerFqdn);
            if (ipAddresses.Length > 0) Debug.LogError("ドメインからIPアドレスが解決できませんでした。");
            // FQDNを指定した時にipAddressesに何が入ってるか確認。ここに実（or仮想）IPが入ってる？FQDNは入ってない？
            sendUDPMessage(peerFqdn, port, data);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    [AOT.MonoPInvokeCallback(typeof(DebugCallbackDelegate))]
    public void DebugCallback(string message)
    {
        Debug.Log(message);
    }

    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public void ReceiveData(byte[] data)
    {
        Debug.Log("ReceiveData Start");
        Debug.Log("Called from C++, ReceiveData : " + data);

        lock (_staticLock)
        {
            _mainThread.Post(_ => _staticMessageSubject.OnNext(System.Text.Encoding.UTF8.GetString(data)), null);
        }
    }

    [AOT.MonoPInvokeCallback(typeof(DebugCallbackDelegate))]
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

    private IEnumerator StartVideoStream()
    {
        var devices = WebCamTexture.devices;
        foreach (var device in devices)
        {
            Debug.Log(device.name);
        }

        if (devices.Length <= 0) yield break;
        _webCamTexture = new WebCamTexture(devices[0].name, 640, 480,30); // 幅と高さを明示的に設定
        myVideoCapture.texture = _webCamTexture;
        _webCamTexture.Play();
        
        // WebCamTexture.widthが100以下の状況の場合初期化が完了していないので、100以上になるまで待機
        while (_webCamTexture.width < 100) {
            yield return null;
        }

        // カメラがスタートしてからバッファのサイズを設定
        _tmpBuffer = new Color32[_webCamTexture.width * _webCamTexture.height];
        
        StartReceiveLoop();
        
        _disposable.Disposable = Observable.EveryUpdate()
            .Subscribe(_ => {
                _webCamTexture.GetPixels32(_tmpBuffer);
                _textureTmp = new Texture2D(_webCamTexture.height, _webCamTexture.width, TextureFormat.ARGB32, false);
                // カメラのピクセルデータを設定
                _textureTmp.SetPixels32(_tmpBuffer);
                // TextureをApply
                _textureTmp.Apply();
                // Encode
                byte[] bin = _textureTmp.EncodeToJPG();
                SendData(bin);
            });
    }

    private void StopVideoStream()
    {
        _disposable.Dispose();
        _webCamTexture.Stop();
    }
}