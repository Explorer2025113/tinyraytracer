#include "raytracer_api.h"
#include "mesh_bvh.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace {

struct Vec3 {
    float x{};
    float y{};
    float z{};
};

inline Vec3 make_vec(const NativeVec3& v) { return {v.x, v.y, v.z}; }

inline Vec3 operator+(Vec3 a, Vec3 b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }

inline Vec3 operator-(Vec3 a, Vec3 b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }

inline Vec3 operator*(Vec3 v, float s) { return {v.x * s, v.y * s, v.z * s}; }

inline Vec3 operator*(float s, Vec3 v) { return v * s; }

inline Vec3 mul(Vec3 a, Vec3 b) { return {a.x * b.x, a.y * b.y, a.z * b.z}; }

inline float dot(Vec3 a, Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

inline float length(Vec3 v) { return std::sqrt(dot(v, v)); }

inline Vec3 normalize(Vec3 v) {
    const float len = length(v);
    if (len <= 1e-20f) {
        return {0.f, 1.f, 0.f};
    }
    return v * (1.f / len);
}

inline Vec3 reflect_in(Vec3 dir_in, Vec3 n) {
    return dir_in - n * (2.f * dot(dir_in, n));
}

inline Vec3 refract_in(Vec3 uv, Vec3 n, float eta_ratio) {
    const float cos_theta = std::min(dot(-1.f * uv, n), 1.f);
    const Vec3 r_out_perp = (uv + n * cos_theta) * eta_ratio;
    const float k = std::max(0.f, 1.f - dot(r_out_perp, r_out_perp));
    const Vec3 r_out_parallel = n * -std::sqrt(k);
    return r_out_perp + r_out_parallel;
}

inline float schlick_reflectance(float cos_theta, float ior) {
    float r0 = (1.f - ior) / (1.f + ior);
    r0 = r0 * r0;
    const float m = 1.f - cos_theta;
    return r0 + (1.f - r0) * m * m * m * m * m;
}

inline float hit_epsilon(float t) {
    return std::max(1e-4f, t * 1e-5f);
}

inline uint32_t wang_hash(uint32_t x) {
    x = (x ^ 61u) ^ (x >> 16);
    x *= 9u;
    x = x ^ (x >> 4);
    x *= 0x27d4eb2du;
    x = x ^ (x >> 15);
    return x;
}

struct Ray {
    Vec3 origin;
    Vec3 dir;
};

struct Hit {
    float t{1e30f};
    Vec3 point{};
    Vec3 normal{};
    int front_face{1};
    int material{0};
    Vec3 albedo{1.f, 1.f, 1.f};
};

inline bool sphere_hit(const NativeSphere& sp, const Ray& r, float t_min, float t_max, Hit& out) {
    const Vec3 c = make_vec(sp.center);
    const Vec3 oc = r.origin - c;
    const float rr = sp.radius;
    const float b_half = dot(oc, r.dir);
    const float c_const = dot(oc, oc) - rr * rr;
    const float disc = b_half * b_half - c_const;
    if (disc < 0.f) {
        return false;
    }
    const float s = std::sqrt(disc);
    float t = -b_half - s;
    if (t <= t_min || t >= t_max) {
        t = -b_half + s;
        if (t <= t_min || t >= t_max) {
            return false;
        }
    }
    out.t = t;
    out.point = r.origin + r.dir * t;
    const Vec3 outward = normalize(out.point - c);
    out.front_face = dot(r.dir, outward) < 0.f ? 1 : 0;
    out.normal = out.front_face ? outward : (outward * -1.f);
    out.material = sp.material;
    out.albedo = make_vec(sp.albedo);
    return true;
}

inline bool world_hit(const NativeSphere* spheres, int sphere_count, const Ray& r, float t_min,
                      float t_max, Hit& rec) {
    Hit best{};
    best.t = t_max;
    bool any = false;
    Hit tmp{};
    for (int i = 0; i < sphere_count; ++i) {
        if (sphere_hit(spheres[i], r, t_min, best.t, tmp)) {
            any = true;
            best = tmp;
        }
    }
    rt::SceneHit mh{};
    if (rt::mesh_trace_closest(r.origin.x, r.origin.y, r.origin.z, r.dir.x, r.dir.y, r.dir.z, t_min,
                               best.t, &mh)) {
        if (mh.t < best.t) {
            any = true;
            best.t = mh.t;
            best.point = {mh.px, mh.py, mh.pz};
            best.normal = {mh.nx, mh.ny, mh.nz};
            best.front_face = mh.front_face != 0 ? 1 : 0;
            best.material = mh.material;
            best.albedo = {mh.ax, mh.ay, mh.az};
        }
    }
    if (any) {
        rec = best;
        return true;
    }
    return false;
}

inline bool world_hit_any(const NativeSphere* spheres, int sphere_count, const Ray& r, float t_min,
                          float t_max) {
    Hit tmp{};
    for (int i = 0; i < sphere_count; ++i) {
        if (sphere_hit(spheres[i], r, t_min, t_max, tmp)) {
            return true;
        }
    }
    return rt::mesh_any_hit(r.origin.x, r.origin.y, r.origin.z, r.dir.x, r.dir.y, r.dir.z, t_min,
                            t_max);
}

struct Lighting {
    int mode{0}; // 0 directional, 1 point
    Vec3 dir{0.45f, 0.85f, 0.35f}; // normalized, from hit -> light
    Vec3 pos{0.f, 4.f, 0.f};
    Vec3 color{1.f, 0.98f, 0.92f};
    float intensity{1.f};
};

inline Vec3 trace_sky(Vec3 dir) {
    const float t = 0.5f * (normalize(dir).y + 1.f);
    const Vec3 c0{0.02f, 0.04f, 0.12f};
    const Vec3 c1{0.35f, 0.55f, 0.95f};
    return c0 * (1.f - t) + c1 * t;
}

inline Vec3 trace(const NativeSphere* spheres, int sphere_count, const Ray& r, int depth,
                  const Lighting& lighting, Vec3 ambient) {
    Hit h{};
    if (!world_hit(spheres, sphere_count, r, 1e-3f, 1e30f, h)) {
        return trace_sky(r.dir);
    }

    if (h.material == 1) {
        if (depth <= 0) {
            return {0.f, 0.f, 0.f};
        }
        const Vec3 refl_dir = reflect_in(r.dir, h.normal);
        const float eps = hit_epsilon(h.t);
        const Ray next{h.point + h.normal * eps, normalize(refl_dir)};
        const Vec3 incoming = trace(spheres, sphere_count, next, depth - 1, lighting, ambient);
        return mul(h.albedo, incoming);
    }

    if (h.material == 2) {
        if (depth <= 0) {
            return {0.f, 0.f, 0.f};
        }
        constexpr float ior_glass = 1.31f;
        constexpr float ior_air = 1.0f;
        const Vec3 unit_dir = normalize(r.dir);
        const float eta_ratio = h.front_face ? (ior_air / ior_glass) : (ior_glass / ior_air);
        const float cos_theta = std::min(dot(-1.f * unit_dir, h.normal), 1.f);
        const float sin_theta = std::sqrt(std::max(0.f, 1.f - cos_theta * cos_theta));
        const bool cannot_refract = eta_ratio * sin_theta > 1.f;
        const float reflect_prob = schlick_reflectance(cos_theta, ior_glass);

        const Vec3 refl_dir = normalize(reflect_in(unit_dir, h.normal));
        const float eps = hit_epsilon(h.t);
        const Ray refl_ray{h.point + refl_dir * eps, refl_dir};
        const Vec3 refl_col = trace(spheres, sphere_count, refl_ray, depth - 1, lighting, ambient);
        if (cannot_refract) {
            return mul(h.albedo, refl_col);
        }

        const Vec3 refr_dir = normalize(refract_in(unit_dir, h.normal, eta_ratio));
        const Ray refr_ray{h.point + refr_dir * eps, refr_dir};
        const Vec3 refr_col = trace(spheres, sphere_count, refr_ray, depth - 1, lighting, ambient);
        return mul(h.albedo, refl_col * reflect_prob + refr_col * (1.f - reflect_prob));
    }

    Vec3 ldir{};
    float max_shadow_t = 1e20f;
    float attenuation = 1.f;
    if (lighting.mode == 1) {
        const Vec3 to_light = lighting.pos - h.point;
        const float dist = std::max(1e-3f, length(to_light));
        ldir = to_light * (1.f / dist);
        max_shadow_t = dist - 1e-3f;
        attenuation = 1.f / (1.f + 0.02f * dist * dist);
    } else {
        ldir = normalize(lighting.dir);
    }
    const float ndl = dot(h.normal, ldir);
    const Vec3 view_dir = normalize(r.dir * -1.f);
    const Vec3 half_vec = normalize(ldir + view_dir);
    float spec_exp = 50.f;
    float spec_strength = 0.16f;
    if (h.material == 1) {
        spec_exp = 512.f;
        spec_strength = 0.06f;
    } else if (h.material == 2) {
        spec_exp = 125.f;
        spec_strength = 0.18f;
    }
    const float spec =
        (ndl > 0.f) ? (std::pow(std::max(0.f, dot(h.normal, half_vec)), spec_exp) * spec_strength)
                    : 0.f;
    float direct = 0.f;
    if (ndl > 0.f) {
        const float eps = hit_epsilon(h.t);
        const Ray shadow{h.point + h.normal * eps, ldir};
        if (!world_hit_any(spheres, sphere_count, shadow, 1e-3f, max_shadow_t)) {
            direct = ndl;
        }
    }
    const Vec3 lit = ambient + lighting.color * (lighting.intensity * attenuation * direct);
    return mul(h.albedo, lit) + lighting.color * (lighting.intensity * attenuation * spec);
}

/// Unity Texture2D raw RGBA: row 0 = image bottom. Ray loop uses y=0 at bottom, t increasing upward.
inline void write_pixel_rgba_unity(unsigned char* buffer, int width, int /*height*/, int x, int y_from_bottom,
                                   const Vec3& linear_rgb) {
    const int unity_row = y_from_bottom;
    const std::size_t i =
        (static_cast<std::size_t>(unity_row) * static_cast<std::size_t>(width) +
         static_cast<std::size_t>(x)) *
        4u;
    const auto clamp_byte = [](float v) -> unsigned char {
        // Unity UI samples color textures as sRGB in linear workflow, so encode linear -> sRGB here.
        const float c = std::clamp(v, 0.f, 1.f);
        const float srgb = std::pow(c, 1.f / 2.2f);
        const int iv = static_cast<int>(std::lround(srgb * 255.f));
        return static_cast<unsigned char>(std::clamp(iv, 0, 255));
    };
    buffer[i + 0] = clamp_byte(linear_rgb.x);
    buffer[i + 1] = clamp_byte(linear_rgb.y);
    buffer[i + 2] = clamp_byte(linear_rgb.z);
    buffer[i + 3] = 255;
}

/// Blur metric for tiering (larger = more defocus). Thin-lens style: |1/z - 1/zf| scaled by aperture.
inline float coc_metric(float z_hit, float z_focus, float aperture_r) {
    const float inv_z = 1.f / z_hit;
    const float inv_zf = 1.f / std::max(z_focus, 1e-3f);
    return aperture_r * std::abs(inv_z - inv_zf);
}

inline int spp_tier(float coc, float t_lo, float t_hi) {
    if (coc <= t_lo) {
        return 4;
    }
    if (coc >= t_hi) {
        return 16;
    }
    return 8;
}

inline Vec3 heat_map_color(int spp) {
    if (spp <= 4) {
        return {0.15f, 0.95f, 0.2f};
    }
    if (spp <= 8) {
        return {0.95f, 0.9f, 0.15f};
    }
    return {0.95f, 0.2f, 0.18f};
}

/// Vogel / golden-angle disk: fewer "separated ghost" lobes than random rings at low spp.
inline void lens_disk_vogel(int sample_index, int spp, float aperture_r, const Vec3& u_axis,
                            const Vec3& v_axis, uint32_t pixel_scramble, Vec3& offset) {
    const float n = static_cast<float>(std::max(spp, 1));
    const float r = aperture_r * std::sqrt((static_cast<float>(sample_index) + 0.5f) / n);
    constexpr float golden_angle = 2.39996322972865332f; // pi * (3 - sqrt(5))
    constexpr float tau = 6.28318530717958647f;
    const float phase = static_cast<float>(pixel_scramble & 0xFFFFu) * (tau / 65536.f);
    const float theta = golden_angle * static_cast<float>(sample_index) + phase;
    offset = u_axis * (r * std::cos(theta)) + v_axis * (r * std::sin(theta));
}

} // namespace

void RenderPinholeSpheres(int width, int height, unsigned char* buffer, const NativeCamera* camera,
                          const NativeSphere* spheres, int sphere_count, int max_depth) {
    if (!buffer || width <= 0 || height <= 0 || !camera) {
        return;
    }
    if (sphere_count < 0) {
        return;
    }
    if (sphere_count > 0 && !spheres) {
        return;
    }

    const Vec3 origin = make_vec(camera->origin);
    const float aspect = camera->aspect > 1e-6f ? camera->aspect : 1.f;
    const float vfov = camera->vfov_degrees * 3.14159265f / 180.f;
    const float half_h = std::tan(vfov * 0.5f);
    const float half_w = aspect * half_h;

    const Vec3 w = normalize(make_vec(camera->forward));
    const Vec3 u = normalize(make_vec(camera->right));
    const Vec3 v = normalize(make_vec(camera->up));

    const Vec3 lower_left = origin + w - u * half_w - v * half_h;
    const Vec3 horizontal = u * (2.f * half_w);
    const Vec3 vertical = v * (2.f * half_h);

    const Vec3 ambient{0.12f, 0.12f, 0.14f};
    Lighting lighting{};
    lighting.mode = camera->light_mode;
    lighting.dir = normalize(make_vec(camera->light_dir));
    lighting.pos = make_vec(camera->light_pos);
    lighting.color = make_vec(camera->light_color);
    lighting.intensity = std::max(0.f, camera->light_intensity);

    const int md = std::clamp(max_depth, 1, 16);

    const bool use_pinhole =
        (camera->pinhole_only != 0) || (camera->aperture_radius <= 1e-7f);

    if (use_pinhole) {
#if defined(RT_USE_OPENMP)
#pragma omp parallel for schedule(dynamic, 1)
#endif
        for (int y = 0; y < height; ++y) {
            for (int x = 0; x < width; ++x) {
                const float s = (static_cast<float>(x) + 0.5f) / static_cast<float>(width);
                const float t = (static_cast<float>(y) + 0.5f) / static_cast<float>(height);
                const Vec3 dir = normalize(lower_left + horizontal * s + vertical * t - origin);
                const Ray ray{origin, dir};
                const Vec3 col = trace(spheres, sphere_count, ray, md, lighting, ambient);
                write_pixel_rgba_unity(buffer, width, height, x, y, col);
            }
        }
        return;
    }

    const float aperture_r = camera->aperture_radius;
    const float focus_dist = std::max(camera->focus_distance, 1e-2f);
    float t_lo = camera->coc_threshold_lo;
    float t_hi = camera->coc_threshold_hi;
    // Tie threshold to projected pixel size so behavior stays consistent across resolutions.
    if (t_lo <= 0.f) {
        const float pixel_scale = (2.f * std::tan(vfov * 0.5f)) / static_cast<float>(height);
        t_lo = pixel_scale * focus_dist;
    }
    if (t_hi <= 0.f) {
        t_hi = 2.5f * t_lo;
    }
    if (t_hi <= t_lo) {
        t_hi = t_lo * 2.f;
    }

    const int heat = camera->heat_map;

#if defined(RT_USE_OPENMP)
#pragma omp parallel for schedule(dynamic, 1)
#endif
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const float s = (static_cast<float>(x) + 0.5f) / static_cast<float>(width);
            const float tpx = (static_cast<float>(y) + 0.5f) / static_cast<float>(height);
            const Vec3 dir_chief =
                normalize(lower_left + horizontal * s + vertical * tpx - origin);
            const Ray chief{origin, dir_chief};
            Hit chief_hit{};
            if (!world_hit(spheres, sphere_count, chief, 1e-3f, 1e30f, chief_hit)) {
                if (heat) {
                    write_pixel_rgba_unity(buffer, width, height, x, y, {0.f, 0.f, 0.f});
                } else {
                    write_pixel_rgba_unity(buffer, width, height, x, y, trace_sky(dir_chief));
                }
                continue;
            }

            const float z_hit = std::max(1e-4f, dot(chief_hit.point - origin, w));
            const float coc = coc_metric(z_hit, focus_dist, aperture_r);
            const int spp = spp_tier(coc, t_lo, t_hi);

            if (heat) {
                write_pixel_rgba_unity(buffer, width, height, x, y, heat_map_color(spp));
                continue;
            }

            Vec3 accum{0.f, 0.f, 0.f};
            const uint32_t pixel_scramble =
                wang_hash(static_cast<uint32_t>(y * width + x));
            const float denom_w = dot(dir_chief, w);
            Vec3 focal_point{};
            if (std::fabs(denom_w) < 1e-5f) {
                focal_point = origin + w * focus_dist;
            } else {
                const float t_fp = focus_dist / denom_w;
                focal_point = origin + dir_chief * t_fp;
            }

            for (int si = 0; si < spp; ++si) {
                Vec3 lens_off{};
                lens_disk_vogel(si, spp, aperture_r, u, v, pixel_scramble, lens_off);
                const Vec3 lens_pos = origin + lens_off;
                const Vec3 dof_dir = normalize(focal_point - lens_pos);
                // Bias along ray, not world forward: +w broke coplanar lens sampling and caused heavy doubling.
                const Ray dof_ray{lens_pos + dof_dir * 1e-3f, dof_dir};
                accum = accum + trace(spheres, sphere_count, dof_ray, md, lighting, ambient);
            }
            const float inv = 1.f / static_cast<float>(spp);
            write_pixel_rgba_unity(buffer, width, height, x, y, accum * inv);
        }
    }
}
