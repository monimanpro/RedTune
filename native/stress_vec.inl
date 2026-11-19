// The Fermat pass and the multiplication for one vector width. The including file defines:
//   V, LANES, PASS, MUL (the names of the two functions), and the operations VSET1, VLOAD, VSTORE, VMUL, VADD,
//   VFMSUB(a,b,c) = a*b-c, VFNMADD(a,b,c) = c-a*b, VROUND (to the nearest integer), VNEGFIX(r,p) = r < 0 ? r+p : r

// a*b mod p for a, b in [0, p), p < 2^50; exact (see stress.h)
static __forceinline V mulmod(V a, V b, V p, V u)
{
    V h = VMUL(a, b);
    V l = VFMSUB(a, b, h);          // the rounding error of h, exactly
    V q = VROUND(VMUL(h, u));       // the quotient, to within one
    V d = VFNMADD(q, p, h);         // h - q*p: an integer under 2^50 in size, so exact
    V r = VADD(d, l);               // in (-p, p)
    return VNEGFIX(r, p);
}

// Four products at once, written stage by stage. One product is a chain of some 25 cycles in which every step waits
// for the one before, and with the chains one after another the processor holds only a few of them in its window:
// measured 17.11, the units stood idle half the time and the core drew a third less current than under Prime95.
// Side by side the four chains, which have nothing to do with each other, keep both multipliers and both adders busy.
#define MULMOD4(r0, r1, r2, r3, a0, a1, a2, a3, b0, b1, b2, b3) do { \
    V h0 = VMUL(a0, b0), h1 = VMUL(a1, b1), h2 = VMUL(a2, b2), h3 = VMUL(a3, b3); \
    V l0 = VFMSUB(a0, b0, h0), l1 = VFMSUB(a1, b1, h1), l2 = VFMSUB(a2, b2, h2), l3 = VFMSUB(a3, b3, h3); \
    V q0 = VMUL(h0, u), q1 = VMUL(h1, u), q2 = VMUL(h2, u), q3 = VMUL(h3, u); \
    q0 = VROUND(q0); q1 = VROUND(q1); q2 = VROUND(q2); q3 = VROUND(q3); \
    h0 = VFNMADD(q0, p, h0); h1 = VFNMADD(q1, p, h1); h2 = VFNMADD(q2, p, h2); h3 = VFNMADD(q3, p, h3); \
    h0 = VADD(h0, l0); h1 = VADD(h1, l1); h2 = VADD(h2, l2); h3 = VADD(h3, l3); \
    r0 = VNEGFIX(h0, p); r1 = VNEGFIX(h1, p); r2 = VNEGFIX(h2, p); r3 = VNEGFIX(h3, p); } while (0)

void MUL(const double* a, const double* b, double* r, int count)
{
    V p = VSET1(rt_pd), u = VSET1(rt_u);
    int i = 0;
    for (; i + 4 * LANES <= count; i += 4 * LANES)     // the same code the pass runs
    {
        V a0 = VLOAD(a + i), a1 = VLOAD(a + i + LANES), a2 = VLOAD(a + i + 2 * LANES), a3 = VLOAD(a + i + 3 * LANES);
        V b0 = VLOAD(b + i), b1 = VLOAD(b + i + LANES), b2 = VLOAD(b + i + 2 * LANES), b3 = VLOAD(b + i + 3 * LANES);
        MULMOD4(a0, a1, a2, a3, a0, a1, a2, a3, b0, b1, b2, b3);
        VSTORE(r + i, a0); VSTORE(r + i + LANES, a1); VSTORE(r + i + 2 * LANES, a2); VSTORE(r + i + 3 * LANES, a3);
    }
    for (; i + LANES <= count; i += LANES) VSTORE(r + i, mulmod(VLOAD(a + i), VLOAD(b + i), p, u));
}

// n: a multiple of 4 * LANES
int PASS(double* buf, int n, double base, int corrupt, int* first)
{
    V p = VSET1(rt_pd), u = VSET1(rt_u), step = VSET1((double)(4 * LANES));
    double iota[4 * LANES];
    for (int k = 0; k < 4 * LANES; k++) iota[k] = base + k;
    V s0 = VLOAD(iota), s1 = VLOAD(iota + LANES), s2 = VLOAD(iota + 2 * LANES), s3 = VLOAD(iota + 3 * LANES);
    // the numbers themselves
    {
        V x0 = s0, x1 = s1, x2 = s2, x3 = s3;
        for (int i = 0; i < n; i += 4 * LANES)
        {
            VSTORE(buf + i, x0); VSTORE(buf + i + LANES, x1); VSTORE(buf + i + 2 * LANES, x2); VSTORE(buf + i + 3 * LANES, x3);
            x0 = VADD(x0, step); x1 = VADD(x1, step); x2 = VADD(x2, step); x3 = VADD(x3, step);
        }
    }
    uint64_t e = rt_p - 1;
    int top = 63; while (!((e >> top) & 1)) top--;
    for (int bit = top - 1; bit >= 0; bit--)
    {
        if ((e >> bit) & 1)
        {
            V x0 = s0, x1 = s1, x2 = s2, x3 = s3;
            for (int i = 0; i < n; i += 4 * LANES)
            {
                V r0 = VLOAD(buf + i), r1 = VLOAD(buf + i + LANES), r2 = VLOAD(buf + i + 2 * LANES), r3 = VLOAD(buf + i + 3 * LANES);
                MULMOD4(r0, r1, r2, r3, r0, r1, r2, r3, r0, r1, r2, r3);
                MULMOD4(r0, r1, r2, r3, r0, r1, r2, r3, x0, x1, x2, x3);
                VSTORE(buf + i, r0); VSTORE(buf + i + LANES, r1); VSTORE(buf + i + 2 * LANES, r2); VSTORE(buf + i + 3 * LANES, r3);
                x0 = VADD(x0, step); x1 = VADD(x1, step); x2 = VADD(x2, step); x3 = VADD(x3, step);
            }
        }
        else
            for (int i = 0; i < n; i += 4 * LANES)
            {
                V r0 = VLOAD(buf + i), r1 = VLOAD(buf + i + LANES), r2 = VLOAD(buf + i + 2 * LANES), r3 = VLOAD(buf + i + 3 * LANES);
                MULMOD4(r0, r1, r2, r3, r0, r1, r2, r3, r0, r1, r2, r3);
                VSTORE(buf + i, r0); VSTORE(buf + i + LANES, r1); VSTORE(buf + i + 2 * LANES, r2); VSTORE(buf + i + 3 * LANES, r3);
            }
        if (bit == corrupt) { uint64_t* w = (uint64_t*)(buf + n / 2); *w ^= 1ull << 3; }
    }
    int bad = 0;
    for (int i = 0; i < n; i++) if (buf[i] != 1.0) { if (!bad) *first = i; bad++; }
    return bad;
}

// ---------- the same pass, every step through memory ----------
// 17.11: at a Curve Optimizer value 15-18 steps too deep the pass above ran a minute without a sign, while Prime95 on
// the same core brought 34-37 corrected errors of the first-level data cache (WHEA 47, bank 0) in the same minute:
// the pass above reads and writes a number once per 18-36 operations and computes in registers, Prime95's transforms
// take nearly every operand from memory. Here a block of numbers goes through the product one step at a time, each
// step a loop of its own that reads its operands from memory and writes its result there: 8 reads and 5 writes per
// product instead of 1 and 1. Two sizes of the block:
//   PASSM  16 vectors: the block and the rows H, L, Q, X stay in the first-level cache, which is read and written
//          as fast as it goes
//   PASSF  rows of rt_row numbers, longer than the first-level cache holds five of. Measured 17.11 on a Ryzen 9 8945HX
//          (32 KB of first-level data cache, two threads of the core at work, the core 18 steps deeper than its own
//          value), corrected cache errors in a minute: rows of 512 numbers 1, of 1024 numbers 40 and 62, of 2048,
//          4096 and 8192 numbers 0; Prime95 34, 37 and 54. Three steps higher: rows of 1024 numbers 4, Prime95 1.
//          With 1024 the five rows are 40 KB a thread: the two threads keep throwing each other's lines out of the
//          cache and bringing them back while the work still goes fast enough to draw the current. Longer rows wait
//          for the second-level cache and the current falls; shorter ones fit and nothing is thrown out.
static __forceinline void memmul(double* a, const double* b, double* H, double* L, double* Q, int len, V p, V u)
{
    for (int j = 0; j < len; j += LANES) VSTORE(H + j, VMUL(VLOAD(a + j), VLOAD(b + j)));
    for (int j = 0; j < len; j += LANES) VSTORE(L + j, VFMSUB(VLOAD(a + j), VLOAD(b + j), VLOAD(H + j)));
    for (int j = 0; j < len; j += LANES) VSTORE(Q + j, VROUND(VMUL(VLOAD(H + j), u)));
    for (int j = 0; j < len; j += LANES) VSTORE(H + j, VFNMADD(VLOAD(Q + j), p, VLOAD(H + j)));
    for (int j = 0; j < len; j += LANES) { V r = VADD(VLOAD(H + j), VLOAD(L + j)); VSTORE(a + j, VNEGFIX(r, p)); }
}

// n: a multiple of len; the rows: len numbers each
static __forceinline int passmem(double* buf, int n, double base, int corrupt, int* first, int len, double* H, double* L, double* Q, double* X)
{
    V p = VSET1(rt_pd), u = VSET1(rt_u), step = VSET1((double)LANES);
    double iota[LANES];
    for (int k = 0; k < LANES; k++) iota[k] = base + k;
    V x0 = VLOAD(iota);
    { V x = x0; for (int i = 0; i < n; i += LANES) { VSTORE(buf + i, x); x = VADD(x, step); } }
    uint64_t e = rt_p - 1;
    int top = 63; while (!((e >> top) & 1)) top--;
    for (int bit = top - 1; bit >= 0; bit--)
    {
        int mul = (int)((e >> bit) & 1);
        V x = x0;
        for (int i = 0; i < n; i += len)
        {
            double* a = buf + i;
            memmul(a, a, H, L, Q, len, p, u);
            if (mul)
            {
                for (int j = 0; j < len; j += LANES) { VSTORE(X + j, x); x = VADD(x, step); }
                memmul(a, X, H, L, Q, len, p, u);
            }
        }
        if (bit == corrupt) { uint64_t* w = (uint64_t*)(buf + n / 2); *w ^= 1ull << 3; }
    }
    int bad = 0;
    for (int i = 0; i < n; i++) if (buf[i] != 1.0) { if (!bad) *first = i; bad++; }
    return bad;
}

// n: a multiple of 16 * LANES
int PASSM(double* buf, int n, double base, int corrupt, int* first)
{
    __declspec(align(64)) double H[16 * LANES], L[16 * LANES], Q[16 * LANES], X[16 * LANES];
    return passmem(buf, n, base, corrupt, first, 16 * LANES, H, L, Q, X);
}

// n: under rt_row, or a multiple of it; the four rows lie behind the numbers, so the memory is n + 4 * RT_ROW numbers
int PASSF(double* buf, int n, double base, int corrupt, int* first)
{
    int len = n < rt_row ? n : rt_row;
    double* rows = buf + n;
    return passmem(buf, n, base, corrupt, first, len, rows, rows + len, rows + 2 * len, rows + 3 * len);
}
