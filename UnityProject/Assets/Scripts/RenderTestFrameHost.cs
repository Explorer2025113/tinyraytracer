using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Phase 2 pinhole + Phase 3 thin lens, chief-ray CoC, 4/8/16 spp, heat-map overlay.
/// </summary>
[DisallowMultipleComponent]
public sealed class RenderTestFrameHost : MonoBehaviour
{
    public enum NativeMaterial
    {
        Diffuse = 0,
        Mirror = 1,
        Glass = 2
    }

    public enum SphereSourceMode
    {
        [Tooltip("使用下方 spheres 列表；全空时回退到场景中 RTSphere_* / Sphere_* 演示物体。")]
        InspectorList = 0,
        [Tooltip("收集场景中所有 RaytracedSphere（适合正式搭景）。")]
        AllRaytracedSpheres = 1,
        [Tooltip("仅收集 sphereGatherRoot 子层级中的 RaytracedSphere。")]
        RaytracedSpheresUnderRoot = 2,
    }

    [Serializable]
    public struct TracedSphere
    {
        public Transform transform;
        [Tooltip("0 uses 0.5 (Unity often leaves new list entries at 0).")]
        [Min(0f)] public float radius;
        public Color albedo;
        public NativeMaterial material;
    }

    const int MaxSpheres = 256;
    const string DllName = "RaytracerNative";

    [SerializeField] RawImage targetImage;
    [SerializeField] int width = 512;
    [SerializeField] int height = 384;
    [Tooltip("If true, calls Phase-1 solid blue (ignores camera/spheres).")]
    [SerializeField] bool legacySolidBlueTest;
    [SerializeField] Camera sceneCamera;
    [SerializeField] [Range(1, 16)] int maxBounceDepth = 5;
    [Header("Scene content")]
    [SerializeField]
    SphereSourceMode sphereSource = SphereSourceMode.InspectorList;
    [Tooltip("当 sphereSource = UnderRoot 时使用；否则可留空。")]
    [SerializeField]
    Transform sphereGatherRoot;
    [Tooltip("收集时是否包含未激活物体上的 RaytracedSphere。")]
    [SerializeField]
    bool gatherIncludesInactiveObjects;
    [SerializeField] TracedSphere[] spheres = Array.Empty<TracedSphere>();
    [Header("Static mesh scene (BVH in native)")]
    [Tooltip("上传场景中静态 Mesh 到 native BVH，与球体一起参与求交与阴影。")]
    [SerializeField]
    bool useStaticMeshScene;
    [Tooltip("仅收集 GameObject 勾选了 Static 的 MeshFilter（demo 场景请把环境标为 Static）。")]
    [SerializeField]
    bool meshOnlyStaticObjects = true;
    [Tooltip("包含未激活物体上的 Mesh（与 FindObjects 设置一致）。")]
    [SerializeField]
    bool meshGatherIncludeInactiveObjects;
    [Tooltip("单帧上传三角面上限；超出部分丢弃并警告一次。")]
    [SerializeField]
    [Min(1024)]
    int maxMeshTriangles = 300000;
    [Tooltip("每帧重建网格数据（物体动画时用；静态场景关）。")]
    [SerializeField]
    bool rebuildMeshEveryFrame;
    [Header("Click-to-Focus")]
    [SerializeField] bool clickToFocus = true;
    [SerializeField] bool autoSyncFocusToMeasuredDistance = true;
    [Tooltip("开启后，悬停探测命中也会自动改焦；关闭则仅点击命中时自动改焦。")]
    [SerializeField] bool autoSyncFocusOnHoverProbe;
    [SerializeField] bool ignoreClickWhenPointerOverUI = true;
    [SerializeField] [Min(0.01f)] float focusRayMaxDistance = 1000f;
    [SerializeField] LayerMask focusLayerMask = ~0;
    [SerializeField] [Min(0.001f)] float focusMatchTolerance = 0.05f;
    [Tooltip("开启后会按间隔自动探测鼠标当前位置，便于确认点击是否生效。")]
    [SerializeField] bool focusProbeOnHover = true;
    [SerializeField] [Min(0.02f)] float focusProbeInterval = 0.08f;
    [Header("Split View (Left=Unity, Right=Raytrace)")]
    [SerializeField] bool splitViewRightHalf;
    [Header("Adaptive Viewport")]
    [SerializeField] bool adaptiveViewport = true;
    [SerializeField] [Min(0.01f)] float settleDelay = 0.2f;
    [SerializeField] bool interactiveDisableDof = true;
    [SerializeField] bool interactiveForcePinhole = true;
    [Header("Click Feedback Preview")]
    [Tooltip("点击后短时间切到低分辨率预览，立刻给交互反馈。")]
    [SerializeField] bool clickPreviewOnPointerDown = true;
    [SerializeField] [Min(64)] int clickPreviewWidth = 320;
    [SerializeField] [Min(64)] int clickPreviewHeight = 180;
    [SerializeField] [Min(0.02f)] float clickPreviewDuration = 0.2f;
    [SerializeField] bool clickPreviewDisableDof = true;
    [SerializeField] bool clickPreviewForcePinhole = true;
    [Header("Interactive Light")]
    [SerializeField] bool useInteractiveLight;
    [SerializeField] bool interactiveLightIsPoint = true;
    [SerializeField] Transform interactiveLightTransform;
    [SerializeField] Color interactiveLightColor = Color.white;
    [SerializeField] [Min(0f)] float interactiveLightIntensity = 1f;
    [Tooltip("Fill Game view: stretch RawImage to parent rect.")]
    [SerializeField] bool stretchOutputToParent = true;

    [Header("Phase 3 thin lens + progressive sampling")]
    [Tooltip("Bypass DOF; same as Phase-2 pinhole.")]
    [SerializeField] bool pinholeOnly;
    [Tooltip("Lens radius on the plane perpendicular to the camera (world units). 0 also forces pinhole.")]
    [SerializeField] float apertureRadius = 0.035f;
    [Tooltip("Distance along camera forward to the focal plane (world units).")]
    [SerializeField] float focusDistance = 3.5f;
    [Tooltip("Focus distance upper bound used by runtime/UI to avoid entering near-pinhole-looking far-focus range.")]
    [SerializeField] [Min(0.5f)] float focusDistanceMax = 12f;
    [SerializeField] [Min(1f)] float focusDistanceHardMax = 300f;
    [SerializeField] bool autoExpandFocusRangeFromProbe = true;
    [SerializeField] [Range(1.01f, 2.0f)] float focusRangeExpandFactor = 1.15f;
    [Tooltip("CoC below this -> 4 spp. 0 = auto from aperture.")]
    [SerializeField] float cocThresholdLo;
    [Tooltip("CoC above this -> 16 spp. 0 = auto.")]
    [SerializeField] float cocThresholdHi;
    [Tooltip("Green / yellow / red = 4 / 8 / 16 spp (no shading).")]
    [SerializeField] bool heatMapMode;

    /// <summary>运行时可由 UI（如 <see cref="RaytraceControlPanel"/>）改写，下一帧生效。</summary>
    public float RuntimeApertureRadius
    {
        get => apertureRadius;
        set => apertureRadius = Mathf.Clamp(value, 0f, 0.25f);
    }

    public float RuntimeFocusDistance
    {
        get => focusDistance;
        set => focusDistance = Mathf.Clamp(value, 0.01f, Mathf.Min(Mathf.Max(0.5f, focusDistanceMax), focusDistanceHardMax));
    }
    public float RuntimeFocusDistanceMax => Mathf.Min(Mathf.Max(0.5f, focusDistanceMax), focusDistanceHardMax);
    public bool RuntimeHasMeasuredDistance => hasMeasuredDistance;
    public float RuntimeMeasuredDistance => measuredDistance;
    public float RuntimeFocusDistanceError =>
        hasMeasuredDistance ? Mathf.Abs(focusDistance - measuredDistance) : 0f;
    public bool RuntimeIsFocusMatched =>
        hasMeasuredDistance && RuntimeFocusDistanceError <= focusMatchTolerance;
    public bool RuntimeFocusProbeOnHover
    {
        get => focusProbeOnHover;
        set => focusProbeOnHover = value;
    }
    public float RuntimeFocusProbeInterval
    {
        get => focusProbeInterval;
        set => focusProbeInterval = Mathf.Clamp(value, 0.02f, 0.5f);
    }
    public string RuntimeFocusProbeStatus => focusProbeStatus;
    public float RuntimeFocusProbeLatencyMs => focusProbeLatencyMs;
    public float RuntimeFocusProbeAge => Mathf.Max(0f, Time.unscaledTime - focusProbeTime);
    public float RuntimeCenterHitFlash01 =>
        Mathf.Clamp01((centerHitFlashUntil - Time.unscaledTime) / Mathf.Max(1e-4f, centerHitFlashDuration));
    public bool RuntimeHasPointerScreenPos => hasPointerScreenPos;
    public Vector2 RuntimePointerScreenPos => pointerScreenPos;
    public bool RuntimeFocusProbeHit => focusProbeHit;

    public bool RuntimePinholeOnly
    {
        get => pinholeOnly;
        set => pinholeOnly = value;
    }

    public bool RuntimeHeatMapMode
    {
        get => heatMapMode;
        set => heatMapMode = value;
    }

    public float RuntimeCocThresholdLo
    {
        get => cocThresholdLo;
        set => cocThresholdLo = Mathf.Max(0f, value);
    }

    public float RuntimeCocThresholdHi
    {
        get => cocThresholdHi;
        set => cocThresholdHi = Mathf.Max(0f, value);
    }

    byte[] pixelBuffer;
    Texture2D tex;
    readonly NativeSphere[] nativeSpheres = new NativeSphere[MaxSpheres];
    TracedSphere[] demoFallback = Array.Empty<TracedSphere>();
    readonly List<TracedSphere> gatherScratch = new List<TracedSphere>(64);
    bool warnedNoSpheres;
    bool warnedSphereCap;
    bool warnedDll;
    bool warnedMeshReadable;
    bool warnedMeshTriangleCap;
    bool warnedTextureReadback;
    NativeMeshTriangle[] meshUploadBuffer;
    readonly Dictionary<Texture, Texture2D> readableTextureCache = new Dictionary<Texture, Texture2D>(32);

    Vector3 lastCameraPos;
    Quaternion lastCameraRot;
    float lastInteractionTime = -999f;
    bool hasMeasuredDistance;
    float measuredDistance;
    string focusProbeStatus = "等待探测";
    float focusProbeLatencyMs;
    float focusProbeTime = -999f;
    [SerializeField] [Min(0.05f)] float centerHitFlashDuration = 0.18f;
    float centerHitFlashUntil = -999f;
    bool hasPointerScreenPos;
    Vector2 pointerScreenPos;
    bool focusProbeHit;
    float clickPreviewUntil = -999f;

    const int ExpectedNativeApiVersion = 9;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "RaytracerNative_ApiVersion")]
    static extern int RaytracerNative_ApiVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void RenderTestFrame(int width, int height, IntPtr buffer);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void RenderPinholeSpheres(int width, int height, IntPtr buffer,
        ref NativeCamera camera, IntPtr spheres, int sphereCount, int maxDepth);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void RaytracerNative_SetMeshTriangles([In] NativeMeshTriangle[] tris, int triCount);

        [StructLayout(LayoutKind.Sequential)]
        struct NativeVec3
        {
            public float x, y, z;

            public static NativeVec3 From(Vector3 v) => new NativeVec3 { x = v.x, y = v.y, z = v.z };
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeSphere
        {
            public NativeVec3 center;
            public float radius;
            public NativeVec3 albedo;
            public int material;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct NativeMeshTriangle
        {
            public NativeVec3 v0;
            public NativeVec3 v1;
            public NativeVec3 v2;
            public NativeVec3 n0;
            public NativeVec3 n1;
            public NativeVec3 n2;
            public NativeVec3 c0;
            public NativeVec3 c1;
            public NativeVec3 c2;
            public NativeVec3 albedo;
            public int material;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeCamera
        {
            public NativeVec3 origin;
            public NativeVec3 forward;
            public NativeVec3 right;
            public NativeVec3 up;
            public float vfovDegrees;
            public float aspect;
            public float apertureRadius;
            public float focusDistance;
            public float cocThresholdLo;
            public float cocThresholdHi;
            public int heatMap;
            public int pinholeOnly;
            public NativeVec3 lightDir;
            public NativeVec3 lightPos;
            public NativeVec3 lightColor;
            public float lightIntensity;
            public int lightMode;
        }

        void Awake()
        {
            if (targetImage == null)
                targetImage = GetComponentInChildren<RawImage>(true);
            if (sceneCamera == null)
                sceneCamera = Camera.main;
            if (sceneCamera == null)
                sceneCamera = FindFirstObjectByType<Camera>();
        }

        void Start()
        {
            if (targetImage == null)
            {
                Debug.LogError("RenderTestFrameHost: assign a RawImage.");
                return;
            }

            if (width <= 0 || height <= 0)
            {
                Debug.LogError("RenderTestFrameHost: width and height must be positive.");
                return;
            }

            tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            pixelBuffer = new byte[width * height * 4];
            targetImage.texture = tex;
            ApplyRawImageLayout();
            if (sceneCamera != null)
            {
                lastCameraPos = sceneCamera.transform.position;
                lastCameraRot = sceneCamera.transform.rotation;
            }

            demoFallback = BuildDemoSpheresFromScene();
            TryVerifyNativeDll();

            meshUploadBuffer = useStaticMeshScene ? new NativeMeshTriangle[maxMeshTriangles] : null;
            if (useStaticMeshScene)
            {
                if (!rebuildMeshEveryFrame)
                    RebuildNativeMeshScene();
            }
            else
                RaytracerNative_SetMeshTriangles(null, 0);

            if (!useStaticMeshScene &&
                sphereSource == SphereSourceMode.InspectorList &&
                !HasAnyAssignedSphere(spheres) && demoFallback.Length == 0)
            {
                Debug.LogWarning(
                    "RenderTestFrameHost: sphereSource=InspectorList 但列表为空且无演示球 RTSphere_* / Sphere_*。" +
                    "正式场景请改用 sphereSource=AllRaytracedSpheres 或给物体挂 RaytracedSphere。");
            }
        }

        void TryVerifyNativeDll()
        {
            if (warnedDll)
                return;
            try
            {
                int v = RaytracerNative_ApiVersion();
                if (v != ExpectedNativeApiVersion)
                {
                    warnedDll = true;
                    Debug.LogError(
                        $"RaytracerNative.dll API version {v} != managed {ExpectedNativeApiVersion}. " +
                        "Rebuild native (tinyraytracer/build/native Release) and copy RaytracerNative.dll to Assets/Plugins/x86_64 (close Unity first if locked).");
                }
            }
            catch (DllNotFoundException)
            {
                warnedDll = true;
                Debug.LogError("Native plugin not found. Expected Assets/Plugins/x86_64/RaytracerNative.dll.");
            }
            catch (EntryPointNotFoundException)
            {
                warnedDll = true;
                Debug.LogError(
                    "RaytracerNative.dll is outdated (missing RaytracerNative_ApiVersion). Rebuild and replace the DLL.");
            }
        }

        static bool HasAnyAssignedSphere(TracedSphere[] list)
        {
            if (list == null || list.Length == 0)
                return false;
            foreach (TracedSphere s in list)
            {
                if (s.transform != null)
                    return true;
            }

            return false;
        }

        void ApplyRawImageLayout()
        {
            if (targetImage == null)
                return;
            RectTransform rt = targetImage.rectTransform;
            if (splitViewRightHalf)
            {
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;
                return;
            }
            if (stretchOutputToParent)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;
            }
            else
            {
                targetImage.SetNativeSize();
            }
        }

        void EnsureRenderTargetSize(int w, int h)
        {
            if (w <= 0 || h <= 0)
                return;
            if (tex != null && tex.width == w && tex.height == h &&
                pixelBuffer != null && pixelBuffer.Length == w * h * 4)
                return;

            tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            pixelBuffer = new byte[w * h * 4];
            targetImage.texture = tex;
            ApplyRawImageLayout();
        }

        void ApplySceneCameraViewport()
        {
            Camera cam = sceneCamera != null ? sceneCamera : Camera.main;
            if (cam == null)
                return;
            if (splitViewRightHalf)
                cam.rect = new Rect(0f, 0f, 0.5f, 1f);
            else
                cam.rect = new Rect(0f, 0f, 1f, 1f);
        }

        bool IsPointerOverUI()
        {
            if (!ignoreClickWhenPointerOverUI)
                return false;
            if (EventSystem.current == null)
                return false;
            if (Input.touchCount > 0)
                return EventSystem.current.IsPointerOverGameObject(Input.GetTouch(0).fingerId);
            return EventSystem.current.IsPointerOverGameObject();
        }

        bool TryMapScreenToRay(Camera cam, Vector3 screenPos, out Ray ray, out bool clickInsideRaytraceView)
        {
            clickInsideRaytraceView = false;
        if (!splitViewRightHalf)
            {
            ray = cam.ScreenPointToRay(screenPos);
            return true;
        }

        // 分屏测距改为按“屏幕右半区”判定，避免 RawImage Rect 与实际显示不一致时误判“范围外”。
        float halfW = Screen.width * 0.5f;
        if (screenPos.x < halfW)
        {
            // 左半区（Unity 实时画面）也允许测距。
            ray = cam.ScreenPointToRay(screenPos);
            return true;
        }

        clickInsideRaytraceView = true;
        float u = Mathf.Clamp01((screenPos.x - halfW) / Mathf.Max(1f, halfW));
        float v = Mathf.Clamp01(screenPos.y / Mathf.Max(1f, (float)Screen.height));
        Vector3 viewport = new Vector3(u, v, 0f);
        ray = cam.ViewportPointToRay(viewport);
        return true;
    }

    bool TryGetPrimaryPointerDown(out Vector3 screenPos)
    {
#if ENABLE_INPUT_SYSTEM
        if (Touchscreen.current != null)
        {
            var t = Touchscreen.current.primaryTouch;
            if (t.press.wasPressedThisFrame)
            {
                Vector2 p = t.position.ReadValue();
                screenPos = new Vector3(p.x, p.y, 0f);
                return true;
            }
        }
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Vector2 p = Mouse.current.position.ReadValue();
            screenPos = new Vector3(p.x, p.y, 0f);
            return true;
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
        {
            screenPos = Input.GetTouch(0).position;
            return true;
        }
        if (Input.GetMouseButtonDown(0))
        {
            screenPos = Input.mousePosition;
            return true;
        }
#endif
        screenPos = Vector3.zero;
        return false;
    }

    bool TryGetPrimaryPointerPosition(out Vector3 screenPos)
    {
#if ENABLE_INPUT_SYSTEM
        if (Touchscreen.current != null)
        {
            var t = Touchscreen.current.primaryTouch;
            Vector2 p = t.position.ReadValue();
            screenPos = new Vector3(p.x, p.y, 0f);
            return true;
        }
        if (Mouse.current != null)
        {
            Vector2 p = Mouse.current.position.ReadValue();
            screenPos = new Vector3(p.x, p.y, 0f);
            return true;
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.touchCount > 0)
        {
            screenPos = Input.GetTouch(0).position;
            return true;
        }
        screenPos = Input.mousePosition;
        return true;
#else
        screenPos = Vector3.zero;
        return false;
#endif
    }

    void UpdatePointerScreenPos()
    {
        if (TryGetPrimaryPointerPosition(out Vector3 p))
        {
            pointerScreenPos = new Vector2(p.x, p.y);
            hasPointerScreenPos = true;
            return;
        }
        hasPointerScreenPos = false;
    }

    bool HasContinuousInputActivity()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            if (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed ||
                Mouse.current.middleButton.isPressed)
                return true;
            Vector2 d = Mouse.current.delta.ReadValue();
            if (Mathf.Abs(d.x) > 1e-4f || Mathf.Abs(d.y) > 1e-4f)
                return true;
        }
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.aKey.isPressed ||
                Keyboard.current.sKey.isPressed || Keyboard.current.dKey.isPressed ||
                Keyboard.current.upArrowKey.isPressed || Keyboard.current.downArrowKey.isPressed ||
                Keyboard.current.leftArrowKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
                return true;
        }
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
            return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) ||
            Mathf.Abs(Input.GetAxisRaw("Mouse X")) > 1e-4f ||
            Mathf.Abs(Input.GetAxisRaw("Mouse Y")) > 1e-4f ||
            Mathf.Abs(Input.GetAxisRaw("Horizontal")) > 1e-4f ||
            Mathf.Abs(Input.GetAxisRaw("Vertical")) > 1e-4f)
            return true;
#endif
        return false;
    }

    void TryClickToFocus()
    {
        if (!clickToFocus)
            return;
        bool pointerDown = TryGetPrimaryPointerDown(out Vector3 screenPos);
        if (pointerDown)
        {
            centerHitFlashUntil = Time.unscaledTime + Mathf.Max(0.05f, centerHitFlashDuration);
            if (clickPreviewOnPointerDown)
                clickPreviewUntil = Time.unscaledTime + Mathf.Max(0.02f, clickPreviewDuration);
        }
        bool hoverProbe = false;
        if (!pointerDown && focusProbeOnHover &&
            (Time.unscaledTime - focusProbeTime) >= Mathf.Max(0.02f, focusProbeInterval))
        {
            hoverProbe = TryGetPrimaryPointerPosition(out screenPos);
        }
        if (!pointerDown && !hoverProbe)
            return;

        Camera cam = sceneCamera != null ? sceneCamera : Camera.main;
        if (cam == null)
        {
            focusProbeStatus = "未找到相机";
            focusProbeHit = false;
            return;
        }
        float t0 = Time.realtimeSinceStartup;
        if (!TryMapScreenToRay(cam, screenPos, out Ray ray, out bool insideRaytraceView))
        {
            focusProbeStatus = "未在可探测视图内";
            focusProbeTime = Time.unscaledTime;
            focusProbeHit = false;
            return;
        }
        if (IsPointerOverUI() && !insideRaytraceView)
        {
            focusProbeStatus = "被 UI 拦截";
            focusProbeTime = Time.unscaledTime;
            focusProbeHit = false;
            return;
        }
        if (Physics.Raycast(ray, out RaycastHit hit, focusRayMaxDistance, focusLayerMask))
        {
            float d = Vector3.Dot(hit.point - cam.transform.position, cam.transform.forward);
            if (d > 0.01f)
            {
                if (autoExpandFocusRangeFromProbe && d > focusDistanceMax)
                {
                    float targetMax = Mathf.Max(d + 1f, d * focusRangeExpandFactor);
                    focusDistanceMax = Mathf.Min(focusDistanceHardMax, targetMax);
                }
                measuredDistance = d;
                hasMeasuredDistance = true;
                if (autoSyncFocusToMeasuredDistance && (pointerDown || autoSyncFocusOnHoverProbe))
                    focusDistance = Mathf.Clamp(d, 0.01f, Mathf.Max(0.5f, focusDistanceMax));
                focusProbeStatus = pointerDown
                    ? $"点击命中: {d:F2} m"
                    : $"悬停命中: {d:F2} m";
                focusProbeHit = true;
            }
            else
            {
                focusProbeStatus = "命中但深度无效";
                focusProbeHit = false;
            }
        }
        else
        {
            focusProbeStatus = pointerDown ? "点击未命中" : "悬停未命中";
            focusProbeHit = false;
        }
        focusProbeLatencyMs = (Time.realtimeSinceStartup - t0) * 1000f;
        focusProbeTime = Time.unscaledTime;
    }

    bool UpdateInteractiveMode()
    {
        if (!adaptiveViewport)
            return false;
        Camera cam = sceneCamera != null ? sceneCamera : Camera.main;
        if (cam == null)
            return false;

        bool inputActivity = HasContinuousInputActivity();

        Transform ct = cam.transform;
        bool camMoved = Vector3.Distance(ct.position, lastCameraPos) > 1e-4f ||
                        Quaternion.Angle(ct.rotation, lastCameraRot) > 0.01f;
        lastCameraPos = ct.position;
        lastCameraRot = ct.rotation;
        if (inputActivity || camMoved)
            lastInteractionTime = Time.unscaledTime;
        bool interactiveNow = (Time.unscaledTime - lastInteractionTime) < settleDelay;
        return interactiveNow;
    }

    void FillInteractiveLight(ref NativeVec3 lightDir,
        ref NativeVec3 lightPos,
        ref NativeVec3 lightColor,
        ref float lightIntensity,
        ref int lightMode)
    {
        if (!useInteractiveLight)
            return;
        if (interactiveLightTransform == null)
        {
            Light l = FindFirstObjectByType<Light>();
            if (l != null)
                interactiveLightTransform = l.transform;
        }
        if (interactiveLightTransform == null)
            return;

        Light lightComp = interactiveLightTransform.GetComponent<Light>();
        bool usePoint = interactiveLightIsPoint;
        Color srcColor = interactiveLightColor;
        float srcIntensity = interactiveLightIntensity;
        if (lightComp != null)
        {
            // Auto-follow actual Unity light type/color/intensity when available.
            usePoint = lightComp.type != LightType.Directional;
            srcColor = lightComp.color;
            srcIntensity = lightComp.intensity;
        }

        lightMode = usePoint ? 1 : 0;
        lightPos = NativeVec3.From(interactiveLightTransform.position);
        lightDir = NativeVec3.From(usePoint
            ? -interactiveLightTransform.forward
            : -interactiveLightTransform.forward);
        Color lc = srcColor.linear;
        lightColor = new NativeVec3 { x = lc.r, y = lc.g, z = lc.b };
        lightIntensity = Mathf.Max(0f, srcIntensity);
    }

    void LateUpdate()
    {
        if (targetImage == null || tex == null || pixelBuffer == null)
            return;

        ApplySceneCameraViewport();
        UpdatePointerScreenPos();
        TryClickToFocus();
        // 三步模式下保留“点击=交互”的短暂预览，避免点击时误感知为卡死。
        bool interactiveNow = clickPreviewOnPointerDown && (Time.unscaledTime < clickPreviewUntil);
        int rh = interactiveNow ? clickPreviewHeight : height;
        float panelAspect = splitViewRightHalf
            ? (Screen.width * 0.5f) / Mathf.Max(1f, Screen.height)
            : (Screen.width / Mathf.Max(1f, (float)Screen.height));
        int rw = interactiveNow ? clickPreviewWidth : width;
        if (panelAspect > 1e-4f)
            rw = Mathf.Max(64, Mathf.RoundToInt(rh * panelAspect));
        EnsureRenderTargetSize(rw, rh);

        if (useStaticMeshScene && rebuildMeshEveryFrame)
            RebuildNativeMeshScene();

        var bufferHandle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
        try
        {
            IntPtr bufferPtr = bufferHandle.AddrOfPinnedObject();

            if (legacySolidBlueTest)
            {
                RenderTestFrame(rw, rh, bufferPtr);
            }
            else
            {
                Camera cam = sceneCamera != null ? sceneCamera : Camera.main;
                if (cam == null)
                {
                    RenderTestFrame(rw, rh, bufferPtr);
                }
                else
                {
                    IReadOnlyList<TracedSphere> src = ResolveSphereSource();
                    if (src.Count == 0 && !useStaticMeshScene && !warnedNoSpheres)
                    {
                        warnedNoSpheres = true;
                        Debug.LogWarning("RenderTestFrameHost: 当前 0 个球体且未启用静态网格，画面只有天空。");
                    }

                    int count = BuildNativeSpheres(src);
                    Transform ct = cam.transform;
                    float ap = Mathf.Max(0f, apertureRadius);
                    bool framePinholeOnly = pinholeOnly ||
                                            (interactiveNow && (interactiveForcePinhole || clickPreviewForcePinhole));
                    if (interactiveNow && (interactiveDisableDof || clickPreviewDisableDof))
                        ap = 0f;
                    NativeVec3 lightDir = new NativeVec3 { x = 0.45f, y = 0.85f, z = 0.35f };
                    NativeVec3 lightPos = new NativeVec3 { x = 0f, y = 4f, z = 0f };
                    NativeVec3 lightColor = new NativeVec3 { x = 1f, y = 0.98f, z = 0.92f };
                    float lightIntensity = 1f;
                    int lightMode = 0;
                    FillInteractiveLight(ref lightDir, ref lightPos, ref lightColor, ref lightIntensity,
                        ref lightMode);
                    var nc = new NativeCamera
                    {
                        origin = NativeVec3.From(ct.position),
                        forward = NativeVec3.From(ct.forward),
                        right = NativeVec3.From(ct.right),
                        up = NativeVec3.From(ct.up),
                        vfovDegrees = cam.fieldOfView,
                        aspect = rw / (float)rh,
                        apertureRadius = ap,
                        focusDistance = Mathf.Clamp(focusDistance, 0.01f, Mathf.Max(0.5f, focusDistanceMax)),
                        cocThresholdLo = cocThresholdLo,
                        cocThresholdHi = cocThresholdHi,
                        heatMap = heatMapMode ? 1 : 0,
                        pinholeOnly = framePinholeOnly ? 1 : 0,
                        lightDir = lightDir,
                        lightPos = lightPos,
                        lightColor = lightColor,
                        lightIntensity = lightIntensity,
                        lightMode = lightMode
                    };

                    var spheresHandle = GCHandle.Alloc(nativeSpheres, GCHandleType.Pinned);
                    try
                    {
                        RenderPinholeSpheres(rw, rh, bufferPtr, ref nc,
                            spheresHandle.AddrOfPinnedObject(), count, maxBounceDepth);
                    }
                    finally
                    {
                        spheresHandle.Free();
                    }
                }
            }

            tex.LoadRawTextureData(pixelBuffer);
            tex.Apply(false, false);
        }
        finally
        {
            bufferHandle.Free();
        }
    }

    static TracedSphere[] BuildDemoSpheresFromScene()
    {
        var list = new System.Collections.Generic.List<TracedSphere>(4);
        void TryAddAny(string[] objectNames, float radius, Color color, NativeMaterial mat)
        {
            foreach (string objectName in objectNames)
            {
                GameObject go = GameObject.Find(objectName);
                if (go == null)
                    continue;
                list.Add(new TracedSphere
                {
                    transform = go.transform,
                    radius = radius,
                    albedo = color,
                    material = mat
                });
                return;
            }
        }

        TryAddAny(new[] { "RTSphere_Red", "Sphere_Red" }, 0.5f, new Color(0.92f, 0.18f, 0.12f),
            NativeMaterial.Diffuse);
        TryAddAny(new[] { "RTSphere_Mirror", "Sphere_Mirror" }, 0.5f, new Color(0.92f, 0.92f, 0.95f),
            NativeMaterial.Mirror);
        TryAddAny(new[] { "RTSphere_Green", "Sphere_Green" }, 0.5f, new Color(0.12f, 0.78f, 0.22f),
            NativeMaterial.Diffuse);
        return list.ToArray();
    }

    IReadOnlyList<TracedSphere> ResolveSphereSource()
    {
        switch (sphereSource)
        {
            case SphereSourceMode.AllRaytracedSpheres:
            case SphereSourceMode.RaytracedSpheresUnderRoot:
                FillGatherList();
                return gatherScratch;
            default:
                if (HasAnyAssignedSphere(spheres))
                    return spheres;
                return demoFallback;
        }
    }

    void FillGatherList()
    {
        gatherScratch.Clear();
        var inactive = gatherIncludesInactiveObjects ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;

        if (sphereSource == SphereSourceMode.AllRaytracedSpheres)
        {
            RaytracedSphere[] found = FindObjectsByType<RaytracedSphere>(inactive, FindObjectsSortMode.None);
            foreach (RaytracedSphere rs in found)
            {
                if (rs == null || !rs.enabled)
                    continue;
                if (!gatherIncludesInactiveObjects && !rs.gameObject.activeInHierarchy)
                    continue;
                gatherScratch.Add(rs.ToTracedSphere());
            }
        }
        else if (sphereGatherRoot != null)
        {
            RaytracedSphere[] found =
                sphereGatherRoot.GetComponentsInChildren<RaytracedSphere>(gatherIncludesInactiveObjects);
            foreach (RaytracedSphere rs in found)
            {
                if (rs == null || !rs.enabled)
                    continue;
                if (!gatherIncludesInactiveObjects && !rs.gameObject.activeInHierarchy)
                    continue;
                gatherScratch.Add(rs.ToTracedSphere());
            }
        }

        if (gatherScratch.Count > MaxSpheres && !warnedSphereCap)
        {
            warnedSphereCap = true;
            Debug.LogWarning(
                $"RenderTestFrameHost: 收集到 {gatherScratch.Count} 个球，超过 MaxSpheres={MaxSpheres}，多出的将被忽略。");
        }
    }

    int BuildNativeSpheres(IReadOnlyList<TracedSphere> src)
    {
        if (src == null || src.Count == 0)
            return 0;

        int count = 0;
        for (int i = 0; i < src.Count && count < MaxSpheres; i++)
        {
            TracedSphere s = src[i];
            if (s.transform == null)
                continue;

            float worldRadius = s.radius > 1e-4f ? s.radius : 0.5f;
            worldRadius = Mathf.Max(1e-4f, worldRadius);
            Color c = s.albedo.linear;
            nativeSpheres[count++] = new NativeSphere
            {
                center = NativeVec3.From(s.transform.position),
                radius = worldRadius,
                albedo = new NativeVec3 { x = c.r, y = c.g, z = c.b },
                material = (int)s.material
            };
        }

        return count;
    }

    static readonly int ShaderPropBaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ShaderPropColor = Shader.PropertyToID("_Color");
    static readonly int ShaderPropMetallic = Shader.PropertyToID("_Metallic");
    static readonly int ShaderPropBaseMap = Shader.PropertyToID("_BaseMap");

    /// <summary>重新扫描场景并上传到 native BVH（静态场景改模型后可调用）。</summary>
    public void RebuildNativeMeshScene()
    {
        if (meshUploadBuffer == null || meshUploadBuffer.Length != maxMeshTriangles)
            meshUploadBuffer = new NativeMeshTriangle[maxMeshTriangles];

        int triWritten = 0;
        var inactive = meshGatherIncludeInactiveObjects ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
        MeshFilter[] filters = FindObjectsByType<MeshFilter>(inactive, FindObjectsSortMode.None);

        foreach (MeshFilter mf in filters)
        {
            if (mf == null || mf.sharedMesh == null)
                continue;
            if (meshOnlyStaticObjects && !mf.gameObject.isStatic)
                continue;
            if (!meshGatherIncludeInactiveObjects && !mf.gameObject.activeInHierarchy)
                continue;

            Mesh mesh = mf.sharedMesh;
            if (!mesh.isReadable)
            {
                if (!warnedMeshReadable)
                {
                    warnedMeshReadable = true;
                    Debug.LogWarning(
                        $"RenderTestFrameHost: 网格「{mesh.name}」（{mf.gameObject.name}）未开启 Read/Write，已跳过。请在 Model Import Settings 勾选 Read/Write Enabled。");
                }

                continue;
            }

            MeshRenderer renderer = mf.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.enabled)
                continue;

            Material[] mats = renderer.sharedMaterials;
            Matrix4x4 worldMat = renderer.localToWorldMatrix;
            Vector3[] verts = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            Vector3[] normals = mesh.normals;
            bool hasNormals = normals != null && normals.Length == verts.Length;
            bool hasUvs = uvs != null && uvs.Length == verts.Length;

            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                int[] tris = mesh.GetTriangles(sm);
                Material subMat = mats != null && mats.Length > 0
                    ? mats[Mathf.Min(sm, mats.Length - 1)]
                    : null;
                Color alb = ExtractAlbedoLinear(subMat);
                int matKind = ClassifyNativeMaterial(subMat);
                Texture2D tex2D = ResolveReadableAlbedoTexture(subMat);
                Vector2 uvScale = Vector2.one;
                Vector2 uvOffset = Vector2.zero;
                if (subMat != null)
                {
                    if (subMat.HasProperty(ShaderPropBaseMap))
                    {
                        uvScale = subMat.GetTextureScale(ShaderPropBaseMap);
                        uvOffset = subMat.GetTextureOffset(ShaderPropBaseMap);
                    }
                    else
                    {
                        uvScale = subMat.mainTextureScale;
                        uvOffset = subMat.mainTextureOffset;
                    }
                }

                for (int t = 0; t < tris.Length; t += 3)
                {
                    if (triWritten >= maxMeshTriangles)
                        goto Upload;

                    Vector3 w0 = worldMat.MultiplyPoint3x4(verts[tris[t]]);
                    Vector3 w1 = worldMat.MultiplyPoint3x4(verts[tris[t + 1]]);
                    Vector3 w2 = worldMat.MultiplyPoint3x4(verts[tris[t + 2]]);
                    Vector3 wn0;
                    Vector3 wn1;
                    Vector3 wn2;
                    if (hasNormals)
                    {
                        wn0 = worldMat.MultiplyVector(normals[tris[t]]).normalized;
                        wn1 = worldMat.MultiplyVector(normals[tris[t + 1]]).normalized;
                        wn2 = worldMat.MultiplyVector(normals[tris[t + 2]]).normalized;
                    }
                    else
                    {
                        Vector3 fn = Vector3.Cross(w1 - w0, w2 - w0).normalized;
                        wn0 = fn;
                        wn1 = fn;
                        wn2 = fn;
                    }
                    Color vc0 = hasUvs ? SampleVertexColor(tex2D, uvs[tris[t]], alb, uvScale, uvOffset) : alb;
                    Color vc1 = hasUvs ? SampleVertexColor(tex2D, uvs[tris[t + 1]], alb, uvScale, uvOffset) : alb;
                    Color vc2 = hasUvs ? SampleVertexColor(tex2D, uvs[tris[t + 2]], alb, uvScale, uvOffset) : alb;
                    meshUploadBuffer[triWritten++] = new NativeMeshTriangle
                    {
                        v0 = NativeVec3.From(w0),
                        v1 = NativeVec3.From(w1),
                        v2 = NativeVec3.From(w2),
                        n0 = NativeVec3.From(wn0),
                        n1 = NativeVec3.From(wn1),
                        n2 = NativeVec3.From(wn2),
                        c0 = new NativeVec3 { x = vc0.r, y = vc0.g, z = vc0.b },
                        c1 = new NativeVec3 { x = vc1.r, y = vc1.g, z = vc1.b },
                        c2 = new NativeVec3 { x = vc2.r, y = vc2.g, z = vc2.b },
                        albedo = new NativeVec3 { x = alb.r, y = alb.g, z = alb.b },
                        material = matKind
                    };
                }
            }
        }

    Upload:
        if (triWritten >= maxMeshTriangles && !warnedMeshTriangleCap)
        {
            warnedMeshTriangleCap = true;
            Debug.LogWarning(
                $"RenderTestFrameHost: 三角面达到上限 {maxMeshTriangles}，其余已丢弃。可调大 Max Mesh Triangles。");
        }

        RaytracerNative_SetMeshTriangles(meshUploadBuffer, triWritten);
    }

    static Color ExtractAlbedoGamma(Material mat)
    {
        if (mat == null)
            return Color.gray;
        if (mat.HasProperty(ShaderPropBaseColor))
            return mat.GetColor(ShaderPropBaseColor);
        if (mat.HasProperty(ShaderPropColor))
            return mat.GetColor(ShaderPropColor);
        return mat.color;
    }

    Color ExtractAlbedoLinear(Material mat) => ExtractAlbedoGamma(mat).linear;

    static int ClassifyNativeMaterial(Material mat)
    {
        if (mat == null || mat.shader == null)
            return 0;
        string shaderName = mat.shader.name;
        if (shaderName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 ||
            shaderName.IndexOf("ice", StringComparison.OrdinalIgnoreCase) >= 0 ||
            shaderName.IndexOf("crystal", StringComparison.OrdinalIgnoreCase) >= 0)
            return 2;
        Color c = ExtractAlbedoGamma(mat);
        if (c.a < 0.96f)
            return 2;
        if (mat.shader.name.IndexOf("mirror", StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;
        if (mat.HasProperty(ShaderPropMetallic) && mat.GetFloat(ShaderPropMetallic) > 0.95f)
            return 1;
        return 0;
    }

    Texture2D ResolveReadableAlbedoTexture(Material mat)
    {
        if (mat == null)
            return null;
        Texture src = null;
        if (mat.HasProperty(ShaderPropBaseMap))
            src = mat.GetTexture(ShaderPropBaseMap);
        if (src == null && mat.mainTexture != null)
            src = mat.mainTexture;
        if (src == null)
            return null;
        if (readableTextureCache.TryGetValue(src, out Texture2D cached) && cached != null)
            return cached;

        var tex2d = src as Texture2D;
        if (tex2d != null)
        {
            if (tex2d.isReadable)
            {
                readableTextureCache[src] = tex2d;
                return tex2d;
            }
            Texture2D copied = CopyTextureReadable(tex2d);
            if (copied != null)
            {
                readableTextureCache[src] = copied;
                return copied;
            }
        }
        if (!warnedTextureReadback)
        {
            warnedTextureReadback = true;
            Debug.LogWarning("RenderTestFrameHost: 部分贴图不可读，已回退纯色或尝试 GPU 读回。");
        }
        readableTextureCache[src] = null;
        return null;
    }

    static Texture2D CopyTextureReadable(Texture src)
    {
        if (src == null)
            return null;
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Default);
        var prev = RenderTexture.active;
        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var dst = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, false);
            dst.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            dst.Apply(false, false);
            return dst;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    static Color SampleVertexColor(Texture2D tex, Vector2 uv, Color fallbackLinear,
        Vector2 uvScale, Vector2 uvOffset)
    {
        if (tex == null)
            return fallbackLinear;
        Vector2 tuv = Vector2.Scale(uv, uvScale) + uvOffset;
        float u = tuv.x - Mathf.Floor(tuv.x);
        float v = tuv.y - Mathf.Floor(tuv.y);
        Color c = tex.GetPixelBilinear(u, v);
        return c.linear * fallbackLinear;
    }

    void OnDestroy()
    {
        foreach (var kv in readableTextureCache)
        {
            Texture2D tex2d = kv.Value;
            if (tex2d != null && !ReferenceEquals(tex2d, kv.Key))
                Destroy(tex2d);
        }
        readableTextureCache.Clear();
        try
        {
            RaytracerNative_SetMeshTriangles(null, 0);
        }
        catch (DllNotFoundException)
        {
            /* ignore */
        }
        catch (EntryPointNotFoundException)
        {
            /* ignore */
        }
    }
}
