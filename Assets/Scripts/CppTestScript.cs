using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

public class CppTestScript : MonoBehaviour
{
    [DllImport("lib")]
    private static extern int sum(int a, int b);
    [DllImport("lib")]
    private static extern IntPtr registerFqdn(string str);
    [DllImport("lib")]
    private static extern IntPtr sendBinaryData(string str);

    void Start()
    {
        Debug.Log(sum(1,2));
        
        var ptr = registerFqdn("test");
        string result = Marshal.PtrToStringAnsi(ptr);
        Debug.Log(result);
        
        var ptr2 = sendBinaryData("test");
        string result2 = Marshal.PtrToStringAnsi(ptr2);
        Debug.Log(result2);

        Debug.Log("debug終了");
    }
    
    void Update()
    {
        
    }
}
