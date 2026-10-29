using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Vivify.Managers;

namespace Vivify.PostProcessing;

// Only queues containing legacy GrabPass materials use this path. All other
// queues retain URP's renderer lists, lighting, batching and visibility handling.
internal sealed class LegacyGrabRenderer : MonoBehaviour
{
    internal readonly Dictionary<(int Eye,bool PerObject,string Texture), RenderTexture> Textures = new();
    private readonly HashSet<(int Eye,bool PerObject,string Texture)> _captured = new();
    private static readonly List<Material> SharedMaterials = new();
    private readonly HashSet<int> _properties = new();
    private RenderTexture? _resolved;
    internal bool FirstPass=true;
    private void OnEnable()=>RenderPipelineManager.beginCameraRendering+=BeginCamera;
    private void OnDisable()=>RenderPipelineManager.beginCameraRendering-=BeginCamera;
    private void BeginCamera(ScriptableRenderContext context,Camera camera){if(camera==GetComponent<Camera>())FirstPass=true;}
    internal long Captures { get; private set; }
    internal long Draws { get; private set; }

    internal sealed class Item
    {
        internal Renderer Renderer = null!;
        internal Material Material = null!;
        internal int Submesh;
        internal AssetBundleCompatibility.GrabShader? Plan;
        internal float Distance;
    }
    private sealed class Data
    {
        internal LegacyGrabRenderer Owner = null!;
        internal Item[] Items = null!;
        internal UniversalCameraData Camera = null!;
        internal TextureHandle Color, Depth;
        internal RenderTextureDescriptor Descriptor;
        internal bool Reset;
    }

    internal static List<Item> Visible(Camera camera, FilteringSettings filter,
        AssetBundleCompatibility.GrabShader[] plans)
    {
        var result = new List<Item>();
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
        Renderer[] renderers=UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        var queues=new HashSet<int>();
        try
        {
            foreach(Renderer renderer in renderers)
            {
                if(!VisibleRenderer(renderer))continue;
                renderer.GetSharedMaterials(SharedMaterials);
                foreach(Material material in SharedMaterials)
                    if(InRange(material) && Plan(material)!=null)queues.Add(material.renderQueue);
            }
            if(queues.Count==0)return result;
            foreach(Renderer renderer in renderers)
            {
                if(!VisibleRenderer(renderer))continue;
                renderer.GetSharedMaterials(SharedMaterials);
                for(int index=0;index<SharedMaterials.Count;index++)
                {
                    Material material=SharedMaterials[index];
                    if(material==null || !queues.Contains(material.renderQueue))continue;
                    result.Add(new Item { Renderer=renderer, Material=material, Submesh=index, Plan=Plan(material),
                        Distance=camera.orthographic
                            ? Vector3.Dot(renderer.bounds.center-camera.transform.position,camera.transform.forward)
                            : (renderer.bounds.center-camera.transform.position).sqrMagnitude });
                }
            }
            return result;
        }
        finally{SharedMaterials.Clear();}

        bool VisibleRenderer(Renderer renderer)
        {
            if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly ||
                ((1 << renderer.gameObject.layer) & camera.cullingMask & filter.layerMask) == 0 ||
                (renderer.renderingLayerMask & filter.renderingLayerMask) == 0 ||
                !GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) return false;
            int layer=UnityEngine.SortingLayer.GetLayerValueFromID(renderer.sortingLayerID);
            return layer>=filter.sortingLayerRange.lowerBound && layer<=filter.sortingLayerRange.upperBound;
        }
        bool InRange(Material material)=>material!=null && material.renderQueue>=filter.renderQueueRange.lowerBound && material.renderQueue<=filter.renderQueueRange.upperBound;
        AssetBundleCompatibility.GrabShader? Plan(Material material)
        {
            foreach(var plan in plans)if(plan.Asset==material.shader)return plan;
            return null;
        }
    }

    internal void Record(RenderGraph graph, ContextContainer frame, TextureHandle color,
        TextureHandle depth, IEnumerable<Item> items, bool opaque)
    {
        using var builder = graph.AddUnsafePass<Data>("Vivify ordered legacy grab", out var data);
        data.Owner=this; data.Camera=frame.Get<UniversalCameraData>(); data.Color=color; data.Depth=depth;
        data.Reset=FirstPass; FirstPass=false;
        data.Items=items.OrderBy(item => UnityEngine.SortingLayer.GetLayerValueFromID(item.Renderer.sortingLayerID))
            .ThenBy(item => item.Renderer.sortingOrder).ThenBy(item => item.Material.renderQueue)
            .ThenBy(item => opaque ? item.Distance : -item.Distance)
            .ThenBy(item => item.Renderer.rendererPriority).ThenBy(item => item.Renderer.GetInstanceID())
            .ThenBy(item => item.Submesh).ToArray();
        var texture = graph.GetTextureDesc(color);
        data.Descriptor=data.Camera.cameraTargetDescriptor;
        data.Descriptor.width=texture.width; data.Descriptor.height=texture.height;
        data.Descriptor.graphicsFormat=texture.colorFormat; data.Descriptor.msaaSamples=(int)texture.msaaSamples;
        data.Descriptor.dimension=texture.dimension; data.Descriptor.volumeDepth=texture.slices;
        builder.UseTexture(color,AccessFlags.ReadWrite);
        if(depth.IsValid())builder.UseTexture(depth,AccessFlags.ReadWrite);
        builder.UseAllGlobalTextures(true); builder.AllowPassCulling(false); builder.AllowGlobalStateModification(true);
        builder.SetRenderFunc(static (Data pass, UnsafeGraphContext context) =>
        {
            if(pass.Owner != null)pass.Owner.Draw(CommandBufferHelpers.GetNativeCommandBuffer(context.cmd),pass);
        });
    }

    private void Draw(CommandBuffer command, Data data)
    {
        if(data.Reset)_captured.Clear();
        bool array=data.Descriptor.dimension==TextureDimension.Tex2DArray;
        int eyes=array?data.Descriptor.volumeDepth:1;
        string[] keywords={"STEREO_INSTANCING_ON","STEREO_MULTIVIEW_ON","UNITY_SINGLE_PASS_STEREO","MULTIPASS_ENABLED"};
        bool[] enabled=keywords.Select(Shader.IsKeywordEnabled).ToArray();
        int previousEye=Shader.GetGlobalInt("_StereoActiveEye");
        Vector4 previousPosition=Shader.GetGlobalVector("_WorldSpaceCameraPos");
        if(array)
        {
            foreach(string keyword in keywords)command.DisableShaderKeyword(keyword);
            command.EnableShaderKeyword("MULTIPASS_ENABLED");
        }
        for(int eye=0;eye<eyes;eye++)
        {
            command.SetGlobalInt("_StereoActiveEye",array?eye:(int)data.Camera.camera.stereoActiveEye);
            Matrix4x4 view=data.Camera.GetViewMatrix(array?eye:0);
            Matrix4x4 projection=GL.GetGPUProjectionMatrix(data.Camera.GetProjectionMatrix(array?eye:0),true);
            foreach(Item item in data.Items)
            {
                if(item.Renderer==null || item.Material==null)continue;
                if(item.Plan != null)
                {
                    foreach(var step in item.Plan.steps)
                    {
                        if(step.passIndex<0)
                        {
                            var key=(eye,step.perObject,step.texture);
                            if(step.perObject || _captured.Add(key))Capture(command,data,eye,key,step.texture);
                            else Bind(command,step.texture,Textures[key]);
                        }
                        else DrawPass(item,step.passIndex);
                    }
                }
                else
                {
                    int pass=ForwardPass(item.Material);
                    if(pass>=0)DrawPass(item,pass);
                }
            }
            void DrawPass(Item item,int pass)
            {
                if(pass>=item.Material.passCount)throw new InvalidOperationException("Legacy shader pass missing: "+item.Material.shader.name+" / "+pass);
                var color=new RenderTargetIdentifier((RenderTargetIdentifier)(RTHandle)data.Color,0,CubemapFace.Unknown,array?eye:-1);
                if(data.Depth.IsValid())command.SetRenderTarget(color,new RenderTargetIdentifier((RenderTargetIdentifier)(RTHandle)data.Depth,0,CubemapFace.Unknown,array?eye:-1));
                else command.SetRenderTarget(color);
                command.SetViewport(new Rect(0,0,data.Descriptor.width,data.Descriptor.height));
                command.SetViewProjectionMatrices(view,projection);
                command.SetGlobalMatrix("unity_MatrixVP",projection*view);
                command.SetGlobalVector("_WorldSpaceCameraPos",view.inverse.GetColumn(3));
                command.DrawRenderer(item.Renderer,item.Material,item.Submesh,pass); Draws++;
            }
        }
        if(array)for(int i=0;i<keywords.Length;i++)
            if(enabled[i])command.EnableShaderKeyword(keywords[i]);else command.DisableShaderKeyword(keywords[i]);
        command.SetGlobalInt("_StereoActiveEye",previousEye);
        command.SetGlobalVector("_WorldSpaceCameraPos",previousPosition);
        command.SetViewProjectionMatrices(data.Camera.GetViewMatrix(),GL.GetGPUProjectionMatrix(data.Camera.GetProjectionMatrix(),true));
        command.SetGlobalMatrix("unity_MatrixVP",GL.GetGPUProjectionMatrix(data.Camera.GetProjectionMatrix(),true)*data.Camera.GetViewMatrix());
    }

    private void Capture(CommandBuffer command,Data data,int eye,(int Eye,bool PerObject,string Texture) key,string name)
    {
        var descriptor=data.Descriptor;
        descriptor.depthBufferBits=0; descriptor.depthStencilFormat=UnityEngine.Experimental.Rendering.GraphicsFormat.None;
        descriptor.msaaSamples=1; descriptor.bindMS=false;
        RenderTargetIdentifier source=data.Color;
        if(data.Descriptor.msaaSamples>1)
        {
            if(_resolved==null || _resolved.width!=descriptor.width || _resolved.height!=descriptor.height ||
                _resolved.dimension!=descriptor.dimension || _resolved.volumeDepth!=descriptor.volumeDepth || _resolved.graphicsFormat!=descriptor.graphicsFormat)
            {
                Release(_resolved); _resolved=new RenderTexture(descriptor){name="Vivify grab resolve"}; _resolved.Create();
            }
            command.ResolveAntiAliasedSurface(((RTHandle)data.Color).rt,_resolved);source=_resolved;
        }
        descriptor.dimension=TextureDimension.Tex2D; descriptor.volumeDepth=1; descriptor.vrUsage=VRTextureUsage.None;
        if(!Textures.TryGetValue(key,out RenderTexture texture) || texture.width!=descriptor.width || texture.height!=descriptor.height || texture.graphicsFormat!=descriptor.graphicsFormat)
        {
            Release(texture);texture=new RenderTexture(descriptor){name="Vivify ordered grab "+key};texture.Create();Textures[key]=texture;
        }
        if(data.Descriptor.dimension==TextureDimension.Tex2DArray)command.CopyTexture(source,eye,0,new RenderTargetIdentifier(texture),0,0);
        else command.Blit(source,texture);
        Bind(command,name,texture);
        Captures++;
    }

    private void Bind(CommandBuffer command,string name,RenderTexture texture)
    {
        int property=Shader.PropertyToID(name);_properties.Add(property);
        command.SetGlobalTexture(property,texture);
        command.SetGlobalVector(Shader.PropertyToID(name+"_TexelSize"),new Vector4(1f/texture.width,1f/texture.height,texture.width,texture.height));
    }

    private static int ForwardPass(Material material)
    {
        foreach(string mode in new[]{"SRPDefaultUnlit","UniversalForward","UniversalForwardOnly"})
            for(int pass=0;pass<material.passCount;pass++)
            {
                string tag=material.shader.FindPassTagValue(pass,new ShaderTagId("LightMode")).name;
                if(String.Equals(tag,mode,StringComparison.OrdinalIgnoreCase) ||
                    (mode=="SRPDefaultUnlit" && String.IsNullOrEmpty(tag)))return pass;
            }
        return -1;
    }
    private static void Release(RenderTexture? texture){if(texture==null)return;texture.Release();Destroy(texture);}
    private void OnDestroy()
    {
        foreach(var pair in Textures)
        {
            foreach(int property in _properties)
                if(Shader.GetGlobalTexture(property)==pair.Value)Shader.SetGlobalTexture(property,null);
            Release(pair.Value);
        }
        Textures.Clear();Release(_resolved);
    }
}
