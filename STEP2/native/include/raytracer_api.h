#pragma once

#ifdef _WIN32
#ifdef RAYTRACER_NATIVE_BUILD
#define RAYTRACER_API __declspec(dllexport)
#else
#define RAYTRACER_API __declspec(dllimport)
#endif
#else
#define RAYTRACER_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct NativeVec3 {
    float x;
    float y;
    float z;
} NativeVec3;

typedef struct NativeSphere {
    NativeVec3 center;
    float radius;
    NativeVec3 albedo;
    int material; /* 0 = diffuse, 1 = mirror, 2 = glass/ice */
} NativeSphere;

/// World-space triangle for BVH mesh. Layout must match C# sequential struct.
typedef struct NativeMeshTriangle {
    NativeVec3 v0;
    NativeVec3 v1;
    NativeVec3 v2;
    NativeVec3 n0;
    NativeVec3 n1;
    NativeVec3 n2;
    NativeVec3 c0;           /* per-vertex albedo sample (linear) */
    NativeVec3 c1;
    NativeVec3 c2;
    NativeVec3 albedo;       /* fallback flat color (linear) */
    int material; /* 0 = diffuse, 1 = mirror, 2 = glass/ice */
} NativeMeshTriangle;

typedef struct NativeCamera {
    NativeVec3 origin;
    NativeVec3 forward; /* Unity Camera: transform.forward (world) */
    NativeVec3 right;   /* Unity Camera: transform.right */
    NativeVec3 up;      /* Unity Camera: transform.up */
    float vfov_degrees;
    float aspect;
    /* Phase 3 thin lens + progressive sampling */
    float aperture_radius;   /* 0 and/or pinhole_only -> pinhole path */
    float focus_distance;    /* distance along forward to focal plane (world units) */
    float coc_threshold_lo;  /* CoC below -> 4 spp; 0 = auto from aperture */
    float coc_threshold_hi;  /* CoC above -> 16 spp; 0 = auto */
    int heat_map;            /* non-zero: write green/yellow/red by spp tier only */
    int pinhole_only;        /* non-zero: ignore aperture (Phase-2 pinhole) */
    /* Lighting controls (for interactive flashlight / scene light sync) */
    NativeVec3 light_dir;    /* used when light_mode==0; should be normalized */
    NativeVec3 light_pos;    /* used when light_mode==1 */
    NativeVec3 light_color;  /* linear RGB */
    float light_intensity;   /* multiplier */
    int light_mode;          /* 0=directional, 1=point */
} NativeCamera;

/// Fills `buffer` with solid blue (RGBA8, Unity-compatible row order: bottom row first).
/// Buffer size must be width * height * 4 bytes.
RAYTRACER_API void RenderTestFrame(int width, int height, unsigned char* buffer);

/// Must match C# check: bump when changing marshalled structs or entry points.
RAYTRACER_API int RaytracerNative_ApiVersion(void);

/// Pinhole if aperture_radius<=0 or pinhole_only!=0.
/// Else thin-lens: chief ray depth -> CoC -> 4/8/16 stratified lens samples; heat_map draws tier colors.
RAYTRACER_API void RenderPinholeSpheres(int width, int height, unsigned char* buffer,
                                        const NativeCamera* camera, const NativeSphere* spheres,
                                        int sphere_count, int max_depth);

/// Replaces triangle mesh used for ray hits. tri_count==0 clears mesh (spheres-only).
RAYTRACER_API void RaytracerNative_SetMeshTriangles(const NativeMeshTriangle* tris, int tri_count);

#ifdef __cplusplus
}
#endif
