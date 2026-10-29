using System;
using System.IO;
using UnityEditor;

public static class BuildLegacyShaders
{
    public static void Build()
    {
        string output = Environment.GetEnvironmentVariable("VIVIFY_SHADER_OUTPUT");
        Directory.CreateDirectory(output);
        var result = BuildPipeline.BuildAssetBundles(output, new[]{ new AssetBundleBuild {
            assetBundleName="legacyshadowcaster2022",
            assetNames=new[]{"Assets/LegacyShadowCaster.shader"}
        }, new AssetBundleBuild {assetBundleName="legacygrabchecks2022",assetNames=new[]{"Assets/GrabCheckSolid.shader","Assets/GrabCheckRefract.shader","Assets/GrabCheckEye.shader","Assets/GrabCheckOther.shader"}}}, BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (result == null) throw new InvalidOperationException("Shader asset bundle compilation failed.");
        uint crc;
        if (!BuildPipeline.GetCRCForAssetBundle(Path.Combine(output,"legacyshadowcaster2022"),out crc)) throw new InvalidOperationException("Shader bundle CRC failed.");
        File.WriteAllText(Path.Combine(output,"crc.json"),"{\"crc\":"+crc+"}");
    }
}
