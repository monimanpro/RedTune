// Red Tune's own processor load with a self-check (17.11.2026), for the auto-tune: what Prime95 is used for.
//
// The work is a Fermat test: for a prime p and any x not divisible by it, x^(p-1) mod p is 1. A buffer of numbers is
// raised to that power by square-and-multiply, one bit of the exponent per sweep over the buffer, and at the end
// every number must be exactly 1. Nothing has to be computed twice or kept as a reference: one wrong bit anywhere in
// the 95 multiplications of a number, or in the memory it lies in between the sweeps, and its result is not 1.
//
// A multiplication modulo p is done in floating point, exactly: p is under 2^50, the product's high part and its
// rounding error (a fused multiply-add gives it) are taken apart, the quotient is a rounded multiply by 1/p, and the
// remainder comes out of another fused multiply-add as an exact integer. So the load is the processor's widest
// units, multiply and FMA, as in Prime95's transforms; in three widths, and a fourth kernel in plain 64-bit integers
// (multiply and divide), which Prime95 does not exercise.
#pragma once
#include <stdint.h>

#define RT_K_INT    0   // 64-bit integer multiply and divide
#define RT_K_FMA128 1   // 2 numbers at a time: light on current, the core clocks high
#define RT_K_AVX2   2   // 4 numbers at a time
#define RT_K_AVX512 3   // 8 numbers at a time
#define RT_K_MEM  0x10   // added to 1..3: the same pass with every step through memory (see stress_vec.inl)
#define RT_K_FILL 0x20   // added to 1..3: through memory on rows too long for the first-level cache
#define RT_ROW    8192   // the most numbers in such a row; the memory for a pass is then n + 4 * RT_ROW numbers
#define RT_ROW_BEST 1024 // the length used when the call names none: see stress_vec.inl
extern __declspec(thread) int rt_row;   // numbers in such a row for the pass this thread runs now (set by rt_stress_run)

// the prime and what goes with it, found once (stress.c)
extern uint64_t rt_p;        // the prime, just under 2^50
extern double rt_pd, rt_u;   // it and its reciprocal as doubles

// one Fermat pass over n numbers (n a multiple of 128), numbers base+0 .. base+n-1; returns how many came out wrong and
// the place of the first. corrupt: the exponent bit after whose sweep one bit of the middle number is flipped
// (a check that an error is caught), -1: none
typedef int (*rt_pass_fn)(double* buf, int n, double base, int corrupt, int* first);
int rt_pass_fma128(double* buf, int n, double base, int corrupt, int* first);
int rt_pass_avx2(double* buf, int n, double base, int corrupt, int* first);
int rt_pass_avx512(double* buf, int n, double base, int corrupt, int* first);
int rt_passm_fma128(double* buf, int n, double base, int corrupt, int* first);
int rt_passm_avx2(double* buf, int n, double base, int corrupt, int* first);
int rt_passm_avx512(double* buf, int n, double base, int corrupt, int* first);
int rt_passf_fma128(double* buf, int n, double base, int corrupt, int* first);
int rt_passf_avx2(double* buf, int n, double base, int corrupt, int* first);
int rt_passf_avx512(double* buf, int n, double base, int corrupt, int* first);
// the multiplication itself on count pairs, for the check against integer arithmetic
void rt_mul_fma128(const double* a, const double* b, double* r, int count);
void rt_mul_avx2(const double* a, const double* b, double* r, int count);
void rt_mul_avx512(const double* a, const double* b, double* r, int count);
