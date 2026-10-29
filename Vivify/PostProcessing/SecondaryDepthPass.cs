using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vivify.PostProcessing;

internal sealed class SecondaryDepthPass : ScriptableRenderPass
{
    private readonly SecondaryCameraController _owner;
    private sealed class Data
    {
        internal TextureHandle Source, Destination;
        internal Material Material = null!;
        internal int Width, Height;
    }
    internal SecondaryDepthPass(SecondaryCameraController owner)
    {
        _owner = owner;
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        ConfigureInput(ScriptableRenderPassInput.Depth);
    }
    public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
    {
        if (!_owner.DepthKey.HasValue) return;
        var resources = frame.Get<UniversalResourceData>();
        var source = resources.cameraDepthTexture;
        if (!source.IsValid()) throw new System.InvalidOperationException("Vivify secondary camera did not produce depth.");
        var destination = _owner.GetDepthOutput(frame.Get<UniversalCameraData>().cameraTargetDescriptor);
        using var builder = graph.AddUnsafePass<Data>("Vivify secondary camera depth", out var data);
        data.Source = source; data.Destination = graph.ImportTexture(destination);
        data.Material = _owner.DepthCopyMaterial;
        data.Width = destination.rt.width; data.Height = destination.rt.height;
        builder.UseTexture(data.Source, AccessFlags.Read); builder.UseTexture(data.Destination, AccessFlags.Write);
        builder.AllowPassCulling(false); builder.AllowGlobalStateModification(true);
        builder.SetRenderFunc(static (Data pass, UnsafeGraphContext context) =>
        {
            CommandBuffer command = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            RenderTexture input = ((RTHandle)pass.Source).rt;
            var element = input.graphicsFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.None || UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsDepthFormat(input.graphicsFormat)
                ? RenderTextureSubElement.Depth : RenderTextureSubElement.Color;
            command.SetRenderTarget(pass.Destination); command.SetViewport(new Rect(0, 0, pass.Width, pass.Height));
            command.SetGlobalTexture("_BlitTexture", pass.Source, element);
            command.SetGlobalVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
            command.DrawProcedural(Matrix4x4.identity, pass.Material, 0, MeshTopology.Triangles, 3);
        });
    }
}
