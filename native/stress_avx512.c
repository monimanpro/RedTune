// The 8-wide (AVX-512) kernel; compiled with /arch:AVX512, run only where the processor has it. See stress.h.
#include <immintrin.h>
#include "stress.h"

#define V __m512d
#define LANES 8
#define PASS rt_pass_avx512
#define PASSM rt_passm_avx512
#define PASSF rt_passf_avx512
#define MUL rt_mul_avx512
#define VSET1(x) _mm512_set1_pd(x)
#define VLOAD(p) _mm512_loadu_pd(p)
#define VSTORE(p, v) _mm512_storeu_pd(p, v)
#define VMUL(a, b) _mm512_mul_pd(a, b)
#define VADD(a, b) _mm512_add_pd(a, b)
#define VFMSUB(a, b, c) _mm512_fmsub_pd(a, b, c)
#define VFNMADD(a, b, c) _mm512_fnmadd_pd(a, b, c)
#define VROUND(x) _mm512_roundscale_pd(x, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC)
#define VNEGFIX(r, p) _mm512_mask_add_pd(r, _mm512_cmp_pd_mask(r, _mm512_setzero_pd(), _CMP_LT_OQ), r, p)
#include "stress_vec.inl"
