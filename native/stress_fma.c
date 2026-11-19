// Red Tune's own processor load, the fused multiply-add kernel (17.11.2026); compiled with /arch:AVX2. See stress.h.
//
// Why it is here: Prime95 finds a weak core with its FMA code and not without it (stress_fft.c tells how that was
// measured), and what that code does that the kernels here did not is keep both fused multiply-add units of the core
// busy on every cycle with nothing else in between. So: twelve numbers x = x * m + c held in registers, m and c new
// on every step from a table of random values of every size and sign, so the alignment of the addend, the
// cancellation and the rounding take every form. Nothing but fused operations: twelve to a step, three steps'
// worth waiting at any moment for two units.
//
// The check costs no reference: every 256-bit number holds the same two values in its upper and its lower half, and
// the two halves go through separate halves of the unit. After every eight steps the halves are compared bit for
// bit. (A sum of all twelve is also compared with the same steps done on 128-bit registers, at the end of a round:
// that catches an error that hit both halves alike.)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include <immintrin.h>
#include "stress.h"

int rt_stress_caps(void);

#define STEPS 1024   // steps in a round; the numbers start anew every round, so they stay within bounds
#define TAB 256      // pairs of m and c

static double tab_m[TAB][4], tab_c[TAB][4];
static volatile LONG tab_ready, tab_lock;

static double rnd_mant(uint64_t* s) { *s = *s * 6364136223846793005ull + 1442695040888963407ull; return 1.0 + (double)(*s >> 12) / 4503599627370496.0; } // [1, 2), all 52 bits
static int rnd_int(uint64_t* s, int n) { *s = *s * 6364136223846793005ull + 1442695040888963407ull; return (int)((*s >> 33) % (uint64_t)n); }

static void table(void)
{
    if (tab_ready) return;
    while (InterlockedCompareExchange(&tab_lock, 1, 0)) Sleep(0);
    if (!tab_ready)
    {
        uint64_t s = 0x243F6A8885A308D3ull;
        for (int i = 0; i < TAB; i++)
        {
            // m: between 0.8 and 1.25 either way and of either sign, so x neither dies out nor runs away within a round;
            // c: any size from 2^-40 to 1, of either sign: the addend meets the product at every distance
            double m = (0.8 + 0.45 * (rnd_mant(&s) - 1.0)) * (rnd_int(&s, 2) ? 1 : -1);
            double c = rnd_mant(&s) * (rnd_int(&s, 2) ? 1 : -1);
            int e = rnd_int(&s, 41); for (int k = 0; k < e; k++) c *= 0.5;
            for (int k = 0; k < 4; k++) { tab_m[i][k] = m; tab_c[i][k] = c; }
        }
        InterlockedExchange(&tab_ready, 1);
    }
    InterlockedExchange(&tab_lock, 0);
}

#define F(x) x = _mm256_fmadd_pd(x, m, c)
#define SAME(x) _mm256_cmpeq_epi64(_mm256_castpd_si256(x), _mm256_castpd_si256(_mm256_permute2f128_pd(x, x, 1)))

// Rounds of STEPS steps for about ms milliseconds, at least one. corrupt: -1, or anything else to flip a bit of one
// half of one number in every round (a check). Adds to *rounds and *errors (rounds in which the halves differed or the
// sum did not match). Returns 0, or -1 when it cannot run here.
__declspec(dllexport) int rt_stress_fma(unsigned long long seed, int ms, int corrupt, long long* rounds, long long* errors)
{
    if (!(rt_stress_caps() & (1 << RT_K_AVX2))) return -1;
    table();
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull;
    LARGE_INTEGER f, t0, now; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    do
    {
        double start[12]; int at = rnd_int(&s, TAB);
        for (int k = 0; k < 12; k++) start[k] = rnd_mant(&s) * (rnd_int(&s, 2) ? 1 : -1);
        __m256d x0 = _mm256_set1_pd(start[0]), x1 = _mm256_set1_pd(start[1]), x2 = _mm256_set1_pd(start[2]), x3 = _mm256_set1_pd(start[3]),
                x4 = _mm256_set1_pd(start[4]), x5 = _mm256_set1_pd(start[5]), x6 = _mm256_set1_pd(start[6]), x7 = _mm256_set1_pd(start[7]),
                x8 = _mm256_set1_pd(start[8]), x9 = _mm256_set1_pd(start[9]), x10 = _mm256_set1_pd(start[10]), x11 = _mm256_set1_pd(start[11]);
        __m256i same = _mm256_set1_epi64x(-1);
        for (int step = 0; step < STEPS; step += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                __m256d m = _mm256_loadu_pd(tab_m[(at + step + k) & (TAB - 1)]), c = _mm256_loadu_pd(tab_c[(at + step + k) & (TAB - 1)]);
                F(x0); F(x1); F(x2); F(x3); F(x4); F(x5); F(x6); F(x7); F(x8); F(x9); F(x10); F(x11);
            }
            if (corrupt >= 0 && step == STEPS / 2) x5 = _mm256_xor_pd(x5, _mm256_castsi256_pd(_mm256_setr_epi64x(1ll << 30, 0, 0, 0)));
            same = _mm256_and_si256(same, _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x0), SAME(x1)), _mm256_and_si256(SAME(x2), SAME(x3))),
                _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x4), SAME(x5)), _mm256_and_si256(SAME(x6), SAME(x7))),
                                 _mm256_and_si256(_mm256_and_si256(SAME(x8), SAME(x9)), _mm256_and_si256(SAME(x10), SAME(x11))))));
        }
        int bad = _mm256_movemask_pd(_mm256_castsi256_pd(same)) != 15;
        // the same round once more, one number at a time on 128-bit registers, for the first four numbers: the two
        // ways have to agree to the bit
        // (once in 64 rounds: it runs four numbers where the round runs twelve, and the units would stand half idle)
        if (!bad && (*rounds & 63) == 0)
        {
            double wide[4][4]; _mm256_storeu_pd(wide[0], x0); _mm256_storeu_pd(wide[1], x1); _mm256_storeu_pd(wide[2], x2); _mm256_storeu_pd(wide[3], x3);
            __m128d y0 = _mm_set1_pd(start[0]), y1 = _mm_set1_pd(start[1]), y2 = _mm_set1_pd(start[2]), y3 = _mm_set1_pd(start[3]);
            for (int step = 0; step < STEPS; step++)
            {
                __m128d m = _mm_loadu_pd(tab_m[(at + step) & (TAB - 1)]), c = _mm_loadu_pd(tab_c[(at + step) & (TAB - 1)]);
                y0 = _mm_fmadd_pd(y0, m, c); y1 = _mm_fmadd_pd(y1, m, c); y2 = _mm_fmadd_pd(y2, m, c); y3 = _mm_fmadd_pd(y3, m, c);
            }
            double narrow[4]; narrow[0] = _mm_cvtsd_f64(y0); narrow[1] = _mm_cvtsd_f64(y1); narrow[2] = _mm_cvtsd_f64(y2); narrow[3] = _mm_cvtsd_f64(y3);
            for (int k = 0; k < 4; k++) if (*(uint64_t*)&narrow[k] != *(uint64_t*)&wide[k][0]) bad = 1;
        }
        (*rounds)++;
        if (bad) (*errors)++;
        QueryPerformanceCounter(&now);
    } while (now.QuadPart < until);
    return 0;
}

// ---------- sums through the fused unit, as Prime95's FMA code makes them ----------
// 17.11: the hot loop of Prime95's FMA transform, read from the running program (RedTune-devtools\owntest-check\
// sample-rip.ps1 and a disassembly of the places it showed), has 638 fused operations to 244 plain sums and
// differences in a pass, and most of the fused ones are a sum or a difference: a copy of a register holding 1.0,
// then 1.0 * a + b and 1.0 * a - b. Both fused units work as adders on every cycle and the adders proper stand
// idle. Here the same: twelve numbers in six pairs, each pair becomes (1.0 * a + b, 1.0 * a - b), then both are
// multiplied by a factor from the table (as Prime95 multiplies by a cosine) so they stay within bounds; the pairs
// are others on every step. The check is the one above: the two halves of every register have to stay the same.
#define BF(a, b, t) t = a; a = _mm256_fmadd_pd(a, one, b); b = _mm256_fmsub_pd(t, one, b); a = _mm256_mul_pd(a, m); b = _mm256_mul_pd(b, m)

__declspec(dllexport) int rt_stress_fma1(unsigned long long seed, int ms, int corrupt, long long* rounds, long long* errors)
{
    if (!(rt_stress_caps() & (1 << RT_K_AVX2))) return -1;
    table();
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull;
    LARGE_INTEGER f, t0, now; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    const __m256d one = _mm256_set1_pd(1.0);
    do
    {
        double start[12]; int at = rnd_int(&s, TAB);
        for (int k = 0; k < 12; k++) start[k] = rnd_mant(&s) * (rnd_int(&s, 2) ? 1 : -1);
        __m256d x0 = _mm256_set1_pd(start[0]), x1 = _mm256_set1_pd(start[1]), x2 = _mm256_set1_pd(start[2]), x3 = _mm256_set1_pd(start[3]),
                x4 = _mm256_set1_pd(start[4]), x5 = _mm256_set1_pd(start[5]), x6 = _mm256_set1_pd(start[6]), x7 = _mm256_set1_pd(start[7]),
                x8 = _mm256_set1_pd(start[8]), x9 = _mm256_set1_pd(start[9]), x10 = _mm256_set1_pd(start[10]), x11 = _mm256_set1_pd(start[11]), t;
        __m256i same = _mm256_set1_epi64x(-1);
        for (int step = 0; step < STEPS; step += 4)
        {
            // the factor: the table's m is 0.8 to 1.25 either way; half of it keeps a pair of sums from growing
            __m256d m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BF(x0, x1, t); BF(x2, x3, t); BF(x4, x5, t); BF(x6, x7, t); BF(x8, x9, t); BF(x10, x11, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 1) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BF(x0, x2, t); BF(x1, x3, t); BF(x4, x6, t); BF(x5, x7, t); BF(x8, x10, t); BF(x9, x11, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 2) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BF(x0, x4, t); BF(x1, x5, t); BF(x2, x6, t); BF(x3, x7, t); BF(x8, x11, t); BF(x9, x10, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 3) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BF(x0, x8, t); BF(x1, x9, t); BF(x2, x10, t); BF(x3, x11, t); BF(x4, x7, t); BF(x5, x6, t);
            // new blood, so the numbers do not die out: the table's c into two of them
            x0 = _mm256_fmadd_pd(x0, one, _mm256_loadu_pd(tab_c[(at + step) & (TAB - 1)]));
            x6 = _mm256_fmsub_pd(x6, one, _mm256_loadu_pd(tab_c[(at + step + 1) & (TAB - 1)]));
            if (corrupt >= 0 && step == STEPS / 2) x5 = _mm256_xor_pd(x5, _mm256_castsi256_pd(_mm256_setr_epi64x(1ll << 30, 0, 0, 0)));
            same = _mm256_and_si256(same, _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x0), SAME(x1)), _mm256_and_si256(SAME(x2), SAME(x3))),
                _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x4), SAME(x5)), _mm256_and_si256(SAME(x6), SAME(x7))),
                                 _mm256_and_si256(_mm256_and_si256(SAME(x8), SAME(x9)), _mm256_and_si256(SAME(x10), SAME(x11))))));
        }
        (*rounds)++;
        if (_mm256_movemask_pd(_mm256_castsi256_pd(same)) != 15) (*errors)++;
        QueryPerformanceCounter(&now);
    } while (now.QuadPart < until);
    return 0;
}

// ---------- the same with plain sums ----------
// 17.11: another core (CCD1 Core 8, 18 steps too deep) passed four minutes of the kernel above and failed Prime95
// within a second; Prime95 without FMA failed it too, in 14 s, and only its SSE2 code ran clean. So that core gives
// way on the plain 256-bit sums and differences (and the products) of Prime95's code without FMA, which the kernel
// above does not have at all. Here: the same pairs, (a + b, a - b) with the adders proper, then the product.
#define BA(a, b, t) t = a; a = _mm256_add_pd(a, b); b = _mm256_sub_pd(t, b); a = _mm256_mul_pd(a, m); b = _mm256_mul_pd(b, m)

__declspec(dllexport) int rt_stress_add1(unsigned long long seed, int ms, int corrupt, long long* rounds, long long* errors)
{
    if (!(rt_stress_caps() & (1 << RT_K_AVX2))) return -1;
    table();
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull;
    LARGE_INTEGER f, t0, now; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    do
    {
        double start[12]; int at = rnd_int(&s, TAB);
        for (int k = 0; k < 12; k++) start[k] = rnd_mant(&s) * (rnd_int(&s, 2) ? 1 : -1);
        __m256d x0 = _mm256_set1_pd(start[0]), x1 = _mm256_set1_pd(start[1]), x2 = _mm256_set1_pd(start[2]), x3 = _mm256_set1_pd(start[3]),
                x4 = _mm256_set1_pd(start[4]), x5 = _mm256_set1_pd(start[5]), x6 = _mm256_set1_pd(start[6]), x7 = _mm256_set1_pd(start[7]),
                x8 = _mm256_set1_pd(start[8]), x9 = _mm256_set1_pd(start[9]), x10 = _mm256_set1_pd(start[10]), x11 = _mm256_set1_pd(start[11]), t;
        __m256i same = _mm256_set1_epi64x(-1);
        for (int step = 0; step < STEPS; step += 4)
        {
            __m256d m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BA(x0, x1, t); BA(x2, x3, t); BA(x4, x5, t); BA(x6, x7, t); BA(x8, x9, t); BA(x10, x11, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 1) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BA(x0, x2, t); BA(x1, x3, t); BA(x4, x6, t); BA(x5, x7, t); BA(x8, x10, t); BA(x9, x11, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 2) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BA(x0, x4, t); BA(x1, x5, t); BA(x2, x6, t); BA(x3, x7, t); BA(x8, x11, t); BA(x9, x10, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 3) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BA(x0, x8, t); BA(x1, x9, t); BA(x2, x10, t); BA(x3, x11, t); BA(x4, x7, t); BA(x5, x6, t);
            x0 = _mm256_add_pd(x0, _mm256_loadu_pd(tab_c[(at + step) & (TAB - 1)]));
            x6 = _mm256_sub_pd(x6, _mm256_loadu_pd(tab_c[(at + step + 1) & (TAB - 1)]));
            if (corrupt >= 0 && step == STEPS / 2) x5 = _mm256_xor_pd(x5, _mm256_castsi256_pd(_mm256_setr_epi64x(1ll << 30, 0, 0, 0)));
            same = _mm256_and_si256(same, _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x0), SAME(x1)), _mm256_and_si256(SAME(x2), SAME(x3))),
                _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x4), SAME(x5)), _mm256_and_si256(SAME(x6), SAME(x7))),
                                 _mm256_and_si256(_mm256_and_si256(SAME(x8), SAME(x9)), _mm256_and_si256(SAME(x10), SAME(x11))))));
        }
        (*rounds)++;
        if (_mm256_movemask_pd(_mm256_castsi256_pd(same)) != 15) (*errors)++;
        QueryPerformanceCounter(&now);
    } while (now.QuadPart < until);
    return 0;
}

// ---------- the same sums with the factors the other way round ----------
// 17.11: the compiler made every sum of rt_stress_fma1 as «number times 1.0» (vfmadd231pd copy-of-b, a, one). Prime95
// has both: its differences are that, its sums are «1.0 times number» (vfmadd213pd a, one, b). The two factors of a
// fused multiply-add go into different inputs of the multiplier, so the two ways load it differently, and a core that
// passed the one (CCD1 Core 8) may give way on the other. Here every sum and difference is «1.0 times number».
#define BG(a, b, t) t = a; a = _mm256_fmadd_pd(one, a, b); b = _mm256_fmsub_pd(one, t, b); a = _mm256_mul_pd(a, m); b = _mm256_mul_pd(b, m)

__declspec(dllexport) int rt_stress_fma2(unsigned long long seed, int ms, int corrupt, long long* rounds, long long* errors)
{
    if (!(rt_stress_caps() & (1 << RT_K_AVX2))) return -1;
    table();
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull;
    LARGE_INTEGER f, t0, now; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    const __m256d one = _mm256_set1_pd(1.0);
    do
    {
        double start[12]; int at = rnd_int(&s, TAB);
        for (int k = 0; k < 12; k++) start[k] = rnd_mant(&s) * (rnd_int(&s, 2) ? 1 : -1);
        __m256d x0 = _mm256_set1_pd(start[0]), x1 = _mm256_set1_pd(start[1]), x2 = _mm256_set1_pd(start[2]), x3 = _mm256_set1_pd(start[3]),
                x4 = _mm256_set1_pd(start[4]), x5 = _mm256_set1_pd(start[5]), x6 = _mm256_set1_pd(start[6]), x7 = _mm256_set1_pd(start[7]),
                x8 = _mm256_set1_pd(start[8]), x9 = _mm256_set1_pd(start[9]), x10 = _mm256_set1_pd(start[10]), x11 = _mm256_set1_pd(start[11]), t;
        __m256i same = _mm256_set1_epi64x(-1);
        for (int step = 0; step < STEPS; step += 4)
        {
            __m256d m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BG(x0, x1, t); BG(x2, x3, t); BG(x4, x5, t); BG(x6, x7, t); BG(x8, x9, t); BG(x10, x11, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 1) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BG(x0, x2, t); BG(x1, x3, t); BG(x4, x6, t); BG(x5, x7, t); BG(x8, x10, t); BG(x9, x11, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 2) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BG(x0, x4, t); BG(x1, x5, t); BG(x2, x6, t); BG(x3, x7, t); BG(x8, x11, t); BG(x9, x10, t);
            m = _mm256_mul_pd(_mm256_loadu_pd(tab_m[(at + step + 3) & (TAB - 1)]), _mm256_set1_pd(0.5));
            BG(x0, x8, t); BG(x1, x9, t); BG(x2, x10, t); BG(x3, x11, t); BG(x4, x7, t); BG(x5, x6, t);
            x0 = _mm256_fmadd_pd(one, x0, _mm256_loadu_pd(tab_c[(at + step) & (TAB - 1)]));
            x6 = _mm256_fmsub_pd(one, x6, _mm256_loadu_pd(tab_c[(at + step + 1) & (TAB - 1)]));
            if (corrupt >= 0 && step == STEPS / 2) x5 = _mm256_xor_pd(x5, _mm256_castsi256_pd(_mm256_setr_epi64x(1ll << 30, 0, 0, 0)));
            same = _mm256_and_si256(same, _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x0), SAME(x1)), _mm256_and_si256(SAME(x2), SAME(x3))),
                _mm256_and_si256(_mm256_and_si256(_mm256_and_si256(SAME(x4), SAME(x5)), _mm256_and_si256(SAME(x6), SAME(x7))),
                                 _mm256_and_si256(_mm256_and_si256(SAME(x8), SAME(x9)), _mm256_and_si256(SAME(x10), SAME(x11))))));
        }
        (*rounds)++;
        if (_mm256_movemask_pd(_mm256_castsi256_pd(same)) != 15) (*errors)++;
        QueryPerformanceCounter(&now);
    } while (now.QuadPart < until);
    return 0;
}
