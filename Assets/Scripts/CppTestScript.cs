using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

public class CppTestScript : MonoBehaviour
{
    [DllImport("lib")]
    private static extern IntPtr registerFqdn(string str);
    [DllImport("lib")]
    private static extern IntPtr sendBinaryData(string str);
    [DllImport("lib")]
    private static extern void setCallback(CallbackDelegate callback);

    [DllImport("lib")]
    private static extern void triggerCallback();
    
    // コールバックのデリゲート型定義
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CallbackDelegate(string message);

    [AOT.MonoPInvokeCallback(typeof(CallbackDelegate))]
    public static void MyCallback(string message)
    {
        Debug.Log("Called from C++ with message: " + message);
    }

    void Start()
    {
        var ptr = registerFqdn("test");
        string result = Marshal.PtrToStringAnsi(ptr);
        Debug.Log(result);
        
        var ptr2 = sendBinaryData("test");
        string result2 = Marshal.PtrToStringAnsi(ptr2);
        Debug.Log(result2);
        
        // C++のコールバックをC#の関数で設定
        setCallback(MyCallback);

        // C++のコールバックをトリガー
        triggerCallback();

        Debug.Log("debug終了");
    }
    
    void Update()
    {
        
    }
}
