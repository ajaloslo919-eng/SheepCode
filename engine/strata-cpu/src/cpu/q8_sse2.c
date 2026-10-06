// SheepCode Strata CPU: signed Q8 dot product using only x64's SSE2 baseline.
// MIT. Block layout and scale conversion come from the pinned ggml dependency.
#define GGML_COMMON_DECL_C
#include "ggml-common.h"
#include "ggml-cpu-impl.h"
#include "simd-mappings.h"
#include <emmintrin.h>
#include <assert.h>

static int dot_block(const int8_t *x, const int8_t *y) {
    const __m128i zero = _mm_setzero_si128();
    __m128i sum = zero;
    for (int offset = 0; offset < QK8_0; offset += 16) {
        const __m128i a = _mm_loadu_si128((const __m128i *)(x + offset));
        const __m128i b = _mm_loadu_si128((const __m128i *)(y + offset));
        const __m128i sa = _mm_cmpgt_epi8(zero, a), sb = _mm_cmpgt_epi8(zero, b);
        // Sign extend before multiplication, including -128. No saturating byte
        // multiply, SSSE3, SSE4 or AVX instructions, and no repacked weights.
        sum = _mm_add_epi32(sum, _mm_madd_epi16(_mm_unpacklo_epi8(a, sa), _mm_unpacklo_epi8(b, sb)));
        sum = _mm_add_epi32(sum, _mm_madd_epi16(_mm_unpackhi_epi8(a, sa), _mm_unpackhi_epi8(b, sb)));
    }
    sum = _mm_add_epi32(sum, _mm_shuffle_epi32(sum, _MM_SHUFFLE(1, 0, 3, 2)));
    sum = _mm_add_epi32(sum, _mm_shuffle_epi32(sum, _MM_SHUFFLE(2, 3, 0, 1)));
    return _mm_cvtsi128_si32(sum);
}

void ggml_vec_dot_q8_0_q8_0(int n, float *GGML_RESTRICT s, size_t bs,
    const void *GGML_RESTRICT vx, size_t bx, const void *GGML_RESTRICT vy, size_t by, int nrc) {
    assert(n % QK8_0 == 0 && nrc == 1);
    GGML_UNUSED(bs); GGML_UNUSED(bx); GGML_UNUSED(by); GGML_UNUSED(nrc);
    const block_q8_0 *x = vx, *y = vy;
    float sum = 0;
    for (int i = 0; i < n / QK8_0; ++i) {
        // Preserve the scalar implementation's order of floating point sums.
        sum += dot_block(x[i].qs, y[i].qs) * (GGML_CPU_FP16_TO_FP32(x[i].d) * GGML_CPU_FP16_TO_FP32(y[i].d));
    }
    *s = sum;
}

int strata_q8_sse2_verify(void) {
    // A correctness check, not an AI benchmark. Exercise signed extremes,
    // unaligned block data, odd block counts and several scale magnitudes.
    block_q8_0 x[129], y[129];
    uint32_t seed = 0x51ee2026;
    for (int i = 0; i < 129; ++i) {
        x[i].d = ggml_fp32_to_fp16((float)(i % 7 + 1) / 32);
        y[i].d = ggml_fp32_to_fp16((float)(i % 11 + 1) / 64);
        for (int j = 0; j < QK8_0; ++j) {
            seed = seed * 1664525u + 1013904223u; x[i].qs[j] = (int8_t)(seed >> 24);
            seed = seed * 1664525u + 1013904223u; y[i].qs[j] = (int8_t)(seed >> 24);
        }
    }
    for (int a = -128; a < 128; ++a) {
        for (int j = 0; j < QK8_0; ++j) { x[0].qs[j] = (int8_t)a; y[0].qs[j] = (int8_t)(j % 2 ? -128 : 127); }
        for (int blocks = 1; blocks <= 129; blocks += 16) {
            float expected = 0, actual = 0;
            for (int i = 0; i < blocks; ++i) {
                int scalar = 0;
                for (int j = 0; j < QK8_0; ++j) scalar += (int)x[i].qs[j] * (int)y[i].qs[j];
                expected += scalar * (GGML_CPU_FP16_TO_FP32(x[i].d) * GGML_CPU_FP16_TO_FP32(y[i].d));
            }
            ggml_vec_dot_q8_0_q8_0(blocks * QK8_0, &actual, 0, x, 0, y, 0, 1);
            if (actual != expected) return 0;
        }
    }
    return 1;
}
