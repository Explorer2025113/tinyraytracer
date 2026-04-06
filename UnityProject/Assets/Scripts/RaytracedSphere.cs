using UnityEngine;

/// <summary>
/// 标记此物体参与 CPU 光追（球体）。挂在任意 GameObject 上；半径优先用 <see cref="radiusWorldOverride"/>，
/// 否则从 <see cref="SphereCollider"/> + 缩放推算，再否则 0.5。
/// </summary>
[DisallowMultipleComponent]
public sealed class RaytracedSphere : MonoBehaviour
{
    [Tooltip(">0 时强制使用该世界空间半径，忽略 Collider。")]
    [Min(0f)]
    [SerializeField]
    float radiusWorldOverride;

    [SerializeField]
    Color albedo = Color.gray;

    [SerializeField]
    RenderTestFrameHost.NativeMaterial material = RenderTestFrameHost.NativeMaterial.Diffuse;

    /// <summary>世界空间半径（供 Host 收集时使用）。</summary>
    public float GetWorldRadius()
    {
        if (radiusWorldOverride > 1e-4f)
            return Mathf.Max(1e-4f, radiusWorldOverride);
        if (TryGetComponent<SphereCollider>(out SphereCollider sc))
        {
            Vector3 l = transform.lossyScale;
            float m = Mathf.Max(Mathf.Abs(l.x), Mathf.Abs(l.y), Mathf.Abs(l.z));
            return Mathf.Max(1e-4f, sc.radius * m);
        }

        return 0.5f;
    }

    public RenderTestFrameHost.TracedSphere ToTracedSphere()
    {
        return new RenderTestFrameHost.TracedSphere
        {
            transform = transform,
            radius = GetWorldRadius(),
            albedo = albedo,
            material = material
        };
    }

    void OnDrawGizmosSelected()
    {
        float r = GetWorldRadius();
        Gizmos.color = new Color(albedo.r, albedo.g, albedo.b, 0.35f);
        Gizmos.DrawWireSphere(transform.position, r);
    }
}
