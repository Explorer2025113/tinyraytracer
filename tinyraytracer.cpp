#include <tuple>
#include <vector>
#include <fstream>
#include <algorithm>
#include <cmath>
#include <random>
#include <chrono>
#include <thread>
#include <cstdint>
#include <string>

struct vec3 {
    float x=0, y=0, z=0;
          float& operator[](const int i)       { return i==0 ? x : (1==i ? y : z); }
    const float& operator[](const int i) const { return i==0 ? x : (1==i ? y : z); }
    vec3  operator*(const float v) const { return {x*v, y*v, z*v};       }
    float operator*(const vec3& v) const { return x*v.x + y*v.y + z*v.z; }
    vec3  operator+(const vec3& v) const { return {x+v.x, y+v.y, z+v.z}; }
    vec3  operator-(const vec3& v) const { return {x-v.x, y-v.y, z-v.z}; }
    vec3  operator-()              const { return {-x, -y, -z};          }
    float norm() const { return std::sqrt(x*x+y*y+z*z); }
    vec3 normalized() const { return (*this)*(1.f/norm()); }
};

vec3 cross(const vec3 v1, const vec3 v2) {
    return { v1.y*v2.z - v1.z*v2.y, v1.z*v2.x - v1.x*v2.z, v1.x*v2.y - v1.y*v2.x };
}

struct Material {
    float refractive_index = 1;
    float albedo[4] = {2,0,0,0};
    vec3 diffuse_color = {0,0,0};
    float specular_exponent = 0;
};

struct Sphere {
    vec3 center;
    float radius;
    Material material;
};

constexpr Material      ivory = {1.0, {0.9,  0.5, 0.1, 0.0}, {0.4, 0.4, 0.3},   50.};
constexpr Material      glass = {1.5, {0.0,  0.9, 0.1, 0.8}, {0.6, 0.7, 0.8},  125.};
constexpr Material red_rubber = {1.0, {1.4,  0.3, 0.0, 0.0}, {0.3, 0.1, 0.1},   10.};
constexpr Material     mirror = {1.0, {0.0, 16.0, 0.8, 0.0}, {1.0, 1.0, 1.0}, 1425.};

constexpr Sphere spheres[] = {
    {{-3,    0,   -16}, 2,      ivory},
    {{-1.0, -1.5, -12}, 2,      glass},
    {{ 1.5, -0.5, -18}, 3, red_rubber},
    {{ 7,    5,   -18}, 4,     mirror}
};

constexpr vec3 lights[] = {
    {-20, 20,  20},
    { 30, 50, -25},
    { 30, 20,  30}
};

vec3 reflect(const vec3 &I, const vec3 &N) {
    return I - N*2.f*(I*N);
}

vec3 refract(const vec3 &I, const vec3 &N, const float eta_t, const float eta_i=1.f) { // Snell's law
    float cosi = - std::max(-1.f, std::min(1.f, I*N));
    if (cosi<0) return refract(I, -N, eta_i, eta_t); // if the ray comes from the inside the object, swap the air and the media
    float eta = eta_i / eta_t;
    float k = 1 - eta*eta*(1 - cosi*cosi);
    return k<0 ? vec3{1,0,0} : I*eta + N*(eta*cosi - std::sqrt(k)); // k<0 = total reflection, no ray to refract. I refract it anyways, this has no physical meaning
}

static float randf() {
    // OpenMP 并行下使用线程局部 RNG，避免 rand() 数据竞争
    thread_local std::mt19937 rng(static_cast<uint32_t>(
        std::chrono::high_resolution_clock::now().time_since_epoch().count() ^
        std::hash<std::thread::id>{}(std::this_thread::get_id())
    ));
    thread_local std::uniform_real_distribution<float> dist(0.f, 1.f);
    return dist(rng);
}

// 在单位圆盘内随机取点（z=0），用于透镜光圈采样
vec3 random_in_unit_disk() {
    while (true) {
        vec3 p{randf()*2.f - 1.f, randf()*2.f - 1.f, 0.f};
        if (p*p < 1.f) return p;
    }
}

// --- Progressive lens sampling helpers (paper-inspired, thin-lens DOF) ---
static uint32_t wang_hash(uint32_t a) {
    a = (a ^ 61u) ^ (a >> 16u);
    a = a + (a << 3u);
    a = a ^ (a >> 4u);
    a = a * 0x27d4eb2du;
    a = a ^ (a >> 15u);
    return a;
}

static float halton(uint32_t index, uint32_t base) {
    // Radical inverse in the given base; returns value in [0,1).
    float f = 1.f;
    float r = 0.f;
    while (index > 0) {
        f /= (float)base;
        r += f * (float)(index % base);
        index /= base;
    }
    return r;
}

// Progressive disk sampling with a polar4-like quadrant split.
// The sequence has the prefix property: samples [0..K) are a subset of [0..K2) for K < K2.
vec3 progressive_in_unit_disk(const uint32_t sample_idx, const uint32_t scramble) {
    // Low-discrepancy points in [0,1).
    float u = halton(sample_idx ^ scramble, 2);
    float v = halton(sample_idx ^ wang_hash(scramble), 3);

    float r = std::sqrt(u);
    float a = v * 4.f;               // map to one of 4 quadrants
    int quad = (int)std::floor(a);  // 0..3
    float phi = (a - quad) * (3.14159265358979323846f * 0.5f); // [0, pi/2)

    float x = r * std::cos(phi);
    float y = r * std::sin(phi);

    // Rotate the point from first-quadrant to selected quadrant.
    vec3 p;
    switch (quad & 3) {
        case 0: p = { x,  y, 0.f}; break;
        case 1: p = {-y,  x, 0.f}; break;
        case 2: p = {-x, -y, 0.f}; break;
        default: p = { y, -x, 0.f}; break;
    }
    return p;
}

std::tuple<bool,float> ray_sphere_intersect(const vec3 &orig, const vec3 &dir, const Sphere &s) { // ret value is a pair [intersection found, distance]
    vec3 L = s.center - orig;
    float tca = L*dir;
    float d2 = L*L - tca*tca;
    if (d2 > s.radius*s.radius) return {false, 0};
    float thc = std::sqrt(s.radius*s.radius - d2);
    float t0 = tca-thc, t1 = tca+thc;
    if (t0>.001) return {true, t0};  // offset the original point by .001 to avoid occlusion by the object itself
    if (t1>.001) return {true, t1};
    return {false, 0};
}

std::tuple<bool,vec3,vec3,Material> scene_intersect(const vec3 &orig, const vec3 &dir) {
    vec3 pt, N;
    Material material;

    float nearest_dist = 1e10;
    if (std::abs(dir.y)>.001) { // intersect the ray with the checkerboard, avoid division by zero
        float d = -(orig.y+4)/dir.y; // the checkerboard plane has equation y = -4
        vec3 p = orig + dir*d;
        if (d>.001 && d<nearest_dist && std::abs(p.x)<10 && p.z<-10 && p.z>-30) {
            nearest_dist = d;
            pt = p;
            N = {0,1,0};
            material.diffuse_color = (int(.5*pt.x+1000) + int(.5*pt.z)) & 1 ? vec3{.3, .3, .3} : vec3{.3, .2, .1};
        }
    }

    for (const Sphere &s : spheres) { // intersect the ray with all spheres
        auto [intersection, d] = ray_sphere_intersect(orig, dir, s);
        if (!intersection || d > nearest_dist) continue;
        nearest_dist = d;
        pt = orig + dir*nearest_dist;
        N = (pt - s.center).normalized();
        material = s.material;
    }
    return { nearest_dist<1000, pt, N, material };
}

vec3 cast_ray(const vec3 &orig, const vec3 &dir, const int depth=0) {
    auto [hit, point, N, material] = scene_intersect(orig, dir);
    if (depth>4 || !hit)
        return {0.2, 0.7, 0.8}; // background color

    vec3 reflect_dir = reflect(dir, N).normalized();
    vec3 refract_dir = refract(dir, N, material.refractive_index).normalized();
    vec3 reflect_color = cast_ray(point, reflect_dir, depth + 1);
    vec3 refract_color = cast_ray(point, refract_dir, depth + 1);

    float diffuse_light_intensity = 0, specular_light_intensity = 0;
    for (const vec3 &light : lights) { // checking if the point lies in the shadow of the light
        vec3 light_dir = (light - point).normalized();
        auto [hit, shadow_pt, trashnrm, trashmat] = scene_intersect(point, light_dir);
        if (hit && (shadow_pt-point).norm() < (light-point).norm()) continue;
        diffuse_light_intensity  += std::max(0.f, light_dir*N);
        specular_light_intensity += std::pow(std::max(0.f, -reflect(-light_dir, N)*dir), material.specular_exponent);
    }
    return material.diffuse_color * diffuse_light_intensity * material.albedo[0] + vec3{1., 1., 1.}*specular_light_intensity * material.albedo[1] + reflect_color*material.albedo[2] + refract_color*material.albedo[3];
}

int main(int argc, char **argv) {
    // Default parameters (can be overridden from UI via CLI).
    int width = 1024;
    int height = 768;
    constexpr float fov = 1.05f; // 60 degrees field of view in radians
    float aperture = 0.5f;
    float focus_dist = 14.f;
    std::string out_path = "out_progressive_dof.ppm";

    for (int i = 1; i < argc; i++) {
        std::string a(argv[i]);
        auto next = [&](void) -> const char* { return (i + 1 < argc) ? argv[++i] : nullptr; };
        if (a == "--width") {
            const char* v = next();
            if (v) width = std::stoi(v);
        } else if (a == "--height") {
            const char* v = next();
            if (v) height = std::stoi(v);
        } else if (a == "--aperture") {
            const char* v = next();
            if (v) aperture = std::stof(v);
        } else if (a == "--focus_dist") {
            const char* v = next();
            if (v) focus_dist = std::stof(v);
        } else if (a == "--out") {
            const char* v = next();
            if (v) out_path = v;
        }
    }

    std::vector<vec3> framebuffer(width * height);
#pragma omp parallel for
    for (int pix = 0; pix<width*height; pix++) { // actual rendering loop
        float dir_x =  (pix%width + 0.5) -  width/2.;
        float dir_y = -(pix/width + 0.5) + height/2.; // this flips the image at the same time
        float dir_z = -height/(2.*tan(fov/2.));
        vec3 pinhole_dir = vec3{dir_x, dir_y, dir_z}.normalized();

        vec3 focus_point = pinhole_dir * focus_dist;

        vec3 ray_orig0{0.f, 0.f, 0.f};
        auto [hit0, point0, N0, material0] = scene_intersect(ray_orig0, pinhole_dir);
        float z = hit0 ? point0.norm() : 1e10f;

        constexpr float rho = 1.4f;
        float A = 2.f * aperture; // treat lens diameter as 2*aperture (our disk radius)
        float coc = A * std::fabs(focus_dist - z) / std::max(z, 1e-3f);

        float csharp = (2.f / (float)height) * focus_dist * std::tan(fov * 0.5f);

        constexpr int samples1 = 4;
        constexpr int samples2 = 8;
        constexpr int samples3 = 16;
        int lens_samples = (coc <= csharp) ? samples1 : ((coc <= rho * csharp) ? samples2 : samples3);

        vec3 color{0,0,0};
        uint32_t scramble = wang_hash((uint32_t)pix);
        for (int s = 0; s < lens_samples; s++) {
            vec3 lens_sample = progressive_in_unit_disk((uint32_t)s, scramble) * aperture;
            vec3 ray_orig{lens_sample.x, lens_sample.y, 0.f};
            vec3 ray_dir = (focus_point - ray_orig).normalized();
            color = color + cast_ray(ray_orig, ray_dir);
        }
        framebuffer[pix] = color * (1.f / (float)lens_samples);
    }

    std::ofstream ofs(out_path, std::ios::binary);
    ofs << "P6\n" << width << " " << height << "\n255\n";
    for (vec3 &color : framebuffer) {
        float max = std::max(1.f, std::max(color[0], std::max(color[1], color[2])));
        for (int chan : {0,1,2})
            ofs << (char)(255 *  color[chan]/max);
    }
    return 0;
}

