using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using UniRx;
using UnityEngine;
using UnityEngine.UI;
public class UdpScript : MonoBehaviour
{
    // UI要素
    public Button startReceiveButton, endReceiveButton, sendButton, startVideoButton;
    public Button receiveTestVideoButton, appStopButton;
    public Text peerFqdnText, receiveTextLog;
    public InputField messageInputField;
    public GameObject stringTestPanel, videoTestPanel;
    public RawImage myVideoCapture, peerVideoCapture;

    // プライベート変数
    private WebCamTexture _webCamTexture;
    private Color32[] _tmpBuffer;
    private SingleAssignmentDisposable _disposable = new SingleAssignmentDisposable();
    private Texture2D _textureTmp;
    private Thread _receiveThread;
    private static Subject<string> _staticMessageSubject = new Subject<string>();
    private static Subject<byte[]> _staticVideoSubject = new Subject<byte[]>();
    private static object _staticLock = new object();
    private static SynchronizationContext _mainThread;
    private List<string> _messageLog = new List<string>() { };
    private Texture2D _receivedTexture;

    /* =============================
     * UDPライブラリから呼び出す関数の宣言
     ============================= */
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
    
    /* =============================
     * Mediaライブラリから呼び出す関数の宣言
     ============================= */
    
    [DllImport("media-lib")]
    private static extern void setCallback(DebugCallbackDelegate debugCallback, CallbackDelegate receiveCallback);
    
    [DllImport("media-lib")]
    private static extern void setLibraryPath(string libraryPath);
    
    [DllImport("media-lib")]
    private static extern int initEncodeVideoData(int videoFormat);
    [DllImport("media-lib")]
    private static extern void encodeVideoData(byte[] inputData,int length);
    [DllImport("media-lib")]
    private static extern void destroyEncoder();
    [DllImport("media-lib")]
    private static extern void initDecodeVideoData();
    [DllImport("media-lib")]
    private static extern void receiveAndDecodeVideoData();
    [DllImport("media-lib")]
    private static extern void destroyDecoder();
    [DllImport("media-lib")]
    private static extern void test();

    // コールバックのデリゲート型定義
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DebugCallbackDelegate(string message);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CallbackDelegate(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex=1)] byte[] data, int size, int type);
    
    [SerializeField] private int port = 8000;
    [SerializeField] private string peerFqdn = null;
    private string _myFqdn;

    
    private int switchFlag = 0;
    private bool videoStreamFlag = false;
    private bool stringStreamFlag = false;
    
    private void Start()
    {
        _receivedTexture = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
        // test
        setCallback(DebugCallback, ReceiveTestVideo);
        setLibraryPath("./Assets/Plugins/CppConnect/libopenh264-2.3.1-mac-arm64.dylib");
        test();
        
        _mainThread = SynchronizationContext.Current;
        
        SetupUI();

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
        
        _staticVideoSubject.ObserveOnMainThread()
            .Subscribe(data =>
            {
                _receivedTexture.LoadRawTextureData(data);
                _receivedTexture.Apply();
                peerVideoCapture.texture = _receivedTexture;
            })
            .AddTo(this);

        // SetFqdn();
    }

    private void SetupUI()
    {
        receiveTextLog.text = "";
        peerFqdnText.text = peerFqdn;
        
        sendButton.onClick.AddListener(() => SendStringData(messageInputField.text));
        startReceiveButton.onClick.AddListener(() =>
        {
            stringStreamFlag = true;
            StartReceiveLoop();
        });
        appStopButton.onClick.AddListener(async () =>
        {
            stringTestPanel.SetActive(true);
            videoTestPanel.SetActive(false);
            await CleanupApplication();
        });
        endReceiveButton.onClick.AddListener(() =>
        {
            if (switchFlag != 1 || !stringStreamFlag) return;
            _receiveThread?.Abort();
            socketClose();
            Debug.Log("停止ボタンによるスレッド停止。");
        });
        
        startVideoButton.onClick.AddListener(() =>
        {
            switchFlag = 2;
            videoStreamFlag = true;
            stringTestPanel.SetActive(false);
            videoTestPanel.SetActive(true);
            StartCoroutine(StartVideoStream());
        });
        
        receiveTestVideoButton.onClick.AddListener(() =>
        {
            switchFlag = 3;
            videoStreamFlag = true;
            stringTestPanel.SetActive(false);
            videoTestPanel.SetActive(true);
            ReceiveTestStartVideoStream();
        });
    }

    private async void OnApplicationQuit()
    {
        Debug.Log("終了ボタンによるスレッド停止。");
        await CleanupApplication();
    }

    // 自作の停止関数
    private async Task CleanupApplication()
    {
        _disposable?.Dispose();
        _receiveThread?.Abort();
        if(switchFlag == 1 || stringStreamFlag) socketClose();
        // ビデオ送信開始してた場合
        if(switchFlag == 2 && videoStreamFlag) StopVideoStream();
        // ビデオ受信開始してた場合
        if(switchFlag == 3 && videoStreamFlag) await Task.Run(destroyDecoder);

        videoStreamFlag = false;
        switchFlag = 0;
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

    private void SendStringData(string data)
    {
        if (peerFqdn == null)
        {
            Debug.LogError("FQDNが設定されていません。");
            return;
        }

        // IPAddressでの接続
        if (IPAddress.TryParse(peerFqdn, out var ipAddress))
        {
            Debug.Log("C# 送信前段階1：" + data);
            
            // 末尾に\0を入れて、文字列の終端を明示的にする必要がある
            byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(data + "\0");
            sendUDPMessage(peerFqdn, port, dataBytes);
            return;
        }

        // FQDNでの接続
        try
        {
            var ipAddresses = Dns.GetHostAddresses(peerFqdn);
            if (ipAddresses.Length > 0) Debug.LogError("ドメインからIPアドレスが解決できませんでした。");
            // FQDNを指定した時にipAddressesに何が入ってるか確認。ここに実（or仮想）IPが入ってる？FQDNは入ってない？
            sendUDPMessage(peerFqdn, port, System.Text.Encoding.UTF8.GetBytes(data));
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
    public void ReceiveData(byte[] data, int size, int type)
    {
        // todo: switch type
        var receivedData = new byte[size];
        Array.Copy(data, receivedData, size);
        
        Debug.Log("C# 受信後段階：" + System.Text.Encoding.UTF8.GetString(receivedData));

        // メインスレッドでUIにアクセス
        lock (_staticLock)
        {
            _mainThread.Post(_ => 
            {
                // ビデオデータの場合の処理
                // _staticVideoSubject.OnNext(receivedData);
                // その他の受信データ処理
                _staticMessageSubject.OnNext(System.Text.Encoding.UTF8.GetString(data));
            }, null);
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
        Debug.Log("StartVideoStreamを開始");
        initEncodeVideoData(1);
        // カメラのセットアップ
        var devices = WebCamTexture.devices;
        if (devices.Length <= 0) yield break;

        _webCamTexture = new WebCamTexture(devices[0].name, 1920, 1080, 30);
        myVideoCapture.texture = _webCamTexture;
        _webCamTexture.Play();

        // 初期化が完了するまで待機
        while (_webCamTexture.width < 100) 
        {
            yield return null;
        }

        // バッファのサイズを設定
        _tmpBuffer = new Color32[_webCamTexture.width * _webCamTexture.height];
        _textureTmp = new Texture2D(_webCamTexture.width, _webCamTexture.height, TextureFormat.RGBA32, false);

        _disposable.Disposable = Observable.Interval(TimeSpan.FromSeconds(1.0 / 30))
            .Subscribe(_ => {
                CallEncodeVideoData();
            });
    }
    
    private void CallEncodeVideoData()
    {
        // WebCamTexture を更新
        _webCamTexture.GetPixels32(_tmpBuffer);
        _textureTmp.SetPixels32(_tmpBuffer);
        _textureTmp.Apply();

        // peerVideoCapture.texture = _textureTmp;
        // バイトデータを取得
        byte[] frameData = _textureTmp.GetRawTextureData();

        // C++ のエンコード関数を呼び出し
        encodeVideoData(frameData,frameData.Length);
    }

    private void StopVideoStream()
    {
        _disposable?.Dispose();
        _webCamTexture.Stop();
        destroyEncoder();
    }

    private void ReceiveTestStartVideoStream()
    {
        initDecodeVideoData();
        _receiveThread = new Thread(new ThreadStart(ReceiveTestVideoDataLoop)) { IsBackground = true };
        _receiveThread.Start();
    }

    private void ReceiveTestVideoDataLoop()
    {
        Debug.Log("C#受信開始");
        while (true)
        {
            receiveAndDecodeVideoData();
        }
    }

    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public void ReceiveTestVideo(byte[] data, int size, int type)
    {
        _staticVideoSubject.OnNext(data);
    }
}
