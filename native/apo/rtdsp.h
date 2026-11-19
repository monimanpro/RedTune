// The sound processing of Red Tune: pre-gain -> parametric equalizer -> compressor -> look-ahead limiter.
// Plain C++ with no Windows in it, so the same code runs inside the effect (apo.cpp) and on the test bench (apotest.cpp).
// Nothing here allocates, locks or waits: Process() is called on the sound thread of Windows.
//
// Equalizer: state-variable filters in the trapezoidal form (Andrew Simper, "Linear Trap Optimised SVF", 2013): they
//   stay stable and quiet while their settings move, which biquads in the direct forms do not.
// Compressor: feed-forward, one gain for all channels (the picture does not shift), soft knee, separate attack and
//   release on the gain (Giannoulis, Massberg, Reiss, 2012). 19.11, after measuring Nahimic's "volume stabilizer" on the
//   R7000P (a 2:1 squeeze from a very low threshold that sounds even where ours pumped): the level is the mean power of
//   the sound as it comes in, over the last 20 ms, BEFORE the pre-gain and the equalizer (a raised bass must not drive
//   the gain of everything else), and the sound itself is 10 ms late against that meter, so the gain has moved by the
//   time a loud moment arrives. A sine's level reads as its peak: the threshold means what the meters show.
// Limiter (rebuilt 19.11 so that it is not heard working; the bench measures it, TestLimiterQuality):
//   - it looks at the real wave, not only at the samples: three more points between every two samples (a 4x
//     interpolation), because a wave can top 3 dB over its samples and the converter would clip it there;
//   - the sound is 5 ms late, the gain for a peak is held for those 5 ms before it and turned towards it through two
//     running means in a row, a smooth S-shaped move instead of a ramp with corners;
//   - letting go follows the music: a fast part (30 ms) for a single hit, and under it a slow part that learns how much
//     is being taken off all the time (0.4 s to learn, 1.5 s to forget). On a dense loud passage the gain then rests on
//     the slow part and does not ride every wave of the bass, which is what was heard as a rasp.
//   No sample leaves above the ceiling (see the proof in Chunk below).
// The sound is always late by the two look-aheads (10 ms + 5 ms), switched on or off, so switching never jumps in time.
#pragma once
#include <math.h>
#include <string.h>
#include <stdint.h>
#include "shared.h"

#define RTD_MAXCH   8
#define RTD_CHUNK   32          /* the settings glide in steps of this many samples */
#define RTD_LOOK    240         /* limiter look-ahead window, samples at 48 kHz (scaled with the rate): 5 ms */
#define RTD_MAXLOOK 1024
#define RTD_TPTAPS  12          /* the wave between the samples: taps per point of the 4x interpolation */
#define RTD_TPLATE  6           /* ...and how many samples late its points come */
#define RTD_WIN     960         /* the compressor's level: mean power of the last 20 ms (samples at 48 kHz) */
#define RTD_MAXWIN  4096
#define RTD_AHEAD   480         /* the compressor's look-ahead: the sound is 10 ms late against the level */
#define RTD_MAXAHEAD 2048
#define RTD_BASSROOM 2.0        /* dB the bass part stays under the limiter's ceiling */

static inline float rtd_clamp(float v, float lo, float hi, float bad) { if (!(v == v)) return bad; return v < lo ? lo : v > hi ? hi : v; }
static inline double rtd_db2lin(double db) { return exp(db * 0.11512925464970228); }   /* ln(10)/20 */
static inline double rtd_lin2db(double x) { return 8.685889638065037 * log(x); }       /* 20/ln(10) */
static inline bool rtd_gaintype(uint32_t t) { return t == RTA_BELL || t == RTA_LOWSHELF || t == RTA_HIGHSHELF; }

/* whatever the file holds becomes something safe to run: the file is data from outside this process */
static inline void rtd_sanitize(RtaParams* p, double rate)
{
    float top = (float)(rate * 0.45); if (top > 22000.f) top = 22000.f;
    p->preampDb = rtd_clamp(p->preampDb, -24.f, 24.f, 0.f);
    for (int i = 0; i < RTA_BANDS; i++)
    {
        RtaBand* b = &p->bands[i];
        if (b->type > RTA_NOTCH) b->type = RTA_OFF;
        b->freq = rtd_clamp(b->freq, 10.f, top, 1000.f);
        b->gainDb = rtd_clamp(b->gainDb, -24.f, 24.f, 0.f);
        b->q = rtd_clamp(b->q, 0.1f, 20.f, 0.707f);
    }
    p->thrDb = rtd_clamp(p->thrDb, -60.f, 0.f, 0.f);
    p->ratio = rtd_clamp(p->ratio, 1.f, 30.f, 1.f);
    p->attackMs = rtd_clamp(p->attackMs, 0.1f, 300.f, 10.f);
    p->releaseMs = rtd_clamp(p->releaseMs, 5.f, 3000.f, 150.f);
    p->kneeDb = rtd_clamp(p->kneeDb, 0.f, 24.f, 6.f);
    p->makeupDb = rtd_clamp(p->makeupDb, -12.f, 24.f, 0.f);
    p->mixPct = rtd_clamp(p->mixPct, 0.f, 100.f, 100.f);
    p->ceilDb = rtd_clamp(p->ceilDb, -12.f, 0.f, -0.1f);
    p->splitHz = (p->splitHz == p->splitHz && p->splitHz >= 40.f) ? rtd_clamp(p->splitHz, 60.f, 1000.f, 0.f) : 0.f;
    for (int i = 0; i < 2; i++)
    {
        RtaCompBand* b = &p->cb[i];
        b->thrDb = rtd_clamp(b->thrDb, -60.f, 0.f, 0.f);
        b->ratio = rtd_clamp(b->ratio, 1.f, 30.f, 1.f);
        b->attackMs = rtd_clamp(b->attackMs, 0.1f, 300.f, 10.f);
        b->releaseMs = rtd_clamp(b->releaseMs, 5.f, 3000.f, 150.f);
        b->kneeDb = rtd_clamp(b->kneeDb, 0.f, 24.f, 6.f);
        b->makeupDb = rtd_clamp(b->makeupDb, -12.f, 24.f, 0.f);
    }
    /* the second split: only with the first, and at least an octave above it */
    float low2 = p->splitHz * 2 > 400.f ? p->splitHz * 2 : 400.f;
    p->split2Hz = (p->splitHz > 0 && p->split2Hz == p->split2Hz && p->split2Hz >= 200.f) ? rtd_clamp(p->split2Hz, low2, 12000.f, 0.f) : 0.f;
    p->mute &= 7u;
}

struct RtdCoef { double a1, a2, a3, m0, m1, m2; };

/* one band's filter for a sample rate; src\AudioFx.cs (Response) draws the curve from the same formulas */
static inline void rtd_coef(RtdCoef* c, uint32_t type, double freq, double gainDb, double q, double rate)
{
    double A = pow(10.0, gainDb / 40.0), g = tan(3.14159265358979323846 * freq / rate), k = 1.0 / q;
    c->m0 = 1; c->m1 = 0; c->m2 = 0;
    switch (type)
    {
    case RTA_BELL:      k = 1.0 / (q * A); c->m1 = k * (A * A - 1); break;
    case RTA_LOWSHELF:  g /= sqrt(A); c->m1 = k * (A - 1); c->m2 = A * A - 1; break;
    case RTA_HIGHSHELF: g *= sqrt(A); c->m0 = A * A; c->m1 = k * (1 - A) * A; c->m2 = 1 - A * A; break;
    case RTA_HIGHPASS:  c->m1 = -k; c->m2 = -1; break;
    case RTA_LOWPASS:   c->m0 = 0; c->m2 = 1; break;
    case RTA_NOTCH:     c->m1 = -k; break;
    default:            g = 0; break;   /* off: passes the sound as it is */
    }
    c->a1 = 1.0 / (1.0 + g * (g + k)); c->a2 = g * c->a1; c->a3 = g * c->a2;
}

/* a crossover's filter for a frequency: 2nd-order Butterworth sections, two in a row on each side (Linkwitz-Riley) */
struct RtdXo { double a1, a2, a3, k; };
static inline void rtd_xo(RtdXo* x, double hz, double rate)
{
    double g = tan(3.14159265358979323846 * hz / rate);
    x->k = 1.4142135623730951; x->a1 = 1.0 / (1.0 + g * (g + x->k)); x->a2 = g * x->a1; x->a3 = g * x->a2;
}

struct RtDsp
{
    double rate; int ch;
    RtaParams target, cur;          /* cur glides towards target */
    bool settled, idle;
    /* what the sound is multiplied by moves a little on every sample while the settings glide (ramp), never in
       steps: a step in a gain is a step in the sound. coef, pre, mk, mixv: now; ...To: at the end of the chunk; ...D: per sample */
    RtdCoef coef[RTA_BANDS], coefTo[RTA_BANDS], coefD[RTA_BANDS];
    bool live[RTA_BANDS];           /* the band does something (not off, and a bell or shelf with some gain) */
    bool fresh[RTA_BANDS];          /* the band has just become another kind of filter: it starts at its new place */
    bool ramp;
    double mk[3], mkTo[3], mkD[3], mixv, mixTo, mixD, preTo, preD;   /* mk: the gain after, per part of the compressor */
    double s1[RTA_BANDS][RTD_MAXCH], s2[RTA_BANDS][RTD_MAXCH];
    double pre;                     /* pre-gain, linear */
    double wet;                     /* 0 = the effect is off (sound untouched), 1 = on; glides, so switching does not click */
    double eqMix, compMix, limMix;  /* the same for the equalizer, the compressor and the limiter alone */
    /* compressor */
    double env;                     /* gain in dB, <= 0 */
    double aAtt[3], aRel[3];        /* per part: [0] the bass or the whole sound, [1] the middle, [2] the top */
    int cwin, alen;                 /* the level's window; the length of the look-ahead line (look-ahead + 1) */
    float pw[RTD_MAXWIN]; int pwi; double pwSum;        /* power of the input, sample by sample, and its sum over the window */
    float ahead[RTD_MAXCH][RTD_MAXAHEAD]; int apos;     /* the input on its way to the equalizer, late by the look-ahead */
    /* 19.11, the compressor in two parts (splitHz > 0), as Nahimic's stabilizer turned out to work: the bass under the
       split and the rest above it each have their own level and their own gain, so a bass drum does not turn the
       voice down. The split is a 4th-order Linkwitz-Riley crossover (two Butterworth sections in a row on each side;
       the two halves add back to the whole sound at an even level). One crossover works on the incoming sound, for
       the two levels; one on the equalized sound, for the two gains. */
    bool split, three; float xoHz, xo2Hz;
    RtdXo xo1, xo2;
    double xd[3][RTD_MAXCH][2], xw[3][RTD_MAXCH][2];   /* crossover states [section][channel]: d on the incoming sound, w on the equalized one */
    float pwL[RTD_MAXWIN], pwH[RTD_MAXWIN]; double pwLSum, pwHSum;
    double envL, envH, xoMix;
    /* 19.11, three parts with settings of their own (split2Hz > 0): the half above the first split goes through a second
       crossover and becomes the middle and the top. The bass half then passes the same second crossover with its two
       outputs added back together: that changes nothing but its timing, the same way the middle and the top were
       changed, so the three parts still add up to the whole sound at an even level. Each part has its own level, its
       own gain and its own settings (PartOf). m3 glides between two parts and three, as xoMix does between one and two. */
    double xd2[3][RTD_MAXCH][2], xw2[3][RTD_MAXCH][2], xwa[3][RTD_MAXCH][2];
    float pwM[RTD_MAXWIN], pwT[RTD_MAXWIN]; double pwMSum, pwTSum;
    double envM, envT, m3;
    /* 19.11, listening to the parts: how much of each goes out — 1, or 0 for a part whose bit is set in the mute of the settings; it
       glides between the two in 15 ms, so switching a part on and off does not click. The levels and the gains of a
       silent part are worked out as ever (its meters live on, and it comes back where it would have been). */
    double aud[3];
    /* The bass part has a ceiling of its own (Nahimic's does too: its bass stops growing from a certain level up, the
       rest goes on at 2:1). Bass raised by the equalizer would otherwise run into the limiter, and the limiter turns
       the whole sound down with every bass note. So the level of the bass part AFTER the equalizer is measured as well
       (pwB), and its gain never lets that level above the limiter's ceiling less RTD_BASSROOM dB. */
    float pwB[RTD_MAXWIN]; int pbi; double pwBSum;
    /* limiter */
    int look;                       /* window, samples */
    int dlen;                       /* the delay line: window + RTD_TPLATE (the sound is dlen-1 samples late) */
    float delay[RTD_MAXCH][RTD_MAXLOOK + 8];
    float dry[RTD_MAXCH][RTD_MAXLOOK + 8];  /* the untouched sound delayed alike, for the glide between on and off */
    int dpos;
    float gq[RTD_MAXLOOK]; uint32_t gqi[RTD_MAXLOOK]; int gqHead, gqTail, gqN; /* lowest gain of the window: values rising from head to tail */
    float box1[RTD_MAXLOOK], box2[RTD_MAXLOOK]; int len1, len2, b1i, b2i; double b1sum, b2sum;   /* the two running means of the gain */
    double limF, limS;              /* dB being taken off: the fast part (what goes out) and the slow part under it */
    double aLimFast, aLimSA, aLimSR;
    float tk[3][RTD_TPTAPS];        /* the interpolation: three points between two samples */
    float th[RTD_MAXCH][16]; int thi;   /* the last 16 samples of each channel */
    uint32_t n;                     /* samples since Init, for the window */
    /* meters */
    float inPeak, outPeak, grMin, limMin; double meterFall, grRise;
    float bandGr[3], bandLv[3];     /* per part of the compressor: its gain (dB, the deepest lately) and the level it reads (dB) */
    uint32_t faults;

    void Init(double sampleRate, int channels)
    {
        memset(this, 0, sizeof(*this));
        rate = sampleRate > 8000 ? sampleRate : 48000; ch = channels < 1 ? 1 : channels > RTD_MAXCH ? RTD_MAXCH : channels;
        look = (int)(RTD_LOOK * rate / 48000.0 + 0.5); if (look < 8) look = 8; if (look > RTD_MAXLOOK) look = RTD_MAXLOOK;
        dlen = look + RTD_TPLATE; len1 = look / 2; len2 = look - len1 + 1;   /* the two means together reach back exactly "look" samples */
        /* a windowed sinc, 4 points per sample, 49 taps centred on tap 24: points p/4 of a sample after sample n-6 */
        for (int p = 1; p <= 3; p++)
        {
            double sum = 0, k[RTD_TPTAPS];
            for (int j = 0; j < RTD_TPTAPS; j++)
            {
                double m = p + 4 * j, t = (m - 24) / 4.0, u = (m - 24) / 24.5;
                /* Kaiser window, beta 6 */
                double arg = 6.0 * sqrt(1 - u * u > 0 ? 1 - u * u : 0), i0 = 1, term = 1; for (int q = 1; q < 20; q++) { term *= (arg / (2 * q)) * (arg / (2 * q)); i0 += term; }
                k[j] = (fabs(t) < 1e-9 ? 1 : sin(3.14159265358979323846 * t) / (3.14159265358979323846 * t)) * i0; sum += k[j];
            }
            for (int j = 0; j < RTD_TPTAPS; j++) tk[p - 1][j] = (float)(k[j] / sum);
        }
        cwin = (int)(RTD_WIN * rate / 48000.0 + 0.5); if (cwin < 16) cwin = 16; if (cwin > RTD_MAXWIN) cwin = RTD_MAXWIN;
        alen = (int)(RTD_AHEAD * rate / 48000.0 + 0.5) + 1; if (alen < 2) alen = 2; if (alen > RTD_MAXAHEAD) alen = RTD_MAXAHEAD;
        RtaParams z; memset(&z, 0, sizeof(z)); z.ratio = 1; z.mixPct = 100; z.attackMs = 10; z.releaseMs = 150; z.ceilDb = -0.1f;
        for (int i = 0; i < 2; i++) { z.cb[i].ratio = 1; z.cb[i].attackMs = 10; z.cb[i].releaseMs = 150; }
        for (int i = 0; i < 3; i++) bandLv[i] = -140;
        for (int i = 0; i < RTA_BANDS; i++) { z.bands[i].freq = 1000; z.bands[i].q = 0.707f; }
        rtd_sanitize(&z, rate);
        target = cur = z; settled = false;
        pre = 1; mixv = 1;
        aLimFast = 1.0 - exp(-1.0 / (0.030 * rate));   /* the limiter lets go of a single hit in ~30 ms */
        aLimSA = 1.0 - exp(-1.0 / (0.4 * rate));       /* ...learns what a loud passage needs in ~0.4 s */
        aLimSR = 1.0 - exp(-1.0 / (1.5 * rate));       /* ...and forgets it in ~1.5 s */
        meterFall = exp(-RTD_CHUNK / (0.3 * rate));    /* the meters fall back in ~0.3 s */
        grRise = 1.0 - meterFall;
        aud[0] = aud[1] = aud[2] = 1;
        Quiet();
        Recalc(1); Arrive();
    }

    /* nothing left in the filters, the compressor and the limiter (the delayed sound itself stays) */
    void Quiet()
    {
        memset(s1, 0, sizeof(s1)); memset(s2, 0, sizeof(s2));
        env = envL = envH = envM = envT = 0; gqHead = gqTail = gqN = 0;
        limF = limS = 0; b1i = b2i = 0; b1sum = len1; b2sum = len2;
        for (int i = 0; i < RTD_MAXLOOK; i++) box1[i] = box2[i] = 1;
        memset(th, 0, sizeof(th)); thi = 0;
    }

    /* delay of the sound through the effect, samples */
    int Latency() const { return alen - 1 + dlen - 1; }

    /* the compressor's settings of a part: 0 the bass (and the whole sound while it is in one part), 1 the middle, 2 the top */
    static inline RtaCompBand PartOf(const RtaParams& p, int b)
    {
        if (b > 0) return p.cb[b - 1];
        RtaCompBand r = { p.thrDb, p.ratio, p.attackMs, p.releaseMs, p.kneeDb, p.makeupDb }; return r;
    }

    /* one channel's sample through a crossover: the half under the split and the half above it */
    inline void Cross(const RtdXo& q, double st[3][RTD_MAXCH][2], int c, double x, double* lo, double* hi)
    {
        const double xa1 = q.a1, xa2 = q.a2, xa3 = q.a3, xk = q.k;
        double v3 = x - st[0][c][1], v1 = xa1 * st[0][c][0] + xa2 * v3, v2 = st[0][c][1] + xa2 * st[0][c][0] + xa3 * v3;
        st[0][c][0] = 2 * v1 - st[0][c][0]; st[0][c][1] = 2 * v2 - st[0][c][1];
        double l = v2, h = x - xk * v1 - v2;
        v3 = l - st[1][c][1]; v1 = xa1 * st[1][c][0] + xa2 * v3; v2 = st[1][c][1] + xa2 * st[1][c][0] + xa3 * v3;
        st[1][c][0] = 2 * v1 - st[1][c][0]; st[1][c][1] = 2 * v2 - st[1][c][1];
        *lo = v2;
        v3 = h - st[2][c][1]; v1 = xa1 * st[2][c][0] + xa2 * v3; v2 = st[2][c][1] + xa2 * st[2][c][0] + xa3 * v3;
        st[2][c][0] = 2 * v1 - st[2][c][0]; st[2][c][1] = 2 * v2 - st[2][c][1];
        *hi = h - xk * v1 - v2;
    }

    /* one frame of input: its power into the level's window (and the powers of its two halves when the compressor works
       in two parts), the samples into the look-ahead line; what comes out of the line (the frame of alen-1 samples ago)
       is left in v[] */
    inline void Ahead(const float* frame, float* v, float* pin)
    {
        double p = 0, pl = 0, ph = 0, pm = 0, pt = 0;
        int rd = apos + 1; if (rd >= alen) rd = 0;
        for (int c = 0; c < ch; c++)
        {
            float s = frame[c]; float av = fabsf(s); if (av > *pin) *pin = av;
            p += (double)s * s;
            if (split)
            {
                double lo, hi; Cross(xo1, xd, c, s, &lo, &hi); pl += lo * lo; ph += hi * hi;
                if (three) { double m, t; Cross(xo2, xd2, c, hi, &m, &t); pm += m * m; pt += t * t; }
            }
            ahead[c][apos] = s; v[c] = ahead[c][rd];
        }
        apos = rd;
        float pf = (float)(p / ch); if (!(pf < 1e6f)) pf = 0;   /* also a number that is not a number */
        pwSum += (double)pf - (double)pw[pwi]; pw[pwi] = pf;
        if (split)
        {
            float lf = (float)(pl / ch), hf = (float)(ph / ch);
            if (!(lf < 1e6f) || !(hf < 1e6f)) { lf = hf = 0; memset(xd, 0, sizeof(xd)); }
            pwLSum += (double)lf - (double)pwL[pwi]; pwL[pwi] = lf;
            pwHSum += (double)hf - (double)pwH[pwi]; pwH[pwi] = hf;
            if (three)
            {
                float mf = (float)(pm / ch), tf = (float)(pt / ch);
                if (!(mf < 1e6f) || !(tf < 1e6f)) { mf = tf = 0; memset(xd2, 0, sizeof(xd2)); }
                pwMSum += (double)mf - (double)pwM[pwi]; pwM[pwi] = mf;
                pwTSum += (double)tf - (double)pwT[pwi]; pwT[pwi] = tf;
            }
        }
        if (++pwi >= cwin)
        {
            pwi = 0; double sum = 0, sl = 0, sh = 0, sm = 0, st = 0; /* no drift */
            for (int i = 0; i < cwin; i++) { sum += pw[i]; sl += pwL[i]; sh += pwH[i]; sm += pwM[i]; st += pwT[i]; }
            pwSum = sum; pwLSum = sl; pwHSum = sh; pwMSum = sm; pwTSum = st;
        }
    }

    /* the gain (dB, <= 0) a level asks for: sum = the power summed over the window; a sine reads as its peak */
    inline double Lvl(double sum) const { double ms2 = 2.0 * sum / cwin; return 4.342944819032518 * log(ms2 > 1e-14 ? ms2 : 1e-14); }
    inline double Want(double sum, double thr, double slope, double knee) const
    {
        double lvl = Lvl(sum), over = lvl - thr;
        if (2 * over < -knee) return 0;
        if (knee > 0 && 2 * over <= knee) { double t = over + knee * 0.5; return slope * t * t / (2 * knee); }
        return slope * over;
    }

    void Set(const RtaParams* p) { target = *p; rtd_sanitize(&target, rate); settled = false; }

    /* where the multipliers must be after the next "frames" samples */
    void Recalc(int frames)
    {
        for (int i = 0; i < RTA_BANDS; i++)
        {
            const RtaBand* b = &cur.bands[i];
            RtdCoef cn; rtd_coef(&cn, b->type, b->freq, b->gainDb, b->q, rate);
            bool now = b->type != RTA_OFF && !(rtd_gaintype(b->type) && fabsf(b->gainDb) < 0.005f && fabsf(target.bands[i].gainDb) < 0.005f);
            if (!now || !live[i] || fresh[i])
            {
                if (!now && live[i]) for (int c = 0; c < RTD_MAXCH; c++) s1[i][c] = s2[i][c] = 0;
                coef[i] = cn; memset(&coefD[i], 0, sizeof(RtdCoef));
            }
            else
            {
                RtdCoef* d = &coefD[i]; const RtdCoef* o = &coef[i];
                d->a1 = (cn.a1 - o->a1) / frames; d->a2 = (cn.a2 - o->a2) / frames; d->a3 = (cn.a3 - o->a3) / frames;
                d->m0 = (cn.m0 - o->m0) / frames; d->m1 = (cn.m1 - o->m1) / frames; d->m2 = (cn.m2 - o->m2) / frames;
            }
            coefTo[i] = cn; live[i] = now; fresh[i] = false;
        }
        preTo = rtd_db2lin(cur.preampDb); preD = (preTo - pre) / frames;
        mixTo = cur.mixPct * 0.01; mixD = (mixTo - mixv) / frames;
        if (cur.splitHz > 0 && cur.splitHz != xoHz) { xoHz = cur.splitHz; rtd_xo(&xo1, xoHz, rate); }
        if (cur.split2Hz > 0 && cur.split2Hz != xo2Hz) { xo2Hz = cur.split2Hz; rtd_xo(&xo2, xo2Hz, rate); }
        for (int b = 0; b < 3; b++)
        {
            const RtaCompBand q = PartOf(cur, b);
            mkTo[b] = q.makeupDb; mkD[b] = (mkTo[b] - mk[b]) / frames;
            aAtt[b] = 1.0 - exp(-1.0 / (q.attackMs * 0.001 * rate));
            aRel[b] = 1.0 - exp(-1.0 / (q.releaseMs * 0.001 * rate));
        }
        ramp = true;
    }

    /* the end of a gliding chunk: exactly there, no rounding left over */
    void Arrive()
    {
        for (int i = 0; i < RTA_BANDS; i++) coef[i] = coefTo[i];
        pre = preTo; for (int b = 0; b < 3; b++) mk[b] = mkTo[b]; mixv = mixTo; ramp = false;
    }

    /* one step of cur towards target; a: how far (0..1) */
    static bool Step(float* c, float t, float a, float eps) { float d = t - *c; if (fabsf(d) <= eps) { if (*c == t) return false; *c = t; return true; } *c += d * a; return true; }
    static bool StepLog(float* c, float t, float a) { if (*c == t) return false; float r = t / *c; if (r < 1.002f && r > 0.998f) { *c = t; return true; } *c *= powf(r, a); return true; }

    bool Glide(int frames)
    {
        float a = (float)(1.0 - exp(-frames / (0.025 * rate))); /* ~25 ms */
        bool moved = false;
        moved |= Step(&cur.preampDb, target.preampDb, a, 0.01f);
        for (int i = 0; i < RTA_BANDS; i++)
        {
            RtaBand* b = &cur.bands[i]; const RtaBand* t = &target.bands[i];
            if (b->type != t->type)
            {
                /* another kind of filter: the old one first glides to no gain, then the new one starts from no gain */
                if (rtd_gaintype(b->type) && fabsf(b->gainDb) > 0.01f) { Step(&b->gainDb, 0, a, 0.01f); moved = true; continue; }
                *b = *t; if (rtd_gaintype(t->type)) b->gainDb = 0;
                for (int c = 0; c < RTD_MAXCH; c++) s1[i][c] = s2[i][c] = 0;
                fresh[i] = true; moved = true; continue;
            }
            if (b->type == RTA_OFF) { *b = *t; continue; }
            moved |= Step(&b->gainDb, t->gainDb, a, 0.01f);
            moved |= StepLog(&b->freq, t->freq, a);
            moved |= StepLog(&b->q, t->q, a);
        }
        moved |= Step(&cur.thrDb, target.thrDb, a, 0.01f);
        moved |= StepLog(&cur.ratio, target.ratio, a);
        moved |= Step(&cur.kneeDb, target.kneeDb, a, 0.01f);
        moved |= Step(&cur.makeupDb, target.makeupDb, a, 0.01f);
        moved |= Step(&cur.mixPct, target.mixPct, a, 0.05f);
        moved |= Step(&cur.ceilDb, target.ceilDb, a, 0.005f);
        if (target.splitHz <= 0 || cur.splitHz <= 0) { if (cur.splitHz != target.splitHz) { cur.splitHz = target.splitHz; moved = true; } }
        else moved |= StepLog(&cur.splitHz, target.splitHz, a);
        if (cur.attackMs != target.attackMs || cur.releaseMs != target.releaseMs) { cur.attackMs = target.attackMs; cur.releaseMs = target.releaseMs; moved = true; }
        for (int i = 0; i < 2; i++)
        {
            RtaCompBand* b = &cur.cb[i]; const RtaCompBand* t = &target.cb[i];
            moved |= Step(&b->thrDb, t->thrDb, a, 0.01f);
            moved |= StepLog(&b->ratio, t->ratio, a);
            moved |= Step(&b->kneeDb, t->kneeDb, a, 0.01f);
            moved |= Step(&b->makeupDb, t->makeupDb, a, 0.01f);
            if (b->attackMs != t->attackMs || b->releaseMs != t->releaseMs) { b->attackMs = t->attackMs; b->releaseMs = t->releaseMs; moved = true; }
        }
        if (target.split2Hz <= 0 || cur.split2Hz <= 0) { if (cur.split2Hz != target.split2Hz) { cur.split2Hz = target.split2Hz; moved = true; } }
        else moved |= StepLog(&cur.split2Hz, target.split2Hz, a);
        cur.flags = target.flags;
        return moved;
    }

    /* interleaved float samples, in and out may be the same buffer */
    void Process(const float* in, float* out, int frames)
    {
        while (frames > 0)
        {
            int k = frames < RTD_CHUNK ? frames : RTD_CHUNK;
            if (!settled) { if (!Glide(k)) settled = true; Recalc(k); }
            Chunk(in, out, k);
            if (ramp) Arrive();
            in += k * ch; out += k * ch; frames -= k;
        }
    }

    static inline void Toward(double* v, double to, double step) { if (*v < to) { *v += step; if (*v > to) *v = to; } else if (*v > to) { *v -= step; if (*v < to) *v = to; } }

    void Chunk(const float* in, float* out, int frames)
    {
        const bool on = (cur.flags & RTA_ON) != 0;
        float pin = 0, pout = 0; double grLow = 0, limLow = 1;
        /* into two parts: from clean filters and empty windows; back to one: once the glide between the two is over */
        const bool splitTo = cur.splitHz > 0;
        if (splitTo && !split)
        {
            memset(xd, 0, sizeof(xd)); memset(xw, 0, sizeof(xw)); memset(pwL, 0, sizeof(pwL)); memset(pwH, 0, sizeof(pwH));
            memset(pwB, 0, sizeof(pwB)); pwBSum = 0; pbi = 0;
            pwLSum = pwHSum = 0; envL = envH = env; split = true;
        }
        if (!splitTo && split && xoMix <= 0) split = false;
        /* the third part comes and goes the same way, inside the two-part sound */
        const bool threeTo = splitTo && cur.split2Hz > 0;
        if (threeTo && !three && split)
        {
            memset(xd2, 0, sizeof(xd2)); memset(xw2, 0, sizeof(xw2)); memset(xwa, 0, sizeof(xwa)); memset(pwM, 0, sizeof(pwM)); memset(pwT, 0, sizeof(pwT));
            pwMSum = pwTSum = 0; envM = envT = envH; three = true;
        }
        if (three && (!split || (!threeTo && m3 <= 0))) { three = false; m3 = 0; }
        double bg[3] = { 0, 0, 0 }, bl[3];

        /* switched off and nothing in flight: the sound goes through as it is, only late by the look-ahead */
        if (!on && wet <= 0)
        {
            if (!idle) { idle = true; Quiet(); eqMix = compMix = limMix = 0; }
            for (int f = 0; f < frames; f++)
            {
                int rd = dpos + 1; if (rd >= dlen) rd = 0;
                float late[RTD_MAXCH]; Ahead(in + f * ch, late, &pin);
                for (int c = 0; c < ch; c++)
                {
                    float v = late[c];
                    dry[c][dpos] = v; delay[c][dpos] = v;
                    float o = dry[c][rd]; float av = fabsf(o); if (av > pout) pout = av;
                    out[f * ch + c] = o;
                }
                dpos = rd; n++;
            }
            bl[0] = Lvl(pwSum); bl[1] = bl[2] = -140;
            Meters(pin, pout, 0, 1, NULL, bl);
            return;
        }
        idle = false;

        const double wetTo = on ? 1 : 0, eqTo = (cur.flags & RTA_EQ) ? 1 : 0, compTo = (cur.flags & RTA_COMP) ? 1 : 0, limTo = (cur.flags & RTA_LIMIT) ? 1 : 0;
        const double fade = 1.0 / (0.015 * rate);     /* 15 ms from one to the other */
        double thr[3], slope[3], knee[3];
        for (int b = 0; b < 3; b++) { const RtaCompBand q = PartOf(cur, b); thr[b] = q.thrDb; slope[b] = 1.0 / q.ratio - 1.0; knee[b] = q.kneeDb; }
        const double ceil = rtd_db2lin(cur.ceilDb);
        double x[RTD_MAXCH];
        bool bad = false;

        for (int f = 0; f < frames; f++)
        {
            Toward(&wet, wetTo, fade); Toward(&eqMix, eqTo, fade); Toward(&compMix, compTo, fade); Toward(&limMix, limTo, fade);
            if (ramp)
            {
                pre += preD; mk[0] += mkD[0]; mk[1] += mkD[1]; mk[2] += mkD[2]; mixv += mixD;
                for (int b = 0; b < RTA_BANDS; b++)
                {
                    if (!live[b]) continue;
                    RtdCoef& k = coef[b]; const RtdCoef& d = coefD[b];
                    k.a1 += d.a1; k.a2 += d.a2; k.a3 += d.a3; k.m0 += d.m0; k.m1 += d.m1; k.m2 += d.m2;
                }
            }

            double peak = 0;
            float late[RTD_MAXCH]; Ahead(in + f * ch, late, &pin);
            for (int c = 0; c < ch; c++)
            {
                float v = late[c];
                dry[c][dpos] = v;
                double s = v;
                if (eqMix > 0)
                {
                    double e = s * pre;
                    for (int b = 0; b < RTA_BANDS; b++)
                    {
                        if (!live[b]) continue;
                        const RtdCoef& k = coef[b];
                        double v3 = e - s2[b][c], v1 = k.a1 * s1[b][c] + k.a2 * v3, v2 = s2[b][c] + k.a2 * s1[b][c] + k.a3 * v3;
                        s1[b][c] = 2 * v1 - s1[b][c]; s2[b][c] = 2 * v2 - s2[b][c];
                        e = k.m0 * e + k.m1 * v1 + k.m2 * v2;
                    }
                    s += (e - s) * eqMix;
                }
                x[c] = s; double as = fabs(s); if (as > peak) peak = as;
            }

            /* compressor: how much gain the level of the incoming sound asks for (the sound in hand is 10 ms older than
               that level), then attack and release on that gain */
            if (compMix > 0)
            {
                Toward(&xoMix, splitTo ? 1 : 0, fade); Toward(&m3, threeTo ? 1 : 0, fade);
                for (int b = 0; b < 3; b++) Toward(&aud[b], ((target.mute >> b) & 1u) ? 0 : 1, fade); /* (a switch, not a value that glides: taken from the settings asked for) */
                double want = Want(pwSum, thr[0], slope[0], knee[0]);
                env += (want - env) * (want < env ? aAtt[0] : aRel[0]);
                double g = rtd_db2lin(env + mk[0]), gm = 1 + (g - 1) * mixv; /* mix: the squeezed sound beside the plain one */
                gm = 1 + (gm - 1) * compMix;
                double gl = 1, gh = 1, gmid = 1, gt = 1, lo[RTD_MAXCH], hi[RTD_MAXCH], lap[RTD_MAXCH], mid[RTD_MAXCH], top[RTD_MAXCH];
                if (split)
                {
                    /* the equalized sound in its two halves, and the level of the bass half as it will go out */
                    double pb = 0;
                    for (int c = 0; c < ch; c++) { Cross(xo1, xw, c, x[c], &lo[c], &hi[c]); pb += lo[c] * lo[c]; }
                    float bf = (float)(pb / ch); if (!(bf < 1e6f)) { bf = 0; memset(xw, 0, sizeof(xw)); }
                    pwBSum += (double)bf - (double)pwB[pbi]; pwB[pbi] = bf;
                    if (++pbi >= cwin) { pbi = 0; double sum = 0; for (int i = 0; i < cwin; i++) sum += pwB[i]; pwBSum = sum; }
                    double ms2 = 2.0 * pwBSum / cwin, bass = 4.342944819032518 * log(ms2 > 1e-14 ? ms2 : 1e-14);
                    double room = cur.ceilDb - RTD_BASSROOM - mk[0] - bass;   /* the most gain the bass part may have */

                    /* the bass by its own settings, the half above the split by the top's (it is the top when there are two parts) */
                    want = Want(pwLSum, thr[0], slope[0], knee[0]); if (want > room) want = room;
                    envL += (want - envL) * (want < envL ? aAtt[0] : aRel[0]);
                    want = Want(pwHSum, thr[2], slope[2], knee[2]); envH += (want - envH) * (want < envH ? aAtt[2] : aRel[2]);
                    g = rtd_db2lin(envL + mk[0]); gl = 1 + (g - 1) * mixv; gl = 1 + (gl - 1) * compMix;
                    g = rtd_db2lin(envH + mk[2]); gh = 1 + (g - 1) * mixv; gh = 1 + (gh - 1) * compMix;
                    if (three)
                    {
                        /* the upper half into the middle and the top; the bass through the same crossover, added back together */
                        bool wrong = false;
                        for (int c = 0; c < ch; c++)
                        {
                            Cross(xo2, xw2, c, hi[c], &mid[c], &top[c]);
                            double a, b; Cross(xo2, xwa, c, lo[c], &a, &b); lap[c] = a + b;
                            if (!(fabs(mid[c]) < 1e6) || !(fabs(top[c]) < 1e6) || !(fabs(lap[c]) < 1e6)) wrong = true;
                        }
                        if (wrong) { memset(xw2, 0, sizeof(xw2)); memset(xwa, 0, sizeof(xwa)); for (int c = 0; c < ch; c++) { mid[c] = top[c] = 0; lap[c] = 0; } bad = true; }
                        want = Want(pwMSum, thr[1], slope[1], knee[1]); envM += (want - envM) * (want < envM ? aAtt[1] : aRel[1]);
                        want = Want(pwTSum, thr[2], slope[2], knee[2]); envT += (want - envT) * (want < envT ? aAtt[2] : aRel[2]);
                        g = rtd_db2lin(envM + mk[1]); gmid = 1 + (g - 1) * mixv; gmid = 1 + (gmid - 1) * compMix;
                        g = rtd_db2lin(envT + mk[2]); gt = 1 + (g - 1) * mixv; gt = 1 + (gt - 1) * compMix;
                    }
                }
                /* how much of the sound in parts: it comes in with the compressor itself, so switching never jumps */
                const double two = split ? xoMix * compMix : 0, three3 = three ? m3 : 0;
                double deep = env;
                if (two >= 1)
                {
                    if (three3 >= 1) { deep = envL < envM ? envL : envM; if (envT < deep) deep = envT; if (envL < bg[0]) bg[0] = envL; if (envM < bg[1]) bg[1] = envM; if (envT < bg[2]) bg[2] = envT; }
                    else { deep = envL < envH ? envL : envH; if (envL < bg[0]) bg[0] = envL; if (envH < bg[2]) bg[2] = envH; }
                }
                else if (env < bg[0]) bg[0] = env;
                if (deep < grLow) grLow = deep;
                peak = 0;
                for (int c = 0; c < ch; c++)
                {
                    double one = x[c] * gm;
                    if (two > 0)
                    {
                        /* (with two parts the half above the split is the top) */
                        double parts = lo[c] * gl * aud[0] + hi[c] * gh * aud[2];
                        if (three3 > 0) parts += (lap[c] * gl * aud[0] + mid[c] * gmid * aud[1] + top[c] * gt * aud[2] - parts) * three3;
                        one += (parts - one) * two;
                    }
                    x[c] = one; double as = fabs(one); if (as > peak) peak = as;
                }
            }
            else
            {
                env += (0 - env) * aRel[0]; envL += (0 - envL) * aRel[0]; envH += (0 - envH) * aRel[2];
                envM += (0 - envM) * aRel[1]; envT += (0 - envT) * aRel[2];
            }

            /* limiter. The highest point of the wave near sample n-6: the sample itself, its neighbour, and the three
               interpolated points between them, over all channels */
            double top = 0;
            for (int c = 0; c < ch; c++)
            {
                float* h = th[c]; h[thi] = (float)x[c];
                double a = fabs((double)h[(thi - 5) & 15]), b = fabs((double)h[(thi - 6) & 15]); if (a > top) top = a; if (b > top) top = b;
                for (int p = 0; p < 3; p++)
                {
                    const float* k = tk[p]; double s = 0;
                    for (int j = 0; j < RTD_TPTAPS; j++) s += (double)k[j] * h[(thi - j) & 15];
                    s = fabs(s); if (s > top) top = s;
                }
            }
            thi = (thi + 1) & 15;
            /* With L = look and the sound delayed by L+5 samples:
                 need[n]  = the gain the wave near sample n-6 needs to stay under the ceiling (1 when it is under it already)
                 hold[n]  = the lowest need of the last L samples
                 g[n]     = the gain of the release below: never above hold[n]
                 gain[n]  = two running means of g in a row, together reaching back L samples: a weighted mean of the last L g
               sample m leaves at n = m+L+5 and was looked at in need[m+6]; every g inside that mean, g[m+6] .. g[m+L+5],
               is <= its hold <= need[m+6]. So the sample that comes out is never above the ceiling. */
            double need = top > ceil ? ceil / top : 1.0;
            if (limMix < 1) need = 1 + (need - 1) * limMix;
            while (gqN > 0 && gq[(gqTail + RTD_MAXLOOK - 1) % RTD_MAXLOOK] >= need) { gqTail = (gqTail + RTD_MAXLOOK - 1) % RTD_MAXLOOK; gqN--; }
            gq[gqTail] = (float)need; if (gq[gqTail] > need) gq[gqTail] = nextafterf(gq[gqTail], 0.f); /* rounded to float: never up */
            gqi[gqTail] = n; gqTail = (gqTail + 1) % RTD_MAXLOOK; gqN++;
            while ((uint32_t)(n - gqi[gqHead]) >= (uint32_t)look) { gqHead = (gqHead + 1) % RTD_MAXLOOK; gqN--; }
            double hold = gq[gqHead];
            /* the release, in dB taken off. r: what is needed now. The fast part limF is what goes out: up to r at once,
               down towards the slow part (or r, whichever is more) in 30 ms. The slow part limS creeps after the fast
               one: while much is taken off again and again it rises to that level, and the fast part then has nowhere
               to fall between two waves of a bass note: the gain stands still. */
            double r = hold < 1 ? -8.685889638065037 * log(hold) : 0;
            /* the slow part: pulled up towards the fast one, and at the same time let down towards what is needed now
               (were it let down towards the fast part, the two would rest on each other and never fall) */
            if (limF > limS) limS += (limF - limS) * aLimSA;
            if (r < limS) limS += (r - limS) * aLimSR;
            double floorDb = r > limS ? r : limS;
            if (r >= limF) limF = r; else limF += (floorDb - limF) * aLimFast;
            double g = limF > 1e-9 ? exp(-0.11512925464970228 * limF) : 1.0;
            float gf = (float)g; if (gf > g) gf = nextafterf(gf, 0.f); /* rounded to float: never up */
            b1sum += (double)gf - (double)box1[b1i]; box1[b1i] = gf;
            if (++b1i >= len1) { b1i = 0; double sum = 0; for (int i = 0; i < len1; i++) sum += box1[i]; b1sum = sum; } /* no drift */
            float m1 = (float)(b1sum / len1); if (m1 > 1) m1 = 1;
            b2sum += (double)m1 - (double)box2[b2i]; box2[b2i] = m1;
            if (++b2i >= len2) { b2i = 0; double sum = 0; for (int i = 0; i < len2; i++) sum += box2[i]; b2sum = sum; }
            double gain = b2sum / len2; if (gain > 1) gain = 1; if (gain < limLow) limLow = gain;

            int rd = dpos + 1; if (rd >= dlen) rd = 0;  /* the oldest sample: stored dlen-1 samples ago */
            for (int c = 0; c < ch; c++) delay[c][dpos] = (float)x[c];
            for (int c = 0; c < ch; c++)
            {
                /* on <-> off glides between the processed sound and the untouched one, both equally late */
                double w = delay[c][rd] * gain, d = dry[c][rd];
                float o = (float)(d + (w - d) * wet);
                float ao = fabsf(o); if (ao > pout) pout = ao;
                if (!(ao <= 1e6f)) bad = true;   /* also true for a number that is not a number */
                out[f * ch + c] = o;
            }
            dpos = rd; n++;
        }

        /* a number that is not a number must not reach the speakers or stay in the filters */
        if (bad)
        {
            for (int i = 0; i < frames * ch; i++) out[i] = 0;
            double keepWet = wet; RtaParams t = target; double r = rate; int c = ch; uint32_t fl = faults + 1;
            Init(r, c); Set(&t); wet = keepWet; faults = fl; pout = 0;
        }
        /* the level each part reads now: the bass (or the whole sound), the middle, the top */
        const bool parts = split && xoMix >= 0.5;
        bl[0] = Lvl(parts ? pwLSum : pwSum);
        bl[1] = parts && three && m3 >= 0.5 ? Lvl(pwMSum) : -140;
        bl[2] = parts ? Lvl(three && m3 >= 0.5 ? pwTSum : pwHSum) : -140;
        Meters(pin, pout, grLow, limLow, bg, bl);
    }

    void Meters(float pin, float pout, double grLow, double limLow, const double* bg, const double* bl)
    {
        for (int b = 0; b < 3; b++)
        {
            double g = bg ? bg[b] : 0, l = bl ? bl[b] : -140;
            bandGr[b] = g < bandGr[b] ? (float)g : (float)(bandGr[b] + (g - bandGr[b]) * grRise);
            bandLv[b] = l > bandLv[b] ? (float)l : (float)(bandLv[b] + (l - bandLv[b]) * grRise);
        }
        inPeak = pin > inPeak ? pin : (float)(inPeak * meterFall);
        outPeak = pout > outPeak ? pout : (float)(outPeak * meterFall);
        grMin = grLow < grMin ? (float)grLow : (float)(grMin + (grLow - grMin) * grRise);
        float lim = (float)rtd_lin2db(limLow > 1e-6 ? limLow : 1e-6);
        limMin = lim < limMin ? lim : (float)(limMin + (lim - limMin) * grRise);
    }
};
