using UnityEngine;
using UnityEngine.Rendering;

namespace Vivify.PostProcessing;

// Legacy prefab cameras use a depth-only render texture for projected shadows.
// URP's normal camera path binds that texture as a color attachment. Draw the
// authored material passes directly into its depth attachment instead.
[RequireComponent(typeof(Camera))]
internal sealed class DepthOnlyCameraController : MonoBehaviour
{
    private Camera _camera = null!;
    private bool _wasEnabled;
    private RenderTexture? _colorAttachment;
    internal long RenderCount { get; private set; }
    internal int LastDrawCount { get; private set; }

    private void OnEnable()
    {
        _camera = GetComponent<Camera>();
        _wasEnabled = _camera.enabled;
        _camera.enabled = false;
    }
    private void OnDisable() { if (_camera != null) _camera.enabled = _wasEnabled; }
    private void OnDestroy() { if (_colorAttachment != null) { _colorAttachment.Release(); Destroy(_colorAttachment); } }
    private void LateUpdate()
    {
        if (_camera.enabled) { _wasEnabled = true; _camera.enabled = false; }
        if (_wasEnabled) RenderDepth();
    }
    internal void RenderDepth()
    {
        RenderTexture target = _camera.targetTexture;
        if (target == null) return;
        if ((target.graphicsFormat != UnityEngine.Experimental.Rendering.GraphicsFormat.None && !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsDepthFormat(target.graphicsFormat)) || target.depthStencilFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.None)
            throw new System.InvalidOperationException("Vivify depth camera has an unexpected render texture: " + target.name);
        var planes = GeometryUtility.CalculateFrustumPlanes(_camera);
        if (_colorAttachment == null || _colorAttachment.width != target.width || _colorAttachment.height != target.height)
        {
            if (_colorAttachment != null) { _colorAttachment.Release(); Destroy(_colorAttachment); }
            _colorAttachment = new RenderTexture(target.width, target.height, 0, RenderTextureFormat.ARGB32) { name = "Vivify depth camera color attachment" };
            _colorAttachment.Create();
        }
        LastDrawCount = 0;
        var command = new CommandBuffer { name = "Vivify prefab depth camera" };
        try
        {
            command.SetRenderTarget(new RenderTargetIdentifier(_colorAttachment), new RenderTargetIdentifier(target));
            command.SetViewport(new Rect(0, 0, target.width, target.height));
            if (_camera.clearFlags != CameraClearFlags.Nothing)
                command.ClearRenderTarget(true, false, Color.clear);
            command.SetViewProjectionMatrices(_camera.worldToCameraMatrix, GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true));
            command.SetGlobalMatrix("unity_MatrixVP", GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true) * _camera.worldToCameraMatrix);
            command.SetGlobalVector("_WorldSpaceCameraPos", _camera.transform.position);
            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || (_camera.cullingMask & (1 << renderer.gameObject.layer)) == 0 ||
                    !GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) continue;
                Material[] materials = renderer.sharedMaterials;
                for (int index = 0; index < materials.Length; index++)
                {
                    Material material = materials[index];
                    if (material == null || material.passCount == 0) continue;
                    // The original camera wrote geometry depth from the first
                    // authored pass, including its deformation and cutouts.
                    command.DrawRenderer(renderer, material, index, 0);
                    LastDrawCount++;
                }
            }
            command.SetViewProjectionMatrices(Shader.GetGlobalMatrix("unity_MatrixV"), Shader.GetGlobalMatrix("unity_MatrixP"));
            command.SetGlobalVector("_WorldSpaceCameraPos", Shader.GetGlobalVector("_WorldSpaceCameraPos"));
            Graphics.ExecuteCommandBuffer(command);
            RenderCount++;
        }
        finally { command.Release(); }
    }
}
