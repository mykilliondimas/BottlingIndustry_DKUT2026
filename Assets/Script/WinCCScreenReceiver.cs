using System;
using System.IO.MemoryMappedFiles;
using UnityEngine;
using Debug = UnityEngine.Debug; // Resolves System.Diagnostics namespace collision

public class WinCCScreenReceiver : MonoBehaviour
{
    public enum ImageRotation
    {
        Auto,
        None,
        Clockwise90,
        HalfTurn,
        CounterClockwise90
    }

    [Header("Shared Memory Name")]
    public string shmName = "PLCSimUnity_SharedMemory";

    [Header("Target Renderer")]
    public Renderer screenRenderer;

    [Header("Screen Resolution")]
    public int textureWidth = 1920;
    public int textureHeight = 1080;
    public ImageRotation imageRotation = ImageRotation.Auto;

    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _view;
    private Texture2D _screenTexture;
    private byte[] _pixelBuffer;
    private int _lastFrameSequence = -1;
    private GameObject _screenOverlay;
    private Mesh _overlayMesh;
    private Material _overlayMaterial;

    private const int FrameSequenceOffset = 104;
    private const int FrameDataOffset = 256;

    void Start()
    {
        if (screenRenderer == null)
            screenRenderer = GetComponent<Renderer>();

        if (textureWidth <= 0 || textureHeight <= 0)
        {
            Debug.LogError("[WinCCScreenReceiver] Texture dimensions must be positive.", this);
            enabled = false;
            return;
        }

        _screenTexture = new Texture2D(textureWidth, textureHeight, TextureFormat.BGRA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixelBuffer = new byte[textureWidth * textureHeight * 4];

        if (screenRenderer == null)
            Debug.LogError("[WinCCScreenReceiver] Assign the display panel Renderer.", this);
        else
            ConfigureScreenSurface();

        ConnectSharedMemory();
    }

    private void ConfigureScreenSurface()
    {
        MeshFilter meshFilter = screenRenderer.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            Debug.LogError("[WinCCScreenReceiver] The display Renderer needs a MeshFilter with a mesh to determine its size and orientation.", this);
            return;
        }

        Bounds bounds = meshFilter.sharedMesh.bounds;
        Vector3 size = bounds.size;
        Transform panelTransform = screenRenderer.transform;
        float[] worldSizes = new float[3];
        for (int axis = 0; axis < 3; axis++)
            worldSizes[axis] = AxisComponent(size, axis) * panelTransform.TransformVector(AxisVector(axis)).magnitude;

        int normalAxis = 0;
        for (int axis = 1; axis < 3; axis++)
        {
            if (worldSizes[axis] < worldSizes[normalAxis])
                normalAxis = axis;
        }

        int firstPlaneAxis = (normalAxis + 1) % 3;
        int secondPlaneAxis = (normalAxis + 2) % 3;
        Vector3 firstDirection = panelTransform.TransformVector(AxisVector(firstPlaneAxis)).normalized;
        Vector3 secondDirection = panelTransform.TransformVector(AxisVector(secondPlaneAxis)).normalized;
        int verticalAxis = Mathf.Abs(Vector3.Dot(firstDirection, Vector3.up)) >= Mathf.Abs(Vector3.Dot(secondDirection, Vector3.up))
            ? firstPlaneAxis
            : secondPlaneAxis;
        int horizontalAxis = verticalAxis == firstPlaneAxis ? secondPlaneAxis : firstPlaneAxis;

        Vector3 horizontalDirection = panelTransform.TransformVector(AxisVector(horizontalAxis)).normalized;
        Vector3 verticalDirection = panelTransform.TransformVector(AxisVector(verticalAxis)).normalized;
        Vector3 planeNormal = Vector3.Cross(horizontalDirection, verticalDirection).normalized;
        Vector3 expectedRight = Vector3.ProjectOnPlane(Vector3.right, planeNormal).normalized;
        if (expectedRight.sqrMagnitude < 0.001f)
            expectedRight = Vector3.ProjectOnPlane(Vector3.forward, planeNormal).normalized;
        Vector3 expectedUp = Vector3.ProjectOnPlane(Vector3.up, planeNormal).normalized;
        float horizontalSign = Vector3.Dot(horizontalDirection, expectedRight) < 0f ? -1f : 1f;
        float verticalSign = expectedUp.sqrMagnitude > 0.001f && Vector3.Dot(verticalDirection, expectedUp) < 0f ? -1f : 1f;

        float panelWidth = worldSizes[horizontalAxis];
        float panelHeight = worldSizes[verticalAxis];
        if (panelWidth <= 0f || panelHeight <= 0f)
        {
            Debug.LogError("[WinCCScreenReceiver] Could not determine the panel's display plane.", this);
            return;
        }

        float rotation = ResolveRotation(panelWidth / panelHeight);
        float imageAspect = (float)textureWidth / textureHeight;
        if (rotation == 1f || rotation == 3f)
            imageAspect = 1f / imageAspect;

        float imageHeight = Mathf.Min(panelHeight, panelWidth / imageAspect);
        float imageWidth = imageHeight * imageAspect;
        float localWidth = imageWidth / panelTransform.TransformVector(AxisVector(horizontalAxis)).magnitude;
        float localHeight = imageHeight / panelTransform.TransformVector(AxisVector(verticalAxis)).magnitude;

        Vector3 localCenter = bounds.center;
        Vector3 worldAxisNormal = panelTransform.TransformVector(AxisVector(normalAxis)).normalized;
        Vector3 worldCenter = panelTransform.TransformPoint(bounds.center);
        Camera viewCamera = Camera.main;
        Vector3 viewDirection = viewCamera != null
            ? viewCamera.transform.position - worldCenter
            : panelTransform.forward;
        float faceSign = Vector3.Dot(worldAxisNormal, viewDirection) >= 0f ? 1f : -1f;
        localCenter[normalAxis] += faceSign * (AxisComponent(size, normalAxis) * 0.5f + 0.002f);

        Vector3 horizontal = AxisVector(horizontalAxis) * horizontalSign * (localWidth * 0.5f);
        Vector3 vertical = AxisVector(verticalAxis) * verticalSign * (localHeight * 0.5f);
        Vector3[] vertices =
        {
            localCenter - horizontal - vertical,
            localCenter + horizontal - vertical,
            localCenter + horizontal + vertical,
            localCenter - horizontal + vertical
        };

        Vector2[] uvs = { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        for (int i = 0; i < uvs.Length; i++)
        {
            Vector2 uv = uvs[i];
            if (rotation == 1f)
                uv = new Vector2(uv.y, 1f - uv.x);
            else if (rotation == 2f)
                uv = Vector2.one - uv;
            else if (rotation == 3f)
                uv = new Vector2(1f - uv.y, uv.x);
            uv.y = 1f - uv.y;
            uvs[i] = uv;
        }

        _overlayMesh = new Mesh { name = "WinCC Screen Overlay" };
        _overlayMesh.vertices = vertices;
        _overlayMesh.uv = uvs;
        bool reverseWinding = Vector3.Dot(Vector3.Cross(horizontalDirection * horizontalSign, verticalDirection * verticalSign), viewDirection) < 0f;
        _overlayMesh.triangles = reverseWinding
            ? new[] { 0, 2, 1, 0, 3, 2 }
            : new[] { 0, 1, 2, 0, 2, 3 };
        _overlayMesh.RecalculateBounds();
        _overlayMesh.RecalculateNormals();

        _screenOverlay = new GameObject("WinCC Screen Overlay");
        _screenOverlay.transform.SetParent(panelTransform, false);
        _screenOverlay.AddComponent<MeshFilter>().sharedMesh = _overlayMesh;
        MeshRenderer overlayRenderer = _screenOverlay.AddComponent<MeshRenderer>();
        overlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        overlayRenderer.receiveShadows = false;
        overlayRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        overlayRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null)
            unlitShader = Shader.Find("Unlit/Texture");
        if (unlitShader == null)
        {
            Debug.LogError("[WinCCScreenReceiver] Could not find a supported unlit texture shader.", this);
            return;
        }

        _overlayMaterial = new Material(unlitShader) { name = "WinCC Screen (Runtime)" };
        _overlayMaterial.mainTexture = _screenTexture;
        _overlayMaterial.color = Color.white;
        overlayRenderer.sharedMaterial = _overlayMaterial;
        Debug.Log($"[WinCCScreenReceiver] Panel measured at {panelWidth:F3} x {panelHeight:F3} world units; fitted display {imageWidth:F3} x {imageHeight:F3}; rotation {(int)rotation}.", this);
    }

    private float ResolveRotation(float panelAspect)
    {
        if (imageRotation == ImageRotation.Auto)
            return panelAspect < 1f ? 1f : 0f;

        switch (imageRotation)
        {
            case ImageRotation.Clockwise90: return 1f;
            case ImageRotation.HalfTurn: return 2f;
            case ImageRotation.CounterClockwise90: return 3f;
            default: return 0f;
        }
    }

    private static Vector3 AxisVector(int axis)
    {
        switch (axis)
        {
            case 0: return Vector3.right;
            case 1: return Vector3.up;
            default: return Vector3.forward;
        }
    }

    private static float AxisComponent(Vector3 value, int axis)
    {
        switch (axis)
        {
            case 0: return value.x;
            case 1: return value.y;
            default: return value.z;
        }
    }

    private void ConnectSharedMemory()
    {
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(shmName);
            _view = _mmf.CreateViewAccessor();
            Debug.Log("[WinCCScreenReceiver] Connected to Shared Memory!");
        }
        catch
        {
            // Auto-retry in Update
        }
    }

    void Update()
    {
        if (_view == null)
        {
            if (Time.frameCount % 60 == 0) ConnectSharedMemory();
            return;
        }

        try
        {
            byte status = _view.ReadByte(64);
            int sequenceBefore = _view.ReadInt32(FrameSequenceOffset);
            if (status != 3 || (sequenceBefore & 1) != 0 || sequenceBefore == _lastFrameSequence)
                return;

            _view.ReadArray(FrameDataOffset, _pixelBuffer, 0, _pixelBuffer.Length);

            int sequenceAfter = _view.ReadInt32(FrameSequenceOffset);
            if (sequenceBefore != sequenceAfter || (sequenceAfter & 1) != 0)
                return;

            _screenTexture.LoadRawTextureData(_pixelBuffer);
            _screenTexture.Apply(false);
            _lastFrameSequence = sequenceAfter;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WinCCScreenReceiver] Frame update error: {ex.Message}");
        }
    }

    void OnDestroy()
    {
        _view?.Dispose();
        _mmf?.Dispose();

        if (_screenOverlay != null)
            Destroy(_screenOverlay);
        if (_overlayMesh != null)
            Destroy(_overlayMesh);
        if (_overlayMaterial != null)
            Destroy(_overlayMaterial);
        if (_screenTexture != null)
            Destroy(_screenTexture);
    }
}