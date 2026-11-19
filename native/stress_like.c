// Red Tune's own processor load: a kernel shaped after Prime95's FMA transform loop (17.11.2026); /arch:AVX2.
//
// Why: a core of the 8945HX (CCD1 Core 8, 18 steps too deep) failed Prime95's AVX2 FMA code within 1 to 5 s and the
// own kernels only after 3 to 30 s. The hot loop of Prime95 was found by sampling the instruction pointer of its
// threads and read in a disassembly; its registers and memory were read from the running program
// (RedTune-devtools\owntest-check\peek-regs.ps1). What it does in one step, and what this kernel does the same way:
//   - 8 vectors are read from 4 rows lying 8 KB + 128 bytes apart (2 vectors of each row), and written back to the
//     same places: four streams through the first-level cache at a stride that just misses one set of it;
//   - 6 vectors of factors are read from a table that streams along beside them (192 bytes a step);
//   - sums and differences are made through the fused units with a factor of exactly 1.0 (a register of ones:
//     1.0 * a + b and 1.0 * a - b), in one of them the first lane carries sqrt(1/2) instead (a blend of the ones);
//   - lane 0 of pairs of numbers is swapped by blends between the sums (the complex halves of a radix-4 step);
//   - the second level is factor * a ± b with the table's factors, then six products with the table's other factors;
//   - the numbers are of the size Prime95 works with (around a million, either sign).
// Own parts, the check: the numbers are worked through twice, in two copies; the second copy holds the same numbers
// with the two 128-bit halves of every vector changed round (and its table likewise), so every number is computed
// once in one half of the unit and once in the other, among other neighbours. After every round the copies must agree
// to the bit. The blends take lanes 0 and 2 instead of lane 0 alone, which keeps the arithmetic the same under that
// change. Two more products (by 0.5) keep the numbers from growing: Prime95 keeps them bounded with its inverse
// transform, which this kernel does not have.
// 17.11, the first form of this kernel held the same two numbers in both halves of a vector and compared the halves:
// 3 runs of 30 s clean on CCD1 Core 8 18 steps too deep, where Prime95 fails within 1 to 5 s. An error of timing
// depends on the numbers; with the same numbers in both halves both halves can err alike and the check sees nothing.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include <immintrin.h>
#include "stress.h"

int rt_stress_caps(void);

#define ROW 1040            // doubles from one row to the next: 8 KB + 128 bytes, as in Prime95
#define STEPS 64            // steps of a pass: 2 vectors of each row a step, 4 KB of each row, as Prime95's inner loop
#define PASSES 48           // passes of a round, then new numbers
#define TW (STEPS * 24)     // a table: 6 vectors of 4 a step
#define COPY (4 * ROW + TW) // one copy: the rows, then its table

static uint64_t nxt(uint64_t* s) { *s = *s * 6364136223846793005ull + 1442695040888963407ull; return *s >> 11; }
static double unit(uint64_t* s) { return (double)nxt(s) / 9007199254740992.0; } // [0, 1)

// buf: 2 * COPY doubles, 32-byte aligned (rt_stress_alloc gives pages). Copy A, then copy B: the same numbers with the
// halves of every vector changed round.
static void fill(double* buf, uint64_t* s)
{
    double* a = buf, * b = buf + COPY;
    for (int i = 0; i < 4 * ROW; i++) a[i] = (unit(s) * 3.0e6 + 1.0e5) * (nxt(s) & 1 ? 1 : -1); // about a million, either sign
    for (int k = 0; k < STEPS; k++)
        for (int v = 0; v < 6; v++)
            for (int l = 0; l < 4; l++)
                // vectors 1, 3, 5 of a step: the factors of the second level (tangents in Prime95: 0.1 to 1);
                // vectors 0, 2, 4: the products after it (cosines there), a little under 0.71 so the numbers stay bounded
                a[4 * ROW + k * 24 + v * 4 + l] = v & 1 ? 0.1 + 0.9 * unit(s) : 0.30 + 0.40 * unit(s);
    for (int i = 0; i < COPY; i += 4) { b[i] = a[i + 2]; b[i + 1] = a[i + 3]; b[i + 2] = a[i]; b[i + 3] = a[i + 1]; }
}

#define L(o) _mm256_load_pd(r + (o))
#define S(o, v) _mm256_store_pd(r + (o), v)
#define BL(a, b) _mm256_blend_pd(a, b, 5)

static void pass(double* copy)
{
    const __m256d ones = _mm256_set1_pd(1.0), half = _mm256_set1_pd(0.5);
    const __m256d w = BL(ones, _mm256_set1_pd(0.70710678118654757));
    double* r = copy; const double* t = copy + 4 * ROW;
    for (int k = 0; k < STEPS; k++, r += 8, t += 24)
    {
        __m256d b1 = L(ROW + 4), d0 = L(3 * ROW), d1 = L(3 * ROW + 4);
        __m256d x5 = BL(b1, d0);
        __m256d x1 = _mm256_fmsub_pd(ones, x5, d1);
        d0 = BL(d0, b1);
        __m256d b0 = L(ROW);
        __m256d x6 = _mm256_fmsub_pd(ones, b0, d0);
        __m256d x11 = _mm256_fmadd_pd(ones, b0, d0);
        x5 = _mm256_fmadd_pd(ones, x5, d1);
        __m256d c0 = L(2 * ROW), a1 = L(4);
        __m256d x8 = BL(a1, c0);
        __m256d c1 = L(2 * ROW + 4);
        __m256d x10 = _mm256_fmadd_pd(ones, x8, c1);
        x8 = _mm256_fmsub_pd(ones, x8, c1);
        c0 = BL(c0, a1);
        __m256d a0 = L(0);
        __m256d x13 = _mm256_fmadd_pd(ones, a0, c0);
        __m256d x12 = _mm256_fmsub_pd(ones, a0, c0);
        __m256d x0 = BL(x10, x11), x4 = BL(x11, x10);
        __m256d x9 = _mm256_fmadd_pd(ones, x6, x1);
        __m256d x3 = _mm256_fmsub_pd(ones, x1, x6);
        x9 = BL(x6, x9); x1 = BL(x1, x3);
        __m256d t3 = _mm256_load_pd(t + 12);
        __m256d x7 = _mm256_fmsub_pd(ones, x0, x5);
        x0 = _mm256_fmadd_pd(ones, x0, x5);
        __m256d t1 = _mm256_load_pd(t + 4);
        x10 = _mm256_fmsub_pd(ones, x13, x4);
        x13 = _mm256_fmadd_pd(ones, x13, x4);
        __m256d t5 = _mm256_load_pd(t + 20);
        x11 = _mm256_fmadd_pd(w, x9, x8);
        x8 = _mm256_fnmadd_pd(x9, w, x8);
        x9 = _mm256_fnmadd_pd(w, x1, x12);
        x12 = _mm256_fmadd_pd(x1, w, x12);
        x6 = _mm256_fmadd_pd(t3, x7, x10);
        x3 = _mm256_fmsub_pd(t3, x10, x7);
        __m256d t2 = _mm256_load_pd(t + 8), t0 = _mm256_load_pd(t);
        S(0, _mm256_mul_pd(x13, half));
        x1 = _mm256_fmadd_pd(t1, x11, x9);
        __m256d x2 = _mm256_fmsub_pd(t1, x9, x11);
        __m256d t4 = _mm256_load_pd(t + 16);
        x4 = _mm256_fmadd_pd(t5, x8, x12);
        x5 = _mm256_fmsub_pd(t5, x12, x8);
        x6 = _mm256_mul_pd(x6, t2); x3 = _mm256_mul_pd(x3, t2);
        x1 = _mm256_mul_pd(x1, t0); x2 = _mm256_mul_pd(x2, t0);
        x4 = _mm256_mul_pd(x4, t4); x5 = _mm256_mul_pd(x5, t4);
        S(4, _mm256_mul_pd(x0, half));
        S(ROW, x3); S(ROW + 4, x6); S(2 * ROW, x2); S(2 * ROW + 4, x1); S(3 * ROW, x5); S(3 * ROW + 4, x4);
    }
}

// the two copies the same, to the bit, with the halves of copy B changed round
static int copies_differ(const double* buf)
{
    const uint64_t* a = (const uint64_t*)buf, * b = a + COPY;
    for (int row = 0; row < 4; row++)
        for (int i = 0; i < STEPS * 8; i += 4)
        {
            const uint64_t* x = a + row * ROW + i, * y = b + row * ROW + i;
            if (x[0] != y[2] || x[1] != y[3] || x[2] != y[0] || x[3] != y[1]) return 1;
        }
    return 0;
}

// Rounds of PASSES passes over each copy for about ms milliseconds, at least one. buf: rt_stress_alloc of at least
// rt_stress_like_size() doubles. corrupt: -1, or anything else to flip a bit of one lane in the middle of every round.
// Adds to *rounds and *errors. Returns 0, or -1 when it cannot run here.
// ---------- the normalization step, as Prime95 makes it after its inverse transform ----------
// 17.11: Prime95 spends about 15 % of its time here (the second hot place of the sampling). It rounds every number to
// a whole one by adding a constant of 1.5 * 2^52 and taking it away again, and splits off the upper digits the same way
// with larger constants: sums of two numbers 30 to 70 binary places apart, the long shift of the adder and its rounding
// at work. Its error on CCD1 Core 8, «Rounding was 0.5», is what an adder that rounded one such sum wrong would give.
// Here the same on the numbers of the rows: r = (x + M) - M, e = x - r, the upper part h = (r + M2) - M2 (multiples of
// 2^20), l = r - h, and the number again from them with the table's factors, so it stays about a million with a
// fraction. Both copies go through it; the comparison of the copies checks it as the rest.
static void norm(double* copy)
{
    const __m256d m1 = _mm256_set1_pd(6755399441055744.0);          // 1.5 * 2^52: whole numbers
    const __m256d m2 = _mm256_set1_pd(7083549724304467820544.0);    // 1.5 * 2^72: multiples of 2^20
    const __m256d down = _mm256_set1_pd(1.0 / 4096.0);
    const double* t = copy + 4 * ROW;
    for (int row = 0; row < 4; row++)
    {
        double* r = copy + row * ROW;
        for (int i = 0; i < STEPS * 8; i += 4)
        {
            __m256d x = _mm256_load_pd(r + i), f = _mm256_load_pd(t + ((row * STEPS * 8 + i) % TW));
            __m256d w = _mm256_sub_pd(_mm256_add_pd(x, m1), m1);       // x rounded to a whole number
            __m256d e = _mm256_sub_pd(x, w);                           // what the rounding took away: -0.5 .. 0.5
            __m256d h = _mm256_sub_pd(_mm256_add_pd(w, m2), m2);       // its upper digits
            __m256d l = _mm256_sub_pd(w, h);                           // its lower digits
            _mm256_store_pd(r + i, _mm256_add_pd(_mm256_fmadd_pd(l, f, _mm256_mul_pd(h, down)), e));
        }
    }
}

// as rt_stress_like, with the normalization step after every sixth pass (Prime95: one after the 12 passes of a
// forward and an inverse transform of 4K)
static int run(double* buf, unsigned long long seed, int ms, int corrupt, int every, long long* rounds, long long* errors);

__declspec(dllexport) int rt_stress_like_norm(double* buf, unsigned long long seed, int ms, int corrupt, long long* rounds, long long* errors)
{
    return run(buf, seed, ms, corrupt, 6, rounds, errors);
}

__declspec(dllexport) int rt_stress_like(double* buf, unsigned long long seed, int ms, int corrupt, long long* rounds, long long* errors)
{
    return run(buf, seed, ms, corrupt, 0, rounds, errors);
}

static int run(double* buf, unsigned long long seed, int ms, int corrupt, int every, long long* rounds, long long* errors)
{
    if (!buf || !(rt_stress_caps() & (1 << RT_K_AVX2))) return -1;
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull;
    LARGE_INTEGER f, t0, now; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    do
    {
        fill(buf, &s);
        for (int p = 0; p < PASSES; p++)
        {
            pass(buf); pass(buf + COPY); // the copies one after the other: an error at one moment hits only one of them
            if (every > 0 && p % every == every - 1) { norm(buf); norm(buf + COPY); }
            if (corrupt >= 0 && p == PASSES / 2) ((uint64_t*)buf)[ROW + 1] ^= 1ull << 30;
        }
        (*rounds)++;
        if (copies_differ(buf)) (*errors)++;
        QueryPerformanceCounter(&now);
    } while (now.QuadPart < until);
    return 0;
}

// the buffer rt_stress_like needs, in doubles
__declspec(dllexport) int rt_stress_like_size(void) { return 2 * COPY; }
