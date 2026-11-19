// Red Tune's own processor load, the transform kernel (17.11.2026); compiled with /arch:AVX2. See stress.h.
//
// Why it is here. On a core set 18 steps deeper than its own value and running at 5.3 GHz, Prime95 stopped on a
// rounding error within 1 to 3 seconds in every run, and the Fermat passes of stress_vec.inl, on the same multipliers
// and adders, ran a minute without a wrong result (they erred only 3 steps deeper). What Prime95 computes is a
// fast Fourier transform: sums and differences of numbers of every size and sign, each multiplied by a root of unity
// taken from memory, results that nearly cancel. The Fermat passes multiply numbers of one size over and over.
// So this kernel does what Prime95 does, with a check of its own:
//
//   a number of n digits of `bits` bits is squared modulo 2^(bits*n) - 1: the digits are transformed (n complex
//   points, forward), every point is squared, transformed back, divided by n and rounded to whole numbers, and the
//   carries are passed on (a cyclic convolution is exactly that squaring). Two checks after every squaring:
//     - rounding: every value before rounding has to be close to a whole number (0.4 at most, as in Prime95); the
//       digits are as large as the 53 bits of a double allow, so a small wrong bit anywhere shows up here;
//     - the remainder of the number modulo 2^(2*bits) - 1, which divides the modulus, has to be the square of the
//       remainder before: integer arithmetic, exact, catches whatever changed a digit.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include <immintrin.h>
#include <math.h>
#include "stress.h"

int rt_stress_caps(void);

// the roots of unity for every stage: at h + i the root exp(-pi*i*sqrt(-1)/h), for the stage whose half is h
typedef struct { double* wr; double* wi; } tw_t;
static tw_t tws[24];
static volatile LONG twlock;

static tw_t* twiddles(int n)
{
    int lg = 0; while ((1 << lg) < n) lg++;
    tw_t* t = &tws[lg];
    if (t->wr) return t;
    while (InterlockedCompareExchange(&twlock, 1, 0)) Sleep(0);
    if (!t->wr)
    {
        double* m = (double*)VirtualAlloc(0, (size_t)n * 16 + 256, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (m)
        {
            double* wr = m, * wi = m + n + 24;    // not a multiple of 4 KB away: see LAYOUT
            wr[0] = 1; wi[0] = 0;
            for (int h = 1; h < n; h <<= 1)
                for (int i = 0; i < h; i++) { double a = -3.14159265358979323846 * i / h; wr[h + i] = cos(a); wi[h + i] = sin(a); }
            t->wi = wi; t->wr = wr;     // wr last: it is what the others look at
        }
    }
    InterlockedExchange(&twlock, 0);
    return t->wr ? t : 0;
}

static void forward_small(double* re, double* im, int n);
static void backward_small(double* re, double* im, int n);

#define LD(p) _mm256_loadu_pd(p)
#define ST(p, v) _mm256_storeu_pd(p, v)

// forward: the stages from the widest down (the result comes out in bit-reversed order, which the squaring does not mind)
static void forward(double* re, double* im, int n, const tw_t* t)
{
    for (int h = n >> 1; h >= 4; h >>= 1)
    {
        const double* wr = t->wr + h, * wi = t->wi + h;
        for (int j = 0; j < n; j += 2 * h)
            for (int i = 0; i < h; i += 4)
            {
                __m256d ar = LD(re + j + i), ai = LD(im + j + i), br = LD(re + j + i + h), bi = LD(im + j + i + h);
                __m256d tr = _mm256_sub_pd(ar, br), ti = _mm256_sub_pd(ai, bi), cr = LD(wr + i), ci = LD(wi + i);
                ST(re + j + i, _mm256_add_pd(ar, br)); ST(im + j + i, _mm256_add_pd(ai, bi));
                ST(re + j + i + h, _mm256_fmsub_pd(tr, cr, _mm256_mul_pd(ti, ci)));
                ST(im + j + i + h, _mm256_fmadd_pd(tr, ci, _mm256_mul_pd(ti, cr)));
            }
    }
    forward_small(re, im, n);
}

// the halves of 2 and of 1, eight numbers at a time; the roots of the half of 2 are 1 and -sqrt(-1)
static void forward_small(double* re, double* im, int n)
{
    const __m256d w2r = _mm256_setr_pd(1, 0, 1, 0), w2i = _mm256_setr_pd(0, -1, 0, -1);
    for (int j = 0; j < n; j += 8)
    {
        __m256d v0r = LD(re + j), v1r = LD(re + j + 4), v0i = LD(im + j), v1i = LD(im + j + 4);
        __m256d ar = _mm256_permute2f128_pd(v0r, v1r, 0x20), br = _mm256_permute2f128_pd(v0r, v1r, 0x31);
        __m256d ai = _mm256_permute2f128_pd(v0i, v1i, 0x20), bi = _mm256_permute2f128_pd(v0i, v1i, 0x31);
        __m256d sr = _mm256_add_pd(ar, br), si = _mm256_add_pd(ai, bi), tr = _mm256_sub_pd(ar, br), ti = _mm256_sub_pd(ai, bi);
        __m256d dr = _mm256_fmsub_pd(tr, w2r, _mm256_mul_pd(ti, w2i)), di = _mm256_fmadd_pd(tr, w2i, _mm256_mul_pd(ti, w2r));
        __m256d y0r = _mm256_permute2f128_pd(sr, dr, 0x20), y1r = _mm256_permute2f128_pd(sr, dr, 0x31);
        __m256d y0i = _mm256_permute2f128_pd(si, di, 0x20), y1i = _mm256_permute2f128_pd(si, di, 0x31);
        __m256d pr = _mm256_unpacklo_pd(y0r, y1r), qr = _mm256_unpackhi_pd(y0r, y1r), pi = _mm256_unpacklo_pd(y0i, y1i), qi = _mm256_unpackhi_pd(y0i, y1i);
        __m256d ur = _mm256_add_pd(pr, qr), vr = _mm256_sub_pd(pr, qr), ui = _mm256_add_pd(pi, qi), vi = _mm256_sub_pd(pi, qi);
        ST(re + j, _mm256_unpacklo_pd(ur, vr)); ST(re + j + 4, _mm256_unpackhi_pd(ur, vr));
        ST(im + j, _mm256_unpacklo_pd(ui, vi)); ST(im + j + 4, _mm256_unpackhi_pd(ui, vi));
    }
}

// back: the same stages in the other order with the conjugate roots; the result is n times the numbers
static void backward_small(double* re, double* im, int n)
{
    const __m256d w2r = _mm256_setr_pd(1, 0, 1, 0), w2i = _mm256_setr_pd(0, -1, 0, -1);
    for (int j = 0; j < n; j += 8)
    {
        __m256d v0r = LD(re + j), v1r = LD(re + j + 4), v0i = LD(im + j), v1i = LD(im + j + 4);
        __m256d pr = _mm256_unpacklo_pd(v0r, v1r), qr = _mm256_unpackhi_pd(v0r, v1r), pi = _mm256_unpacklo_pd(v0i, v1i), qi = _mm256_unpackhi_pd(v0i, v1i);
        __m256d ur = _mm256_add_pd(pr, qr), vr = _mm256_sub_pd(pr, qr), ui = _mm256_add_pd(pi, qi), vi = _mm256_sub_pd(pi, qi);
        __m256d y0r = _mm256_unpacklo_pd(ur, vr), y1r = _mm256_unpackhi_pd(ur, vr), y0i = _mm256_unpacklo_pd(ui, vi), y1i = _mm256_unpackhi_pd(ui, vi);
        __m256d ar = _mm256_permute2f128_pd(y0r, y1r, 0x20), br = _mm256_permute2f128_pd(y0r, y1r, 0x31);
        __m256d ai = _mm256_permute2f128_pd(y0i, y1i, 0x20), bi = _mm256_permute2f128_pd(y0i, y1i, 0x31);
        __m256d xr = _mm256_fmadd_pd(br, w2r, _mm256_mul_pd(bi, w2i)), xi = _mm256_fmsub_pd(bi, w2r, _mm256_mul_pd(br, w2i));
        __m256d sr = _mm256_add_pd(ar, xr), dr = _mm256_sub_pd(ar, xr), si = _mm256_add_pd(ai, xi), di = _mm256_sub_pd(ai, xi);
        ST(re + j, _mm256_permute2f128_pd(sr, dr, 0x20)); ST(re + j + 4, _mm256_permute2f128_pd(sr, dr, 0x31));
        ST(im + j, _mm256_permute2f128_pd(si, di, 0x20)); ST(im + j + 4, _mm256_permute2f128_pd(si, di, 0x31));
    }
}

static void backward(double* re, double* im, int n, const tw_t* t)
{
    backward_small(re, im, n);
    for (int h = 4; h < n; h <<= 1)
    {
        const double* wr = t->wr + h, * wi = t->wi + h;
        for (int j = 0; j < n; j += 2 * h)
            for (int i = 0; i < h; i += 4)
            {
                __m256d ar = LD(re + j + i), ai = LD(im + j + i), br = LD(re + j + i + h), bi = LD(im + j + i + h), cr = LD(wr + i), ci = LD(wi + i);
                __m256d xr = _mm256_fmadd_pd(br, cr, _mm256_mul_pd(bi, ci)), xi = _mm256_fmsub_pd(bi, cr, _mm256_mul_pd(br, ci));
                ST(re + j + i, _mm256_add_pd(ar, xr)); ST(im + j + i, _mm256_add_pd(ai, xi));
                ST(re + j + i + h, _mm256_sub_pd(ar, xr)); ST(im + j + i + h, _mm256_sub_pd(ai, xi));
            }
    }
}

// ---------- the same two transforms, every sum folded into a fused multiply-add ----------
// 17.11: what in Prime95 finds the weak core was narrowed down with Prime95 itself, on a core 18 steps too deep at
// 5.3 GHz: its AVX2 build with FMA stopped on a rounding error within 1 to 8 seconds at any transform size (4K, 36K,
// 1024K), the same sizes with AVX but without FMA ran the minute clean, and so did SSE2. The transforms above, with
// two fused operations and four plain sums to a butterfly, ran four minutes clean at the same clock, voltage and
// current. Prime95's FMA code has no plain sums: a butterfly is fused operations feeding one another, the result of
// one being the addend of the next. So here: a + b*w is two fused operations in a row for each part, and (a - b)*w is
// taken product by product, three in a row. Eight fused operations to a butterfly, each waiting for the one before.
static void forward_dense(double* re, double* im, int n, const tw_t* t)
{
    for (int h = n >> 1; h >= 4; h >>= 1)
    {
        const double* wr = t->wr + h, * wi = t->wi + h;
        for (int j = 0; j < n; j += 2 * h)
            for (int i = 0; i < h; i += 4)
            {
                __m256d ar = LD(re + j + i), ai = LD(im + j + i), br = LD(re + j + i + h), bi = LD(im + j + i + h), cr = LD(wr + i), ci = LD(wi + i);
                // (a - b) * w: re = ar*cr - br*cr - ai*ci + bi*ci, im = ar*ci - br*ci + ai*cr - bi*cr
                __m256d xr = _mm256_fmadd_pd(ar, cr, _mm256_fnmadd_pd(br, cr, _mm256_fnmadd_pd(ai, ci, _mm256_mul_pd(bi, ci))));
                __m256d xi = _mm256_fmadd_pd(ar, ci, _mm256_fnmadd_pd(br, ci, _mm256_fmsub_pd(ai, cr, _mm256_mul_pd(bi, cr))));
                ST(re + j + i, _mm256_add_pd(ar, br)); ST(im + j + i, _mm256_add_pd(ai, bi));
                ST(re + j + i + h, xr); ST(im + j + i + h, xi);
            }
    }
    forward_small(re, im, n);
}

static void backward_dense(double* re, double* im, int n, const tw_t* t)
{
    backward_small(re, im, n);
    for (int h = 4; h < n; h <<= 1)
    {
        const double* wr = t->wr + h, * wi = t->wi + h;
        for (int j = 0; j < n; j += 2 * h)
            for (int i = 0; i < h; i += 4)
            {
                __m256d ar = LD(re + j + i), ai = LD(im + j + i), br = LD(re + j + i + h), bi = LD(im + j + i + h), cr = LD(wr + i), ci = LD(wi + i);
                // a + b * conj(w) and a - b * conj(w): b * conj(w) = (br*cr + bi*ci) + (bi*cr - br*ci) sqrt(-1)
                ST(re + j + i, _mm256_fmadd_pd(br, cr, _mm256_fmadd_pd(bi, ci, ar)));
                ST(im + j + i, _mm256_fmadd_pd(bi, cr, _mm256_fnmadd_pd(br, ci, ai)));
                ST(re + j + i + h, _mm256_fnmadd_pd(br, cr, _mm256_fnmadd_pd(bi, ci, ar)));
                ST(im + j + i + h, _mm256_fnmadd_pd(bi, cr, _mm256_fmadd_pd(br, ci, ai)));
            }
    }
}

// ---------- the same two transforms, the sums made through the fused units as Prime95 makes them ----------
// 17.11: CCD1 Core 8, 18 steps too deep, failed Prime95's FMA code within 1 to 4 s and its code without FMA after
// 14 s; the plain transform above failed it after 3 to 18 s, the one of chained fused operations after 18 s, and sums
// through the fused units on numbers kept in registers (stress_fma.c) not at all. What is left of Prime95's FMA code
// is its sums on the data of a transform: 1.0 * a + b and 1.0 * a - b through the fused units, numbers of every size
// from memory. So here: the plain transform with every sum and difference made that way.
static void forward_one(double* re, double* im, int n, const tw_t* t)
{
    const __m256d one = _mm256_set1_pd(1.0);
    for (int h = n >> 1; h >= 4; h >>= 1)
    {
        const double* wr = t->wr + h, * wi = t->wi + h;
        for (int j = 0; j < n; j += 2 * h)
            for (int i = 0; i < h; i += 4)
            {
                __m256d ar = LD(re + j + i), ai = LD(im + j + i), br = LD(re + j + i + h), bi = LD(im + j + i + h);
                __m256d tr = _mm256_fmsub_pd(ar, one, br), ti = _mm256_fmsub_pd(ai, one, bi), cr = LD(wr + i), ci = LD(wi + i);
                ST(re + j + i, _mm256_fmadd_pd(ar, one, br)); ST(im + j + i, _mm256_fmadd_pd(ai, one, bi));
                ST(re + j + i + h, _mm256_fmsub_pd(tr, cr, _mm256_mul_pd(ti, ci)));
                ST(im + j + i + h, _mm256_fmadd_pd(tr, ci, _mm256_mul_pd(ti, cr)));
            }
    }
    forward_small(re, im, n);
}

static void backward_one(double* re, double* im, int n, const tw_t* t)
{
    const __m256d one = _mm256_set1_pd(1.0);
    backward_small(re, im, n);
    for (int h = 4; h < n; h <<= 1)
    {
        const double* wr = t->wr + h, * wi = t->wi + h;
        for (int j = 0; j < n; j += 2 * h)
            for (int i = 0; i < h; i += 4)
            {
                __m256d ar = LD(re + j + i), ai = LD(im + j + i), br = LD(re + j + i + h), bi = LD(im + j + i + h), cr = LD(wr + i), ci = LD(wi + i);
                __m256d xr = _mm256_fmadd_pd(br, cr, _mm256_mul_pd(bi, ci)), xi = _mm256_fmsub_pd(bi, cr, _mm256_mul_pd(br, ci));
                ST(re + j + i, _mm256_fmadd_pd(ar, one, xr)); ST(im + j + i, _mm256_fmadd_pd(ai, one, xi));
                ST(re + j + i + h, _mm256_fmsub_pd(ar, one, xr)); ST(im + j + i + h, _mm256_fmsub_pd(ai, one, xi));
            }
    }
}

static void square(double* re, double* im, int n)
{
    for (int k = 0; k < n; k += 4)
    {
        __m256d r = LD(re + k), i = LD(im + k), ri = _mm256_mul_pd(r, i);
        ST(re + k, _mm256_fmsub_pd(r, r, _mm256_mul_pd(i, i))); ST(im + k, _mm256_add_pd(ri, ri));
    }
}

// ---------- the squaring of radix 4, the data kept in the core's own cache and in registers (18.11.2026) ----------
// 17.11: CCD1 Core 8, 18 steps too deep, failed Prime95 within 0.7 to 5 s nearly every time, the transforms above
// after 1 to 30 s or not at all, weighted or not, at the edge of precision or not. What is left between them is the
// code of the transform itself. Prime95's is written by hand: radix 4 and 8, a butterfly whole in registers, two
// passes over memory for a squaring, the second pass doing its part of the forward transform, the squaring and its part
// of the transform back on a block that stays in the first-level cache. The transforms above are radix 2: every stage
// a pass over all the numbers, half of every butterfly's time spent loading and storing.
// So here, the same squaring done the way Prime95 does it:
//   - a stage of radix 4 does two stages of radix 2 with the numbers held in registers (the same result, in the same
//     order: a[i], a[i+q], a[i+2q], a[i+3q] become y0, y2 * w^2i, y1 * w^i, y3 * w^3i); w^3i is made from w^i and w^2i
//     by a complex multiply, as Prime95 makes some of its roots;
//   - depth first: one stage over the whole number, then the four quarters each to the end and back before the next,
//     so below 1024 points (16 KB) the block stays in the first-level cache through forward, squaring and back;
//   - the last 16 points of every block: the last four stages forward, the squaring and the first four back without
//     a store between them.
// ones: every sum and difference through the fused units, 1.0 * a + b, as Prime95's FMA code makes them.
typedef struct { __m256d r, i; } cv_t;

static __forceinline __m256d vadd(__m256d a, __m256d b, int ones) { return ones ? _mm256_fmadd_pd(a, _mm256_set1_pd(1.0), b) : _mm256_add_pd(a, b); }
static __forceinline __m256d vsub(__m256d a, __m256d b, int ones) { return ones ? _mm256_fmsub_pd(a, _mm256_set1_pd(1.0), b) : _mm256_sub_pd(a, b); }
static __forceinline cv_t cmul(cv_t a, __m256d wr, __m256d wi)   // a * w
{
    cv_t o; o.r = _mm256_fmsub_pd(a.r, wr, _mm256_mul_pd(a.i, wi)); o.i = _mm256_fmadd_pd(a.r, wi, _mm256_mul_pd(a.i, wr)); return o;
}
static __forceinline cv_t cmulc(cv_t a, __m256d wr, __m256d wi)  // a * conj(w)
{
    cv_t o; o.r = _mm256_fmadd_pd(a.r, wr, _mm256_mul_pd(a.i, wi)); o.i = _mm256_fmsub_pd(a.i, wr, _mm256_mul_pd(a.r, wi)); return o;
}
static __forceinline cv_t cld(const double* re, const double* im, int k) { cv_t v; v.r = LD(re + k); v.i = LD(im + k); return v; }
static __forceinline void cst(double* re, double* im, int k, cv_t v) { ST(re + k, v.r); ST(im + k, v.i); }

// one butterfly of radix 4 forward on registers: x0..x3 in, out in place in the order of the stores (see above)
static __forceinline void bf4f(cv_t* x0, cv_t* x1, cv_t* x2, cv_t* x3, __m256d w1r, __m256d w1i, __m256d w2r, __m256d w2i, int ones)
{
    cv_t t0, t1, t2, t3, y0, y1, y2, y3;
    t0.r = vadd(x0->r, x2->r, ones); t0.i = vadd(x0->i, x2->i, ones); t1.r = vsub(x0->r, x2->r, ones); t1.i = vsub(x0->i, x2->i, ones);
    t2.r = vadd(x1->r, x3->r, ones); t2.i = vadd(x1->i, x3->i, ones);
    t3.r = vsub(x1->i, x3->i, ones); t3.i = vsub(x3->r, x1->r, ones);           // (x1 - x3) * -sqrt(-1)
    y0.r = vadd(t0.r, t2.r, ones); y0.i = vadd(t0.i, t2.i, ones); y2.r = vsub(t0.r, t2.r, ones); y2.i = vsub(t0.i, t2.i, ones);
    y1.r = vadd(t1.r, t3.r, ones); y1.i = vadd(t1.i, t3.i, ones); y3.r = vsub(t1.r, t3.r, ones); y3.i = vsub(t1.i, t3.i, ones);
    __m256d w3r = _mm256_fmsub_pd(w1r, w2r, _mm256_mul_pd(w1i, w2i)), w3i = _mm256_fmadd_pd(w1r, w2i, _mm256_mul_pd(w1i, w2r));
    *x0 = y0; *x1 = cmul(y2, w2r, w2i); *x2 = cmul(y1, w1r, w1i); *x3 = cmul(y3, w3r, w3i);
}

// and back: the conjugate roots, the stages in the other order; a0..a3 in, out0..out3 in place
static __forceinline void bf4b(cv_t* a0, cv_t* a1, cv_t* a2, cv_t* a3, __m256d w1r, __m256d w1i, __m256d w2r, __m256d w2i, int ones)
{
    __m256d w3r = _mm256_fmsub_pd(w1r, w2r, _mm256_mul_pd(w1i, w2i)), w3i = _mm256_fmadd_pd(w1r, w2i, _mm256_mul_pd(w1i, w2r));
    cv_t A = *a0, B = cmulc(*a1, w2r, w2i), C = cmulc(*a2, w1r, w1i), D = cmulc(*a3, w3r, w3i), s0, s1, s2, s3;
    s0.r = vadd(A.r, B.r, ones); s0.i = vadd(A.i, B.i, ones); s1.r = vsub(A.r, B.r, ones); s1.i = vsub(A.i, B.i, ones);
    s2.r = vadd(C.r, D.r, ones); s2.i = vadd(C.i, D.i, ones); s3.r = vsub(C.r, D.r, ones); s3.i = vsub(C.i, D.i, ones);
    a0->r = vadd(s0.r, s2.r, ones); a0->i = vadd(s0.i, s2.i, ones); a2->r = vsub(s0.r, s2.r, ones); a2->i = vsub(s0.i, s2.i, ones);
    a1->r = vsub(s1.r, s3.i, ones); a1->i = vadd(s1.i, s3.r, ones);             // s1 + sqrt(-1) * s3
    a3->r = vadd(s1.r, s3.i, ones); a3->i = vsub(s1.i, s3.r, ones);             // s1 - sqrt(-1) * s3
}

// a stage of radix 4 over len numbers, in blocks of L (q = L / 4 at least 4): w^i from the table of the half L/2, w^2i of L/4
static __forceinline void r4f_pass(double* re, double* im, int len, int L, const tw_t* t, int ones)
{
    int q = L >> 2;
    const double* w1r = t->wr + 2 * q, * w1i = t->wi + 2 * q, * w2r = t->wr + q, * w2i = t->wi + q;
    for (int j = 0; j < len; j += L)
        for (int i = 0; i < q; i += 4)
        {
            int k = j + i;
            cv_t x0 = cld(re, im, k), x1 = cld(re, im, k + q), x2 = cld(re, im, k + 2 * q), x3 = cld(re, im, k + 3 * q);
            bf4f(&x0, &x1, &x2, &x3, LD(w1r + i), LD(w1i + i), LD(w2r + i), LD(w2i + i), ones);
            cst(re, im, k, x0); cst(re, im, k + q, x1); cst(re, im, k + 2 * q, x2); cst(re, im, k + 3 * q, x3);
        }
}

static __forceinline void r4b_pass(double* re, double* im, int len, int L, const tw_t* t, int ones)
{
    int q = L >> 2;
    const double* w1r = t->wr + 2 * q, * w1i = t->wi + 2 * q, * w2r = t->wr + q, * w2i = t->wi + q;
    for (int j = 0; j < len; j += L)
        for (int i = 0; i < q; i += 4)
        {
            int k = j + i;
            cv_t x0 = cld(re, im, k), x1 = cld(re, im, k + q), x2 = cld(re, im, k + 2 * q), x3 = cld(re, im, k + 3 * q);
            bf4b(&x0, &x1, &x2, &x3, LD(w1r + i), LD(w1i + i), LD(w2r + i), LD(w2i + i), ones);
            cst(re, im, k, x0); cst(re, im, k + q, x1); cst(re, im, k + 2 * q, x2); cst(re, im, k + 3 * q, x3);
        }
}

// a stage of radix 2 (the half h) over len numbers, where the number of stages is odd: the first forward, the last back
static __forceinline void r2f_pass(double* re, double* im, int len, int h, const tw_t* t, int ones)
{
    const double* wr = t->wr + h, * wi = t->wi + h;
    for (int j = 0; j < len; j += 2 * h)
        for (int i = 0; i < h; i += 4)
        {
            cv_t a = cld(re, im, j + i), b = cld(re, im, j + i + h), s, d;
            s.r = vadd(a.r, b.r, ones); s.i = vadd(a.i, b.i, ones); d.r = vsub(a.r, b.r, ones); d.i = vsub(a.i, b.i, ones);
            cst(re, im, j + i, s); cst(re, im, j + i + h, cmul(d, LD(wr + i), LD(wi + i)));
        }
}

static __forceinline void r2b_pass(double* re, double* im, int len, int h, const tw_t* t, int ones)
{
    const double* wr = t->wr + h, * wi = t->wi + h;
    for (int j = 0; j < len; j += 2 * h)
        for (int i = 0; i < h; i += 4)
        {
            cv_t a = cld(re, im, j + i), b = cmulc(cld(re, im, j + i + h), LD(wr + i), LD(wi + i)), s, d;
            s.r = vadd(a.r, b.r, ones); s.i = vadd(a.i, b.i, ones); d.r = vsub(a.r, b.r, ones); d.i = vsub(a.i, b.i, ones);
            cst(re, im, j + i, s); cst(re, im, j + i + h, d);
        }
}

// the halves of 2 and 1 on eight points in registers (forward_small and backward_small without the memory)
static __forceinline void fs8(cv_t* v0, cv_t* v1, int ones)
{
    const __m256d w2r = _mm256_setr_pd(1, 0, 1, 0), w2i = _mm256_setr_pd(0, -1, 0, -1);
    __m256d ar = _mm256_permute2f128_pd(v0->r, v1->r, 0x20), br = _mm256_permute2f128_pd(v0->r, v1->r, 0x31);
    __m256d ai = _mm256_permute2f128_pd(v0->i, v1->i, 0x20), bi = _mm256_permute2f128_pd(v0->i, v1->i, 0x31);
    __m256d sr = vadd(ar, br, ones), si = vadd(ai, bi, ones), tr = vsub(ar, br, ones), ti = vsub(ai, bi, ones);
    __m256d dr = _mm256_fmsub_pd(tr, w2r, _mm256_mul_pd(ti, w2i)), di = _mm256_fmadd_pd(tr, w2i, _mm256_mul_pd(ti, w2r));
    __m256d y0r = _mm256_permute2f128_pd(sr, dr, 0x20), y1r = _mm256_permute2f128_pd(sr, dr, 0x31);
    __m256d y0i = _mm256_permute2f128_pd(si, di, 0x20), y1i = _mm256_permute2f128_pd(si, di, 0x31);
    __m256d pr = _mm256_unpacklo_pd(y0r, y1r), qr = _mm256_unpackhi_pd(y0r, y1r), pi = _mm256_unpacklo_pd(y0i, y1i), qi = _mm256_unpackhi_pd(y0i, y1i);
    __m256d ur = vadd(pr, qr, ones), vr = vsub(pr, qr, ones), ui = vadd(pi, qi, ones), vi = vsub(pi, qi, ones);
    v0->r = _mm256_unpacklo_pd(ur, vr); v1->r = _mm256_unpackhi_pd(ur, vr); v0->i = _mm256_unpacklo_pd(ui, vi); v1->i = _mm256_unpackhi_pd(ui, vi);
}

static __forceinline void bs8(cv_t* v0, cv_t* v1, int ones)
{
    const __m256d w2r = _mm256_setr_pd(1, 0, 1, 0), w2i = _mm256_setr_pd(0, -1, 0, -1);
    __m256d pr = _mm256_unpacklo_pd(v0->r, v1->r), qr = _mm256_unpackhi_pd(v0->r, v1->r), pi = _mm256_unpacklo_pd(v0->i, v1->i), qi = _mm256_unpackhi_pd(v0->i, v1->i);
    __m256d ur = vadd(pr, qr, ones), vr = vsub(pr, qr, ones), ui = vadd(pi, qi, ones), vi = vsub(pi, qi, ones);
    __m256d y0r = _mm256_unpacklo_pd(ur, vr), y1r = _mm256_unpackhi_pd(ur, vr), y0i = _mm256_unpacklo_pd(ui, vi), y1i = _mm256_unpackhi_pd(ui, vi);
    __m256d ar = _mm256_permute2f128_pd(y0r, y1r, 0x20), br = _mm256_permute2f128_pd(y0r, y1r, 0x31);
    __m256d ai = _mm256_permute2f128_pd(y0i, y1i, 0x20), bi = _mm256_permute2f128_pd(y0i, y1i, 0x31);
    __m256d xr = _mm256_fmadd_pd(br, w2r, _mm256_mul_pd(bi, w2i)), xi = _mm256_fmsub_pd(bi, w2r, _mm256_mul_pd(br, w2i));
    __m256d sr = vadd(ar, xr, ones), dr = vsub(ar, xr, ones), si = vadd(ai, xi, ones), di = vsub(ai, xi, ones);
    v0->r = _mm256_permute2f128_pd(sr, dr, 0x20); v1->r = _mm256_permute2f128_pd(sr, dr, 0x31);
    v0->i = _mm256_permute2f128_pd(si, di, 0x20); v1->i = _mm256_permute2f128_pd(si, di, 0x31);
}

static __forceinline void csq(cv_t* v) { __m256d ri = _mm256_mul_pd(v->r, v->i); v->r = _mm256_fmsub_pd(v->r, v->r, _mm256_mul_pd(v->i, v->i)); v->i = _mm256_add_pd(ri, ri); }

// every 16 points of a block: the stage of radix 4 with q = 4, the halves of 2 and 1, the squaring, and back, in registers
static __forceinline void sq16(double* re, double* im, int len, const tw_t* t, int ones)
{
    __m256d w1r = LD(t->wr + 8), w1i = LD(t->wi + 8), w2r = LD(t->wr + 4), w2i = LD(t->wi + 4);
    for (int j = 0; j < len; j += 16)
    {
        cv_t v0 = cld(re, im, j), v1 = cld(re, im, j + 4), v2 = cld(re, im, j + 8), v3 = cld(re, im, j + 12);
        bf4f(&v0, &v1, &v2, &v3, w1r, w1i, w2r, w2i, ones);
        fs8(&v0, &v1, ones); fs8(&v2, &v3, ones);
        csq(&v0); csq(&v1); csq(&v2); csq(&v3);
        bs8(&v0, &v1, ones); bs8(&v2, &v3, ones);
        bf4b(&v0, &v1, &v2, &v3, w1r, w1i, w2r, w2i, ones);
        cst(re, im, j, v0); cst(re, im, j + 4, v1); cst(re, im, j + 8, v2); cst(re, im, j + 12, v3);
    }
}

// LAYOUT. The first-level cache of a Zen 4 core (32 KB) has 8 ways of 4 KB: numbers a multiple of 4 KB apart compete
// for the same 8 places. With the real parts, the imaginary parts and the roots each a power of two long and laid one
// after another, a stage of radix 4 over 1024 points or more reads 8 numbers and 4 roots at such distances, and the
// cache throws out what the next butterfly needs. So the parts lie a few lines apart from the power of two (as Prime95
// lays its rows 8 KB + 128 bytes apart): the imaginary parts 40 numbers further, the two carry rows 48 and 56, the
// imaginary parts of the roots 24. 18.11, 32768 points: the squaring of radix 4 136 -> 116 us.
static int r4_block = 1024;   // points a block is squared in through to the end: 16 KB of numbers, half the first-level cache

static __forceinline void sq_block(double* re, double* im, int len, const tw_t* t, int ones)
{
    int lg = 0; while ((1 << lg) < len) lg++;
    int odd = (lg - 2) & 1, L;                      // stages of the half 4 and up: lg - 2
    if (odd) r2f_pass(re, im, len, len >> 1, t, ones);
    for (L = odd ? len >> 1 : len; L > 16; L >>= 2) r4f_pass(re, im, len, L, t, ones);
    sq16(re, im, len, t, ones);
    for (L = 64; L <= (odd ? len >> 1 : len); L <<= 2) r4b_pass(re, im, len, L, t, ones);
    if (odd) r2b_pass(re, im, len, len >> 1, t, ones);
}

static void sq_rec(double* re, double* im, int len, const tw_t* t, int ones)
{
    if (len <= r4_block) { if (ones) sq_block(re, im, len, t, 1); else sq_block(re, im, len, t, 0); return; }
    int lg = 0; while ((1 << lg) < len) lg++;
    if ((lg - 2) & 1)
    {
        if (ones) r2f_pass(re, im, len, len >> 1, t, 1); else r2f_pass(re, im, len, len >> 1, t, 0);
        sq_rec(re, im, len >> 1, t, ones); sq_rec(re + (len >> 1), im + (len >> 1), len >> 1, t, ones);
        if (ones) r2b_pass(re, im, len, len >> 1, t, 1); else r2b_pass(re, im, len, len >> 1, t, 0);
    }
    else
    {
        if (ones) r4f_pass(re, im, len, len, t, 1); else r4f_pass(re, im, len, len, t, 0);
        for (int k = 0; k < 4; k++) sq_rec(re + k * (len >> 2), im + k * (len >> 2), len >> 2, t, ones);
        if (ones) r4b_pass(re, im, len, len, t, 1); else r4b_pass(re, im, len, len, t, 0);
    }
}

// Divide by n, round, pass the carries on; returns how far from a whole number the worst value was.
// A rounded value is d0 + 2^bits * d1 + 2^(2*bits) * d2 with d0 and d1 of bits bits and d2 small; digit k becomes
// d0[k] + d1[k-1] + d2[k-2] (round the circle): one bit wider than a digit proper, and no carry has to run along
// the number, so this too goes four at a time.
static double carry(double* re, double* im, double* t1, double* t2, int n, int bits)
{
    const __m256d inv = _mm256_set1_pd(1.0 / n), up = _mm256_set1_pd((double)(1ull << bits)), down = _mm256_set1_pd(1.0 / (double)(1ull << bits));
    const __m256d absm = _mm256_castsi256_pd(_mm256_set1_epi64x(0x7FFFFFFFFFFFFFFFll)), zero = _mm256_setzero_pd();
    __m256d worst = zero;
    for (int k = 0; k < n; k += 4)
    {
        __m256d v = _mm256_mul_pd(LD(re + k), inv), r = _mm256_round_pd(v, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
        worst = _mm256_max_pd(worst, _mm256_and_pd(_mm256_sub_pd(v, r), absm));
        __m256d c1 = _mm256_round_pd(_mm256_mul_pd(r, down), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
        __m256d d0 = _mm256_fnmadd_pd(c1, up, r);
        __m256d d2 = _mm256_round_pd(_mm256_mul_pd(c1, down), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
        ST(re + k, d0); ST(t1 + k, _mm256_fnmadd_pd(d2, up, c1)); ST(t2 + k, d2); ST(im + k, zero);
    }
    double first[4];
    for (int k = 0; k < 4; k++) first[k] = re[k] + t1[(k - 1 + n) % n] + t2[(k - 2 + n) % n];
    for (int k = 4; k < n; k += 4) ST(re + k, _mm256_add_pd(LD(re + k), _mm256_add_pd(LD(t1 + k - 1), LD(t2 + k - 2))));
    for (int k = 0; k < 4; k++) re[k] = first[k];
    double w[4]; _mm256_storeu_pd(w, worst);
    double m = w[0]; for (int k = 1; k < 4; k++) if (w[k] > m) m = w[k];
    return m;
}

// the number modulo 2^(2*bits) - 1: the even digits plus 2^bits times the odd ones
static uint64_t residue(const double* re, int n, int bits)
{
    int64_t even = 0, odd = 0;
    for (int k = 0; k < n; k += 2) { even += (int64_t)re[k]; odd += (int64_t)re[k + 1]; }
    int64_t m = (int64_t)((1ull << (2 * bits)) - 1), s = (even % m) + ((odd % m) << bits) % m;
    s %= m; if (s < 0) s += m;
    return (uint64_t)s;
}

static void fill(double* re, double* im, int n, int bits, uint64_t* s)
{
    for (int k = 0; k < n; k++)
    {
        *s = *s * 6364136223846793005ull + 1442695040888963407ull;
        re[k] = (double)((int64_t)((*s >> 33) & ((1ull << bits) - 1)) - (int64_t)(1ull << (bits - 1)));
        im[k] = 0;
    }
}

// The digit size for a transform of n points: the largest whose rounding stays well under the 0.4 of the check.
// Measured 17.11.2026 on a sound processor, the worst rounding in 0.4 s (every bit more makes it four times larger):
//   1024 points: 20 bits 0.018, 21 bits 0.070 · 4096: 20 bits 0.037, 21 bits 0.156 · 16384: 20 bits 0.078, 21 bits
//   0.375 · 65536: 20 bits 0.164, 21 bits over 0.4 (errors). So 21 bits up to 4096 points, 20 above.
static int digit_bits(int n)
{
    int lg = 0; while ((1 << lg) < n) lg++;
    return lg <= 12 ? 21 : 20;
}

// Squarings of a number of n digits (n points: a power of two, 64 to 65536) for about ms milliseconds, at least one.
// buf: 4 * n + 64 numbers (see LAYOUT). bits: the digit size, 0 for the one that suits n; plus 0x100 for the transforms made of fused
// operations only (forward_dense), 0x200 for the sums through the fused units (forward_one), 0x400 for the squaring of radix 4 (sq_rec; with 0x200 its sums through the fused units). corrupt: -1, or anything else to flip one bit
// in the middle of every squaring (a check: every squaring must then count as an error).
// Adds to *iters and *errors; *worst: the largest rounding seen. Returns 0, or -1 when it cannot run here.
__declspec(dllexport) int rt_stress_fft(double* buf, int n, int bits, unsigned long long seed, int ms, int corrupt, long long* iters, long long* errors, double* worst)
{
    if (!buf || n < 64 || n > 65536 || (n & (n - 1)) || !(rt_stress_caps() & (1 << RT_K_AVX2))) return -1;
    tw_t* t = twiddles(n);
    if (!t) return -1;
    int dense = bits & 0x100, ones = bits & 0x200, quad = bits & 0x400; bits &= 0xFF;
    if (bits <= 0) bits = digit_bits(n);
    if (bits < 8 || bits > 22) return -1;
    double* re = buf, * im = buf + n + 40, * t1 = buf + 2 * n + 48, * t2 = buf + 3 * n + 56;   // LAYOUT
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull, m = (1ull << (2 * bits)) - 1;
    fill(re, im, n, bits, &s);
    uint64_t before = residue(re, n, bits);
    LARGE_INTEGER f, t0, now; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    do
    {
        if (quad)
        {
            if (corrupt >= 0) *(uint64_t*)(re + n / 2) ^= 1ull << 40;    // forward and squaring go together here: the flip comes before
            sq_rec(re, im, n, t, ones != 0);
        }
        else
        {
            if (ones) forward_one(re, im, n, t); else if (dense) forward_dense(re, im, n, t); else forward(re, im, n, t);
            if (corrupt >= 0) *(uint64_t*)(re + n / 2) ^= 1ull << 40;
            square(re, im, n);
            if (ones) backward_one(re, im, n, t); else if (dense) backward_dense(re, im, n, t); else backward(re, im, n, t);
        }
        double e = carry(re, im, t1, t2, n, bits);
        uint64_t after = residue(re, n, bits), hi, lo = _umul128(before, before, &hi), want;
        _udiv128(hi, lo, m, &want);
        (*iters)++;
        if (e > *worst) *worst = e;
        if (e > 0.4 || after != want) { (*errors)++; fill(re, im, n, bits, &s); before = residue(re, n, bits); }
        else before = after;
        QueryPerformanceCounter(&now);
    } while (now.QuadPart < until);
    return 0;
}

// ---------- the weighted transform: what Prime95 squares, after Crandall and Fagin (1994) ----------
// 17.11: on CCD1 Core 8 (18 steps too deep) the transform above finds an error after 1 to 30 s, Prime95 after 1 to 5 s;
// a copy of Prime95's hot loop with its memory strides and its normalization step did not find it at all, so it is the
// arithmetic of the whole transform that counts. The main known difference left: Prime95 squares modulo 2^q - 1 with
// q not a multiple of the number of points, so the digits are of two sizes and every digit is multiplied by its own
// irrational weight 2^(fraction) before the transform and divided by it after. Every number at every stage then has all
// 53 bits of its mantissa in use; above, the transform starts on whole numbers of 21 bits.
// Here the same: X = sum of x_j * 2^ceil(q*j/n), digit j of ceil(q*(j+1)/n) - ceil(q*j/n) bits, balanced; weight
// a_j = 2^(ceil(q*j/n) - q*j/n); X^2 mod 2^q - 1 = carries of round(IFFT(FFT(a*x)^2) / (n * a)). Numbers are rounded the
// way Prime95 rounds them, by adding 1.5 * 2^52 and taking it away. Checks after every squaring:
//   - rounding: every value at most 0.45 from a whole number, as in Prime95;
//   - q is a multiple of 31, so 2^31 - 1 divides 2^q - 1: the remainder of X modulo 2^31 - 1 must be the square of the
//     remainder before, in integers, exact.
#define M31 0x7FFFFFFFull

typedef struct { int n, q; double* w; double* iw; unsigned char* b; uint32_t* pw; } dw_t;
static dw_t dws[32];
static volatile LONG dwlock;

static dw_t* weights(int n, int q)
{
    dw_t* found = 0;
    while (InterlockedCompareExchange(&dwlock, 1, 0)) Sleep(0);
    for (int i = 0; i < 32 && !found; i++) if (dws[i].n == n && dws[i].q == q && dws[i].w) found = &dws[i];
    for (int i = 0; i < 32 && !found; i++)
        if (!dws[i].w)
        {
            char* m = (char*)VirtualAlloc(0, (size_t)n * (8 + 8 + 4 + 1) + 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (!m) break;
            dw_t* d = &dws[i];
            double* w = (double*)m, * iw = w + n; uint32_t* pw = (uint32_t*)(iw + n); unsigned char* b = (unsigned char*)(pw + n);
            for (int j = 0; j < n; j++)
            {
                long long p = ((long long)q * j + n - 1) / n, p1 = ((long long)q * (j + 1) + n - 1) / n;
                double a = exp2((double)(p * n - (long long)q * j) / n);
                w[j] = a; iw[j] = 1.0 / ((double)n * a); b[j] = (unsigned char)(p1 - p); pw[j] = 1u << (p % 31);
            }
            for (int j = 0; j < 4; j++) b[n + j] = b[j];
            d->iw = iw; d->b = b; d->pw = pw; d->n = n; d->q = q; d->w = w; // w last: it is what the search looks at
            found = d;
        }
    InterlockedExchange(&dwlock, 0);
    return found;
}

// q for n points and about `tenths` / 10 bits a digit: a multiple of 31 that is not a multiple of n
static int modulus(int n, int tenths)
{
    int q = (int)(((long long)n * tenths / 10 + 15) / 31) * 31;
    while (q % n == 0) q += 31;
    return q;
}

// the default digit size, tenths of a bit. Measured 17.11 on a sound 8945HX, the worst rounding in 0.3 s (each half bit
// more doubles it; the limit of the check is 0.4): 1024 points 21.5 bits 0.156; 4096: 21.0 bits 0.156, 21.5 bits 0.313;
// 16384: 20.5 bits 0.172, 21.0 bits 0.344; 32768: 20.0 bits 0.125, 20.5 bits 0.250; 65536: 20.0 bits 0.164, 20.5 bits
// 0.344. Over 2.5 s: 1024 points 22.0 bits 0.375; 4096: 21.2 bits 0.25, 21.5 bits 0.375; 16384: 20.8 bits 0.31; 32768:
// 20.5 bits 0.31; 65536: 20.3 bits 0.31. Prime95 squares at the limit of every length (its torture test picks the
// largest exponent the length takes: 21.3 bits at 4K, 20.8 at 16K, 20.5 at 32K, 20.4 at 36K) and stops at a rounding of
// 0.45: with the worst near 0.3 a small error of the hardware is enough to cross it. A first choice here kept the worst
// at 0.16 under a limit of 0.4 and caught CCD1 Core 8, 18 steps too deep, in none of 4 runs where Prime95 catches it
// within a second. So the same here: the limits of Prime95, the same 0.45. With the carry in vectors (dwt_carry) the
// digits come out a bit wider and the limits fall by half a bit; over 2.5 s: 1024 points 21.3 bits 0.25, 21.5 bits 0.375;
// 4096: 20.8 bits 0.25, 21.0 bits 0.375; 16384: 20.0 bits 0.22, 20.3 bits 0.31; 32768: 20.0 bits 0.28, 20.3 bits one 0.5.
// --stress-check prints the worst.
static int dwt_tenths(int n)
{
    int lg = 0; while ((1 << lg) < n) lg++;
    return lg <= 10 ? 213 : lg <= 12 ? 208 : lg <= 14 ? 200 : lg == 15 ? 199 : 196;
}

// The squaring of radix 4 (kinds 3, 4) runs nearer the edge, with the threshold of Prime95 (18.11). Why: a small error of
// the hardware in one number of the forward transform reaches every output after the transform back, each a little
// off. The exact remainder sees it only when some output is pushed past half a unit; the rounding check sees it when
// some output is pushed past its threshold. Prime95 (its report on CCD1 Core 8, 18 steps too deep: «Rounding was
// 0.488, expected less than 0.4», 36K) sits with its worst rounding just under 0.4; the first choice here sat at 0.25
// under 0.45, so an error had to be eight times larger to be seen. Measured 18.11 on a sound core, 3 s each (the
// rounding comes in steps of 1/16 or 1/32: outputs are near 2^48): 1024 points 21.6 bits 0.375, 21.7 bits over;
// 4096: 21.0 bits 0.375, 21.1 over; 16384: 20.5 bits 0.375, 20.6 0.438; 32768: 20.2 bits 0.375, 20.3 0.438; 65536:
// 20.0 bits 0.375, 20.1 over. Taken: a tenth of a bit under the last clean one, threshold 0.4; over 90 s 1024 points at
// 21.5 bits and 65536 at 19.9 still went over 0.4 now and then (1 in 26 million, 4 in 220 thousand, the same in both
// kinds: the data, not the hardware), so those two are a tenth lower again. Then all 32 threads for 3 minutes (4.0 GHz,
// 0.78 V) flagged 7 sound cores at once, which one thread never did in 4 minutes: everything a tenth lower again.
static int q_tenths(int n)
{
    int lg = 0; while ((1 << lg) < n) lg++;
    return lg <= 10 ? 213 : lg <= 12 ? 208 : lg <= 14 ? 203 : lg == 15 ? 200 : 197;
}
#define Q_LIMIT 0.40

// 17.11: the first form of the carry and of the remainder went digit by digit and took 80 % of the time: the squaring
// ran at 5 to 7 billion operations a second where the plain transform runs at 27 to 32, a light load that let the core
// clock high and caught nothing. Prime95's normalization is in vectors and takes about 15 % of its time; so here.

// 2^b for four widths b of a byte table: the exponent field built from the bytes
static __forceinline __m256d pow2b(const unsigned char* b, int neg)
{
    __m256i e = _mm256_cvtepu8_epi64(_mm_cvtsi32_si128(*(const int*)b));
    e = neg ? _mm256_sub_epi64(_mm256_set1_epi64x(1023), e) : _mm256_add_epi64(_mm256_set1_epi64x(1023), e);
    return _mm256_castsi256_pd(_mm256_slli_epi64(e, 52));
}

// the remainder modulo 2^31 - 1 of the digits in re, and the digits weighted for the next forward transform
static uint32_t dwt_finish(double* re, int n, const dw_t* d)
{
    const __m256i m31 = _mm256_set1_epi64x((long long)M31), zero = _mm256_setzero_si256();
    __m256i acc = zero;
    for (int j = 0; j < n; j += 4)
    {
        __m256d x = LD(re + j);
        __m256i v = _mm256_cvtepi32_epi64(_mm256_cvtpd_epi32(x));                 // |digit| < 2^23
        v = _mm256_add_epi64(v, _mm256_and_si256(_mm256_cmpgt_epi64(zero, v), m31)); // a remainder: 0 .. 2^31 - 1
        __m256i pr = _mm256_mul_epu32(v, _mm256_cvtepu32_epi64(_mm_loadu_si128((const __m128i*)(d->pw + j)))); // < 2^62
        acc = _mm256_add_epi64(acc, _mm256_add_epi64(_mm256_and_si256(pr, m31), _mm256_srli_epi64(pr, 31))); // < 2^32 each
        ST(re + j, _mm256_mul_pd(x, _mm256_loadu_pd(d->w + j)));
    }
    uint64_t a[4]; _mm256_storeu_si256((__m256i*)a, acc);
    uint64_t sum = 0; for (int k = 0; k < 4; k++) { uint64_t x = a[k]; while (x > M31) x = (x & M31) + (x >> 31); sum += x; }
    while (sum > M31) sum = (sum & M31) + (sum >> 31);
    return (uint32_t)(sum == M31 ? 0 : sum);
}

// new digits (balanced, b_j bits), their remainder, and the digits weighted in re
static uint32_t dwt_fill(double* re, double* im, int n, const dw_t* d, uint64_t* s)
{
    for (int j = 0; j < n; j++)
    {
        *s = *s * 6364136223846793005ull + 1442695040888963407ull;
        long long half = 1ll << (d->b[j] - 1);
        re[j] = (double)((long long)((*s >> 20) & (2 * half - 1)) - half);
        im[j] = 0;
    }
    return dwt_finish(re, n, d);
}

// Round, take the weight away, pass the carries on, in vectors as Prime95 does: every value v rounded by adding
// 1.5 * 2^52 and taking it away, then split as v = d0 + 2^b_j * (d1 + 2^b_j+1 * d2) with d0, d1 balanced; digit j
// becomes d0_j + d1_j-1 + d2_j-2 (round the circle: 2^q is 1). The digits come out a bit wider than b_j, as in Prime95.
// t1, t2: n numbers each. Returns the worst rounding; re holds the digits, im zeros.
static double dwt_carry(double* re, double* im, double* t1, double* t2, int n, const dw_t* d)
{
    const __m256d magic = _mm256_set1_pd(6755399441055744.0), absm = _mm256_castsi256_pd(_mm256_set1_epi64x(0x7FFFFFFFFFFFFFFFll)), zero = _mm256_setzero_pd();
    __m256d worst = zero;
    for (int j = 0; j < n; j += 4)
    {
        __m256d v = _mm256_mul_pd(LD(re + j), _mm256_loadu_pd(d->iw + j));
        __m256d r = _mm256_sub_pd(_mm256_add_pd(v, magic), magic);
        worst = _mm256_max_pd(worst, _mm256_and_pd(_mm256_sub_pd(v, r), absm));
        __m256d bb = pow2b(d->b + j, 0), ib = pow2b(d->b + j, 1), bb1 = pow2b(d->b + j + 1, 0), ib1 = pow2b(d->b + j + 1, 1);
        __m256d c1 = _mm256_sub_pd(_mm256_add_pd(_mm256_mul_pd(r, ib), magic), magic);
        __m256d d0 = _mm256_fnmadd_pd(c1, bb, r);
        __m256d c2 = _mm256_sub_pd(_mm256_add_pd(_mm256_mul_pd(c1, ib1), magic), magic);
        ST(re + j, d0); ST(t1 + j, _mm256_fnmadd_pd(c2, bb1, c1)); ST(t2 + j, c2); ST(im + j, zero);
    }
    double first[4];
    for (int k = 0; k < 4; k++) first[k] = re[k] + t1[(k - 1 + n) % n] + t2[(k - 2 + n) % n];
    for (int k = 4; k < n; k += 4) ST(re + k, _mm256_add_pd(LD(re + k), _mm256_add_pd(LD(t1 + k - 1), LD(t2 + k - 2))));
    for (int k = 0; k < 4; k++) re[k] = first[k];
    double w[4]; _mm256_storeu_pd(w, worst);
    double m = w[0]; for (int k = 1; k < 4; k++) if (w[k] > m) m = w[k];
    return m;
}

// Squarings modulo 2^q - 1 of a number of n digits (n points: 1024 to 65536), weighted as Prime95 does, for about ms
// milliseconds, at least one. buf: 4 * n + 64 numbers. tenths: bits a digit in tenths, 0 for the default of n. kind: 0 the
// plain transform, 1 of chained fused operations, 2 with its sums through the fused units, 3 the squaring of radix 4
// (sq_rec), 4 the same with its sums through the fused units. corrupt: -1, or anything
// else to flip one bit after every forward transform (a check: every squaring must then count as an error).
// Adds to *iters and *errors; *worst: the largest rounding seen. Returns 0, or -1 when it cannot run here.
__declspec(dllexport) int rt_stress_dwt(double* buf, int n, int tenths, int kind, unsigned long long seed, int ms, int corrupt, long long* iters, long long* errors, double* worst)
{
    if (!buf || n < 1024 || n > 65536 || (n & (n - 1)) || !(rt_stress_caps() & (1 << RT_K_AVX2))) return -1;
    if (tenths <= 0) tenths = kind >= 3 ? q_tenths(n) : dwt_tenths(n);
    if (tenths < 120 || tenths > 230) return -1;
    tw_t* t = twiddles(n);
    dw_t* d = weights(n, modulus(n, tenths));
    if (!t || !d) return -1;
    double* re = buf, * im = buf + n + 40, * t1 = buf + 2 * n + 48, * t2 = buf + 3 * n + 56;   // LAYOUT
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull;
    uint32_t before = dwt_fill(re, im, n, d, &s);
    LARGE_INTEGER f, t0, now; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    do
    {
        if (kind >= 3)
        {
            if (corrupt >= 0) *(uint64_t*)(re + n / 2) ^= 1ull << 40;
            sq_rec(re, im, n, t, kind == 4);
        }
        else
        {
            if (kind == 2) forward_one(re, im, n, t); else if (kind == 1) forward_dense(re, im, n, t); else forward(re, im, n, t);
            if (corrupt >= 0) *(uint64_t*)(re + n / 2) ^= 1ull << 40;
            square(re, im, n);
            if (kind == 2) backward_one(re, im, n, t); else if (kind == 1) backward_dense(re, im, n, t); else backward(re, im, n, t);
        }
        double e = dwt_carry(re, im, t1, t2, n, d);
        uint32_t after = dwt_finish(re, n, d), want = (uint32_t)(((uint64_t)before * before) % M31);
        (*iters)++;
        if (e > *worst) *worst = e;
        if (e > (kind >= 3 ? Q_LIMIT : 0.45) || after != want) { (*errors)++; before = dwt_fill(re, im, n, d, &s); }
        else before = after;
        QueryPerformanceCounter(&now);
    } while (now.QuadPart < until);
    return 0;
}

// a measurement: microseconds of reps runs of one part (0 the transform of radix 2 forward + squaring + back, 1 of radix 4,
// 2 the carry of the weighted transform, 3 its remainder; 100 and up: radix 4 with blocks of that many points) on n points
// of buf (4 * n + 64 numbers)
__declspec(dllexport) double rt_stress_fft_bench(double* buf, int n, int part, int reps)
{
    tw_t* t = twiddles(n); dw_t* d = weights(n, modulus(n, dwt_tenths(n)));
    double* re = buf, * im = buf + n + 40; uint64_t s = 7;
    dwt_fill(re, im, n, d, &s);
    LARGE_INTEGER f, a, b; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&a);
    for (int r = 0; r < reps; r++)
        if (part == 0) { forward(re, im, n, t); square(re, im, n); backward(re, im, n, t); _mm256_storeu_pd(re, _mm256_mul_pd(LD(re), _mm256_set1_pd(1.0 / n))); }
        else if (part == 1) { sq_rec(re, im, n, t, 0); }
        else if (part >= 100) { int keep = r4_block; r4_block = part; sq_rec(re, im, n, t, 0); r4_block = keep; }
        else if (part == 2) dwt_carry(re, im, buf + 2 * n + 48, buf + 3 * n + 56, n, d);
        else dwt_finish(re, n, d);
    QueryPerformanceCounter(&b);
    return (double)(b.QuadPart - a.QuadPart) * 1e6 / f.QuadPart / reps;
}
