using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 手动自由相机：右键看向 + WASD/空格/Ctrl 移动。
/// 用于替代场景里自动移动的 CameraControl。
/// </summary>
[DisallowMultipleComponent]
public sealed class RaytraceFreeCameraController : MonoBehaviour
{
    [SerializeField] float moveSpeed = 6f;
    [SerializeField] float sprintMultiplier = 2.2f;
    [SerializeField] float lookSensitivity = 0.12f;

    float yaw;
    float pitch;

    void Awake()
    {
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x;
    }

    void Update()
    {
        // 三步对焦模式下明确禁用相机移动/看向输入。
        return;
    }

    void UpdateLook()
    {
        Vector2 delta = GetLookDelta();
        yaw += delta.x * lookSensitivity;
        pitch -= delta.y * lookSensitivity;
        pitch = Mathf.Clamp(pitch, -89f, 89f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void UpdateMove()
    {
        float dt = Time.unscaledDeltaTime;
        Vector3 move = Vector3.zero;
        Vector2 axis = GetMoveAxis();
        move += transform.forward * axis.y;
        move += transform.right * axis.x;
        if (IsUpHeld())
            move += Vector3.up;
        if (IsDownHeld())
            move += Vector3.down;
        if (move.sqrMagnitude > 1e-6f)
            move.Normalize();

        float spd = moveSpeed * (IsSprintHeld() ? sprintMultiplier : 1f);
        transform.position += move * (spd * dt);
    }

    static bool IsKeyPressedLegacy(KeyCode key)
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKey(key);
#else
        return false;
#endif
    }

    static Vector2 GetMoveAxis()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            float x = (Keyboard.current.dKey.isPressed ? 1f : 0f) - (Keyboard.current.aKey.isPressed ? 1f : 0f);
            float y = (Keyboard.current.wKey.isPressed ? 1f : 0f) - (Keyboard.current.sKey.isPressed ? 1f : 0f);
            if (Mathf.Abs(x) > 1e-4f || Mathf.Abs(y) > 1e-4f)
                return new Vector2(x, y);
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return new Vector2(
            (IsKeyPressedLegacy(KeyCode.D) ? 1f : 0f) - (IsKeyPressedLegacy(KeyCode.A) ? 1f : 0f),
            (IsKeyPressedLegacy(KeyCode.W) ? 1f : 0f) - (IsKeyPressedLegacy(KeyCode.S) ? 1f : 0f));
#else
        return Vector2.zero;
#endif
    }

    static Vector2 GetLookDelta()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
            return Mouse.current.delta.ReadValue();
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#else
        return Vector2.zero;
#endif
    }

    static bool IsRightMouseHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
            return Mouse.current.rightButton.isPressed;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetMouseButton(1);
#else
        return false;
#endif
    }

    static bool IsUpHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
            return Keyboard.current.spaceKey.isPressed || Keyboard.current.eKey.isPressed;
#endif
        return IsKeyPressedLegacy(KeyCode.Space) || IsKeyPressedLegacy(KeyCode.E);
    }

    static bool IsDownHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
            return Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.qKey.isPressed;
#endif
        return IsKeyPressedLegacy(KeyCode.LeftControl) || IsKeyPressedLegacy(KeyCode.Q);
    }

    static bool IsSprintHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
            return Keyboard.current.leftShiftKey.isPressed;
#endif
        return IsKeyPressedLegacy(KeyCode.LeftShift);
    }
}

