#include "raytracer_api.h"

#include <cstddef>

int RaytracerNative_ApiVersion(void) {
    return 9;
}

void RenderTestFrame(int width, int height, unsigned char* buffer) {
    if (!buffer || width <= 0 || height <= 0) {
        return;
    }

    // Unity Texture2D RGBA32: row 0 = bottom, row-major, R G B A per pixel.
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const std::size_t i =
                (static_cast<std::size_t>(y) * static_cast<std::size_t>(width) +
                 static_cast<std::size_t>(x)) *
                4u;
            buffer[i + 0] = 0;     // R
            buffer[i + 1] = 0;     // G
            buffer[i + 2] = 255;   // B
            buffer[i + 3] = 255;   // A
        }
    }
}
