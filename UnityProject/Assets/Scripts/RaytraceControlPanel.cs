using UnityEngine;

/// <summary>
/// Play 模式下可拖拽的即时 GUI 面板，调节 <see cref="RenderTestFrameHost"/> 的景深与热力图参数。
/// 挂到任意激活物体上；可将 <see cref="host"/> 留空以自动查找场景中的 Host。
/// </summary>
[DisallowMultipleComponent]
public sealed class RaytraceControlPanel : MonoBehaviour
{
    [SerializeField] RenderTestFrameHost host;
    [Tooltip("取消勾选则隐藏调参窗口")]
    [SerializeField] bool showPanel = true;
    [SerializeField] bool showCenterReticle = true;
    [SerializeField] Color reticleColor = new Color(1f, 1f, 1f, 0.85f);
    [SerializeField] Color reticleHoverHitColor = new Color(0.35f, 1f, 0.4f, 0.92f);
    [SerializeField] Color reticleHitFlashColor = new Color(0.3f, 1f, 0.35f, 0.95f);
    [SerializeField] float reticleGap = 6f;
    [SerializeField] float reticleArm = 10f;
    [SerializeField] float reticleThickness = 2f;

    Rect windowRect = new Rect(12f, 12f, 360f, 320f);
    Vector2 scroll;

    void Awake()
    {
        if (host == null)
            host = FindFirstObjectByType<RenderTestFrameHost>();
    }

    void OnGUI()
    {
        if (host == null)
            return;

        if (showCenterReticle)
            DrawCenterReticle();
        if (showPanel)
            windowRect = GUI.Window(192001, windowRect, DrawWindow, "光追 / 景深 调参");
    }

    void DrawCenterReticle()
    {
        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        if (host.RuntimeHasPointerScreenPos)
        {
            Vector2 p = host.RuntimePointerScreenPos;
            cx = p.x;
            // Input screen position uses bottom-left origin; OnGUI uses top-left origin.
            cy = Screen.height - p.y;
        }
        float t = Mathf.Max(1f, reticleThickness);
        float gap = Mathf.Max(1f, reticleGap);
        float arm = Mathf.Max(2f, reticleArm);

        Color old = GUI.color;
        float flash = host.RuntimeCenterHitFlash01;
        Color baseColor = host.RuntimeFocusProbeHit ? reticleHoverHitColor : reticleColor;
        GUI.color = Color.Lerp(baseColor, reticleHitFlashColor, flash);

        // Horizontal
        GUI.DrawTexture(new Rect(cx - gap - arm, cy - t * 0.5f, arm, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx + gap, cy - t * 0.5f, arm, t), Texture2D.whiteTexture);
        // Vertical
        GUI.DrawTexture(new Rect(cx - t * 0.5f, cy - gap - arm, t, arm), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - t * 0.5f, cy + gap, t, arm), Texture2D.whiteTexture);
        // Hit flash center dot
        if (flash > 1e-3f)
        {
            float d = Mathf.Lerp(3f, 9f, flash);
            GUI.DrawTexture(new Rect(cx - d * 0.5f, cy - d * 0.5f, d, d), Texture2D.whiteTexture);
        }
        GUI.color = old;
    }

    void DrawWindow(int windowId)
    {
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Width(340f), GUILayout.Height(255f));

        GUILayout.Label("光圈半径（越大焦外越虚）");
        host.RuntimeApertureRadius = GUILayout.HorizontalSlider(host.RuntimeApertureRadius, 0f, 0.25f);
        GUILayout.Label($"值: {host.RuntimeApertureRadius:F4}");

        GUILayout.Space(8f);
        GUILayout.Label("对焦距离（沿相机 forward，世界单位）");
        host.RuntimeFocusDistance = GUILayout.HorizontalSlider(host.RuntimeFocusDistance, 0.5f, host.RuntimeFocusDistanceMax);
        GUILayout.Label($"值: {host.RuntimeFocusDistance:F2}");
        if (host.RuntimeHasMeasuredDistance)
        {
            GUILayout.Label($"目标测距: {host.RuntimeMeasuredDistance:F2} m");
            GUILayout.Label($"合焦误差: {host.RuntimeFocusDistanceError:F2} m");
            GUILayout.Label(host.RuntimeIsFocusMatched ? "状态: 已合焦" : "状态: 未合焦");
        }
        else
        {
            GUILayout.Label("目标测距: 点击画面目标后显示");
        }
        GUILayout.Label($"测距探针: {host.RuntimeFocusProbeStatus}");
        GUILayout.Label($"探测耗时: {host.RuntimeFocusProbeLatencyMs:F2} ms");
        host.RuntimeFocusProbeOnHover = GUILayout.Toggle(host.RuntimeFocusProbeOnHover, "开启悬停连续测距");
        GUILayout.Label("悬停探测间隔（秒）");
        host.RuntimeFocusProbeInterval = GUILayout.HorizontalSlider(host.RuntimeFocusProbeInterval, 0.02f, 0.20f);
        GUILayout.Label($"值: {host.RuntimeFocusProbeInterval:F2}s，最近探测距今: {host.RuntimeFocusProbeAge:F2}s");

        GUILayout.Space(8f);
        GUILayout.Label("CoC 低阈（越小越容易用 8/16 spp）。0 = 自动");
        host.RuntimeCocThresholdLo = GUILayout.HorizontalSlider(host.RuntimeCocThresholdLo, 0f, 0.05f);
        GUILayout.Label($"Lo: {host.RuntimeCocThresholdLo:F5}");

        GUILayout.Space(4f);
        GUILayout.Label("CoC 高阈（越大越容易上 16 spp）。0 = 自动");
        host.RuntimeCocThresholdHi = GUILayout.HorizontalSlider(host.RuntimeCocThresholdHi, 0f, 0.15f);
        GUILayout.Label($"Hi: {host.RuntimeCocThresholdHi:F5}");

        if (GUILayout.Button("CoC 阈值恢复自动 (0 / 0)"))
        {
            host.RuntimeCocThresholdLo = 0f;
            host.RuntimeCocThresholdHi = 0f;
        }

        GUILayout.Space(10f);
        host.RuntimePinholeOnly = GUILayout.Toggle(host.RuntimePinholeOnly, "强制针孔（关景深）");
        host.RuntimeHeatMapMode = GUILayout.Toggle(host.RuntimeHeatMapMode, "热力图（绿/黄/红 = 4/8/16 spp）");
        if (host.RuntimePinholeOnly)
            GUILayout.Label("提示：当前是针孔模式，景深虚化会被禁用。");
        else if (host.RuntimeApertureRadius < 0.01f)
            GUILayout.Label("提示：光圈半径很小，虚化会很弱。");
        else if (host.RuntimeFocusDistance > host.RuntimeFocusDistanceMax * 0.85f)
            GUILayout.Label("提示：焦距接近上限，远处场景会更容易看起来“全清晰”。");

        GUILayout.EndScrollView();

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }
}
