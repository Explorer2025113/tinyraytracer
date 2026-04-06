#pragma once

#include "raytracer_api.h"

namespace rt {

void mesh_clear();
void mesh_upload(const NativeMeshTriangle* tris, int tri_count);

struct SceneHit {
    float t;
    float px;
    float py;
    float pz;
    float nx;
    float ny;
    float nz;
    int front_face;
    float ax;
    float ay;
    float az;
    int material;
};

/// Closest hit along ray in (t_min, t_max). Returns false if no hit. `out` only valid if true.
bool mesh_trace_closest(float ox,
                        float oy,
                        float oz,
                        float dx,
                        float dy,
                        float dz,
                        float t_min,
                        float t_max,
                        SceneHit* out);

/// Any hit in (t_min, t_max) - for shadow rays.
bool mesh_any_hit(float ox, float oy, float oz, float dx, float dy, float dz, float t_min, float t_max);

} // namespace rt
