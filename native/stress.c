// Red Tune's own processor load with a self-check: the prime, the integer kernel, what the program calls. See stress.h.
// Built by build.ps1 into RedTuneStress.dll (static C runtime: nothing else to install).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include "stress.h"

uint64_t rt_p;
double rt_pd, rt_u;
static int caps;        // bit k: kernel k can run on this processor
__declspec(thread) int rt_row = RT_ROW_BEST;
static LONG ready;

static uint64_t mulmod64(uint64_t a, uint64_t b, uint64_t m) { uint64_t hi, lo = _umul128(a, b, &hi), r; _udiv128(hi, lo, m, &r); return r; }

static uint64_t powmod64(uint64_t x, uint64_t e, uint64_t m)
{
    uint64_t r = 1; x %= m;
    while (e) { if (e & 1) r = mulmod64(r, x, m); x = mulmod64(x, x, m); e >>= 1; }
    return r;
}

// Miller-Rabin with the bases that decide every 64-bit number
static int is_prime(uint64_t n)
{
    static const uint64_t bases[] = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 };
    if (n < 2) return 0;
    for (int i = 0; i < 12; i++) { if (n == bases[i]) return 1; if (n % bases[i] == 0) return 0; }
    uint64_t d = n - 1; int s = 0;
    while (!(d & 1)) { d >>= 1; s++; }
    for (int i = 0; i < 12; i++)
    {
        uint64_t x = powmod64(bases[i], d, n);
        if (x == 1 || x == n - 1) continue;
        int composite = 1;
        for (int k = 1; k < s; k++) { x = mulmod64(x, x, n); if (x == n - 1) { composite = 0; break; } }
        if (composite) return 0;
    }
    return 1;
}

static void init(void)
{
    if (InterlockedCompareExchange(&ready, 1, 0) != 0) { while (ready != 2) Sleep(0); return; }
    uint64_t p = (1ull << 50) - 1;
    while (!is_prime(p)) p -= 2;    // the largest prime under 2^50
    rt_p = p; rt_pd = (double)p; rt_u = 1.0 / (double)p;
    int r[4];
    caps = 1 << RT_K_INT;
    __cpuid(r, 1);
    int osxsave = (r[2] >> 27) & 1, avx = (r[2] >> 28) & 1, fma = (r[2] >> 12) & 1;
    unsigned long long xcr = osxsave ? _xgetbv(0) : 0;
    __cpuidex(r, 7, 0);
    int avx2 = (r[1] >> 5) & 1, avx512f = (r[1] >> 16) & 1, avx512dq = (r[1] >> 17) & 1;
    if (avx && fma && avx2 && (xcr & 6) == 6) caps |= 1 << RT_K_FMA128 | 1 << RT_K_AVX2;
    if ((caps & (1 << RT_K_AVX2)) && avx512f && avx512dq && (xcr & 0xE6) == 0xE6) caps |= 1 << RT_K_AVX512;
    InterlockedExchange(&ready, 2);
}

// the integer kernel: the same Fermat pass with 64-bit multiply and divide
static int pass_int(double* buf, int n, double base, int corrupt, int* first)
{
    uint64_t* w = (uint64_t*)buf; uint64_t p = rt_p, b0 = (uint64_t)base, e = p - 1;
    for (int i = 0; i < n; i++) w[i] = b0 + i;
    int top = 63; while (!((e >> top) & 1)) top--;
    for (int bit = top - 1; bit >= 0; bit--)
    {
        if ((e >> bit) & 1) for (int i = 0; i < n; i++) { uint64_t r = mulmod64(w[i], w[i], p); w[i] = mulmod64(r, b0 + i, p); }
        else for (int i = 0; i < n; i++) w[i] = mulmod64(w[i], w[i], p);
        if (bit == corrupt) w[n / 2] ^= 1ull << 3;
    }
    int bad = 0;
    for (int i = 0; i < n; i++) if (w[i] != 1) { if (!bad) *first = i; bad++; }
    return bad;
}

static rt_pass_fn kernel(int k)
{
    int mem = k & RT_K_MEM, fill = k & RT_K_FILL; k &= ~(RT_K_MEM | RT_K_FILL);
    if (k < 0 || k > 3 || !(caps & (1 << k)) || ((mem || fill) && k == RT_K_INT)) return 0;
    if (fill) return k == RT_K_AVX512 ? rt_passf_avx512 : k == RT_K_AVX2 ? rt_passf_avx2 : rt_passf_fma128;
    if (mem) return k == RT_K_AVX512 ? rt_passm_avx512 : k == RT_K_AVX2 ? rt_passm_avx2 : rt_passm_fma128;
    return k == RT_K_AVX512 ? rt_pass_avx512 : k == RT_K_AVX2 ? rt_pass_avx2 : k == RT_K_FMA128 ? rt_pass_fma128 : pass_int;
}

// ---------- what RedTune.exe calls ----------

// which kernels this processor can run: bit 0 integer, 1 FMA 128, 2 AVX2, 3 AVX-512
__declspec(dllexport) int rt_stress_caps(void) { init(); return caps; }

// the prime in use (for the report)
__declspec(dllexport) unsigned long long rt_stress_prime(void) { init(); return rt_p; }


// memory for a thread's numbers (whole pages, so aligned for any kernel)
__declspec(dllexport) void* rt_stress_alloc(size_t bytes) { return VirtualAlloc(0, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE); }
__declspec(dllexport) void rt_stress_free(void* p) { if (p) VirtualFree(p, 0, MEM_RELEASE); }

// Fermat passes over n numbers (a multiple of 128) for about ms milliseconds, at least one pass.
// Adds to *passes and *errors (wrong numbers); *first: the place of the first wrong number of the last bad pass.
// corrupt: see stress.h (-1 in real use). Returns 0, or -1 when the kernel cannot run here.
__declspec(dllexport) int rt_stress_run(int k, double* buf, int n, unsigned long long seed, int ms, int corrupt, long long* passes, long long* errors, int* first)
{
    init();
    // bits 8 and up of k: the length of a row of an RT_K_FILL pass in units of 128 numbers (0: RT_ROW_BEST)
    int row = (k >> 8) * 128; k &= 0xFF;
    rt_row = row >= 128 && row <= RT_ROW ? row : RT_ROW_BEST;
    rt_pass_fn fn = kernel(k);
    if (!fn || !buf || n < 128 || (n & 127) || ((k & RT_K_FILL) && n > rt_row && n % rt_row)) return -1;
    LARGE_INTEGER f, t0, t; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    long long until = t0.QuadPart + f.QuadPart * ms / 1000;
    uint64_t s = seed * 6364136223846793005ull + 1442695040888963407ull;
    do
    {
        s = s * 6364136223846793005ull + 1442695040888963407ull;
        double base = (double)(2 + (s >> 24));          // under 2^40: every number stays far below the prime
        int where = 0, bad = fn(buf, n, base, corrupt, &where);
        (*passes)++;
        if (bad) { *errors += bad; *first = where; }
        QueryPerformanceCounter(&t);
    } while (t.QuadPart < until);
    return 0;
}

// The multiplication of kernel k against integer arithmetic on `count` random pairs: how many products differ
// (0 on a sound processor: the floating-point way is exact). -1: the kernel cannot run here.
__declspec(dllexport) int rt_stress_selftest(int k, int count, unsigned long long seed)
{
    init();
    k &= ~(RT_K_MEM | RT_K_FILL);
    if (!kernel(k)) return -1;
    if (k == RT_K_INT) return 0;
    double a[64], b[64], r[64]; int wrong = 0; uint64_t s = seed | 1, p = rt_p;
    for (int done = 0; done < count; done += 64)
    {
        for (int i = 0; i < 64; i++)
        {
            s = s * 6364136223846793005ull + 1442695040888963407ull; uint64_t x = (s >> 11) % p;
            s = s * 6364136223846793005ull + 1442695040888963407ull; uint64_t y = (s >> 11) % p;
            if (i < 4) { x = p - 1 - i; }                // the corners too
            if (i >= 4 && i < 8) { y = p - 1; }
            a[i] = (double)x; b[i] = (double)y;
        }
        if (k == RT_K_AVX512) rt_mul_avx512(a, b, r, 64); else if (k == RT_K_AVX2) rt_mul_avx2(a, b, r, 64); else rt_mul_fma128(a, b, r, 64);
        for (int i = 0; i < 64; i++) if (r[i] != (double)mulmod64((uint64_t)a[i], (uint64_t)b[i], p)) wrong++;
    }
    return wrong;
}
