﻿using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// deferred msaa
/// </summary>
[RequireComponent(typeof(Camera))]
public class DeferredMSAA : MonoBehaviour
{
    /// <summary>
    /// is scene view
    /// </summary>
    public static bool isSceneView = false;

    /// <summary>
    /// msaa sample
    /// </summary>
    public enum MSAASample
    {
        Msaa2X = 0,
        Msaa4X,
        Msaa8X
    }

    [DllImport("SetGBufferTarget")]
    static extern bool SetGBufferColor(int _index, int _msaaFactor, IntPtr _colorBuffer);

    [DllImport("SetGBufferTarget")]
    static extern bool SetGBufferDepth(int _msaaFactor, IntPtr _depthBuffer);

    [DllImport("SetGBufferTarget")]
    static extern void SetClearColor(float[] _color);

    [DllImport("SetGBufferTarget")]
    static extern void Release();

    [DllImport("SetGBufferTarget")]
    static extern IntPtr GetRenderEventFunc();

    /// <summary>
    /// msaa factor
    /// </summary>
    public MSAASample msaaFactor = MSAASample.Msaa2X;

    /// <summary>
    /// msaa threshold
    /// </summary>
    [Range(0, 1)]
    public float msaaThreshold = 0.1f;

    /// <summary>
    /// debug msaa
    /// </summary>
    public bool debugMsaa = false;

    /// <summary>
    /// resolve aa material
    /// </summary>
    public Material resolveAA;

    /// <summary>
    /// transfer aa instead resolve
    /// </summary>
    public Material transferAA;

    /// <summary>
    /// resolve aa depth material
    /// </summary>
    public Material resolveAADepth;

    RenderTexture diffuseRT;
    RenderTexture specularRT;
    RenderTexture normalRT;
    RenderTexture emissionRT;
    RenderTexture shadowMaskRT;
    RenderTexture depthRT;
    RenderTexture skyTexture;

    RenderTexture diffuseAry;
    RenderTexture specularAry;
    RenderTexture normalAry;
    RenderTexture shadowMaskAry;

    Camera attachedCam;
    CommandBuffer msGBuffer;
    CommandBuffer copyGBuffer;

    bool initSucceed = true;
    string[] texName = { "_MsaaTex_2X", "_MsaaTex_4X", "_MsaaTex_8X" };

    int[] msaaFactors = { 2, 4, 8 };
    int lastWidth;
    int lastHeight;
    int lastMsaa;

    void Awake()
    {
        attachedCam = GetComponent<Camera>();
        attachedCam.renderingPath = RenderingPath.DeferredShading;
        attachedCam.allowHDR = true;

        float[] bgColor = new float[4];
        bgColor[0] = attachedCam.backgroundColor.linear.r;
        bgColor[1] = attachedCam.backgroundColor.linear.g;
        bgColor[2] = attachedCam.backgroundColor.linear.b;
        bgColor[3] = attachedCam.backgroundColor.linear.a;

        SetClearColor(bgColor);

        CreateMapAndColorBuffer("Custom diffuse", 0, RenderTextureFormat.ARGB32, 0, msaaFactors[(int)msaaFactor], ref diffuseRT);
        CreateMapAndColorBuffer("Custom specular", 0, RenderTextureFormat.ARGB32, 1, msaaFactors[(int)msaaFactor], ref specularRT);
        CreateMapAndColorBuffer("Custom normal", 0, RenderTextureFormat.ARGB2101010, 2, msaaFactors[(int)msaaFactor], ref normalRT);
        CreateMapAndColorBuffer("Custom emission", 0, RenderTextureFormat.ARGBHalf, 3, msaaFactors[(int)msaaFactor], ref emissionRT);
        CreateMapAndColorBuffer("Custom shadowmask", 0, RenderTextureFormat.ARGB32, 4, msaaFactors[(int)msaaFactor], ref shadowMaskRT);
        CreateMapAndColorBuffer("Cutsom depth", 32, RenderTextureFormat.Depth, -1, msaaFactors[(int)msaaFactor], ref depthRT);

        // sky tex, quarter res is enough
        skyTexture = new RenderTexture(Screen.width / 4, Screen.height / 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        skyTexture.name = "Sky Texture";

        CreateAryMap("Diffuse Ary", RenderTextureFormat.ARGB32, ref diffuseAry);
        CreateAryMap("Specular Ary", RenderTextureFormat.ARGB32, ref specularAry);
        CreateAryMap("Normal Ary", RenderTextureFormat.ARGB2101010, ref normalAry);
        CreateAryMap("Shadowmask Ary", RenderTextureFormat.ARGB32, ref shadowMaskAry);

        initSucceed = initSucceed && SetGBufferDepth(msaaFactors[(int)msaaFactor], depthRT.GetNativeDepthBufferPtr());

        if (!initSucceed)
        {
            Debug.Log("MainGraphic : [DeferredMSAA] detph native failed.");
            enabled = false;
            OnDestroy();
            return;
        }

        msGBuffer = new CommandBuffer();
        msGBuffer.name = "Bind MS GBuffer";
        msGBuffer.IssuePluginEvent(GetRenderEventFunc(), 0);

        copyGBuffer = new CommandBuffer();
        copyGBuffer.name = "Copy MS GBuffer";


        copyGBuffer.SetGlobalFloat("_MsaaFactor", msaaFactors[(int)msaaFactor]);
        copyGBuffer.SetGlobalTexture("_SkyTextureForResolve", skyTexture);

        int texIdx = 0;
        texIdx = (msaaFactors[(int)msaaFactor] == 4) ? 1 : texIdx;
        texIdx = (msaaFactors[(int)msaaFactor] == 8) ? 2 : texIdx;

        copyGBuffer.SetGlobalTexture(texName[texIdx], emissionRT);
        copyGBuffer.Blit(null, BuiltinRenderTextureType.CameraTarget, resolveAA);

        copyGBuffer.SetGlobalTexture(texName[texIdx], diffuseRT);
        copyGBuffer.Blit(null, BuiltinRenderTextureType.GBuffer0, resolveAA);

        copyGBuffer.SetGlobalTexture(texName[texIdx], specularRT);
        copyGBuffer.Blit(null, BuiltinRenderTextureType.GBuffer1, resolveAA);

        copyGBuffer.SetGlobalTexture(texName[texIdx], normalRT);
        copyGBuffer.SetGlobalFloat("_IsNormal", 1f);
        copyGBuffer.Blit(null, BuiltinRenderTextureType.GBuffer2, resolveAA);
        copyGBuffer.SetGlobalFloat("_IsNormal", 0f);

        copyGBuffer.SetGlobalTexture(texName[texIdx], depthRT);
        copyGBuffer.Blit(null, BuiltinRenderTextureType.CameraTarget, resolveAADepth);

        for (int i = 0; i < msaaFactors[(int)msaaFactor]; i++)
        {
            copyGBuffer.SetGlobalFloat("_TransferAAIndex", i);

            copyGBuffer.SetRenderTarget(diffuseAry, 0, CubemapFace.Unknown, i);
            copyGBuffer.SetGlobalTexture("_MsaaTex", diffuseRT);
            copyGBuffer.Blit(null, BuiltinRenderTextureType.CurrentActive, transferAA);

            copyGBuffer.SetRenderTarget(specularAry, 0, CubemapFace.Unknown, i);
            copyGBuffer.SetGlobalTexture("_MsaaTex", specularRT);
            copyGBuffer.Blit(null, BuiltinRenderTextureType.CurrentActive, transferAA);

            copyGBuffer.SetRenderTarget(normalAry, 0, CubemapFace.Unknown, i);
            copyGBuffer.SetGlobalTexture("_MsaaTex", normalRT);
            copyGBuffer.Blit(null, BuiltinRenderTextureType.CurrentActive, transferAA);

            copyGBuffer.SetRenderTarget(shadowMaskAry, 0, CubemapFace.Unknown, i);
            copyGBuffer.SetGlobalTexture("_MsaaTex", shadowMaskRT);
            copyGBuffer.Blit(null, BuiltinRenderTextureType.CurrentActive, transferAA);
        }

        copyGBuffer.SetGlobalTexture("_GBuffer0", diffuseAry);
        copyGBuffer.SetGlobalTexture("_GBuffer1", specularAry);
        copyGBuffer.SetGlobalTexture("_GBuffer2", normalAry);
        copyGBuffer.SetGlobalTexture("_GBuffer4", shadowMaskAry);

        lastWidth = Screen.width;
        lastHeight = Screen.height;
        lastMsaa = msaaFactors[(int)msaaFactor];
    }

    void OnEnable()
    {
        EnableDeferredAA();
    }

    void OnDisable()
    {
        DisableDeferredAA();
    }

    void OnDestroy()
    {
        if (msGBuffer != null)
        {
            attachedCam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, msGBuffer);
            msGBuffer.Release();
        }

        if (copyGBuffer != null)
        {
            attachedCam.RemoveCommandBuffer(CameraEvent.AfterGBuffer, copyGBuffer);
            copyGBuffer.Release();
        }

        DestroyMap(diffuseRT);
        DestroyMap(specularRT);
        DestroyMap(normalRT);
        DestroyMap(emissionRT);
        DestroyMap(shadowMaskRT);
        DestroyMap(depthRT);
        DestroyMap(skyTexture);

        DestroyMap(diffuseAry);
        DestroyMap(specularAry);
        DestroyMap(normalAry);
        DestroyMap(shadowMaskAry);

        Release();

        Shader.SetGlobalFloat("_MsaaFactor", 1);
    }

    void OnPreCull()
    {
        Graphics.SetRenderTarget(skyTexture);
        if (attachedCam.clearFlags == CameraClearFlags.Skybox)
        {
            GL.ClearWithSkybox(false, attachedCam);
        }
        else
        {
            GL.Clear(false, true, attachedCam.backgroundColor);
        }
        Graphics.SetRenderTarget(null);
    }

    void Update()
    {
#if DEBUG
        // resize check
        bool needResize = Screen.width != lastWidth || Screen.height != lastHeight || lastMsaa != msaaFactors[(int)msaaFactor];
        if (needResize)
        {
            OnDestroy();
            Awake();
            OnEnable();
        }

        lastWidth = Screen.width;
        lastHeight = Screen.height;
        lastMsaa = msaaFactors[(int)msaaFactor];
#endif

#if UNITY_EDITOR
        DisableDeferredAA();
        EnableDeferredAA();

        // check scene view
        if (isSceneView)
        {
            DisableDeferredAA();
            isSceneView = false;
        }
#endif

        Shader.SetGlobalFloat("_MsaaThreshold", msaaThreshold);
        Shader.SetGlobalFloat("_DebugMsaa", (debugMsaa) ? 1f : 0f);
    }

    void EnableDeferredAA()
    {
        if (msGBuffer != null)
        {
            attachedCam.AddCommandBuffer(CameraEvent.BeforeGBuffer, msGBuffer);
        }

        if (copyGBuffer != null)
        {
            attachedCam.AddCommandBuffer(CameraEvent.AfterGBuffer, copyGBuffer);
        }

        Shader.SetGlobalFloat("_MsaaFactor", msaaFactors[(int)msaaFactor]);
    }

    void DisableDeferredAA()
    {
        if (msGBuffer != null)
        {
            attachedCam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, msGBuffer);
        }

        if (copyGBuffer != null)
        {
            attachedCam.RemoveCommandBuffer(CameraEvent.AfterGBuffer, copyGBuffer);
        }

        Shader.SetGlobalFloat("_MsaaFactor", 1);
    }

    void CreateMapAndColorBuffer(string _rtName, int _depth, RenderTextureFormat _format, int _gBufferIdx, int _msaaFactor, ref RenderTexture _rt)
    {
        _rt = new RenderTexture(Screen.width, Screen.height, _depth, _format, RenderTextureReadWrite.Linear);
        _rt.name = _rtName;
        _rt.antiAliasing = _msaaFactor;
        _rt.bindTextureMS = (_msaaFactor > 1);
        _rt.Create();   // create rt so that we have native ptr

        if (_gBufferIdx >= 0)
        {
            bool nativeSucceed = SetGBufferColor(_gBufferIdx, _msaaFactor, _rt.GetNativeTexturePtr());
            if (!nativeSucceed)
            {
                Debug.Log("MainGraphic : [DeferredMSAA] " + _rtName + " native failed.");
                initSucceed = false;
            }
        }
    }

    void CreateAryMap(string _rtName, RenderTextureFormat _format, ref RenderTexture _rt)
    {
        _rt = new RenderTexture(Screen.width, Screen.height, 0, _format, RenderTextureReadWrite.Linear);
        _rt.name = _rtName;
        _rt.dimension = TextureDimension.Tex2DArray;
        _rt.volumeDepth = msaaFactors[(int)msaaFactor];
    }

    void DestroyMap(RenderTexture _rt)
    {
        if (_rt)
        {
            _rt.Release();
            DestroyImmediate(_rt);
        }
    }
}
