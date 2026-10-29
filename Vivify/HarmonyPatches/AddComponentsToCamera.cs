using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SiraUtil.Affinity;
using SiraUtil.Logging;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Vivify.Controllers;
using Vivify.PostProcessing;
using Zenject;

namespace Vivify.HarmonyPatches;

internal class AddComponentsToCamera : IAffinity
{
    internal static long Calls { get; private set; }
    private readonly SiraLog _log;
    private readonly DiContainer _container;
    private readonly List<Component> _injected = new();
    private readonly ConditionalWeakTable<Camera, UrpCameraPass[]> _passes = new();
    private readonly ConditionalWeakTable<Camera, SecondaryDepthPass> _depthPasses = new();
    private AddComponentsToCamera(SiraLog log, DiContainer container) { _log = log; _container = container; }

    [AffinityPostfix]
    [AffinityPatch(typeof(MainEffectRendererFeature), nameof(MainEffectRendererFeature.AddRenderPasses))]
    private void AddPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        Calls++;
        Camera camera = renderingData.cameraData.camera;
        if (camera == null || camera.GetComponent<MainEffectController>() == null) return;
        var grabRenderer=camera.GetComponent<LegacyGrabRenderer>();
        if(grabRenderer!=null)grabRenderer.FirstPass=true;
        var secondary = camera.GetComponent<SecondaryCameraController>();
        if (secondary != null)
        {
            if (secondary.DepthKey.HasValue) renderer.EnqueuePass(_depthPasses.GetValue(camera, _ => new SecondaryDepthPass(secondary)));
            return;
        }
        _injected.RemoveAll(component => component == null);
        SafeAddComponent<PostProcessingController>(camera.gameObject);
        SafeAddComponent<CameraPropertyController>(camera.gameObject);
        var processor = camera.GetComponent<PostProcessingController>();
        CameraEffectApplier.Active?.ConfigureController(processor);
        if (!processor.isActiveAndEnabled || !processor.HasWork) return;
        UrpCameraPass[] passes = _passes.GetValue(camera, _ =>
        {
            var result = new UrpCameraPass[8];
            for (int index = 0; index < result.Length; index++) result[index] = new UrpCameraPass(processor, (PostProcessingOrder)index);
            return result;
        });
        foreach (var pass in passes) renderer.EnqueuePass(pass);
    }
    private void SafeAddComponent<T>(GameObject target) where T : Component
    {
        T found = target.GetComponent<T>();
        if (found != null)
        {
            if (!_injected.Contains(found)) { _container.Inject(found); _injected.Add(found); }
            return;
        }
        _injected.Add(_container.InstantiateComponent<T>(target));
    }
}
