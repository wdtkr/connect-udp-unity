using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UniRx;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UI;
public class UdpScript : MonoBehaviour
{
    // UI要素
    public Button startReceiveButton, endReceiveButton, sendButton, startVideoButton,streamingTestButton;
    public Button receiveTestVideoButton, appStopButton;
    public Text peerFqdnText, receiveTextLog;
    public InputField messageInputField;
    public GameObject stringTestPanel, videoTestPanel, peerVideoObject;
    
    // API化した時に、使用者側が表示するために設定するUI
    public RawImage myVideoCapture, peerVideoCapture;
    public AudioSource receiveAudioSource;

    // プライベート変数
    private WebCamTexture _webCamTexture;
    private Color32[] _tmpBuffer;
    private SingleAssignmentDisposable _sendVideoDisposable = new SingleAssignmentDisposable();
    private SingleAssignmentDisposable _sendAudioDisposable = new SingleAssignmentDisposable();
    private Texture2D _textureTmp;
    private Thread _receiveDataThread, _tcpReceiveThread, _receiveVideoThread, _receiveAudioThread;
    private IEnumerator _tcpCoroutine;
    private static Subject<string> _staticMessageSubject = new Subject<string>();
    private static Subject<byte[]> _staticVideoSubject = new Subject<byte[]>();
    private static Subject<byte[]> _staticAudioSubject = new Subject<byte[]>();
    private static object _staticLock = new object();
    private static SynchronizationContext _mainThread;
    private List<string> _messageLog = new List<string>() { };
    private Texture2D _receivedTexture;
    private string _myFqdn,_micName;
    private int _micSampleRate = 48000; // サンプルレート
    private int _sampleSize = 960; // Opus FRAME_SIZEに合わせる
    private AudioClip _microphoneClip;
    private AudioClip _receiveClip;
    private int _microphonePosition;
    private float[] _samples = new float[960];
    private byte[] _audioData;
    private short[] _receiveShortAudioData;
    private float[] _receiveFloatAudioData;

        
    [SerializeField] private int port = 8000;
    [SerializeField] private string peerFqdn = null;

    [SerializeField] private string peerAddress = "127.0.0.1";
    [SerializeField] private int myTcpPort = 30000;
    [SerializeField] private int peerTcpPort = 30001;
    [SerializeField] private int myVideoPort = 30002;
    [SerializeField] private int peerVideoPort = 30006;
    [SerializeField] private int myAudioPort = 30004;
    [SerializeField] private int peerAudioPort = 30008;
    [SerializeField] private int cameraNumber = 0;
    [SerializeField] private int micNumber = 0;
    
    private int switchFlag = 0;
    private bool videoStreamFlag = false;
    private bool stringStreamFlag = false;
    private bool endTcpConnect = false;

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
    private static extern void setMediaCallback(DebugCallbackDelegate debugCallback, CallbackDelegate receiveVideoCallback,CallbackDelegate receiveAudioCallback);
    
    [DllImport("media-lib")]
    private static extern void setLibraryPath(string libraryPath);
    [DllImport("media-lib")]
    private static extern bool initializeTcpSender();
    [DllImport("media-lib")]
    private static extern bool initializeTcpReceiver();
    [DllImport("media-lib")]
    private static extern void closeTcpSocket();
    
    
    [DllImport("media-lib")]
    private static extern int initEncodeVideoData(int videoFormat);
    [DllImport("media-lib")]
    private static extern void encodeVideoData(byte[] inputData,int length);
    [DllImport("media-lib")]
    private static extern void initEncodeAudioData();
    [DllImport("media-lib")]
    private static extern void encodeAudioData(byte[] audioData, int length);
    [DllImport("media-lib")]
    private static extern void destroyEncoder();
    
    
    [DllImport("media-lib")]
    private static extern void initDecodeVideoData();
    [DllImport("media-lib")]
    private static extern void receiveAndDecodeVideoData();
    [DllImport("media-lib")]
    private static extern void initDecodeAudioData();
    [DllImport("media-lib")]
    private static extern void receiveAndDecodeAudioData();
    [DllImport("media-lib")]
    private static extern void destroyDecoder();
    
    
    [DllImport("media-lib")]
    private static extern void setAddressAndPort(string address, int mytcpport, int peertcpport, int myvideoport, int peervideoport, int myaudioport, int peeraudioport);
    [DllImport("media-lib")]
    private static extern void test();

    // コールバックのデリゲート型定義
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DebugCallbackDelegate(string message);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CallbackDelegate(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex=1)] byte[] data, int size, int type);
    
    private void Start()
    {
        setMediaCallback(DebugCallback, ReceiveVideo, ReceiveAudio);
        setAddressAndPort(peerAddress, myTcpPort, peerTcpPort, myVideoPort, peerVideoPort, myAudioPort, peerAudioPort);
        setLibraryPath("./Assets/Plugins/CppConnect/libopenh264-2.3.1-mac-arm64.dylib");

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
            .Subscribe(UpdateReceivedTexture)
            .AddTo(this);
        
        _staticAudioSubject.ObserveOnMainThread()
            .Subscribe(PlayReceiveAudioData)
            .AddTo(this);

        // SetFqdn();
    }

    // ボタンに処理追加など、UIのセットアップ関数
    private void SetupUI()
    {
        _receivedTexture = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
        peerVideoCapture.texture = _receivedTexture;
        
        receiveTextLog.text = "";
        peerFqdnText.text = peerFqdn;
        
        sendButton.onClick.AddListener(() => SendStringData(messageInputField.text));
        startReceiveButton.onClick.AddListener(() =>
        {
            stringStreamFlag = true;
            StartStringReceiveLoop();
        });
        
        appStopButton.onClick.AddListener(async () =>
        {
            stringTestPanel.SetActive(true);
            videoTestPanel.SetActive(false);
            await CleanupApplication();
        });
        
        endReceiveButton.onClick.AddListener(async () =>
        {
            await CleanupApplication();
            Debug.Log("停止ボタンによるスレッド停止。");
        });
        
        startVideoButton.onClick.AddListener(() =>
        {
            stringTestPanel.SetActive(false);
            videoTestPanel.SetActive(true);
            
            _tcpCoroutine = StartVideoStream();
            StartCoroutine(_tcpCoroutine);
        });
        
        receiveTestVideoButton.onClick.AddListener(() =>
        {
            switchFlag = 3;
            stringTestPanel.SetActive(false);
            videoTestPanel.SetActive(true);
            ReceiveTestStartVideoStream();
        });
        
        streamingTestButton.onClick.AddListener(() =>
        {
            videoStreamFlag = true;
            stringTestPanel.SetActive(false);
            videoTestPanel.SetActive(true);
            
            _tcpCoroutine = StartVideoStream();
            StartCoroutine(_tcpCoroutine);
            ReceiveTestStartVideoStream();
        });
    }

    // アプリケーション停止時に実行される関数
    private async void OnApplicationQuit()
    {
        Debug.Log("終了ボタンによるスレッド停止。");
        await CleanupApplication();
    }

    // 停止時のクリーンアップ用の関数
    private async Task CleanupApplication()
    {
        _sendVideoDisposable?.Dispose();
        _sendAudioDisposable?.Dispose();
        
        _receiveDataThread?.Abort();
        _receiveVideoThread?.Abort();
        _receiveAudioThread?.Abort();
        _tcpReceiveThread?.Abort();
        
        if(_webCamTexture != null) _webCamTexture.Stop();
        if(_tcpCoroutine != null) StopCoroutine(_tcpCoroutine);
        
        if(switchFlag == 1 || stringStreamFlag) socketClose();
        // ビデオ送信開始してた場合
        if (switchFlag == 2 || (videoStreamFlag && endTcpConnect))
        {
            // test
            Microphone.End(_micName);
            destroyEncoder();
        }
        // ビデオ受信開始してた場合
        if(switchFlag == 3 || videoStreamFlag) await Task.Run(() =>
        {
            closeTcpSocket();
            destroyDecoder();
        });

        videoStreamFlag = false;
        switchFlag = 0;
    }

    // デバッグ用のコールバック関数
    [AOT.MonoPInvokeCallback(typeof(DebugCallbackDelegate))]
    public void DebugCallback(string message)
    {
        Debug.Log(message);
    }
    [AOT.MonoPInvokeCallback(typeof(DebugCallbackDelegate))]
    public void StartCallback(string message)
    {
        Debug.Log(message);
        _receiveDataThread = new Thread(new ThreadStart(ReceiveLoop)) { IsBackground = true };
        _receiveDataThread.Start();
    }

    private void SetPort(string portInp)
    {
        port = int.Parse(portInp);
    }

    private void SetFqdn(string fqdn)
    {
        peerFqdn = fqdn;
    }

    /* ===========================================
     *
     *          文字列の送受信をおこなう関数
     * 
     ========================================== */
    private void StartStringReceiveLoop()
    {
        // コールバック関数の設定
        setCallback(DebugCallback, ReceiveData, StartCallback);

        preReceiveUDPMessage(port);
    }

    // 送信関数
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
    
    // 受信関数
    private void ReceiveLoop()
    {
        _staticMessageSubject.OnNext("受信を開始しました。");
        while (true)
        {
            receiveUDPMessage();
        }
    }

    // 文字データ受信時に、C++からコールバックされる関数
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

    /* ===========================================
     *
     *         映像・音声データの送受信をおこなう関数
     * 
     ========================================== */
    
    // カメラ映像の送信を開始する関数
    private IEnumerator StartVideoStream()
    {
        while (true)
        {
            // TCPで送信先が見つかるまで待機
            if (initializeTcpSender()) break;
            yield return new WaitForSeconds(1);
        }
        
        Debug.Log("StartVideoStreamを開始");
        switchFlag = 2;
        endTcpConnect = true;
        
        // ビデオデータ用のセットアップ
        initEncodeVideoData(1);
        var devices = WebCamTexture.devices;
        if (devices.Length <= 0) yield break;
        _webCamTexture = new WebCamTexture(devices[cameraNumber].name, 1920, 1080, 30);
        _webCamTexture.Play();
        
        // 初期化が完了するまで待機
        while (_webCamTexture.width < 100) 
        {
            Debug.Log("カメラ初期化中");
            yield return null;
        }
        // バッファのサイズを設定
        _tmpBuffer = new Color32[_webCamTexture.width * _webCamTexture.height];
        _textureTmp = new Texture2D(_webCamTexture.width, _webCamTexture.height, TextureFormat.RGBA32, false);
        myVideoCapture.texture = _textureTmp;
        
        // 音声データ用のセットアップ
        if (Microphone.devices.Length <= 0) yield break;
        _micName = Microphone.devices[micNumber];
        initEncodeAudioData();
        _microphoneClip = Microphone.Start(_micName, true, 1, _micSampleRate);
        
        CheckMicrophoneSamplingRate();

        _sendVideoDisposable.Disposable = Observable.Interval(TimeSpan.FromSeconds(1.0 / 30))
            .Subscribe(_ => {
                CallEncodeVideoData();
            });
        
        _sendAudioDisposable.Disposable = Observable.Interval(TimeSpan.FromSeconds(960.0 / 48000))
            .Subscribe(_ => {
                CallEncodeAudioData();
            });
    }

    private void CheckMicrophoneSamplingRate()
    {
        Microphone.GetDeviceCaps(null, out var minFreq, out var maxFreq);
        Debug.Log($"Minimum Frequency: {minFreq}, Maximum Frequency: {maxFreq}");
        
        if (_sampleSize != 960)
        {
            Debug.LogError($"Audio buffer size mismatch: {_sampleSize} != {960}");
        }
        else
        {
            Debug.Log("Audio buffer size is correct.");
        }
    }
    
    // 30FPSで呼ばれる、カメラ映像をフレームごとに取得し、エンコードを実行する関数
    private void CallEncodeVideoData()
    {
        // WebCamTexture を更新
        _webCamTexture.GetPixels32(_tmpBuffer);
        _textureTmp.SetPixels32(_tmpBuffer);
        _textureTmp.Apply();
        
        // バイトデータを取得
        byte[] frameData = _textureTmp.GetRawTextureData();

        // C++ のエンコード関数を呼び出し
        encodeVideoData(frameData,frameData.Length);
    }
    
    // (1.0 / 50)secで呼ばれる、音声データを取得し、エンコードを実行する関数
    private void CallEncodeAudioData()
    {
        _microphonePosition = Microphone.GetPosition(null) - _sampleSize;

        if (_microphonePosition < 0)
            return;

        _samples = new float[_sampleSize];
        _microphoneClip.GetData(_samples, _microphonePosition);
        
        // float配列を16ビット整数に変換
        short[] intData = new short[_samples.Length];
        for (int i = 0; i < _samples.Length; i++)
        {
            intData[i] = (short)(_samples[i] * short.MaxValue);
        }

        // float配列をbyte配列に変換
        _audioData = new byte[intData.Length * sizeof(short)];
        Buffer.BlockCopy(intData, 0, _audioData, 0, _audioData.Length);

        // C++の関数に音声データを渡す
        encodeAudioData(_audioData, _audioData.Length);
    }


    // カメラ映像の受信を開始する関数
    private void ReceiveTestStartVideoStream()
    {
        initDecodeVideoData();
        initDecodeAudioData();
        
        // TCPで受信待機
        _tcpReceiveThread = new Thread(new ThreadStart(TcpReceiveStart)) { IsBackground = true };
        _tcpReceiveThread.Start();

        Debug.Log("受信スレッド作成");
        _receiveVideoThread = new Thread(new ThreadStart(ReceiveVideoLoop)) { IsBackground = true };
        _receiveVideoThread.Start();

        _receiveAudioThread = new Thread(new ThreadStart(ReceiveAudioLoop)) { IsBackground = true };
        _receiveAudioThread.Start();
    }

    // TCPでコネクションを受信する関数
    private void TcpReceiveStart()
    {
        if (!initializeTcpReceiver())
        {
            Debug.Log("Failed to initialize TCP server");
        }
    }

    // カメラ映像を受信する関数
    private void ReceiveVideoLoop()
    {
        Debug.Log("C#受信開始");
        while (true)
        {
            receiveAndDecodeVideoData();
        }
    }
    
    // 受信したビデオフレームでテクスチャを更新するメソッド
    private void UpdateReceivedTexture(byte[] data)
    {
        if (_receivedTexture != null)
        {
            _receivedTexture.LoadRawTextureData(data);
            _receivedTexture.Apply();
        }
    }

    // 映像データ受信時にC++からコールバックされる関数
    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public void ReceiveVideo(byte[] data, int size, int type)
    {
        _staticVideoSubject.OnNext(data);
    }
    
    // 音声を受信する関数
    private void ReceiveAudioLoop()
    {
        Debug.Log("C#受信開始");
        while (true)
        {
            receiveAndDecodeAudioData();
        }
    }
    
    private void PlayReceiveAudioData(byte[] audioData)
    {
        // 受信データを16ビット整数の配列に変換
        _receiveShortAudioData = new short[audioData.Length / 2];
        Buffer.BlockCopy(audioData, 0,  _receiveShortAudioData, 0, audioData.Length);

        // float配列に変換
        _receiveFloatAudioData = new float[_receiveShortAudioData.Length];
        for (int i = 0; i < _receiveShortAudioData.Length; i++)
        {
            _receiveFloatAudioData[i] = _receiveShortAudioData[i] / (float)short.MaxValue;
        }

        // AudioClipの作成
        _receiveClip = AudioClip.Create("ReceivedAudio", _receiveShortAudioData.Length, 1, 48000, false);
        _receiveClip.SetData(_receiveFloatAudioData, 0);

        // AudioClipの再生
        receiveAudioSource.clip = _receiveClip;
        receiveAudioSource.Play();
    }
    
    // 音声データ受信時にC++からコールバックされる関数
    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public void ReceiveAudio(byte[] data, int size, int type)
    {
        _staticAudioSubject.OnNext(data);
    }
}
