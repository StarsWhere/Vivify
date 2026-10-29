using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace Vivify.Managers;

internal static class AssetBundleCompatibility
{
    private static string DirectoryPath => Path.Combine(Path.GetDirectoryName(Application.dataPath)!,"UserData","Vivify","Compatibility");
    internal static bool HasOverride(uint checksum)=>File.Exists(Path.Combine(DirectoryPath,checksum+".vivify"));
    [Serializable]
    private sealed class Manifest
    {
        public string gameBuild = "";
        public uint originalChecksum;
        public string sourceSha256 = "";
        public string outputSha256 = "";
        public string[] grabTextures = Array.Empty<string>();
        public GrabShader[] grabShaders = Array.Empty<GrabShader>();
    }
    internal static AssetBundle Load(string path, uint checksum)
        => LoadWithMetadata(path, checksum, out _);
    internal static AssetBundle LoadWithMetadata(string path, uint checksum, out string[] grabTextures)
        => LoadWithMetadata(path, checksum, out grabTextures, out _);
    internal static AssetBundle LoadWithMetadata(string path, uint checksum, out string[] grabTextures, out GrabShader[] grabShaders)
    {
        grabTextures = Array.Empty<string>();
        grabShaders = Array.Empty<GrabShader>();
        AssetBundle original = AssetBundle.LoadFromFile(path, checksum)
            ?? throw new InvalidDataException("Vivify asset bundle failed CRC validation: " + path);
        string directory = DirectoryPath;
        string converted = Path.Combine(directory, checksum + ".vivify");
        string metadata = Path.Combine(directory, checksum + ".json");
        if (!File.Exists(converted)) return original;
        original.Unload(true);
        Manifest manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(metadata))!;
        if (manifest == null || manifest.gameBuild != "1.45.2_28848" || manifest.originalChecksum != checksum ||
            manifest.sourceSha256 != Hash(path) || manifest.outputSha256 != Hash(converted))
            throw new InvalidDataException("Vivify compatibility bundle fingerprint mismatch: " + converted);
        grabTextures = manifest.grabTextures ?? Array.Empty<string>();
        grabShaders = manifest.grabShaders ?? Array.Empty<GrabShader>();
        if (grabTextures.Length > 0 && grabShaders.Length == 0)
            throw new InvalidDataException("Vivify grab metadata must be rebuilt for this source port: " + metadata);
        var textureNames=new HashSet<string>(grabTextures);
        if(grabShaders.Select(plan=>plan.shader).Distinct().Count()!=grabShaders.Length ||
            grabShaders.Any(plan=>String.IsNullOrWhiteSpace(plan.shader) || plan.steps.Length==0 ||
                plan.steps.Any(step=>step.passIndex < -1 || (step.passIndex==-1 && !textureNames.Contains(step.texture)))))
            throw new InvalidDataException("Invalid ordered grab shader metadata: "+metadata);
        AssetBundle result=AssetBundle.LoadFromFile(converted)
            ?? throw new InvalidDataException("Vivify compatibility bundle could not be loaded: " + converted);
        try
        {
            var assets=result.LoadAllAssets();
            Shader[] shaders=assets.OfType<Shader>()
                .Concat(assets.OfType<Material>().Select(material=>material.shader))
                .Concat(assets.OfType<GameObject>().SelectMany(root=>root.GetComponentsInChildren<Renderer>(true))
                    .SelectMany(renderer=>renderer.sharedMaterials).Where(material=>material!=null).Select(material=>material.shader))
                .Distinct().ToArray();
            foreach(var plan in grabShaders)
            {
                plan.Asset=shaders.SingleOrDefault(shader=>shader.name==plan.shader)
                    ?? throw new InvalidDataException("Grab shader missing from bundle: "+plan.shader);
                if(plan.steps.Any(step=>step.passIndex>=plan.Asset.passCount))
                    throw new InvalidDataException("Grab shader pass index out of range: "+plan.shader);
            }
            return result;
        }
        catch { Unload(result); throw; }
    }

    [Serializable] internal sealed class GrabShader
    {
        public string shader = "";
        public GrabStep[] steps = Array.Empty<GrabStep>();
        [NonSerialized] internal Shader? Asset;
    }
    [Serializable] internal sealed class GrabStep
    {
        public int passIndex;
        public string texture = "";
        public bool perObject;
    }
    private static string Hash(string path)
    {
        using var hash = SHA256.Create(); using var stream = File.OpenRead(path);
        return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    internal static void Unload(AssetBundle bundle)
    {
        var targets = new HashSet<RenderTexture>(bundle.LoadAllAssets<RenderTexture>());
        foreach (var target in bundle.LoadAllAssets<GameObject>().SelectMany(root => root.GetComponentsInChildren<Camera>(true)).Select(camera => camera.targetTexture))
            if (target != null) targets.Add(target);
        foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
            if (camera.targetTexture != null && targets.Contains(camera.targetTexture))
            {
                camera.enabled = false;
                camera.targetTexture = null;
            }
        bundle.Unload(true);
    }
}
