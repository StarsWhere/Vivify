using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vivify.PostProcessing;

internal sealed class UrpCameraPass : ScriptableRenderPass
{
    private readonly PostProcessingController _owner;
    private readonly PostProcessingOrder _order;
    private sealed class PassData
    {
        internal PostProcessingController Owner = null!;
        internal PostProcessingOrder Order;
        internal TextureHandle Color;
        internal RenderTextureDescriptor Descriptor;
    }
    internal UrpCameraPass(PostProcessingController owner, PostProcessingOrder order)
    {
        _owner = owner;
        _order = order;
        requiresIntermediateTexture = true;
        // Request depth after opaques. Requesting it before opaques forces the
        // game's depth prepass to bind D32 as a color attachment in 1.45.2.
        ConfigureInput(ScriptableRenderPassInput.Color | (order == PostProcessingOrder.AfterOpaque ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None));
        renderPassEvent = order switch
        {
            PostProcessingOrder.BeforeOpaque => RenderPassEvent.BeforeRenderingOpaques,
            PostProcessingOrder.AfterOpaque => RenderPassEvent.AfterRenderingOpaques,
            PostProcessingOrder.BeforeSkybox => RenderPassEvent.BeforeRenderingSkybox,
            PostProcessingOrder.AfterSkybox => RenderPassEvent.AfterRenderingSkybox,
            PostProcessingOrder.BeforeAlpha => RenderPassEvent.BeforeRenderingTransparents,
            PostProcessingOrder.AfterAlpha => RenderPassEvent.AfterRenderingTransparents,
            PostProcessingOrder.BeforeMainEffect => (RenderPassEvent)549,
            PostProcessingOrder.AfterMainEffect => (RenderPassEvent)551,
            _ => throw new System.ArgumentOutOfRangeException(nameof(order))
        };
    }
    public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
    {
        if (_owner == null || !_owner.isActiveAndEnabled) return;
        var resources = frame.Get<UniversalResourceData>();
        var camera = frame.Get<UniversalCameraData>();
        using var builder = graph.AddUnsafePass<PassData>("Vivify " + _order, out var data);
        data.Owner = _owner; data.Order = _order;
        data.Color = resources.activeColorTexture; data.Descriptor = camera.cameraTargetDescriptor;
        var texture = graph.GetTextureDesc(data.Color);
        if (UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsDepthFormat(texture.colorFormat))
            throw new System.InvalidOperationException($"Vivify color pass received a depth attachment: {camera.camera.name}, {_order}, {texture.colorFormat}");
        data.Descriptor.width = texture.width; data.Descriptor.height = texture.height;
        data.Descriptor.dimension = texture.dimension; data.Descriptor.volumeDepth = texture.slices;
        data.Descriptor.graphicsFormat = texture.colorFormat; data.Descriptor.msaaSamples = (int)texture.msaaSamples;
        builder.UseTexture(data.Color, AccessFlags.ReadWrite);
        if (renderPassEvent >= RenderPassEvent.AfterRenderingOpaques && resources.cameraDepthTexture.IsValid()) builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
        builder.AllowPassCulling(false);
        builder.AllowGlobalStateModification(true);
        builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext context) =>
        {
            if (pass.Owner == null) return;
            CommandBuffer command = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            pass.Owner.RenderUrp(command, pass.Descriptor, pass.Color, pass.Order, ((RTHandle)pass.Color).rt);
        });
    }
}
