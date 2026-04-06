#include "mesh_bvh.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <vector>

namespace {

struct Vec3 {
    float x{};
    float y{};
    float z{};
};

inline Vec3 operator+(Vec3 a, Vec3 b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }

inline Vec3 operator-(Vec3 a, Vec3 b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }

inline Vec3 operator*(Vec3 v, float s) { return {v.x * s, v.y * s, v.z * s}; }

inline float dot(Vec3 a, Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

inline Vec3 cross(Vec3 a, Vec3 b) {
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}

inline float length(Vec3 v) { return std::sqrt(dot(v, v)); }

inline Vec3 normalize(Vec3 v) {
    const float len = length(v);
    if (len <= 1e-20f) {
        return {0.f, 1.f, 0.f};
    }
    return v * (1.f / len);
}

struct Ray {
    Vec3 origin;
    Vec3 dir;
};

struct Tri {
    Vec3 v0;
    Vec3 v1;
    Vec3 v2;
    Vec3 n0;
    Vec3 n1;
    Vec3 n2;
    Vec3 c0;
    Vec3 c1;
    Vec3 c2;
    Vec3 alb;
    int mat{};
};

struct BvhNode {
    float bmin[3]{};
    float bmax[3]{};
    int left{-1};
    int right{-1};
    int leaf_begin{-1};
    int leaf_count{0};
};

static std::vector<Tri> g_tris;
static std::vector<int> g_order;
static std::vector<BvhNode> g_nodes;
static int g_root = -1;

inline void tri_expand_bounds(const Tri& tri, float* out_min, float* out_max) {
    for (int k = 0; k < 3; ++k) {
        const float c0 = (&tri.v0.x)[k];
        const float c1 = (&tri.v1.x)[k];
        const float c2 = (&tri.v2.x)[k];
        const float lo = std::min(c0, std::min(c1, c2));
        const float hi = std::max(c0, std::max(c1, c2));
        out_min[k] = std::min(out_min[k], lo);
        out_max[k] = std::max(out_max[k], hi);
    }
}

inline float tri_centroid_axis(const Tri& tri, int axis) {
    return ((&tri.v0.x)[axis] + (&tri.v1.x)[axis] + (&tri.v2.x)[axis]) * (1.f / 3.f);
}

inline int longest_axis(const float* bmin, const float* bmax) {
    const float dx = bmax[0] - bmin[0];
    const float dy = bmax[1] - bmin[1];
    const float dz = bmax[2] - bmin[2];
    if (dx >= dy && dx >= dz) {
        return 0;
    }
    if (dy >= dz) {
        return 1;
    }
    return 2;
}

inline void copy_bb(BvhNode& n, const float* bmin, const float* bmax) {
    for (int i = 0; i < 3; ++i) {
        n.bmin[i] = bmin[i];
        n.bmax[i] = bmax[i];
    }
}

inline void node_aabb(int node_idx, float* bmin, float* bmax) {
    const BvhNode& n = g_nodes[static_cast<std::size_t>(node_idx)];
    for (int i = 0; i < 3; ++i) {
        bmin[i] = n.bmin[i];
        bmax[i] = n.bmax[i];
    }
}

inline void merge_bb(const float* amin, const float* amax, const float* bmin, const float* bmax,
                     float* out_min, float* out_max) {
    for (int i = 0; i < 3; ++i) {
        out_min[i] = std::min(amin[i], bmin[i]);
        out_max[i] = std::max(amax[i], bmax[i]);
    }
}

inline bool aabb_ray(const float* bmin, const float* bmax, const Ray& r, float t_min, float t_max) {
    for (int a = 0; a < 3; ++a) {
        const float o = (&r.origin.x)[a];
        const float d = (&r.dir.x)[a];
        const float inv_d = 1.f / (std::fabs(d) > 1e-20f ? d : (d >= 0.f ? 1e-20f : -1e-20f));
        float t0 = ((&bmin[0])[a] - o) * inv_d;
        float t1 = ((&bmax[0])[a] - o) * inv_d;
        if (inv_d < 0.f) {
            std::swap(t0, t1);
        }
        t_min = std::max(t_min, t0);
        t_max = std::min(t_max, t1);
        if (t_max < t_min) {
            return false;
        }
    }
    return true;
}

inline bool tri_hit(const Tri& tri, const Ray& r, float t_min, float t_max, rt::SceneHit& out) {
    const Vec3 e1 = tri.v1 - tri.v0;
    const Vec3 e2 = tri.v2 - tri.v0;
    const Vec3 p = cross(r.dir, e2);
    const float det = dot(e1, p);
    if (std::fabs(det) < 1e-20f) {
        return false;
    }
    const float inv_det = 1.f / det;
    const Vec3 tv = r.origin - tri.v0;
    const float u = dot(tv, p) * inv_det;
    if (u < 0.f || u > 1.f) {
        return false;
    }
    const Vec3 q = cross(tv, e1);
    const float v = dot(r.dir, q) * inv_det;
    if (v < 0.f || u + v > 1.f) {
        return false;
    }
    const float tt = dot(e2, q) * inv_det;
    if (tt <= t_min || tt >= t_max) {
        return false;
    }
    out.t = tt;
    const Vec3 pos = r.origin + r.dir * tt;
    out.px = pos.x;
    out.py = pos.y;
    out.pz = pos.z;
    const float w = 1.f - u - v;
    Vec3 n = normalize(tri.n0 * w + tri.n1 * u + tri.n2 * v);
    if (length(n) <= 1e-6f) {
        n = normalize(cross(e1, e2));
    }
    const bool front_face = dot(n, r.dir) < 0.f;
    if (!front_face) {
        n = n * -1.f;
    }
    out.nx = n.x;
    out.ny = n.y;
    out.nz = n.z;
    out.front_face = front_face ? 1 : 0;
    Vec3 c = tri.c0 * w + tri.c1 * u + tri.c2 * v;
    if (c.x <= 1e-6f && c.y <= 1e-6f && c.z <= 1e-6f) {
        c = tri.alb;
    }
    out.ax = c.x;
    out.ay = c.y;
    out.az = c.z;
    out.material = tri.mat;
    return true;
}

bool trace_closest_leaf(int node_idx, const Ray& r, float t_min, float t_max, rt::SceneHit& best) {
    const BvhNode& n = g_nodes[static_cast<std::size_t>(node_idx)];
    if (!aabb_ray(n.bmin, n.bmax, r, t_min, t_max)) {
        return false;
    }
    if (n.leaf_count > 0) {
        bool any = false;
        for (int i = 0; i < n.leaf_count; ++i) {
            const int ti = g_order[static_cast<std::size_t>(n.leaf_begin + i)];
            rt::SceneHit tmp{};
            if (tri_hit(g_tris[static_cast<std::size_t>(ti)], r, t_min, best.t, tmp)) {
                best = tmp;
                any = true;
            }
        }
        return any;
    }
    bool hit = false;
    float cur_cap = best.t;
    if (n.left >= 0) {
        hit |= trace_closest_leaf(n.left, r, t_min, cur_cap, best);
    }
    cur_cap = best.t;
    if (n.right >= 0) {
        hit |= trace_closest_leaf(n.right, r, t_min, cur_cap, best);
    }
    return hit;
}

bool any_hit_leaf(int node_idx, const Ray& r, float t_min, float t_max) {
    const BvhNode& n = g_nodes[static_cast<std::size_t>(node_idx)];
    if (!aabb_ray(n.bmin, n.bmax, r, t_min, t_max)) {
        return false;
    }
    if (n.leaf_count > 0) {
        rt::SceneHit tmp{};
        for (int i = 0; i < n.leaf_count; ++i) {
            const int ti = g_order[static_cast<std::size_t>(n.leaf_begin + i)];
            if (tri_hit(g_tris[static_cast<std::size_t>(ti)], r, t_min, t_max, tmp)) {
                return true;
            }
        }
        return false;
    }
    if (n.left >= 0 && any_hit_leaf(n.left, r, t_min, t_max)) {
        return true;
    }
    if (n.right >= 0 && any_hit_leaf(n.right, r, t_min, t_max)) {
        return true;
    }
    return false;
}

int build_bvh(std::vector<int>& ord, int begin, int end) {
    const int n = end - begin;
    float bmin[3] = {1e30f, 1e30f, 1e30f};
    float bmax[3] = {-1e30f, -1e30f, -1e30f};
    for (int i = begin; i < end; ++i) {
        tri_expand_bounds(g_tris[static_cast<std::size_t>(ord[static_cast<std::size_t>(i)])], bmin,
                          bmax);
    }
    if (n <= 4) {
        BvhNode leaf{};
        copy_bb(leaf, bmin, bmax);
        leaf.left = leaf.right = -1;
        leaf.leaf_begin = begin;
        leaf.leaf_count = n;
        g_nodes.push_back(leaf);
        return static_cast<int>(g_nodes.size()) - 1;
    }
    const int axis = longest_axis(bmin, bmax);
    const int mid = begin + n / 2;
    std::nth_element(
        ord.begin() + begin, ord.begin() + mid, ord.begin() + end,
        [&](int ia, int ib) { return tri_centroid_axis(g_tris[static_cast<std::size_t>(ia)], axis) <
                                      tri_centroid_axis(g_tris[static_cast<std::size_t>(ib)], axis); });
    const int left = build_bvh(ord, begin, mid);
    const int right = build_bvh(ord, mid, end);
    float lbmin[3], lbmax[3], rbmin[3], rbmax[3];
    node_aabb(left, lbmin, lbmax);
    node_aabb(right, rbmin, rbmax);
    float mbmin[3], mbmax[3];
    merge_bb(lbmin, lbmax, rbmin, rbmax, mbmin, mbmax);
    BvhNode internal{};
    copy_bb(internal, mbmin, mbmax);
    internal.left = left;
    internal.right = right;
    internal.leaf_begin = -1;
    internal.leaf_count = 0;
    g_nodes.push_back(internal);
    return static_cast<int>(g_nodes.size()) - 1;
}

} // namespace

namespace rt {

void mesh_clear() {
    g_tris.clear();
    g_order.clear();
    g_nodes.clear();
    g_root = -1;
}

void mesh_upload(const NativeMeshTriangle* tris, int tri_count) {
    mesh_clear();
    if (tri_count <= 0 || tris == nullptr) {
        return;
    }
    g_tris.resize(static_cast<std::size_t>(tri_count));
    for (int i = 0; i < tri_count; ++i) {
        const NativeMeshTriangle& m = tris[i];
        Tri& t = g_tris[static_cast<std::size_t>(i)];
        t.v0 = {m.v0.x, m.v0.y, m.v0.z};
        t.v1 = {m.v1.x, m.v1.y, m.v1.z};
        t.v2 = {m.v2.x, m.v2.y, m.v2.z};
        t.n0 = {m.n0.x, m.n0.y, m.n0.z};
        t.n1 = {m.n1.x, m.n1.y, m.n1.z};
        t.n2 = {m.n2.x, m.n2.y, m.n2.z};
        t.c0 = {m.c0.x, m.c0.y, m.c0.z};
        t.c1 = {m.c1.x, m.c1.y, m.c1.z};
        t.c2 = {m.c2.x, m.c2.y, m.c2.z};
        t.alb = {m.albedo.x, m.albedo.y, m.albedo.z};
        t.mat = m.material;
    }
    g_order.resize(static_cast<std::size_t>(tri_count));
    for (int i = 0; i < tri_count; ++i) {
        g_order[static_cast<std::size_t>(i)] = i;
    }
    g_nodes.reserve(static_cast<std::size_t>(tri_count) * 2);
    g_root = build_bvh(g_order, 0, tri_count);
}

bool mesh_trace_closest(float ox,
                        float oy,
                        float oz,
                        float dx,
                        float dy,
                        float dz,
                        float t_min,
                        float t_max,
                        SceneHit* out) {
    if (g_root < 0 || out == nullptr) {
        return false;
    }
    Ray r{{ox, oy, oz}, {dx, dy, dz}};
    SceneHit best{};
    best.t = t_max;
    if (!trace_closest_leaf(g_root, r, t_min, t_max, best) || best.t >= t_max) {
        return false;
    }
    *out = best;
    return true;
}

bool mesh_any_hit(float ox, float oy, float oz, float dx, float dy, float dz, float t_min, float t_max) {
    if (g_root < 0) {
        return false;
    }
    Ray r{{ox, oy, oz}, {dx, dy, dz}};
    return any_hit_leaf(g_root, r, t_min, t_max);
}

} // namespace rt

extern "C" RAYTRACER_API void RaytracerNative_SetMeshTriangles(const NativeMeshTriangle* tris,
                                                               int tri_count) {
    rt::mesh_upload(tris, tri_count);
}
