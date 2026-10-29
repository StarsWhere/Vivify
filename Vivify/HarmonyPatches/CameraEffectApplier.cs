using System;
using System.Collections.Generic;
using System.Linq;
using CustomJSONData.CustomBeatmap;
using Heck.ReLoad;
using JetBrains.Annotations;
using SiraUtil.Affinity;
using UnityEngine;
using Vivify.PostProcessing;
using Vivify.Managers;
using Zenject;

namespace Vivify.HarmonyPatches;

internal class CameraEffectApplier : IInitializable, IDisposable
{
    private readonly Dictionary<MainEffectController, PostProcessingController> _postProcessingControllers = new();

    private readonly ReLoader? _reLoader;
    private readonly int _prewarmCount;
    private readonly string[] _grabTextures;
    internal AssetBundleCompatibility.GrabShader[] GrabShaders { get; }

    [UsedImplicitly]
    private CameraEffectApplier(
        IReadonlyBeatmapData beatmapData,
        AssetBundleManager assetBundleManager,
        [InjectOptional] ReLoader? reLoader)
    {
        _reLoader = reLoader;
        _grabTextures = assetBundleManager.GrabTextures;
        GrabShaders = assetBundleManager.GrabShaders;
        if (reLoader != null)
        {
            reLoader.Rewinded += OnRewind;
        }

        // TODO: correctly count concurrent created cameras
        _prewarmCount = ((CustomBeatmapData)beatmapData).customEventDatas.Any(
            n => n.eventType == VivifyController.DECLARE_CULLING_TEXTURE)
            ? 1
            : 0;

        foreach (PostProcessingOrder key in Enum.GetValues(typeof(PostProcessingOrder)))
        {
            if (!Effects.ContainsKey(key))
            {
                Effects[key] = [];
            }
        }
    }

    internal Dictionary<string, CreateCameraData> CameraDatas { get; } = new();

    internal Dictionary<string, CreateScreenTextureData> DeclaredTextureDatas { get; } = new();

    internal Dictionary<PostProcessingOrder, List<MaterialData>> Effects { get; } = new();

    internal static CameraEffectApplier? Active { get; private set; }

    public void Initialize() { Active = this; }

    public void Dispose()
    {
        if (ReferenceEquals(Active, this)) Active = null;
        foreach(var renderer in Resources.FindObjectsOfTypeAll<LegacyGrabRenderer>())UnityEngine.Object.Destroy(renderer);
        _postProcessingControllers.Clear();
        Reset();

        if (_reLoader != null)
        {
            _reLoader.Rewinded -= OnRewind;
        }
    }

    private void OnRewind()
    {
        Reset();
    }

    private void Reset()
    {
        CameraDatas.Clear();
        DeclaredTextureDatas.Clear();

        foreach (List<MaterialData>? materials in Effects.Values)
        {
            materials.Clear();
        }
    }

    internal void ConfigureController(PostProcessingController controller)
    {
        var main = controller.GetComponent<MainEffectController>();
        if (_postProcessingControllers.ContainsKey(main)) return;
        _postProcessingControllers[main] = controller;
        controller.CameraDatas = CameraDatas;
        controller.DeclaredTextureDatas = DeclaredTextureDatas;
        controller.Effects = Effects;
        controller.GrabTextures = _grabTextures;
        controller.PrewarmCameras(_prewarmCount);
    }
}
