using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SiraUtil.Affinity;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using Vivify.PostProcessing;

namespace Vivify.HarmonyPatches;

internal sealed class OrderedGrabPass : IAffinity
{
    private static readonly FieldInfo Filter=AccessTools.Field(typeof(DrawObjectsPass),"m_FilteringSettings");
    private static readonly FieldInfo Opaque=AccessTools.Field(typeof(DrawObjectsPass),"m_IsOpaque");
    private static readonly MethodInfo Render=AccessTools.Method(typeof(DrawObjectsPass),"Render");
    [ThreadStatic] private static bool _splitting;

    [AffinityPrefix]
    [AffinityPatch(typeof(DrawObjectsPass),"Render")]
    private bool Prefix(DrawObjectsPass __instance,object[] __args)
    {
        if(_splitting)return true;
        var plans=CameraEffectApplier.Active?.GrabShaders;
        if(plans==null || plans.Length==0)return true;
        var graph=(RenderGraph)__args[0];var frame=(ContextContainer)__args[1];
        var camera=frame.Get<UniversalCameraData>().camera;
        var filter=(FilteringSettings)Filter.GetValue(__instance);
        var items=LegacyGrabRenderer.Visible(camera,filter,plans);
        int[] queues=items.Where(item=>item.Plan!=null).Select(item=>item.Material.renderQueue).Distinct().OrderBy(q=>q).ToArray();
        if(queues.Length==0)return true;
        var owner=camera.GetComponent<LegacyGrabRenderer>();
        if(owner==null){owner=camera.gameObject.AddComponent<LegacyGrabRenderer>();owner.FirstPass=true;}
        int first=filter.renderQueueRange.lowerBound;
        try
        {
            _splitting=true;
            foreach(int queue in queues)
            {
                Native(first,queue-1);
                owner.Record(graph,frame,(TextureHandle)__args[2],(TextureHandle)__args[3],items.Where(item=>item.Material.renderQueue==queue),(bool)Opaque.GetValue(__instance));
                first=queue+1;
            }
            Native(first,filter.renderQueueRange.upperBound);
        }
        finally{Filter.SetValue(__instance,filter);_splitting=false;}
        return false;

        void Native(int lower,int upper)
        {
            if(lower>upper)return;
            var subset=filter;subset.renderQueueRange=new RenderQueueRange(lower,upper);
            Filter.SetValue(__instance,subset);Render.Invoke(__instance,__args);
        }
    }
}
