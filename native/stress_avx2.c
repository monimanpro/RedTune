// The 4-wide (AVX2 + FMA) and the 2-wide (FMA on 128 bits) kernels; compiled with /arch:AVX2. See stress.h.
#include <immintrin.h>
#include "stress.h"

#define V __m256d
#define LANES 4
#define PASS rt_pass_avx2
#define PASSM rt_passm_avx2
#define PASSF rt_passf_avx2
#define passmem passmem256
#define MUL rt_mul_avx2
#define VSET1(x) _mm256_set1_pd(x)
#define VLOAD(p) _mm256_loadu_pd(p)
#define VSTORE(p, v) _mm256_storeu_pd(p, v)
#define VMUL(a, b) _mm256_mul_pd(a, b)
#define VADD(a, b) _mm256_add_pd(a, b)
#define VFMSUB(a, b, c) _mm256_fmsub_pd(a, b, c)
#define VFNMADD(a, b, c) _mm256_fnmadd_pd(a, b, c)
#define VROUND(x) _mm256_round_pd(x, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC)
#define VNEGFIX(r, p) _mm256_add_pd(r, _mm256_and_pd(_mm256_cmp_pd(r, _mm256_setzero_pd(), _CMP_LT_OQ), p))
#define mulmod mulmod256
#define memmul memmul256
#include "stress_vec.inl"
#undef V
#undef LANES
#undef PASS
#undef PASSM
#undef PASSF
#undef passmem
#undef memmul
#undef MUL
#undef VSET1
#undef VLOAD
#undef VSTORE
#undef VMUL
#undef VADD
#undef VFMSUB
#undef VFNMADD
#undef VROUND
#undef VNEGFIX
#undef mulmod

#define V __m128d
#define LANES 2
#define PASS rt_pass_fma128
#define PASSM rt_passm_fma128
#define PASSF rt_passf_fma128
#define passmem passmem128
#define MUL rt_mul_fma128
#define VSET1(x) _mm_set1_pd(x)
#define VLOAD(p) _mm_loadu_pd(p)
#define VSTORE(p, v) _mm_storeu_pd(p, v)
#define VMUL(a, b) _mm_mul_pd(a, b)
#define VADD(a, b) _mm_add_pd(a, b)
#define VFMSUB(a, b, c) _mm_fmsub_pd(a, b, c)
#define VFNMADD(a, b, c) _mm_fnmadd_pd(a, b, c)
#define VROUND(x) _mm_round_pd(x, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC)
#define VNEGFIX(r, p) _mm_add_pd(r, _mm_and_pd(_mm_cmplt_pd(r, _mm_setzero_pd()), p))
#define mulmod mulmod128
#define memmul memmul128
#include "stress_vec.inl"
