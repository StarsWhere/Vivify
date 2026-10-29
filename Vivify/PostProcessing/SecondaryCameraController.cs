using System.Collections.Generic;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Vivify.Controllers;
using Vivify.Managers;
using Zenject;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;
#if !V1_29_1
using UnityEngine.XR.OpenXR;
#endif

namespace Vivify.PostProcessing;

// this is attached to the secondary camera because trying to set the targettexture of a camera disable stereo on a camera for some reason
// https://forum.unity.com/threads/how-to-create-stereo-rendertextures-and-cameras.925175/#post-6968408
// also for some reason trying to manually force a camera to render with Camera.Render() causes it to not write to the right eye
// so we have a camera for each culling mask
internal class SecondaryCameraController : CullingCameraController
{
    private PostProcessingController _postProcessingController = null!;
    private readonly Dictionary<Camera.MonoOrStereoscopicEye, RTHandle> _depthHandles = new();
    private Material? _depthCopyMaterial;
    internal Material DepthCopyMaterial => _depthCopyMaterial ??= new Material(Blitter.GetBlitMaterial(TextureDimension.Tex2D));
    internal RTHandle GetDepthOutput(RenderTextureDescriptor descriptor)
    {
        descriptor.dimension = TextureDimension.Tex2D; descriptor.volumeDepth = 1; descriptor.vrUsage = VRTextureUsage.None;
        descriptor.depthBufferBits = 0; descriptor.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
        descriptor.colorFormat = RenderTextureFormat.RFloat; descriptor.msaaSamples = 1; descriptor.bindMS = false;
        if (!_depthHandles.TryGetValue(_requestedEye, out var handle) || handle.rt.width != descriptor.width || handle.rt.height != descriptor.height)
        {
            if (handle != null) { var previous = handle.rt; handle.Release(); previous.Release(); Destroy(previous); }
            var texture = new RenderTexture(descriptor) { name = "Vivify secondary depth " + _requestedEye }; texture.Create();
            handle = RTHandles.Alloc(texture); _depthHandles[_requestedEye] = handle;
            RenderTexturesDepth[_requestedEye] = texture;
        }
        return handle;
    }
    private CameraPropertyController _cameraPropertyController = null!;

    private bool _ready;
    private Camera.MonoOrStereoscopicEye _requestedEye=Camera.MonoOrStereoscopicEye.Mono;
    internal long RenderCount { get; private set; }

    internal int? Key { get; private set; }

    internal int? DepthKey { get; private set; }

    internal Dictionary<Camera.MonoOrStereoscopicEye, RenderTexture> RenderTextures { get; } = new();

    internal Dictionary<Camera.MonoOrStereoscopicEye, RenderTexture> RenderTexturesDepth { get; } = new();

    internal void Init(CreateCameraData cameraData)
    {
        _cameraPropertyController.Id = cameraData.Name;
        Key = null; DepthKey = null;
        if (cameraData.Texture != null)
        {
            Key = Shader.PropertyToID(cameraData.Texture);
        }

        if (cameraData.DepthTexture != null)
        {
            DepthKey = Shader.PropertyToID(cameraData.DepthTexture);
        }

        RefreshCamera();
    }

    protected override void OnPreCull()
    {
        Camera camera = Camera;
        Camera other = _postProcessingController.Camera;
        if (!CamEquals(camera, other))
        {
            RefreshCamera();
        }

        Transform transform1 = transform;
        transform1.localPosition = Vector3.zero;
        transform1.localRotation = Quaternion.identity;

        base.OnPreCull();

        if (_requestedEye != Camera.MonoOrStereoscopicEye.Mono)
        {
            var eye = (Camera.StereoscopicEye)(int)_requestedEye;
            camera.projectionMatrix = other.GetStereoProjectionMatrix(eye);
            camera.worldToCameraMatrix = other.GetStereoViewMatrix(eye);
        }
        else { camera.projectionMatrix = other.projectionMatrix; camera.worldToCameraMatrix = other.worldToCameraMatrix; }
        camera.nonJitteredProjectionMatrix = camera.projectionMatrix;
        camera.cullingMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;
    }

    // very simple comparison
    private static bool RTEquals(RenderTexture lhs, RenderTexture rhs)
    {
        return lhs.vrUsage == rhs.vrUsage &&
               lhs.width == rhs.width &&
               lhs.height == rhs.height;
    }

    private static bool CamEquals(Camera lhs, Camera rhs)
    {
        return lhs.stereoEnabled == rhs.stereoEnabled &&
               lhs.cullingMask == rhs.cullingMask &&
               Mathf.Approximately(lhs.fieldOfView, rhs.fieldOfView) &&
               Mathf.Approximately(lhs.nearClipPlane, rhs.nearClipPlane) &&
               Mathf.Approximately(lhs.farClipPlane, rhs.farClipPlane);
    }

    [UsedImplicitly]
    [Inject]
    private void Construct(
        IInstantiator instantiator,
        PostProcessingController postProcessingController)
    {
        _postProcessingController = postProcessingController;
        _cameraPropertyController = instantiator.InstantiateComponent<CameraPropertyController>(gameObject);
        _cameraPropertyController.enabled = false;
        Camera.CopyFrom(_postProcessingController.Camera);
    }

    private void RefreshCamera()
    {
        // copyfrom lags for some reason
        ////Camera.CopyFrom(_postProcessingController.Camera);
        Camera other = _postProcessingController.Camera;
        Camera.fieldOfView = other.fieldOfView;
        Camera.aspect = other.aspect;
        Camera.depth = other.depth - 1;
        Camera.nearClipPlane = other.nearClipPlane;
        Camera.farClipPlane = other.farClipPlane;
        Camera.layerCullDistances = other.layerCullDistances;
        Camera.targetTexture = null;
        Camera.enabled = false;
        Camera.cullingMask = other.cullingMask;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        foreach (var handle in _depthHandles.Values) handle.Release();
        _depthHandles.Clear();
        if (_depthCopyMaterial != null) Destroy(_depthCopyMaterial);
        RenderTextures.Values.Do(n => n.Release());
        RenderTextures.Clear();
        RenderTexturesDepth.Values.Do(n => n.Release());
        RenderTexturesDepth.Clear();
    }

    internal void RenderForParent()
    {
        Camera parent = _postProcessingController.Camera;
        Camera.enabled = false;
        var descriptor = parent.targetTexture != null ? parent.targetTexture.descriptor
            : new RenderTextureDescriptor(Mathf.Max(1, parent.pixelWidth), Mathf.Max(1, parent.pixelHeight), RenderTextureFormat.ARGBHalf, 24);
        descriptor.dimension = TextureDimension.Tex2D; descriptor.volumeDepth = 1;
        descriptor.vrUsage = VRTextureUsage.None; descriptor.msaaSamples = 1;
        Camera.GetComponent<MainEffectController>().enabled = MainEffect;
        var target = RenderTexture.GetTemporary(descriptor);
        try
        {
            int eyes = parent.stereoEnabled ? 2 : 1;
            for (int index = 0; index < eyes; index++)
            {
                Camera.MonoOrStereoscopicEye eye = eyes == 1 ? Camera.MonoOrStereoscopicEye.Mono : (Camera.MonoOrStereoscopicEye)index;
                _requestedEye = eye;
                if (eyes > 1)
                {
                    var stereoEye = (Camera.StereoscopicEye)index;
                    Camera.worldToCameraMatrix = parent.GetStereoViewMatrix(stereoEye);
                    Camera.projectionMatrix = parent.GetStereoProjectionMatrix(stereoEye);
                }
                else { Camera.worldToCameraMatrix = parent.worldToCameraMatrix; Camera.projectionMatrix = parent.projectionMatrix; }
                Camera.cullingMatrix = Camera.projectionMatrix * Camera.worldToCameraMatrix;
                RenderPipeline.SubmitRenderRequest(Camera, new RenderPipeline.StandardRequest {destination=target});
                RenderCount++;
                CompleteRender(target, eye);
            }
        }
        finally { _requestedEye = Camera.MonoOrStereoscopicEye.Mono; RenderTexture.ReleaseTemporary(target); }
    }

    private void CompleteRender(RenderTexture src, Camera.MonoOrStereoscopicEye eye)
    {
        Camera.MonoOrStereoscopicEye stereoActiveEye = eye;
        RenderTextureDescriptor descriptor = src.descriptor;
        descriptor.msaaSamples = 1;

        if (!_ready)
        {
            GetRenderTexture(RenderTextures, descriptor, false, true);
            GetRenderTexture(RenderTexturesDepth, descriptor, true, true);

            _cameraPropertyController.enabled = true;
            _ready = true;
        }

        if (Key != null)
        {
            RenderTexture colorTexture = GetRenderTexture(RenderTextures, descriptor, false, false);

            Graphics.Blit(src, colorTexture);
        }

        // Depth is copied inside the secondary camera's graph, while its depth
        // texture is alive and before URP restores another camera's globals.

        if (Key == null && DepthKey == null)
        {
            gameObject.SetActive(false);
        }

        return;

        RenderTexture GetRenderTexture(
            Dictionary<Camera.MonoOrStereoscopicEye, RenderTexture> dictionary,
            RenderTextureDescriptor renderTextureDescriptor,
            bool depth,
            bool create)
        {
            // ReSharper disable once InvertIf
            if (!dictionary.TryGetValue(stereoActiveEye, out RenderTexture renderTexture) ||
                !RTEquals(renderTexture, src))
            {
                renderTexture?.Release();

                if (depth)
                {
                    renderTextureDescriptor.colorFormat = RenderTextureFormat.RFloat;
                }

                renderTexture = new RenderTexture(renderTextureDescriptor);

                dictionary[stereoActiveEye] = renderTexture;

                if (create)
                {
                    renderTexture.Create();
                }
            }

            return renderTexture;
        }
    }
}
