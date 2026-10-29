using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IPA.Utilities;
using JetBrains.Annotations;
using SiraUtil.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Zenject;
using static Vivify.VivifyController;

namespace Vivify.PostProcessing;

[RequireComponent(typeof(Camera))]
internal class PostProcessingController : CullingCameraController
{
    private readonly Dictionary<CreateCameraData, string> _activeCreateCameraDatas = new();
    private readonly Dictionary<CreateScreenTextureData, string> _activeDeclaredTextures = new();
    private readonly Dictionary<string, CullingCameraController> _cullingCameraControllers = new();
    private readonly Dictionary<string, RenderTextureHolder> _declaredTextures = new();
    private readonly Stack<SecondaryCameraController> _disabledCullingCameraControllers = new();

    private readonly List<CreateCameraData> _reusableCameraKeys = [];
    private readonly List<CreateScreenTextureData> _reusableDeclaredKeys = [];

    private readonly int _mainTex = Shader.PropertyToID("_MainTex");

    private SiraLog _log = null!;
    private IInstantiator _instantiator = null!;

    private RenderTextureDescriptor? _cachedMainDescriptor;
    private RenderTexture? _resolvedInput;

    internal Camera.MonoOrStereoscopicEye RenderEye { get; private set; }
    internal long RenderPassCount { get; private set; }
    internal long MaterialBlitCount { get; private set; }
    internal string[] GrabTextures { get; set; } = Array.Empty<string>();

    internal bool HasWork => GrabTextures.Length > 0 || CameraDatas.Count > 0 || DeclaredTextureDatas.Count > 0 || Effects.Values.Any(list => list.Count > 0);

    internal Dictionary<string, CreateCameraData> CameraDatas { get; set; } = new();

    internal Dictionary<string, CreateScreenTextureData> DeclaredTextureDatas { get; set; } = new();

    internal Dictionary<PostProcessingOrder, List<MaterialData>> Effects { get; set; } = new();

    internal void PrewarmCameras(int count)
    {
        count -= _disabledCullingCameraControllers.Count + _cullingCameraControllers.Count;
        for (int i = 0; i < count; i++)
        {
            _disabledCullingCameraControllers.Push(CreateCamera());
        }
    }

    protected override void OnPreCull()
    {
        foreach ((CreateCameraData textureData, string id) in _activeCreateCameraDatas)
        {
            if (CameraDatas.ContainsValue(textureData))
            {
                continue;
            }

            if (_cullingCameraControllers.TryGetValue(id, out CullingCameraController cameraController) &&
                cameraController is SecondaryCameraController secondaryCameraController2)
            {
                secondaryCameraController2.gameObject.SetActive(false);
                _disabledCullingCameraControllers.Push(secondaryCameraController2);
            }

            _cullingCameraControllers.Remove(id);
            _reusableCameraKeys.Add(textureData);
        }

        foreach (CreateCameraData createCameraData in _reusableCameraKeys)
        {
            _activeCreateCameraDatas.Remove(createCameraData);
        }

        _reusableCameraKeys.Clear();

        foreach ((string textureName, CreateCameraData cameraData) in CameraDatas)
        {
            if (_activeCreateCameraDatas.ContainsKey(cameraData))
            {
                continue;
            }

            _activeCreateCameraDatas[cameraData] = textureName;

            SecondaryCameraController finalController = _disabledCullingCameraControllers.Count > 0
                ? _disabledCullingCameraControllers.Pop()
                : CreateCamera();

            finalController.Init(cameraData);
            finalController.gameObject.SetActive(true);
            _cullingCameraControllers[textureName] = finalController;
        }

        // delete old declared textures
        foreach ((CreateScreenTextureData value, string textureName) in _activeDeclaredTextures)
        {
            if (DeclaredTextureDatas.ContainsValue(value))
            {
                continue;
            }

            foreach (RenderTexture? renderTexture in _declaredTextures[textureName].Textures.Values)
            {
                if (renderTexture != null)
                {
                    renderTexture.Release();
                }
            }

            _declaredTextures.Remove(textureName);
            _reusableDeclaredKeys.Add(value);
        }

        foreach (CreateScreenTextureData declareRenderTextureData in _reusableDeclaredKeys)
        {
            _activeDeclaredTextures.Remove(declareRenderTextureData);
        }

        _reusableDeclaredKeys.Clear();

        // instantiate RenderTextureHolders
        foreach ((string textureName, CreateScreenTextureData declareRenderTextureData) in DeclaredTextureDatas)
        {
            if (_activeDeclaredTextures.ContainsKey(declareRenderTextureData))
            {
                continue;
            }

            _declaredTextures.Add(textureName, new RenderTextureHolder(declareRenderTextureData));
            _activeDeclaredTextures.Add(declareRenderTextureData, textureName);
        }

        base.OnPreCull();
    }

    internal void RenderUrp(CommandBuffer command, RenderTextureDescriptor descriptor,
        RenderTargetIdentifier target, PostProcessingOrder order, RenderTexture? sourceTexture)
    {
        RenderPassCount++;
        int sourceSamples = descriptor.msaaSamples;
        descriptor.depthBufferBits = 0;
        descriptor.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
        descriptor.msaaSamples = 1;
        descriptor.bindMS = false;
        _cachedMainDescriptor = descriptor;
        bool stereoArray = descriptor.dimension == TextureDimension.Tex2DArray;
        int eyes = stereoArray ? descriptor.volumeDepth : 1;
        bool instancing = Shader.IsKeywordEnabled("STEREO_INSTANCING_ON");
        bool multiview = Shader.IsKeywordEnabled("STEREO_MULTIVIEW_ON");
        bool singlePass = Shader.IsKeywordEnabled("UNITY_SINGLE_PASS_STEREO");
        bool multipass = Shader.IsKeywordEnabled("MULTIPASS_ENABLED");
        int previousEye = Shader.GetGlobalInt("_StereoActiveEye");
        RenderTargetIdentifier inputTarget = target;
        if (sourceSamples > 1)
        {
            if (sourceTexture == null) throw new InvalidOperationException("Multisampled Vivify input has no render texture.");
            if (_resolvedInput == null || _resolvedInput.descriptor.width != descriptor.width || _resolvedInput.descriptor.height != descriptor.height || _resolvedInput.descriptor.dimension != descriptor.dimension || _resolvedInput.descriptor.volumeDepth != descriptor.volumeDepth || _resolvedInput.graphicsFormat != descriptor.graphicsFormat)
            {
                if (_resolvedInput != null) { _resolvedInput.Release(); Destroy(_resolvedInput); }
                _resolvedInput = new RenderTexture(descriptor) { name = "Vivify resolved camera input" }; _resolvedInput.Create();
            }
            command.ResolveAntiAliasedSurface(sourceTexture, _resolvedInput);
            inputTarget = new RenderTargetIdentifier(_resolvedInput);
        }
        if (stereoArray)
        {
            command.DisableShaderKeyword("STEREO_INSTANCING_ON");
            command.DisableShaderKeyword("STEREO_MULTIVIEW_ON");
            command.DisableShaderKeyword("UNITY_SINGLE_PASS_STEREO");
            command.EnableShaderKeyword("MULTIPASS_ENABLED");
        }
        for (int eye = 0; eye < eyes; eye++)
        {
            RenderEye = stereoArray ? (Camera.MonoOrStereoscopicEye)eye : Camera.stereoActiveEye;
            command.SetGlobalInt("_StereoActiveEye", (int)RenderEye);
            RenderTextureDescriptor mono = descriptor;
            mono.dimension = TextureDimension.Tex2D; mono.volumeDepth = 1; mono.vrUsage = VRTextureUsage.None;
            CreateDeclaredTextures(mono);
            foreach (var holder in _declaredTextures.Values)
                if (holder.Textures.TryGetValue(RenderEye, out var texture)) command.SetGlobalTexture(holder.Data.PropertyId, texture);
            foreach (var controller in _cullingCameraControllers.Values)
            {
                if (controller is not SecondaryCameraController secondary) continue;
                if (secondary.Key.HasValue && secondary.RenderTextures.TryGetValue(RenderEye, out var color)) command.SetGlobalTexture(secondary.Key.Value, color);
                if (secondary.DepthKey.HasValue && secondary.RenderTexturesDepth.TryGetValue(RenderEye, out var depth)) command.SetGlobalTexture(secondary.DepthKey.Value, depth);
            }
            if (stereoArray)
            {
                int input = Shader.PropertyToID("_VivifyEyeInput");
                int output = Shader.PropertyToID("_VivifyEyeOutput");
                command.GetTemporaryRT(input, mono); command.GetTemporaryRT(output, mono);
                command.CopyTexture(inputTarget, eye, 0, new RenderTargetIdentifier(input), 0, 0);
                RenderImage(command, mono, input, output, Effects[order]);
                command.SetRenderTarget(target, 0, CubemapFace.Unknown, eye);
                Blitter.BlitTexture(command, new RenderTargetIdentifier(output), new Vector4(1, 1, 0, 0), Blitter.GetBlitMaterial(TextureDimension.Tex2D), 0);
                command.ReleaseTemporaryRT(input); command.ReleaseTemporaryRT(output);
            }
            else RenderImage(command, mono, inputTarget, target, Effects[order]);
        }
        if (stereoArray)
        {
            Restore("STEREO_INSTANCING_ON", instancing); Restore("STEREO_MULTIVIEW_ON", multiview);
            Restore("UNITY_SINGLE_PASS_STEREO", singlePass); Restore("MULTIPASS_ENABLED", multipass);
        }
        RenderEye = Camera.MonoOrStereoscopicEye.Mono;
        command.SetGlobalInt("_StereoActiveEye", previousEye);
        void Restore(string keyword, bool enabled) { if (enabled) command.EnableShaderKeyword(keyword); else command.DisableShaderKeyword(keyword); }
    }

    protected override void OnBeforeUrpRendering()
    {
        if (!HasWork) return;
        foreach (var controller in _cullingCameraControllers.Values)
            if (controller is SecondaryCameraController secondary) secondary.RenderForParent();
    }

    private void CreateDeclaredTextures(RenderTextureDescriptor descriptor)
    {
        Camera.MonoOrStereoscopicEye stereoActiveEye = RenderEye;

        // set up declared textures
        foreach ((string textureName, RenderTextureHolder value) in _declaredTextures)
        {
            if (value.Textures.ContainsKey(stereoActiveEye))
            {
                continue;
            }

            CreateScreenTextureData data = value.Data;

            RenderTextureDescriptor newDescriptor = descriptor;
            newDescriptor.width = (int)((data.Width ?? descriptor.width) / data.XRatio);
            newDescriptor.height = (int)((data.Height ?? descriptor.height) / data.YRatio);

            if (data.Format.HasValue)
            {
                RenderTextureFormat format = data.Format.Value;
                newDescriptor.colorFormat = format;
            }

            RenderTexture texture = new(newDescriptor);
            if (data.FilterMode.HasValue)
            {
                texture.filterMode = data.FilterMode.Value;
            }

            value.Textures[stereoActiveEye] = texture;
            _log.Debug(
                $"Created texture for [{gameObject.name}] [{stereoActiveEye}]: {textureName}, {texture.width} : {texture.height} : {texture.filterMode} : {texture.format}");
        }
    }

    private void RenderImage(CommandBuffer command, RenderTextureDescriptor mainDescriptor, RenderTargetIdentifier src, RenderTargetIdentifier dst, List<MaterialData> materials)
    {
        Camera.MonoOrStereoscopicEye stereoActiveEye = RenderEye;

        if (materials.Count == 0)
        {
            if (!src.Equals(dst)) command.Blit(src, dst);
            return;
        }

        int tempIDNext = 69;

        // blit all passes
        int mainID = _mainTex; // TODO: I am not actually sure what this should be
        RenderTargetIdentifier main = new(mainID);
        command.GetTemporaryRT(mainID, mainDescriptor);

        command.Blit(src, main);
        for (int i = materials.Count - 1; i >= 0; i--)
        {
            MaterialData materialData = materials[i];
            if (materialData.Frame != null && materialData.Frame != Time.frameCount)
            {
                materials.RemoveAt(i);
                continue;
            }

            Material? material = materialData.Material;

            if (materialData.Source == CAMERA_TARGET)
            {
                foreach (string materialDataTarget in materialData.Targets)
                {
                    if (materialDataTarget == CAMERA_TARGET)
                    {
                        if (material == null)
                        {
                            continue;
                        }

                        int tempID = ++tempIDNext;
                        RenderTargetIdentifier temp = new(tempID);
                        command.GetTemporaryRT(tempID, mainDescriptor);
                        command.Blit(main, temp, material, materialData.Pass);
                        MaterialBlitCount++;
                        command.ReleaseTemporaryRT(mainID);
                        main = temp;
                        mainID = tempID;
                    }
                    else
                    {
                        if (_declaredTextures.TryGetValue(materialDataTarget, out RenderTextureHolder targetHolder) &&
                            targetHolder.Textures.TryGetValue(stereoActiveEye, out RenderTexture? target))
                        {
                            Blit(main, target, material, materialData.Pass);
                        }
                        else
                        {
                            _log.Warn($"[{gameObject.name}] Unable to find destination [{materialDataTarget}]");
                        }
                    }
                }
            }
            else if (_declaredTextures.TryGetValue(materialData.Source, out RenderTextureHolder sourceHolder))
            {
                foreach (string materialDataTarget in materialData.Targets)
                {
                    sourceHolder.Textures.TryGetValue(stereoActiveEye, out RenderTexture? source);
                    if (materialDataTarget == CAMERA_TARGET)
                    {
                        Blit(source, main, material, materialData.Pass);
                    }
                    else if (_declaredTextures.TryGetValue(materialDataTarget, out RenderTextureHolder targetHolder))
                    {
                        // extra stuff becuase we cannot blit directly into itself
                        if (sourceHolder == targetHolder)
                        {
                            if (material == null)
                            {
                                _log.Warn($"[{materialDataTarget}] Attempting to blit to self without material");
                                continue;
                            }

                            int tempID = ++tempIDNext;
                            RenderTargetIdentifier temp = new(tempID);
                            command.GetTemporaryRT(tempID, source!.descriptor, source.filterMode);
                            command.Blit(source, temp, material, materialData.Pass);
                            MaterialBlitCount++;
                            command.Blit(temp, source);
                            command.ReleaseTemporaryRT(tempID);

                            continue;
                        }

                        if (targetHolder.Textures.TryGetValue(stereoActiveEye, out RenderTexture? target))
                        {
                            Blit(source, target, material, materialData.Pass);
                        }
                    }
                    else
                    {
                        _log.Warn($"[{gameObject.name}] Unable to find destination [{materialDataTarget}]");
                    }
                }
            }
            else if (_cullingCameraControllers.TryGetValue(
                         materialData.Source,
                         out CullingCameraController cullingCameraController) &&
                     cullingCameraController is SecondaryCameraController secondaryCameraController)
            {
                foreach (string materialDataTarget in materialData.Targets)
                {
                    secondaryCameraController.RenderTextures.TryGetValue(stereoActiveEye, out RenderTexture? source);
                    if (materialDataTarget == CAMERA_TARGET)
                    {
                        Blit(source, main, material, materialData.Pass);
                    }
                    else if (_declaredTextures.TryGetValue(materialDataTarget, out RenderTextureHolder targetHolder) &&
                            targetHolder.Textures.TryGetValue(stereoActiveEye, out RenderTexture? target))
                    {
                        Blit(source, target, material, materialData.Pass);
                    }
                    else
                    {
                        _log.Warn($"[{gameObject.name}] Unable to find destination [{materialDataTarget}]");
                    }
                }
            }
            else
            {
                _log.Warn($"[{gameObject.name}] Unable to find source [{materialData.Source}]");
            }

            continue;

            void Blit(RenderTargetIdentifier? blitSrc, RenderTargetIdentifier? blitDst, Material? blitMat, int blitPass)
            {
                if (!blitDst.HasValue || !blitSrc.HasValue)
                {
                    return;
                }

                if (blitMat != null)
                {
                    command.Blit(blitSrc.Value, blitDst.Value, blitMat, blitPass);
                    MaterialBlitCount++;
                }
                else
                {
                    command.Blit(blitSrc.Value, blitDst.Value);
                }
            }
        }

        command.Blit(main, dst);
        command.ReleaseTemporaryRT(mainID);

    }

    private SecondaryCameraController CreateCamera()
    {
        GameObject newObject = new("VivifyCamera");
        ////newObject.SetActive(false);
        newObject.transform.SetParent(transform, false);
        newObject.AddComponent<Camera>();
        MainEffectController parentEffect = GetComponent<MainEffectController>();
        MainEffectController effect = newObject.AddComponent<MainEffectController>();
        effect._mainEffectContainer = parentEffect._mainEffectContainer;
        effect._fadeValue = parentEffect._fadeValue;
        var data = newObject.AddComponent<UniversalAdditionalCameraData>();
        data.allowXRRendering = false; data.requiresDepthTexture = true;
        SecondaryCameraController result =
            _instantiator.InstantiateComponent<SecondaryCameraController>(newObject, [this]);
        return result;
    }

    [UsedImplicitly]
    [Inject]
    private void Construct(SiraLog log, IInstantiator instantiator)
    {
        _log = log;
        _instantiator = instantiator;
    }

    private void Awake()
    {

        foreach (PostProcessingOrder key in Enum.GetValues(typeof(PostProcessingOrder)))
        {
            if (!Effects.ContainsKey(key))
            {
                Effects[key] = [];
            }
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_resolvedInput != null) { _resolvedInput.Release(); Destroy(_resolvedInput); }
        foreach (RenderTexture texture in _declaredTextures.Values.SelectMany(n => n.Textures.Values))
        {
            if (texture != null)
            {
                texture.Release();
            }
        }

        _declaredTextures.Clear();

        foreach (CullingCameraController cullingCameraController in _cullingCameraControllers.Values.Concat(
                     _disabledCullingCameraControllers))
        {
            if (cullingCameraController is SecondaryCameraController)
            {
                Destroy(cullingCameraController.gameObject);
            }
        }

        _cullingCameraControllers.Clear();
        _disabledCullingCameraControllers.Clear();
    }
}

internal readonly record struct MaterialData : IComparable<MaterialData>
{
    internal MaterialData(
        Material? material,
        int priority,
        string? source,
        string[]? targets,
        int? pass,
        int? frame = null)
    {
        Material = material;
        Priority = priority;
        Source = source ?? CAMERA_TARGET;
        Targets = targets ?? [CAMERA_TARGET];
        Pass = pass ?? -1;
        Frame = frame;
    }

    internal int? Frame { get; }

    internal Material? Material { get; }

    internal int Pass { get; }

    internal int Priority { get; }

    internal string Source { get; }

    internal string[] Targets { get; }

    public int CompareTo(MaterialData other)
    {
        int result = Priority.CompareTo(other.Priority);
        return result < 0 ? -1 : 1;
    }
}
