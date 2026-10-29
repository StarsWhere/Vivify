using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using CustomJSONData.CustomBeatmap;
using Heck;
using JetBrains.Annotations;
using SiraUtil.Logging;
using UnityEngine;
using static Vivify.VivifyController;
using Object = UnityEngine.Object;

namespace Vivify.Managers;

internal class AssetBundleManager : IDisposable
{
    private readonly Dictionary<string, Object> _assets = new();
    private readonly SiraLog _log;
    private readonly AssetBundle? _mainBundle;
    private bool _disposed;
    internal string[] GrabTextures { get; } = Array.Empty<string>();
    internal AssetBundleCompatibility.GrabShader[] GrabShaders { get; } = Array.Empty<AssetBundleCompatibility.GrabShader>();

    [UsedImplicitly]
    private AssetBundleManager(
        SiraLog log,
        IReadonlyBeatmapData beatmapData,
#if !PRE_V1_37_1
        BeatmapLevel beatmapLevel,
#else
        IDifficultyBeatmap difficultyBeatmap,
#endif
        Config config)
    {
#if !PRE_V1_37_1
        if (beatmapLevel.previewMediaData is not FileSystemPreviewMediaData fileSystemPreviewMediaData)
        {
            throw new ArgumentException(
                $"Was not correct type. Expected: {nameof(FileSystemPreviewMediaData)}, was: {beatmapLevel.previewMediaData.GetType().Name}.",
                nameof(beatmapLevel));
        }
#else
        if (difficultyBeatmap is not CustomDifficultyBeatmap customDifficultyBeatmap)
        {
            throw new ArgumentException(
                $"Was not correct type. Expected: {nameof(CustomDifficultyBeatmap)}, was: {difficultyBeatmap.GetType().Name}.",
                nameof(difficultyBeatmap));
        }
#endif

        if (beatmapData is not CustomBeatmapData customBeatmapData)
        {
            throw new ArgumentException(
                $"Was not correct type. Expected: {nameof(CustomBeatmapData)}, was: {beatmapData.GetType().Name}.",
                nameof(beatmapData));
        }

        _log = log;

#if !PRE_V1_37_1
        string path = Path.Combine(
            Path.GetDirectoryName(fileSystemPreviewMediaData._previewAudioClipPath)!,
            BUNDLE_FILE);
#else
        string path = Path.Combine(
            ((CustomBeatmapLevel)customDifficultyBeatmap.level).customLevelPath,
            BUNDLE_FILE);
#endif
        if (!File.Exists(path))
        {
            _log.Error($"[{BUNDLE_FILE}] not found");
            return;
        }

        CustomData levelCustomData = customBeatmapData.levelCustomData;
        uint? assetBundleChecksum = levelCustomData.Get<CustomData>(ASSET_BUNDLE)?.Get<uint>(BUNDLE_CHECKSUM);
        if (HeckController.DebugMode && (!assetBundleChecksum.HasValue || !AssetBundleCompatibility.HasOverride(assetBundleChecksum.Value)))
        {
            _mainBundle = AssetBundle.LoadFromFile(path);
        }
        else
        {
            if (assetBundleChecksum != null)
            {
                _mainBundle = AssetBundleCompatibility.LoadWithMetadata(path, assetBundleChecksum.Value, out string[] grabTextures, out var grabShaders);
                GrabTextures = grabTextures;
                GrabShaders = grabShaders;
            }
            else
            {
                _log.Error("Checksum not defined");
            }
        }

        if (_mainBundle == null)
        {
            _log.Error($"Failed to load [{path}]");
            return;
        }

        string[] assetnames = _mainBundle.GetAllAssetNames();
        foreach (string name in assetnames)
        {
            Object asset = _mainBundle.LoadAsset(name);
            _assets.Add(name, asset);
        }

        // Results screens can retain the player context until application shutdown.
        // Detach bundle camera targets before Unity's global asset teardown starts.
        Application.quitting += Dispose;

        /*
         In Version 1.31.0,
         If you have any of the DLC, AssetBundle.UnloadAllAssetBundles(true) is called in
         BeatmapLevelDataLoader.Dispose when switching scenes.
         AssetBundle used by the mod will be unloaded. */
        ////mainBundle.Unload(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_mainBundle != null)
        {
            AssetBundleCompatibility.Unload(_mainBundle);
        }

        _assets.Clear();
        _disposed = true;
        Application.quitting -= Dispose;
    }

    internal bool TryGetAsset<T>(string assetName, [NotNullWhen(true)] out T? asset)
    {
        if (_assets.TryGetValue(assetName, out Object gameObject))
        {
            if (gameObject is T t)
            {
                asset = t;
                return true;
            }

            _log.Error($"Found {assetName}, but was null or not [{typeof(T).FullName}]");
        }
        else
        {
            _log.Error($"Could not find {typeof(T).FullName} [{assetName}]");
        }

        asset = default;
        return false;
    }
}
