// The sound processing of Red Tune: denoiser -> noise gate -> pre-gain -> parametric equalizer -> de-esser -> compressor -> look-ahead limiter.
// 21.11, the de-esser (the user's wish): the sound is split at dsHz by a Linkwitz-Riley crossover (the compressor's), and
//   the top (with a top edge dsHz2, the band up to it: the top's part under a second crossover there, so the very top,
//   the "air", is left alone) is turned down while its power stands out of the whole sound's (both over ~5 ms, all channels together) by
//   more than dsThrDb: an "s" or "sh" is mostly top, a vowel tens of dB less. Twice the excess is taken off, at most
//   dsRangeDb, in ~0.5 ms, back over ~60 ms. Relative, so the threshold means the same on a quiet microphone.
// 21.11, the denoiser: RNNoise 0.2 (Jean-Marc Valin, Xiph.Org, BSD-3; rnnoise\), a small recurrent network that tells a voice
//   from noise band by band, in blocks of 10 ms at 48 kHz. Its state is made outside (ns, off the sound thread). Once it has
//   been switched on it stays in the chain, so the sound is late by one block from then on and switching it does not jump
//   in time; off, it is skipped and only the untouched sound passes through its delay line. Other rates are resampled to
//   48 kHz and back (linear: the voice is far below where that matters). The strength mixes the cleaned sound with the
//   untouched one, delayed alike.
//   (fixed 21.11 with the reverb remover: RNNoise 0.2 gives back the block from two blocks before, it looks one block
//   ahead, so at a strength under 100 % the untouched sound was mixed in 10 ms off and the voice was doubled. The mix is
//   now made inside the 48 kHz path, with the untouched block the network's output stands for.)
// 21.11, the reverb remover (the user, after Supertone Clear): late reverberation taken off band by band, the method of
//   Lebart, Boucher, Denbigh (2001) as Habets wrote it up (2007): in a room the sound dies away exponentially, so what
//   the room still holds of the sound 50 ms ago is that sound turned down by its decay; that part is reverberation and
//   is taken off by a Wiener gain (decision-directed, Ephraim-Malah) with a floor. Frames of 20 ms every 10 ms (a
//   square-root Hann window, its squares sum to one, so with the gains at 1 the sound comes out exactly as it went in).
//   The room's reverb time is heard, not asked: sound in a room cannot die away faster than the room lets it, so the
//   steepest decays after words (a quarter of the last sixteen) tell the room's time.
//   The 48 kHz path is: the network (2 blocks late) -> the strength mix -> the reverb remover (1 block late) -> out.
// 21.11, DeepFilterNet 3 (Hendrik Schroeter, MIT/Apache-2.0; dfn\, through rtdf.lib) as the other model of the denoiser
//   (RTA_DFN): better than RNNoise in noise (the bench in RedTune-devtools\denoise-compare: +3..5 dB SI-SDR on a real
//   voice), dearer (about 4 % of a core) and three blocks late by itself. Its engine allocates memory as it works, so it
//   does not run on the sound thread: a worker thread of the owner (DfWork) takes each block from a small queue and
//   gives it back two blocks later; a block not back in time goes out untouched (dfMiss), never late. Its strength is
//   its attenuation limit, the same depth the RNNoise mix gives: -20 log10(1 - strength).
//   (fixed 21.11, the user: «it cuts my voice off». The model was trained on sound of ordinary loudness; on a quiet
//   microphone (peaks under -30 dBFS, the user's at -39) it takes the voice for silence and mutes everything: the
//   bench in RedTune-devtools\denoise-compare had a real voice 35..54 dB down there, where RNNoise did not care. So the
//   sound going to it is lifted to an ordinary level (the voice's upper level held at -25 dBFS, never turned down, at
//   most +60 dB) and what comes back is put down again by exactly the gain its block went with: the model's output of
//   a block stands for the input three blocks before it, sample for sample, and the gain is a ramp within each block.)
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
// 21.11, the compressor's character (the user): how it squeezes, on all its parts —
//   clean (VCA): as above; FET: attack three times faster, a hard knee (1.5 dB at most);
//   optical: attack 2.5 times slower, a soft knee (10 dB at least) and a release in two stages, half of it fast (60 ms)
//   and half slow (0.4 s, up to 3 s the longer and deeper it has been squeezing: a photocell's memory);
//   vari-mu (tube): no fixed ratio: it starts 6 dB under the threshold and its slope eases from 1:1 to the ratio set
//   over 24 dB (a smoothstep, integrated: the curve itself stays smooth), attack and release 1.5 times slower.
// 21.11, the second compressor (the user, for microphones: two in a row; «both alike, the main one with the limiter
//   last»): first in the chain, on each frame as it comes (before the look-ahead line, so the gate, the equalizer and the
//   main compressor all get what it lets out, and the main one's level is read from it), in up to three parts split as
//   the main one splits (Linkwitz-Riley, the bass through the second crossover's all-pass), each part's level its power
//   over ~10 ms, its gain by its own settings and the character (the same formulas), a mix with the plain sound
//   (parallel compression). No delay of its own.
// 21.11, the colour after the compressor (the user): saturation of one kind, driven up to 12 dB into it and put back, so
//   quiet sound passes as it is and the louder the more harmonics: tube (asymmetric: the 2nd), transistor (2nd and 3rd),
//   tape (soft and symmetric: the 3rd; plus a bump at 80 Hz and a soft top), transformer (the bass only: the 3rd of
//   the bass, the rest clean). It runs four times over the rate (its own 32-tap interpolation up: the limiter's 12-tap one
//   left an image of a high tone that the saturation mixed back into the hearing; a 97-tap filter down) so new harmonics
//   above the top do not fold back; off, the sound goes through a plain delay of the same 28 samples, and the untouched
//   sound for the on/off glide is delayed alike (also on the idle path), so the effect is always 28 samples later.
#pragma once
#include <math.h>
#include <string.h>
#include <stdint.h>
#include "shared.h"
#include "rnnoise/include/rnnoise.h"
#include <atomic>
#include <chrono>
#include <xmmintrin.h>
/* 27.11: Red Tune's own trained network (rtnet.h), the third model of the denoiser. Its weights are a generated header
   (RedTune-devtools\denoiser\export.py); without that file here the effect is built without the model and the flag
   RTA_OWN falls back to RNNoise. */
#if !defined(RTD_NO_OWN) && defined(__has_include)
#if __has_include("rtnet_weights.h")
#define RTD_OWN
#include "rtnet.h"
#endif
#endif
#ifdef RTD_DFN
extern "C" {
    void* rtdf_create(float atten_lim_db);
    float rtdf_process(void* st, const float* in, float* out);
    void rtdf_set_atten(void* st, float lim_db);
    void rtdf_free(void* st);
}
#endif

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
#define RTD_GHOLD   0.150       /* 21.11, the gate: seconds it stays open after the sound went under the threshold */
#define RTD_GHYST   3.0         /* ...dB under the threshold the sound must fall before it starts closing */
#define RTD_GATT    0.001       /* ...seconds to open (the detector reads the sound 10 ms ahead, so it is open in time) */
#define RTD_NSBLOCK 480         /* the denoiser's block at 48 kHz */
#define RTD_NSFIFO  4096        /* its output at the device's rate, waiting */
#define RTD_NSDRY   6144        /* the untouched sound delayed alike (the whole 48 kHz path: up to 60 ms) */
#define RTD_NSHIST  6           /* the untouched blocks kept: the network's output stands for one 2 (RNNoise) or 5 (DeepFilterNet) back */
#define RTD_DFQ     8           /* DeepFilterNet's queue to and from its thread, blocks */
#define RTD_FFT     1024        /* the reverb remover's frame: 960 samples (20 ms) padded to 1024 */
#define RTD_FWIN    960
#define RTD_BINS    513
#define RTD_DRND    5           /* frames (10 ms each) back to where the late reverberation begins: 50 ms */
#define RTD_DRRUN   160         /* the longest decay looked at, frames */
#define RTD_CDLY    28          /* 21.11: the colour's delay, samples: 16 of the interpolation up, 12 of the filter down */
#define RTD_CITAPS  32          /* ...its interpolation up: taps per point */
#define RTD_CTAPS   97          /* ...the filter down at four times the rate */
#define RTD_STAGES  8           /* 21.11: the sections of a band: up to four, a straight bell eight (two shelves of four) */
#define RTD_SLOPES  4

/* 21.11: a dot product four at a time (SSE, every x64 has it): the colour's filters */
static inline float rtd_dot(const float* a, const float* b, int n)
{
    __m128 s = _mm_setzero_ps(); int i = 0;
    for (; i + 4 <= n; i += 4) s = _mm_add_ps(s, _mm_mul_ps(_mm_loadu_ps(a + i), _mm_loadu_ps(b + i)));
    float r[4]; _mm_storeu_ps(r, s); float t = (r[0] + r[1]) + (r[2] + r[3]);
    for (; i < n; i++) t += a[i] * b[i];
    return t;
}
/* 21.11: tanh as a rational curve: x (27 + x^2) / (27 + 9 x^2) under 3, then +-1; its slope reaches 0 exactly at 3, so it
   joins the flat part smoothly, and it rises everywhere (the slope is 9 (x^2 - 9)^2 / (27 + 9 x^2)^2) */
static inline double rtd_tanh(double x) { if (x >= 3) return 1; if (x <= -3) return -1; double x2 = x * x; return x * (27 + x2) / (27 + 9 * x2); }
static inline float rtd_clamp(float v, float lo, float hi, float bad) { if (!(v == v)) return bad; return v < lo ? lo : v > hi ? hi : v; }
static inline double rtd_db2lin(double db) { return exp(db * 0.11512925464970228); }   /* ln(10)/20 */
static inline double rtd_lin2db(double x) { return 8.685889638065037 * log(x); }       /* 20/ln(10) */
static inline bool rtd_gaintype(uint32_t t) { t &= 0xFFu; return t == RTA_BELL || t == RTA_LOWSHELF || t == RTA_HIGHSHELF; }
static inline bool rtd_cuttype(uint32_t t) { t &= 0xFFu; return t == RTA_HIGHPASS || t == RTA_LOWPASS; }
static inline int rtd_slope(uint32_t t) { return (int)((t >> 8) & 0xFFu) + 1; }
/* 21.11, the slope (the user: steeper or gentler for every kind, as in mixing programs): n sections of a Butterworth
   filter of order 2n, the sections' widths 1 / (2 sin((2j+1) pi / 4n)); the band's own Q scales the narrowest one, so
   at Q 0.707 a cut is a plain Butterworth (-3 dB at the frequency) and above that it rings at the frequency as one
   section does. src\AudioFx.cs (CutQ, Plan) draws the curve with the same sections. */
static inline double rtd_cutq(int n, int j, double q)
{
    if (n <= 1) return q;
    double w = 1.0 / (2.0 * sin((2 * j + 1) * 3.14159265358979323846 / (4.0 * n)));
    return j == 0 ? w * q / 0.7071067811865476 : w;
}

/* whatever the file holds becomes something safe to run: the file is data from outside this process */
static inline void rtd_sanitize(RtaParams* p, double rate)
{
    float top = (float)(rate * 0.45); if (top > 22000.f) top = 22000.f;
    p->preampDb = rtd_clamp(p->preampDb, -24.f, 24.f, 0.f);
    for (int i = 0; i < RTA_BANDS; i++)
    {
        RtaBand* b = &p->bands[i];
        { uint32_t k = b->type & 0xFFu; int n = rtd_slope(b->type); if (k > RTA_NOTCH || k == RTA_OFF) k = RTA_OFF, n = 1; if (n > RTD_SLOPES) n = RTD_SLOPES; b->type = k | ((uint32_t)(n - 1) << 8); }
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
    { uint32_t ns = (p->mute >> 8) & 0xFFu, dr = (p->mute >> 16) & 0xFFu; if (ns > 100u) ns = 100u; if (dr > 100u) dr = 100u; p->mute = (p->mute & 7u) | (ns << 8) | (dr << 16); }
    p->gateThrDb = rtd_clamp(p->gateThrDb, -90.f, 0.f, -50.f);
    p->gateRangeDb = rtd_clamp(p->gateRangeDb, -90.f, 0.f, -40.f);
    p->gateReleaseMs = p->gateReleaseMs > 0 ? rtd_clamp(p->gateReleaseMs, 10.f, 2000.f, 150.f) : 150.f;
    { float dtop = (float)(rate * 0.42); if (dtop > 12000.f) dtop = 12000.f;
      p->dsHz = p->dsHz > 0 ? rtd_clamp(p->dsHz, 2000.f, dtop, 6500.f) : 6500.f; }
    p->dsThrDb = p->dsThrDb == 0 && p->dsRangeDb == 0 ? -15.f : rtd_clamp(p->dsThrDb, -40.f, 0.f, -15.f);
    p->dsRangeDb = p->dsRangeDb == 0 && p->dsThrDb == -15.f ? -8.f : rtd_clamp(p->dsRangeDb, -24.f, 0.f, -8.f);
    { float dtop = (float)(rate * 0.42); if (dtop > 20000.f) dtop = 20000.f;
      p->dsHz2 = p->dsHz2 > 0 ? rtd_clamp(p->dsHz2, p->dsHz * 1.25f, 20000.f, 20000.f) : 20000.f;
    p->compChar = (float)(int)rtd_clamp(p->compChar, 0.f, 3.f, 0.f);
    p->colorType = (float)(int)rtd_clamp(p->colorType, 0.f, 4.f, 0.f);
    p->colorAmt = rtd_clamp(p->colorAmt, 0.f, 100.f, 0.f);
      if (p->dsHz2 > dtop) p->dsHz2 = 20000.f; }   /* at or over 0.42 of the rate: no top edge */
    p->spare[0] = 0;
    for (int i = 0; i < 3; i++)
    {
        RtaCompBand* b = &p->c2b[i];
        b->thrDb = rtd_clamp(b->thrDb, -60.f, 0.f, -20.f);
        b->ratio = rtd_clamp(b->ratio, 1.f, 30.f, 3.f);
        b->attackMs = rtd_clamp(b->attackMs, 0.1f, 300.f, 10.f);
        b->releaseMs = rtd_clamp(b->releaseMs, 5.f, 3000.f, 150.f);
        b->kneeDb = rtd_clamp(b->kneeDb, 0.f, 24.f, 6.f);
        b->makeupDb = rtd_clamp(b->makeupDb, -12.f, 24.f, 0.f);
    }
    p->c2Split = (p->c2Split == p->c2Split && p->c2Split >= 40.f) ? rtd_clamp(p->c2Split, 60.f, 1000.f, 0.f) : 0.f;
    { float lo2 = p->c2Split * 2 > 400.f ? p->c2Split * 2 : 400.f;
      p->c2Split2 = (p->c2Split > 0 && p->c2Split2 == p->c2Split2 && p->c2Split2 >= 200.f) ? rtd_clamp(p->c2Split2, lo2, 12000.f, 0.f) : 0.f; }
    p->c2Mix = rtd_clamp(p->c2Mix, 0.f, 100.f, 100.f);
    p->c2Char = (float)(int)rtd_clamp(p->c2Char, 0.f, 3.f, 2.f);
    for (int i = 0; i < 8; i++) p->spare2[i] = 0;
}

/* 21.11: the gain (dB, <= 0) a level asks of a compressor of a character: threshold, slope (1/ratio - 1), knee (dB);
   the same formulas as RtDsp::Want and WantC (the first compressor, whose level comes as a power sum) */
static inline double rtd_want(double lvl, double thr, double slope, double knee, int chr)
{
    if (chr == 3)
    {
        double over = lvl - thr + 6; if (over <= 0) return 0;
        double t = over / 24, S = t < 1 ? t * t * t - t * t * t * t / 2 : 0.5 + (t - 1);
        return slope * 24 * S;
    }
    if (chr == 1 && knee > 1.5) knee = 1.5; else if (chr == 2 && knee < 10) knee = 10;
    double over = lvl - thr;
    if (2 * over < -knee) return 0;
    if (knee > 0 && 2 * over <= knee) { double t = over + knee * 0.5; return slope * t * t / (2 * knee); }
    return slope * over;
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

/* one section's level at f, dB: the analogue prototype, frequency warped (what rtd_coef makes, as Analytic draws it) */
static inline double rtd_secdb(uint32_t type, double fc, double gainDb, double q, double f, double rate)
{
    const double PI_ = 3.14159265358979323846;
    double A = pow(10.0, gainDb / 40.0), g = tan(PI_ * fc / rate), k = 1.0 / q, m0 = 1, m1 = 0, m2 = 0;
    switch (type)
    {
    case RTA_BELL: k = 1.0 / (q * A); m1 = k * (A * A - 1); break;
    case RTA_LOWSHELF: g /= sqrt(A); m1 = k * (A - 1); m2 = A * A - 1; break;
    case RTA_HIGHSHELF: g *= sqrt(A); m0 = A * A; m1 = k * (1 - A) * A; m2 = 1 - A * A; break;
    case RTA_HIGHPASS: m1 = -k; m2 = -1; break;
    case RTA_LOWPASS: m0 = 0; m2 = 1; break;
    case RTA_NOTCH: m1 = -k; break;
    default: return 0;
    }
    double w = tan(PI_ * f / rate) / g, dr = 1 - w * w, di = k * w, nr = m2, ni = m1 * w, dd = dr * dr + di * di;
    double hr = m0 + (nr * dr + ni * di) / dd, hi = (ni * dr - nr * di) / dd;
    return 10.0 * log10(hr * hr + hi * hi + 1e-30);
}

/* 21.11, the sections a band is made of, by its slope n (rtd_slope):
   - a cut: n Butterworth sections (rtd_cutq);
   - a shelf: n shelves of a 1/n of the gain each, their widths as a cut's: the turn gets steeper;
   - a notch: n notches in a row: deeper and wider;
   - a bell: n = 1 the bell itself (smooth); n > 1 "straight": a band of the gain between f/r and f*r, made of a low
     shelf at f*r up and one at f/r down, each of n sections, so the top is flat and the sides steep. Its width comes
     from Q as a bell's (bandwidth in octaves 2 asinh(1/2Q) / ln 2, at least 0.6 octave), and the gains are corrected
     so that the frequency itself has the band's gain (the shelves overlap there). */
struct RtdSec { uint32_t type; double f, g, q; };
static inline int rtd_plan(uint32_t t, double f, double g, double q, double rate, RtdSec* s)
{
    uint32_t k = t & 0xFFu; int n = rtd_slope(t); if (n > RTD_SLOPES) n = RTD_SLOPES;
    if (n <= 1 || k == RTA_OFF) { s[0].type = k; s[0].f = f; s[0].g = g; s[0].q = q; return 1; }
    if (k == RTA_BELL)
    {
        double bw = 2.0 * asinh(1.0 / (2.0 * q)) / 0.6931471805599453; if (bw < 0.6) bw = 0.6;
        double r = pow(2.0, bw / 2), top = rate * 0.45, f2 = f * r > top ? top : f * r, f1 = f / r < 10 ? 10 : f / r;
        double gg = g / n;
        for (int it = 0; it < 3 && fabs(g) > 1e-4; it++)
        {
            double c = 0; for (int j = 0; j < n; j++) { double cq = rtd_cutq(n, j, 0.7071067811865476); c += rtd_secdb(RTA_LOWSHELF, f2, gg, cq, f, rate) + rtd_secdb(RTA_LOWSHELF, f1, -gg, cq, f, rate); }
            if (fabs(c) < 1e-6) break;
            double m = g / c; if (m > 3) m = 3; if (m < 0.5) m = 0.5; gg *= m;
        }
        for (int j = 0; j < n; j++)
        {
            double cq = rtd_cutq(n, j, 0.7071067811865476);
            s[2 * j].type = RTA_LOWSHELF; s[2 * j].f = f2; s[2 * j].g = gg; s[2 * j].q = cq;
            s[2 * j + 1].type = RTA_LOWSHELF; s[2 * j + 1].f = f1; s[2 * j + 1].g = -gg; s[2 * j + 1].q = cq;
        }
        return 2 * n;
    }
    for (int j = 0; j < n; j++)
    {
        s[j].type = k; s[j].f = f;
        s[j].g = (k == RTA_LOWSHELF || k == RTA_HIGHSHELF) ? g / n : 0;
        s[j].q = k == RTA_NOTCH ? q : rtd_cutq(n, j, q);
    }
    return n;
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
    RtdCoef coef[RTA_BANDS][RTD_STAGES], coefTo[RTA_BANDS][RTD_STAGES], coefD[RTA_BANDS][RTD_STAGES];
    int nst[RTA_BANDS];             /* sections in use per band (rtd_plan) */
    bool live[RTA_BANDS];           /* the band does something (not off, and a bell or shelf with some gain) */
    bool fresh[RTA_BANDS];          /* the band has just become another kind of filter: it starts at its new place */
    bool ramp;
    double mk[3], mkTo[3], mkD[3], mixv, mixTo, mixD, preTo, preD;   /* mk: the gain after, per part of the compressor */
    double s1[RTA_BANDS][RTD_STAGES][RTD_MAXCH], s2[RTA_BANDS][RTD_STAGES][RTD_MAXCH];
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
    /* 21.11, the character: the attack and release of each part as the character has them; the optical's two stages
       and memory per envelope ([0] env, [1] envL, [2] envH, [3] envM, [4] envT) */
    int chr, chrWas; double cAtt[3], cRel[3], opRelF, opMemA, opF[5], opS[5], opM[5];
    /* 21.11, the second compressor: its level (power over ~10 ms), gain (dB), coefficients, the optical's stages, the
       switch's glide, the gain after (linear, gliding), the deepest of the chunk for the meter */
    /* the second compressor's crossovers (made for c2Hz1/c2Hz2) and their states, the parts it works in now (n2), each
       part's power over ~10 ms, gain (dB), optical stages and memory, attack and release, the gain after (linear,
       gliding), the switch's glide, and its meters: the deepest gain and the highest level of each part in a chunk */
    RtdXo c2xo1, c2xo2; double c2Hz1, c2Hz2, c2w1[3][RTD_MAXCH][2], c2w2[3][RTD_MAXCH][2], c2wa[3][RTD_MAXCH][2]; int c2n;
    double c2P[3], c2E[3], c2F[3], c2S[3], c2M[3], c2At[3], c2Re[3], c2G[3], c2PowA, c2On; int c2ChrWas;
    float c2Min[3], c2LvNow[3]; double c2Low[3], c2Hi[3];
    /* 21.11, the colour: each channel's last 32 samples, the four-times-rate samples (128 a channel), the filter down,
       the untouched sound's 18-sample delay, the DC blocker, the tape's two filters, the transformer's bass split */
    /* (chist and uq are written twice, at i and at i + their size, so a filter's window is always one run of memory;
       hd and ti are stored reversed, to go with it) */
    float chist[RTD_MAXCH][128]; int chi; float uq[RTD_MAXCH][256]; int uqi; float hd[RTD_CTAPS], ti[3][RTD_CITAPS];
    float dryD[RTD_MAXCH][64]; double colMix, dcX[RTD_MAXCH], dcY[RTD_MAXCH], lpT[RTD_MAXCH], lpA;
    RtdCoef tapeC[2]; double tapeS[2][RTD_MAXCH][2]; float tapeFor; int colType;
    uint32_t n;                     /* samples since Init, for the window */
    /* 21.11, the noise gate: gDet = the peak of the incoming sound (it reads the sound before the 10 ms look-ahead line,
       so the gate is open by the time a word arrives), gGain = what the sound is multiplied by (1 open .. range closed),
       gHold = samples left before it may close, gateMix glides in and out as eqMix does */
    double gDet, gGain, gDetFall, gAtt, gateMix; int gHold; bool gOpen; float gateMin;
    /* 21.11, the denoiser (see the top of the file). ns: made and freed by the owner (after Init, which clears everything) */
    DenoiseState* ns;
    bool nsLive; double nsMix;
    float nsIn[RTD_NSBLOCK], nsOut[RTD_NSBLOCK]; int nsPos, nsOutPos, nsOutN;
    double upPhase, dnPhase; float upLast, dnLast;
    float nsFifo[RTD_NSFIFO]; int nsHead, nsCount, nsLat;
    float nsDry[RTD_MAXCH][RTD_NSDRY]; int nsDPos;
    float vad, vadMeter; uint32_t nsUnder, nsBlocks;
    float nsHist[RTD_NSHIST][RTD_NSBLOCK]; int nsHi;   /* the untouched blocks, nsHi: where the current one goes */
    int nsModel;                    /* the model the path runs: 0 RNNoise, 1 DeepFilterNet, 2 Red Tune's own (the path restarts when it changes) */
#ifdef RTD_OWN
    /* 27.11, the own network: it runs on the sound thread as RNNoise does and is as late (two blocks). ownWarm: blocks
       it has been given since it started anew; its first two answers are not sound yet (the overlap and the look-ahead
       fill up), the untouched sound goes out meanwhile */
    RtNet own; int ownWarm;
#endif
    /* DeepFilterNet's queue: the sound thread publishes block s in dfIn[s % RTD_DFQ] and dfHead = s + 1; the worker gives
       it back in dfOut with dfOutSeq = s. df: the model, made and freed by the owner's worker (Init clears the pointer). */
    void* df; float dfIn[RTD_DFQ][RTD_NSBLOCK], dfOut[RTD_DFQ][RTD_NSBLOCK], dfSnr[RTD_DFQ];
    std::atomic<uint32_t> dfInSeq[RTD_DFQ], dfOutSeq[RTD_DFQ], dfHead, dfWantA, dfLimBits;
    uint32_t dfSeq, dfDone, dfMiss; float dfLimSet; bool dfSync, dfNoNet;
    /* how long it takes (microseconds, the most lately): the model on one block, and a block from published to given back */
    double dfPub[RTD_DFQ]; float dfRunMax, dfTurnMax;
    /* the lift before the model: the voice's upper level (block RMS, held, falling 10 dB/s), the gain at the end of each
       block (the ramp of block s goes from dfGain[(s-1)%Q] to dfGain[s%Q]) */
    double dfEnv, dfGain[RTD_DFQ]; float dfLift;
    static double Now() { return std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
    void (*dfWake)(void*); void* dfWakeCtx;
    float nsNet[RTD_NSBLOCK]; double dnMix, dnTo, drTo; bool drEst, nsNoNet; /* nsNoNet: the bench, the path without the network */
    /* the reverb remover */
    float drWin[RTD_FWIN], drCos[RTD_FFT / 2], drSin[RTD_FFT / 2]; uint16_t drRev[RTD_FFT];
    float drRe[RTD_FFT], drIm[RTD_FFT], drPrev[RTD_NSBLOCK], drOlap[RTD_NSBLOCK];
    float drPs[RTD_BINS], drHist[RTD_DRND][RTD_BINS], drGp[RTD_BINS], drPp[RTD_BINS], drG[RTD_BINS]; int drHi;
    double drAmt, drT60, drFloor, drLastE, drRunStart, drRunMin; bool drFloorSet, drRun;
    float drRunE[RTD_DRRUN]; int drRunN; float drTs[16]; int drTn, drTi; float drT60Meter;
    /* 21.11, the de-esser: its crossover (made for dsXoHz) and states, the two powers, the gain on the top (dB) */
    RtdXo dsXo, dsXo2; double dsXoHz, dsXoHz2, dsSt[3][RTD_MAXCH][2], dsSt2[3][RTD_MAXCH][2], dsSt3[3][RTD_MAXCH][2], dsPH, dsPF, dsGr, dsMix, dsA, dsAtt, dsRel; float dsMin;
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
        gGain = 1; gDetFall = exp(-1.0 / (0.010 * rate)); gAtt = 1.0 - exp(-1.0 / (RTD_GATT * rate));
        dsA = 1.0 - exp(-1.0 / (0.005 * rate)); dsAtt = 1.0 - exp(-1.0 / (0.0005 * rate)); dsRel = 1.0 - exp(-1.0 / (0.060 * rate));
        /* the reverb remover's window and transform (Init is called off the sound thread) */
        for (int j = 0; j < RTD_FWIN; j++) drWin[j] = (float)sqrt(0.5 - 0.5 * cos(2 * 3.14159265358979323846 * j / RTD_FWIN));
        for (int j = 0; j < RTD_FFT / 2; j++) { drCos[j] = (float)cos(2 * 3.14159265358979323846 * j / RTD_FFT); drSin[j] = (float)sin(2 * 3.14159265358979323846 * j / RTD_FFT); }
        for (int i = 0; i < RTD_FFT; i++) { int r = 0; for (int b = 0; b < 10; b++) if (i & (1 << b)) r |= 1 << (9 - b); drRev[i] = (uint16_t)r; }
#ifdef RTD_OWN
        own.Init();     /* the own network's tables (the memset above cleared them) */
#endif
        drT60 = 0.5;
        /* the colour's filter down: a windowed sinc at 0.45 of the rate (0.1125 at four times it), Kaiser beta 7, sum 1 */
        {
            double sum = 0, kk[RTD_CTAPS];
            for (int k = 0; k < RTD_CTAPS; k++)
            {
                double t = k - (RTD_CTAPS - 1) / 2.0, u = t / ((RTD_CTAPS - 1) / 2.0), x = 2 * 0.1125 * t;
                double arg = 7.0 * sqrt(1 - u * u > 0 ? 1 - u * u : 0), i0 = 1, term = 1; for (int q = 1; q < 25; q++) { term *= (arg / (2 * q)) * (arg / (2 * q)); i0 += term; }
                kk[k] = (fabs(x) < 1e-12 ? 1 : sin(3.14159265358979323846 * x) / (3.14159265358979323846 * x)) * i0; sum += kk[k];
            }
            for (int k = 0; k < RTD_CTAPS; k++) hd[RTD_CTAPS - 1 - k] = (float)(kk[k] / sum);   /* reversed */
        }
        /* the colour's interpolation up: points p/4 after sample n-16 from samples n-31 .. n, Kaiser beta 8 */
        for (int p = 1; p <= 3; p++)
        {
            double sum = 0, kk[RTD_CITAPS];
            for (int j = 0; j < RTD_CITAPS; j++)
            {
                double t = 16.0 - j - p / 4.0, u = t / 16.5;
                double arg = 8.0 * sqrt(1 - u * u > 0 ? 1 - u * u : 0), i0 = 1, term = 1; for (int q = 1; q < 25; q++) { term *= (arg / (2 * q)) * (arg / (2 * q)); i0 += term; }
                kk[j] = (fabs(t) < 1e-12 ? 1 : sin(3.14159265358979323846 * t) / (3.14159265358979323846 * t)) * i0; sum += kk[j];
            }
            for (int j = 0; j < RTD_CITAPS; j++) ti[p - 1][RTD_CITAPS - 1 - j] = (float)(kk[j] / sum);   /* reversed */
        }
        lpA = 1.0 - exp(-2 * 3.14159265358979323846 * 150.0 / (4 * rate));   /* the transformer's bass: under 150 Hz */
        opRelF = 1.0 - exp(-1.0 / (0.060 * rate)); opMemA = 1.0 - exp(-1.0 / (3.0 * rate)); chrWas = -1; tapeFor = -1;
        c2PowA = 1.0 - exp(-1.0 / (0.010 * rate)); c2ChrWas = -1; for (int i = 0; i < 3; i++) { c2G[i] = 1; c2LvNow[i] = -140; }
        for (int i = 0; i < RTD_DFQ; i++) { dfInSeq[i].store(0xFFFFFFFFu); dfOutSeq[i].store(0xFFFFFFFFu); }
        dfHead.store(0); dfWantA.store(0); dfLimBits.store(0); dfLimSet = -1;
        dfEnv = 0.056; for (int i = 0; i < RTD_DFQ; i++) dfGain[i] = 1;
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
    int Latency() const { return alen - 1 + dlen - 1 + RTD_CDLY; }

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
        /* the gate's detector: the highest sample of this frame, falling back in ~10 ms */
        { double pk = 0; for (int c = 0; c < ch; c++) { double a = fabs((double)frame[c]); if (a > pk) pk = a; } if (!(pk < 1e6)) pk = 0; gDet = pk > gDet ? pk : gDet * gDetFall; }
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

    /* 21.11, the gain a level asks for, by the character (see the top of the file) */
    inline double WantC(double sum, double thr, double slope, double knee) const
    {
        switch (chr)
        {
        case 1: return Want(sum, thr, slope, knee < 1.5 ? knee : 1.5);
        case 2: return Want(sum, thr, slope, knee > 10 ? knee : 10);
        case 3:
        {
            /* the slope (the share of each dB over that is taken off) eases in: (1 - 1/ratio) * smoothstep(over / 24),
               integrated: 24 * (t^3 - t^4 / 2) up to t = 1, then a straight line on */
            double over = Lvl(sum) - thr + 6; if (over <= 0) return 0;
            double t = over / 24, S = t < 1 ? t * t * t - t * t * t * t / 2 : 0.5 + (t - 1);
            return slope * 24 * S;   /* slope = 1/ratio - 1, negative */
        }
        default: return Want(sum, thr, slope, knee);
        }
    }

    /* 21.11, the envelope's attack and release by the character; the optical: half fast, half slow with a memory */
    inline void Ball(double* e, double want, int b, int i)
    {
        if (chr == 2)
        {
            double& f = opF[i]; double& sl = opS[i];
            f += (want - f) * (want < f ? cAtt[b] : opRelF);
            const double relS = 1.0 / ((0.4 + 2.6 * opM[i]) * rate);
            sl += (want - sl) * (want < sl ? cAtt[b] : relS);
            *e = 0.5 * (f + sl);
            double depth = -*e / 12; if (depth < 0) depth = 0; if (depth > 1) depth = 1;
            opM[i] += (depth - opM[i]) * opMemA;
        }
        else *e += (want - *e) * (want < *e ? cAtt[b] : cRel[b]);
    }

    /* 21.11, the colour's saturations (unity slope at zero: quiet sound passes as it is) */
    inline double Sat(double v, int type, int c)
    {
        switch (type)
        {
        case 1: { double y = v + 0.35 * v * v / (1 + 0.12 * v * v); return y / sqrt(1 + 0.06 * y * y); }   /* tube: a bent line, the 2nd leads; rises everywhere */
        case 2: { double t = rtd_tanh(v); return t + 0.18 * t * t; }                                           /* transistor */
        case 3: return v / sqrt(1 + v * v);                                                                    /* tape */
        case 4: { double& lp = lpT[c]; lp += (v - lp) * lpA; return v + (rtd_tanh(lp) - lp); }                 /* transformer: the bass */
        default: return v;
        }
    }

    void Set(const RtaParams* p) { target = *p; rtd_sanitize(&target, rate); settled = false; }

    /* where the multipliers must be after the next "frames" samples */
    void Recalc(int frames)
    {
        for (int i = 0; i < RTA_BANDS; i++)
        {
            const RtaBand* b = &cur.bands[i];
            bool now = b->type != RTA_OFF && !(rtd_gaintype(b->type) && fabsf(b->gainDb) < 0.005f && fabsf(target.bands[i].gainDb) < 0.005f);
            RtdSec sec[RTD_STAGES]; int n = rtd_plan(b->type, b->freq, b->gainDb, b->q, rate, sec);
            /* the number of sections changed: they start at their places, the new ones from silence */
            bool jump = !now || !live[i] || fresh[i] || n != nst[i];
            if (!now && live[i]) { memset(s1[i], 0, sizeof(s1[i])); memset(s2[i], 0, sizeof(s2[i])); }
            for (int j = nst[i]; j < n; j++) for (int c = 0; c < RTD_MAXCH; c++) s1[i][j][c] = s2[i][j][c] = 0;
            for (int j = 0; j < n; j++)
            {
                RtdCoef cn; rtd_coef(&cn, sec[j].type, sec[j].f, sec[j].g, sec[j].q, rate);
                if (jump) { coef[i][j] = cn; memset(&coefD[i][j], 0, sizeof(RtdCoef)); }
                else
                {
                    RtdCoef* d = &coefD[i][j]; const RtdCoef* o = &coef[i][j];
                    d->a1 = (cn.a1 - o->a1) / frames; d->a2 = (cn.a2 - o->a2) / frames; d->a3 = (cn.a3 - o->a3) / frames;
                    d->m0 = (cn.m0 - o->m0) / frames; d->m1 = (cn.m1 - o->m1) / frames; d->m2 = (cn.m2 - o->m2) / frames;
                }
                coefTo[i][j] = cn;
            }
            nst[i] = n; live[i] = now; fresh[i] = false;
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
            /* 21.11: as the character has them */
            const int c = (int)cur.compChar;
            double fa = c == 1 ? 1.0 / 3 : c == 2 ? 2.5 : c == 3 ? 1.5 : 1, fr = c == 3 ? 1.5 : 1;
            double at = q.attackMs * fa; if (c == 2 && at < 10) at = 10;
            cAtt[b] = 1.0 - exp(-1.0 / (at * 0.001 * rate));
            cRel[b] = 1.0 - exp(-1.0 / (q.releaseMs * fr * 0.001 * rate));
            {
                /* the second compressor's part b by its own character */
                const int c2 = (int)cur.c2Char; const RtaCompBand& q2 = cur.c2b[b];
                double fa2 = c2 == 1 ? 1.0 / 3 : c2 == 2 ? 2.5 : c2 == 3 ? 1.5 : 1, fr2 = c2 == 3 ? 1.5 : 1;
                double at2 = q2.attackMs * fa2; if (c2 == 2 && at2 < 10) at2 = 10;
                c2At[b] = 1.0 - exp(-1.0 / (at2 * 0.001 * rate));
                c2Re[b] = 1.0 - exp(-1.0 / (q2.releaseMs * fr2 * 0.001 * rate));
            }
        }
        ramp = true;
    }

    /* the end of a gliding chunk: exactly there, no rounding left over */
    void Arrive()
    {
        for (int i = 0; i < RTA_BANDS; i++) for (int j = 0; j < nst[i]; j++) coef[i][j] = coefTo[i][j];
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
                memset(s1[i], 0, sizeof(s1[i])); memset(s2[i], 0, sizeof(s2[i]));
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
        /* the gate's settings take effect at once: its own opening and closing are the glide */
        moved |= StepLog(&cur.dsHz, target.dsHz, a);
        if (cur.compChar != target.compChar || cur.colorType != target.colorType) { cur.compChar = target.compChar; cur.colorType = target.colorType; moved = true; }
        moved |= Step(&cur.colorAmt, target.colorAmt, a, 0.05f);
        for (int i = 0; i < 3; i++)
        {
            RtaCompBand* b = &cur.c2b[i]; const RtaCompBand* t = &target.c2b[i];
            moved |= Step(&b->thrDb, t->thrDb, a, 0.01f); moved |= StepLog(&b->ratio, t->ratio, a);
            moved |= StepLog(&b->attackMs, t->attackMs, a); moved |= StepLog(&b->releaseMs, t->releaseMs, a);
            moved |= Step(&b->kneeDb, t->kneeDb, a, 0.01f); moved |= Step(&b->makeupDb, t->makeupDb, a, 0.01f);
        }
        if (cur.c2Split != target.c2Split || cur.c2Split2 != target.c2Split2) { cur.c2Split = target.c2Split; cur.c2Split2 = target.c2Split2; moved = true; }
        moved |= Step(&cur.c2Mix, target.c2Mix, a, 0.05f);
        if (cur.c2Char != target.c2Char) { cur.c2Char = target.c2Char; moved = true; }
        moved |= StepLog(&cur.dsHz2, target.dsHz2, a);
        moved |= Step(&cur.dsThrDb, target.dsThrDb, a, 0.05f);
        moved |= Step(&cur.dsRangeDb, target.dsRangeDb, a, 0.05f);
        if (cur.gateThrDb != target.gateThrDb || cur.gateRangeDb != target.gateRangeDb || cur.gateReleaseMs != target.gateReleaseMs)
        { cur.gateThrDb = target.gateThrDb; cur.gateRangeDb = target.gateRangeDb; cur.gateReleaseMs = target.gateReleaseMs; moved = true; }
        if (cur.mute != target.mute) { cur.mute = target.mute; } /* the strengths (bits 8..23) glide in the 48 kHz path */
        cur.flags = target.flags;
        return moved;
    }

    /* the 48 kHz path comes into the chain: its delay from now on. The output waits for one block (a whole block at the
       device's rate and a little more, so the resampled output never runs dry), then the path's own delay. */
    /* blocks back the network's output stands for: RNNoise 2; DeepFilterNet 3, and two more for its thread (21.11: a
       microphone at 44.1 kHz came in calls of 448 frames, 487.6 samples at 48 kHz, so every ~0.64 s one call finished two
       blocks and the second had no time on the thread: a click. Two blocks back leaves it at least a whole call.) */
    static int NetBack(int model) { return model == 1 ? 5 : 2; }   /* 27.11: the own network is as late as RNNoise */
    void NsStart(int model)
    {
#ifdef RTD_OWN
        own.Reset(); ownWarm = 0;
#endif
        nsLive = true; nsMix = 0; nsPos = 0; nsOutN = 0; nsOutPos = 0; upPhase = dnPhase = 0; upLast = dnLast = 0;
        memset(nsOut, 0, sizeof(nsOut)); memset(nsDry, 0, sizeof(nsDry)); nsDPos = 0;
        memset(nsHist, 0, sizeof(nsHist)); nsHi = 0; dnMix = 0; drAmt = 0; nsModel = model;
        memset(drPrev, 0, sizeof(drPrev)); memset(drOlap, 0, sizeof(drOlap));
        /* what the path adds at 48 kHz besides its block: the network, and the reverb remover's 480 */
        const int path = NetBack(model) * RTD_NSBLOCK + RTD_NSBLOCK;
        int fill = rate == 48000 ? 0 : (int)(RTD_NSBLOCK * rate / 48000.0) + 8;
        nsLat = rate == 48000 ? RTD_NSBLOCK + path : fill + (int)(path * rate / 48000.0 + 0.5) + 2; /* +2: the two linear resamplings, a sample each */
        if (nsLat >= RTD_NSDRY) nsLat = RTD_NSDRY - 1;
        nsHead = 0; nsCount = fill; memset(nsFifo, 0, sizeof(nsFifo));
    }

    /* the transform of the reverb remover: complex, in place, 1024 points */
    void Fft(float* re, float* im, bool inverse) const
    {
        for (int i = 0; i < RTD_FFT; i++) { int j = drRev[i]; if (i < j) { float t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; } }
        for (int len = 2; len <= RTD_FFT; len <<= 1)
        {
            int half = len >> 1, step = RTD_FFT / len;
            for (int i = 0; i < RTD_FFT; i += len)
                for (int j = 0; j < half; j++)
                {
                    float wr = drCos[j * step], wi = inverse ? drSin[j * step] : -drSin[j * step];
                    float* ar = re + i + j; float* ai = im + i + j; float* br = ar + half; float* bi = ai + half;
                    float xr = *br * wr - *bi * wi, xi = *br * wi + *bi * wr;
                    *br = *ar - xr; *bi = *ai - xi; *ar += xr; *ai += xi;
                }
        }
    }

    /* the room's reverb time from the level of one frame (300 Hz .. 4 kHz, dB): a decay after a word is followed down
       to 8 dB over the quiet; its slope (from 6 dB under where it began, past the direct sound) gives a time, and the
       times of the last sixteen decays, the steepest quarter of them, give the room's */
    void RoomTime(double e)
    {
        if (!drFloorSet) { drFloor = e; drFloorSet = true; drLastE = e; return; }
        if (e < drFloor) drFloor = e; else drFloor += 0.02;           /* the quiet: the lowest level, rising 2 dB a second */
        if (drRun)
        {
            bool end = e > drRunMin + 2.0 || e < drFloor + 8 || drRunN >= RTD_DRRUN;
            if (!end) { if (e < drRunMin) drRunMin = e; if (e < drRunStart - 6) drRunE[drRunN++] = (float)e; }
            else
            {
                drRun = false;
                if (drRunN >= 6 && drRunE[0] - drRunE[drRunN - 1] >= 10)
                {
                    double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = drRunN;
                    for (int i = 0; i < n; i++) { sx += i; sy += drRunE[i]; sxx += (double)i * i; sxy += i * (double)drRunE[i]; }
                    double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);   /* dB per frame (10 ms) */
                    if (slope < -0.05)
                    {
                        drTs[drTi] = (float)(-60.0 / slope * 0.010); drTi = (drTi + 1) & 15; if (drTn < 16) drTn++;
                        if (drTn >= 3)
                        {
                            float v[16]; int m = drTn; for (int i = 0; i < m; i++) v[i] = drTs[i];
                            for (int i = 1; i < m; i++) { float t = v[i]; int j = i - 1; while (j >= 0 && v[j] > t) { v[j + 1] = v[j]; j--; } v[j + 1] = t; }
                            double est = v[m / 4]; est = est < 0.15 ? 0.15 : est > 1.5 ? 1.5 : est;
                            drT60 += 0.3 * (est - drT60);
                        }
                    }
                }
            }
        }
        if (!drRun && e < drLastE && drLastE > drFloor + 20) { drRun = true; drRunStart = drLastE; drRunMin = e; drRunN = 0; }
        drLastE = e;
    }

    /* one 48 kHz block through the reverb remover; what comes out is the block before (the frame overlaps by half) */
    void Dereverb(const float* blk, float* out)
    {
        float* re = drRe; float* im = drIm;
        for (int j = 0; j < RTD_NSBLOCK; j++) { re[j] = drPrev[j] * drWin[j]; re[RTD_NSBLOCK + j] = blk[j] * drWin[RTD_NSBLOCK + j]; }
        for (int j = RTD_FWIN; j < RTD_FFT; j++) re[j] = 0;
        memset(im, 0, sizeof(drIm));
        memcpy(drPrev, blk, sizeof(drPrev));
        Toward(&drAmt, drTo, 0.15);                                   /* the strength glides over ~70 ms */
        if (drEst || drAmt > 0)
        {
            Fft(re, im, false);
            if (drEst)
            {
                double e = 0; for (int k = 6; k <= 85; k++) e += (double)re[k] * re[k] + (double)im[k] * im[k];
                RoomTime(10 * log10(e + 1e-12));
            }
            if (drAmt > 0)
            {
                const double decay = pow(10.0, -6.0 * RTD_DRND * 0.010 / drT60);   /* what is left of a sound after 50 ms (power) */
                const double floorG = pow(10.0, -18.0 * drAmt / 20.0), beta = 0.5 + 1.5 * drAmt;
                float* old = drHist[drHi];
                for (int k = 0; k < RTD_BINS; k++)
                {
                    double P = (double)re[k] * re[k] + (double)im[k] * im[k];
                    drPs[k] = (float)(0.5 * drPs[k] + 0.5 * P);
                    double Pr = decay * old[k] + 1e-14;                 /* the late reverberation in this band now */
                    old[k] = drPs[k];
                    double gam = P / Pr;
                    double xi = 0.9 * drGp[k] * drGp[k] * drPp[k] / Pr + 0.1 * (gam > 1 ? gam - 1 : 0);
                    double G = xi / (xi + beta); if (G < floorG) G = floorG;
                    drGp[k] = (float)G; drPp[k] = (float)P;
                }
                drHi = (drHi + 1) % RTD_DRND;
                /* the gains smoothed over neighbouring bands, and falling over ~20 ms (rising at once, a word's onset stays) */
                for (int k = 0; k < RTD_BINS; k++)
                {
                    double g = (drGp[k > 0 ? k - 1 : 0] + 2.0 * drGp[k] + drGp[k < RTD_BINS - 1 ? k + 1 : k]) * 0.25;
                    double prev = drG[k] > 0 ? drG[k] : 1;
                    if (g < prev) g = 0.5 * prev + 0.5 * g;
                    drG[k] = (float)g;
                }
                for (int k = 0; k < RTD_BINS; k++) { re[k] *= drG[k]; im[k] *= drG[k]; if (k > 0 && k < RTD_FFT / 2) { re[RTD_FFT - k] *= drG[k]; im[RTD_FFT - k] *= drG[k]; } }
            }
            else
            {
                /* off: keep the history going (it is the sound's own power), so switching on does not start from nothing */
                float* old = drHist[drHi];
                for (int k = 0; k < RTD_BINS; k++) { double P = (double)re[k] * re[k] + (double)im[k] * im[k]; drPs[k] = (float)(0.5 * drPs[k] + 0.5 * P); old[k] = drPs[k]; drGp[k] = 1; drPp[k] = (float)P; drG[k] = 1; }
                drHi = (drHi + 1) % RTD_DRND;
            }
            Fft(re, im, true);
            for (int j = 0; j < RTD_FWIN; j++) re[j] *= 1.0f / RTD_FFT;
        }
        /* untouched (neither on nor hearing the room): the windowed frame goes out as it is, the windows twice sum to one */
        for (int j = 0; j < RTD_NSBLOCK; j++) { out[j] = re[j] * drWin[j] + drOlap[j]; drOlap[j] = re[RTD_NSBLOCK + j] * drWin[RTD_NSBLOCK + j]; }
        drT60Meter = drEst && drTn >= 3 ? (float)drT60 : 0;
    }

    /* DeepFilterNet's side of the sound thread: block s to the worker, and the worker's output of block s - 2 back
       (false: not back in time, or the model not made yet) */
    inline bool DfExchange(const float* in, float* out, float* snr)
    {
        if (!dfWantA.load(std::memory_order_relaxed)) return false;   /* 21.11: the denoiser off: the model does not run at all */
        const uint32_t s = dfSeq++, q = s % RTD_DFQ;
        /* the lift: up to an ordinary level, a ramp from the last block's gain (down at once by up to 12 dB a block when
           the voice comes, up by 0.2 dB a block, 20 dB a second, in the quiet) */
        {
            double e = 0; for (int i = 0; i < RTD_NSBLOCK; i++) e += (double)in[i] * in[i];
            double rms = sqrt(e / RTD_NSBLOCK);
            dfEnv = rms > dfEnv ? rms : dfEnv * 0.98855; if (dfEnv < 1e-5) dfEnv = 1e-5;   /* 0.98855 a block: 10 dB a second */
            double want = 0.056 / dfEnv; if (want < 1) want = 1; if (want > 1000) want = 1000;
            const double g0 = dfGain[(s + RTD_DFQ - 1) % RTD_DFQ];
            double g1 = want < g0 * 0.2512 ? g0 * 0.2512 : want > g0 * 1.0233 ? g0 * 1.0233 : want;
            dfGain[q] = g1; dfLift = (float)g1;
            for (int i = 0; i < RTD_NSBLOCK; i++) dfIn[q][i] = (float)(in[i] * (g0 + (g1 - g0) * (i + 1) / RTD_NSBLOCK));
        }
        dfPub[q] = Now(); dfInSeq[q].store(s, std::memory_order_release);
        dfHead.store(s + 1, std::memory_order_release);
        if (dfSync) DfWork(); else if (dfWake) dfWake(dfWakeCtx);
        if (s < 5) return false;
        const uint32_t p = s - 2, r = p % RTD_DFQ;
        if (dfOutSeq[r].load(std::memory_order_acquire) != p) { dfMiss++; return false; }
        /* down again by the gain the input three blocks before went with, sample for sample */
        const uint32_t b = p - 3;
        const double g0 = dfGain[(b + RTD_DFQ - 1) % RTD_DFQ], g1 = dfGain[b % RTD_DFQ];
        for (int i = 0; i < RTD_NSBLOCK; i++) out[i] = (float)(dfOut[r][i] / (g0 + (g1 - g0) * (i + 1) / RTD_NSBLOCK));
        *snr = dfSnr[r];
        return true;
    }

    /* the worker's side (the owner's thread, or the sound thread itself on the bench): every block published and not
       done yet, in order; far behind, it skips to the newest. Without a model nothing is given back. */
    void DfWork()
    {
        const uint32_t head = dfHead.load(std::memory_order_acquire);
        if (head - dfDone > RTD_DFQ - 2) dfDone = head - 1;
#ifdef RTD_DFN
        float lim; { uint32_t b = dfLimBits.load(std::memory_order_relaxed); memcpy(&lim, &b, 4); }
        if (df && lim != dfLimSet) { rtdf_set_atten(df, lim); dfLimSet = lim; }
#endif
        while (dfDone != head)
        {
            const uint32_t s = dfDone++, q = s % RTD_DFQ;
            if (dfInSeq[q].load(std::memory_order_acquire) != s) continue;
            if (dfNoNet)
            {
                /* the bench: the block from three before, as the model gives (its own delay), or silence */
                const uint32_t o = s - 3, oq = o % RTD_DFQ;
                if (s >= 3 && dfInSeq[oq].load(std::memory_order_acquire) == o) memcpy(dfOut[q], dfIn[oq], sizeof(dfOut[q])); else memset(dfOut[q], 0, sizeof(dfOut[q]));
                dfSnr[q] = 0;
            }
#ifdef RTD_DFN
            else if (df) { double t0 = Now(); dfSnr[q] = rtdf_process(df, dfIn[q], dfOut[q]); double t1 = Now(); if (t1 - t0 > dfRunMax) dfRunMax = (float)(t1 - t0); if (t1 - dfPub[q] > dfTurnMax) dfTurnMax = (float)(t1 - dfPub[q]); }
#endif
            else continue;
            dfOutSeq[q].store(s, std::memory_order_release);
        }
    }

    /* one 48 kHz sample into the block; a full block goes through the path */
    inline void NsPush48(float v)
    {
        nsIn[nsPos++] = v;
        if (nsPos < RTD_NSBLOCK) return;
        nsPos = 0;
        float* old = nsHist[(nsHi + RTD_NSHIST - NetBack(nsModel)) % RTD_NSHIST];   /* the block the network's output stands for */
        bool got = false;
        if (nsModel == 1)
        {
            /* DeepFilterNet: always fed while the path runs on it (its state follows the sound); its strength is its limit */
            float snr = 0;
            got = DfExchange(nsIn, nsNet, &snr);
            if (got) vad = (float)((snr + 5.0) / 15.0 < 0 ? 0 : (snr + 5.0) / 15.0 > 1 ? 1 : (snr + 5.0) / 15.0);
            else vad = 0;
        }
#ifdef RTD_OWN
        else if (nsModel == 2)
        {
            if (!nsNoNet && (dnMix > 0 || dnTo > 0))
            {
                own.Process(nsIn, nsNet);
                if (ownWarm < 3) ownWarm++;
                got = ownWarm >= 3;
                /* no voice probability of its own: the most it lets through between 300 Hz and 3.4 kHz (nothing in noise, about 1 on a voice) */
                float top = 0; for (int b = 0; b < RTN_NB; b++) if (RTN_CENTRE[b] >= 6 && RTN_CENTRE[b] <= 72 && own.gain[b] > top) top = own.gain[b];
                vad = got ? top : 0;
            }
            else { if (ownWarm) { own.Reset(); ownWarm = 0; } vad = 0; }   /* at rest: the next time it starts from nothing, not from old sound */
        }
#endif
        else if (ns && !nsNoNet && (dnMix > 0 || dnTo > 0))
        {
            float s[RTD_NSBLOCK]; for (int i = 0; i < RTD_NSBLOCK; i++) s[i] = nsIn[i] * 32768.f;
            vad = rnnoise_process_frame(ns, nsNet, s);
            for (int i = 0; i < RTD_NSBLOCK; i++) nsNet[i] *= (1.f / 32768.f);
            got = true;
        }
        else vad = 0;
        float blk[RTD_NSBLOCK];
        const double dstep = 1.0 / (0.015 * 48000);
        for (int i = 0; i < RTD_NSBLOCK; i++)
        {
            Toward(&dnMix, got ? dnTo : 0, dstep);
            double net = got ? nsNet[i] : old[i];
            blk[i] = (float)(old[i] + (net - old[i]) * dnMix);
        }
        memcpy(nsHist[nsHi], nsIn, sizeof(nsIn)); nsHi = (nsHi + 1) % RTD_NSHIST;
        Dereverb(blk, nsOut);
        nsBlocks++;
        if (rate != 48000)
        {
            /* the block back to the device's rate, into the fifo */
            double step = 48000.0 / rate;
            for (int i = 0; i < RTD_NSBLOCK; i++)
            {
                float cur = nsOut[i];
                while (dnPhase < 1.0)
                {
                    float o = dnLast + (cur - dnLast) * (float)dnPhase;
                    if (nsCount < RTD_NSFIFO) { nsFifo[(nsHead + nsCount) % RTD_NSFIFO] = o; nsCount++; }
                    dnPhase += step;
                }
                dnPhase -= 1.0; dnLast = cur;
            }
        }
    }

    /* one frame (all channels) through the path: the processed mono sound in place of each channel's untouched one
       (mixTo: 1 while the denoiser or the reverb remover is on, the glide between the two otherwise) */
    inline void NsFrame(const float* in, float* out, double mixTo)
    {
        double m = 0; for (int c = 0; c < ch; c++) m += in[c]; float mono = (float)(m / ch);
        float d;
        if (rate == 48000)
        {
            d = nsOut[nsPos];               /* the processed block is read while the next one fills: a block late, and the path's own delay */
            NsPush48(mono);
        }
        else
        {
            double step = rate / 48000.0;
            while (upPhase < 1.0) { NsPush48(upLast + (mono - upLast) * (float)upPhase); upPhase += step; }
            upPhase -= 1.0; upLast = mono;
            if (nsCount > 0) { d = nsFifo[nsHead]; nsHead = (nsHead + 1) % RTD_NSFIFO; nsCount--; } else { d = 0; nsUnder++; }
        }
        Toward(&nsMix, mixTo, 1.0 / (0.015 * rate));
        int rd = nsDPos - nsLat; if (rd < 0) rd += RTD_NSDRY;
        for (int c = 0; c < ch; c++)
        {
            nsDry[c][nsDPos] = in[c];
            float dry = nsDry[c][rd];
            out[c] = (float)(dry + (d - dry) * nsMix);
        }
        if (++nsDPos >= RTD_NSDRY) nsDPos = 0;
    }

    /* 21.11, the second compressor on one frame, in place (see the top of the file) */
    inline void Comp2Frame(float* fr, double to, int chr, double fade)
    {
        Toward(&c2On, to, fade);
        if (c2On <= 0) { for (int i = 0; i < 3; i++) { c2E[i] += (0 - c2E[i]) * c2Re[i]; c2F[i] = c2S[i] = c2E[i]; c2P[i] = 0; } return; }
        const int n = c2n;
        double part[3][RTD_MAXCH], pw[3] = { 0, 0, 0 };
        for (int c = 0; c < ch; c++)
        {
            const double x = fr[c];
            if (n == 1) { part[0][c] = x; part[1][c] = part[2][c] = 0; }
            else
            {
                double lo, hi; Cross(c2xo1, c2w1, c, x, &lo, &hi);
                if (n == 3)
                {
                    double m, t, a, b; Cross(c2xo2, c2w2, c, hi, &m, &t); Cross(c2xo2, c2wa, c, lo, &a, &b);
                    part[0][c] = a + b; part[1][c] = m; part[2][c] = t;
                }
                else { part[0][c] = lo; part[1][c] = 0; part[2][c] = hi; }
            }
            for (int k = 0; k < 3; k++) pw[k] += part[k][c] * part[k][c];
        }
        const double mix = cur.c2Mix * 0.01;
        double g[3];
        for (int k = 0; k < 3; k++)
        {
            if ((n == 1 && k > 0) || (n == 2 && k == 1)) { g[k] = 1; continue; }
            c2P[k] += (pw[k] / ch - c2P[k]) * c2PowA;
            const RtaCompBand& q = cur.c2b[k];
            const double lvl = 4.342944819032518 * log(2 * c2P[k] > 1e-14 ? 2 * c2P[k] : 1e-14);   /* a sine's level reads as its peak */
            if (lvl > c2Hi[k]) c2Hi[k] = lvl;
            const double want = rtd_want(lvl, q.thrDb, 1.0 / q.ratio - 1.0, q.kneeDb, chr);
            if (chr == 2)
            {
                c2F[k] += (want - c2F[k]) * (want < c2F[k] ? c2At[k] : opRelF);
                c2S[k] += (want - c2S[k]) * (want < c2S[k] ? c2At[k] : 1.0 / ((0.4 + 2.6 * c2M[k]) * rate));
                c2E[k] = 0.5 * (c2F[k] + c2S[k]);
                double depth = -c2E[k] / 12; if (depth < 0) depth = 0; if (depth > 1) depth = 1; c2M[k] += (depth - c2M[k]) * opMemA;
            }
            else c2E[k] += (want - c2E[k]) * (want < c2E[k] ? c2At[k] : c2Re[k]);
            c2G[k] += (rtd_db2lin(q.makeupDb) - c2G[k]) * 0.001;   /* the gain after glides (~20 ms) */
            double gg = rtd_db2lin(c2E[k]) * c2G[k];
            g[k] = 1 + (gg - 1) * mix;
            if (c2E[k] * c2On < c2Low[k]) c2Low[k] = c2E[k] * c2On;
        }
        for (int c = 0; c < ch; c++)
        {
            double y = part[0][c] * g[0] + part[1][c] * g[1] + part[2][c] * g[2];
            fr[c] = (float)(fr[c] + (y - fr[c]) * c2On);
        }
    }

    /* interleaved float samples, in and out may be the same buffer */
    void Process(const float* in, float* out, int frames)
    {
        while (frames > 0)
        {
            int k = frames < RTD_CHUNK ? frames : RTD_CHUNK;
            if (!settled) { if (!Glide(k)) settled = true; Recalc(k); }
            /* 21.11: the denoiser and the reverb remover first, into a chunk of their own (in and out may be the same buffer) */
            const bool on = (cur.flags & RTA_ON) != 0;
            int model = 0;
#ifdef RTD_DFN
            if (cur.flags & RTA_DFN) model = 1;
#endif
#ifdef RTD_OWN
            if (cur.flags & RTA_OWN) model = 2;     /* 27.11: the own network; an effect built without it runs RNNoise instead */
#endif
            /* RNNoise needs its state; DeepFilterNet its worker (the model comes from there); the own network is part of this object */
            const bool nsOn = on && (cur.flags & RTA_DENOISE) && (model != 0 || ns), drOn = on && (cur.flags & RTA_DEREVERB);
            /* the model counts only while the denoiser is on: switching it restarts the path (its delay changes) */
            if ((nsOn || drOn) && !nsLive) NsStart(nsOn ? model : 0);
            else if (nsLive && nsOn && model != nsModel) NsStart(model);
            dfWantA.store(nsLive && nsModel == 1 && nsOn ? 1u : 0u, std::memory_order_relaxed);   /* 21.11: only while the denoiser is on (its model is freed after a minute without) */
            if (nsLive)
            {
                float tmp[RTD_CHUNK * RTD_MAXCH];
                const double strength = nsOn ? ((cur.mute >> 8) & 0xFFu) * 0.01 : 0;
                if (nsModel == 1)
                {
                    /* DeepFilterNet in full, its limit the depth the RNNoise mix would give (100 %: no limit) */
                    dnTo = nsOn ? 1 : 0;
                    float lim = strength >= 0.995 ? 100.f : (float)(-20.0 * log10(1.0 - strength)); if (lim < 0.5f) lim = 0.5f;
                    uint32_t b; memcpy(&b, &lim, 4); dfLimBits.store(b, std::memory_order_relaxed);
                }
                else dnTo = strength;
                drTo = drOn ? ((cur.mute >> 16) & 0xFFu) * 0.01 : 0;
                drEst = drOn;
                for (int f = 0; f < k; f++) NsFrame(in + f * ch, tmp + f * ch, nsOn || drOn ? 1.0 : 0.0);
                vadMeter = (dnMix > 0 && vad > vadMeter) ? vad : (float)(vadMeter * meterFall);
                Chunk(tmp, out, k);
            }
            else Chunk(in, out, k);
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
            if (!idle) { idle = true; Quiet(); eqMix = compMix = limMix = gateMix = 0; gGain = 1; gOpen = false; gHold = 0; }
            for (int f = 0; f < frames; f++)
            {
                int rd = dpos + 1; if (rd >= dlen) rd = 0;
                float late[RTD_MAXCH]; Ahead(in + f * ch, late, &pin);
                for (int c = 0; c < ch; c++)
                {
                    float v = late[c];
                    /* 21.11: the colour's 28 samples here too, so switching on does not jump in time */
                    dryD[c][chi] = v; chist[c][chi] = chist[c][chi + 64] = v; float dv = dryD[c][(chi - RTD_CDLY) & 63];
                    dry[c][dpos] = dv; delay[c][dpos] = dv;
                    float o = dry[c][rd]; float av = fabsf(o); if (av > pout) pout = av;
                    out[f * ch + c] = o;
                }
                chi = (chi + 1) & 63; uqi = (uqi + 4) & 127;
                dpos = rd; n++;
            }
            bl[0] = Lvl(pwSum); bl[1] = bl[2] = -140;
            gateMin = 0;
            Meters(pin, pout, 0, 1, NULL, bl);
            return;
        }
        idle = false;

        const double wetTo = on ? 1 : 0, eqTo = (cur.flags & RTA_EQ) ? 1 : 0, compTo = (cur.flags & RTA_COMP) ? 1 : 0, limTo = (cur.flags & RTA_LIMIT) ? 1 : 0;
        const double gateTo = (cur.flags & RTA_GATE) ? 1 : 0;
        const double gOpenLin = rtd_db2lin(cur.gateThrDb), gCloseLin = rtd_db2lin(cur.gateThrDb - RTD_GHYST), gRange = rtd_db2lin(cur.gateRangeDb);
        const double gRel = 1.0 - exp(-1.0 / (cur.gateReleaseMs * 0.001 * rate));
        const int gHoldN = (int)(RTD_GHOLD * rate);
        double gLow = 1;
        const double fade = 1.0 / (0.015 * rate);     /* 15 ms from one to the other */
        double thr[3], slope[3], knee[3];
        for (int b = 0; b < 3; b++) { const RtaCompBand q = PartOf(cur, b); thr[b] = q.thrDb; slope[b] = 1.0 / q.ratio - 1.0; knee[b] = q.kneeDb; }
        const double ceil = rtd_db2lin(cur.ceilDb);
        double x[RTD_MAXCH];
        bool bad = false;
        const double dsTo = (cur.flags & RTA_DEESS) ? 1 : 0, dsThr = cur.dsThrDb, dsRange = cur.dsRangeDb;
        /* the character: entering the optical one, its two stages start where the envelopes are */
        chr = (int)cur.compChar;
        if (chr != chrWas)
        {
            const double e[5] = { env, envL, envH, envM, envT };
            for (int i = 0; i < 5; i++) { opF[i] = opS[i] = e[i]; opM[i] = 0; }
            chrWas = chr;
        }
        /* the colour: with the compressor, a kind and a strength; the tape's filters follow its strength */
        const int colNow = (int)cur.colorType;
        const double colTo = (cur.flags & RTA_COMP) && colNow > 0 && cur.colorAmt > 0.05f ? 1 : 0;
        if (colTo > 0 || colMix > 0) { if (colNow > 0) colType = colNow; }
        const double colDrive = rtd_db2lin(cur.colorAmt * 0.12);
        /* the second compressor: its switch and character (entering the optical one, its stages start at its gains), its
           parts (another number: the crossovers start clean), the meters of this chunk */
        const double c2To = (cur.flags & RTA_COMP2) ? 1 : 0; const int c2Chr = (int)cur.c2Char;
        if (c2Chr != c2ChrWas) { for (int i = 0; i < 3; i++) { c2F[i] = c2S[i] = c2E[i]; c2M[i] = 0; } c2ChrWas = c2Chr; }
        { const int n2 = cur.c2Split > 0 ? (cur.c2Split2 > 0 ? 3 : 2) : 1;
          if (n2 != c2n) { memset(c2w1, 0, sizeof(c2w1)); memset(c2w2, 0, sizeof(c2w2)); memset(c2wa, 0, sizeof(c2wa)); for (int i = 0; i < 3; i++) c2P[i] = 0; c2n = n2; }
          if (n2 >= 2 && cur.c2Split != c2Hz1) { c2Hz1 = cur.c2Split; rtd_xo(&c2xo1, c2Hz1, rate); }
          if (n2 >= 3 && cur.c2Split2 != c2Hz2) { c2Hz2 = cur.c2Split2; rtd_xo(&c2xo2, c2Hz2, rate); } }
        for (int i = 0; i < 3; i++) { c2Low[i] = 0; c2Hi[i] = -140; }
        if (colType == 3 && cur.colorAmt != tapeFor)
        {
            tapeFor = cur.colorAmt; double a = cur.colorAmt * 0.01;
            rtd_coef(&tapeC[0], RTA_BELL, 80, 1.5 * a, 0.8, rate);
            rtd_coef(&tapeC[1], RTA_HIGHSHELF, rate * 0.45 < 14000 ? rate * 0.3 : 14000, -2.0 * a, 0.7, rate);
        }
        if (dsTo > 0 && dsMix <= 0) { memset(dsSt, 0, sizeof(dsSt)); memset(dsSt2, 0, sizeof(dsSt2)); memset(dsSt3, 0, sizeof(dsSt3)); dsPH = dsPF = 0; dsGr = 0; }   /* comes in from clean filters */
        if (cur.dsHz != dsXoHz) { dsXoHz = cur.dsHz; rtd_xo(&dsXo, dsXoHz, rate); }
        const bool dsTop = cur.dsHz2 < 19999.f;
        if (dsTop && cur.dsHz2 != dsXoHz2) { if (dsXoHz2 <= 0) { memset(dsSt2, 0, sizeof(dsSt2)); memset(dsSt3, 0, sizeof(dsSt3)); } dsXoHz2 = cur.dsHz2; rtd_xo(&dsXo2, dsXoHz2, rate); }
        if (!dsTop) dsXoHz2 = 0;
        double dsLow = 0;

        for (int f = 0; f < frames; f++)
        {
            Toward(&wet, wetTo, fade); Toward(&eqMix, eqTo, fade); Toward(&compMix, compTo, fade); Toward(&limMix, limTo, fade);
            if (ramp)
            {
                pre += preD; mk[0] += mkD[0]; mk[1] += mkD[1]; mk[2] += mkD[2]; mixv += mixD;
                for (int b = 0; b < RTA_BANDS; b++)
                {
                    if (!live[b]) continue;
                    for (int j = 0; j < nst[b]; j++)
                    {
                        RtdCoef& k = coef[b][j]; const RtdCoef& d = coefD[b][j];
                        k.a1 += d.a1; k.a2 += d.a2; k.a3 += d.a3; k.m0 += d.m0; k.m1 += d.m1; k.m2 += d.m2;
                    }
                }
            }

            double peak = 0;
            /* 21.11: the second compressor first, on the frame as it comes (the input meter keeps the untouched peak) */
            float fr[RTD_MAXCH]; const float* src = in + f * ch;
            for (int c = 0; c < ch; c++) { fr[c] = src[c]; float av = fabsf(src[c]); if (av > pin) pin = av; }
            Comp2Frame(fr, c2To, c2Chr, fade);
            float late[RTD_MAXCH], pinIgnored = 0; Ahead(fr, late, &pinIgnored);
            /* the gate: open while the sound ahead is over the threshold, kept open for the hold after it fell 3 dB under;
               the gain moves to 1 in ~1 ms and down to the range over the release */
            Toward(&gateMix, gateTo, fade);
            double gm = 1;
            if (gateMix > 0)
            {
                if (gDet >= gOpenLin) { gOpen = true; gHold = gHoldN; }
                else if (gOpen) { if (gDet >= gCloseLin) gHold = gHoldN; else if (gHold > 0) gHold--; else gOpen = false; }
                double want = gOpen ? 1.0 : gRange;
                gGain += (want - gGain) * (want > gGain ? gAtt : gRel);
                gm = 1 + (gGain - 1) * gateMix;
                if (gm < gLow) gLow = gm;
            }
            else { gGain = 1; gOpen = false; gHold = 0; }
            for (int c = 0; c < ch; c++)
            {
                float v = late[c];
                /* (the untouched sound waits the colour's 28 samples too, for the on/off glide) */
                dryD[c][chi] = v; dry[c][dpos] = dryD[c][(chi - RTD_CDLY) & 63];
                double s = v * gm;
                if (eqMix > 0)
                {
                    double e = s * pre;
                    for (int b = 0; b < RTA_BANDS; b++)
                    {
                        if (!live[b]) continue;
                        for (int j = 0; j < nst[b]; j++)
                        {
                            const RtdCoef& k = coef[b][j]; double& z1 = s1[b][j][c]; double& z2 = s2[b][j][c];
                            double v3 = e - z2, v1 = k.a1 * z1 + k.a2 * v3, v2 = z2 + k.a2 * z1 + k.a3 * v3;
                            z1 = 2 * v1 - z1; z2 = 2 * v2 - z2;
                            e = k.m0 * e + k.m1 * v1 + k.m2 * v2;
                        }
                    }
                    s += (e - s) * eqMix;
                }
                x[c] = s; double as = fabs(s); if (as > peak) peak = as;
            }

            /* the de-esser: the top split off, turned down while it stands out of the whole (see the top of the file) */
            Toward(&dsMix, dsTo, fade);
            if (dsMix > 0)
            {
                /* with a top edge: three parts in step (Linkwitz-Riley): the low part through the second crossover's all-pass,
                   the band and the very top from the second crossover; untouched they add up to the sound, phase-shifted only */
                double rest[RTD_MAXCH], bd[RTD_MAXCH], ph = 0, pf = 0;
                for (int c = 0; c < ch; c++)
                {
                    double lo, hi; Cross(dsXo, dsSt, c, x[c], &lo, &hi);
                    if (dsTop)
                    {
                        double b, t, la, lb; Cross(dsXo2, dsSt2, c, hi, &b, &t); Cross(dsXo2, dsSt3, c, lo, &la, &lb);
                        bd[c] = b; rest[c] = la + lb + t;
                    }
                    else { bd[c] = hi; rest[c] = lo; }
                    ph += bd[c] * bd[c]; pf += x[c] * x[c];
                }
                dsPH += (ph / ch - dsPH) * dsA; dsPF += (pf / ch - dsPF) * dsA;
                double want = 0;
                if (dsPF > 1e-9)   /* not in silence (-90 dBFS) */
                {
                    double over = 4.342944819032518 * log((dsPH + 1e-30) / dsPF) - dsThr;
                    if (over > 0) { want = -2 * over; if (want < dsRange) want = dsRange; }
                }
                dsGr += (want - dsGr) * (want < dsGr ? dsAtt : dsRel);
                const double g = rtd_db2lin(dsGr);
                for (int c = 0; c < ch; c++) x[c] += (rest[c] + bd[c] * g - x[c]) * dsMix;
                if (dsGr * dsMix < dsLow) dsLow = dsGr * dsMix;
            }
            else dsGr = 0;

            /* compressor: how much gain the level of the incoming sound asks for (the sound in hand is 10 ms older than
               that level), then attack and release on that gain */
            if (compMix > 0)
            {
                Toward(&xoMix, splitTo ? 1 : 0, fade); Toward(&m3, threeTo ? 1 : 0, fade);
                for (int b = 0; b < 3; b++) Toward(&aud[b], ((target.mute >> b) & 1u) ? 0 : 1, fade); /* (a switch, not a value that glides: taken from the settings asked for) */
                double want = WantC(pwSum, thr[0], slope[0], knee[0]);
                Ball(&env, want, 0, 0);
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
                    want = WantC(pwLSum, thr[0], slope[0], knee[0]); if (want > room) want = room;
                    Ball(&envL, want, 0, 1);
                    want = WantC(pwHSum, thr[2], slope[2], knee[2]); Ball(&envH, want, 2, 2);
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
                        want = WantC(pwMSum, thr[1], slope[1], knee[1]); Ball(&envM, want, 1, 3);
                        want = WantC(pwTSum, thr[2], slope[2], knee[2]); Ball(&envT, want, 2, 4);
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


            /* 21.11, the colour (see the top of the file): always 28 samples late, coloured or plain */
            Toward(&colMix, colTo, fade);
            for (int c = 0; c < ch; c++)
            {
                float* hh = chist[c]; hh[chi] = hh[chi + 64] = (float)x[c];
                const double plain = hh[(chi - RTD_CDLY) & 63];
                double o = plain;
                if (colMix > 0)
                {
                    /* four times the rate: sample n-16 and the three points after it, through the saturation */
                    float* u = uq[c]; const int base = uqi;
                    const float* win = hh + ((chi - (RTD_CITAPS - 1)) & 63);   /* samples n-31 .. n, in a row */
                    float v0 = (float)(Sat(hh[(chi - 16) & 63] * colDrive, colType, c) / colDrive);
                    u[base] = u[base + 128] = v0;
                    for (int p = 0; p < 3; p++)
                    {
                        float vp = (float)(Sat(rtd_dot(ti[p], win, RTD_CITAPS) * colDrive, colType, c) / colDrive);
                        int at = (base + 1 + p) & 127; u[at] = u[at + 128] = vp;
                    }
                    /* back down: centred on sample n-28 */
                    double y = rtd_dot(hd, u + ((base - (RTD_CTAPS - 1)) & 127), RTD_CTAPS);
                    /* the DC the even harmonics bring, taken off; the tape's bump and soft top */
                    double hp = y - dcX[c] + 0.9995 * dcY[c]; dcX[c] = y; dcY[c] = hp; y = hp;
                    if (colType == 3)
                        for (int j = 0; j < 2; j++)
                        {
                            const RtdCoef& k = tapeC[j]; double& z1 = tapeS[j][c][0]; double& z2 = tapeS[j][c][1];
                            double v3 = y - z2, v1 = k.a1 * z1 + k.a2 * v3, v2 = z2 + k.a2 * z1 + k.a3 * v3;
                            z1 = 2 * v1 - z1; z2 = 2 * v2 - z2; y = k.m0 * y + k.m1 * v1 + k.m2 * v2;
                        }
                    o = plain + (y - plain) * colMix;
                }
                else { for (int p = 0; p < 4; p++) { int at = (uqi + p) & 127; uq[c][at] = uq[c][at + 128] = 0; } dcX[c] = dcY[c] = 0; }
                x[c] = o;
            }
            chi = (chi + 1) & 63; uqi = (uqi + 4) & 127;

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
        { double gdb = rtd_lin2db(gLow > 1e-6 ? gLow : 1e-6); gateMin = gdb < gateMin ? (float)gdb : (float)(gateMin + (gdb - gateMin) * grRise); }
        dsMin = dsLow < dsMin ? (float)dsLow : (float)(dsMin + (dsLow - dsMin) * grRise);
        for (int i = 0; i < 3; i++) { c2Min[i] = c2Low[i] < c2Min[i] ? (float)c2Low[i] : (float)(c2Min[i] + (c2Low[i] - c2Min[i]) * grRise); c2LvNow[i] = (float)c2Hi[i]; }
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
