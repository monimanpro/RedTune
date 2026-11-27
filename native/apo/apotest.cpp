// The test bench of the sound effect. Part 1 runs the processing (rtdsp.h) on sines and noise and compares it with
// what the formulas say. Part 2 loads RedTuneApo.dll the way Windows does (class factory, Initialize, LockForProcess,
// APOProcess) with a shared file of its own, without installing anything.
//   apotest.exe [RedTuneApo.dll] [scratch folder]      exit code = the number of failed checks
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <xmmintrin.h>
#include <stdio.h>
#include <stdlib.h>
#include <complex>
#include <vector>
#include "rtdsp.h"
#pragma comment(lib, "winmm.lib")   // timeBeginPeriod: the real-pace run of DeepFilterNet

static int g_fail = 0;
static void Check(bool ok, const char* what, const char* fmt = "", ...)
{
    char more[256] = ""; va_list va; va_start(va, fmt); vsnprintf(more, sizeof(more), fmt, va); va_end(va);
    printf("%s  %s  %s\n", ok ? "ok  " : "FAIL", what, more);
    if (!ok) g_fail++;
}

// 22.11: how much of a core something takes depends on the machine: on GitHub Actions' runner (slower, shared) the
// figure is printed but not judged — v0.4.8's first build failed on «dfn: 10.2 % of one core» against a 10 % limit
// that this laptop meets with room to spare. The same rule as the paint-time limits of the program's own checks.
static void CheckCost(bool ok, const char* what, const char* fmt, double v)
{
    char ci[8] = ""; GetEnvironmentVariableA("GITHUB_ACTIONS", ci, sizeof(ci));
    if (!ok && lstrcmpiA(ci, "true") == 0) { printf("note  %s  ", what); printf(fmt, v); printf("  (not judged on the CI runner)\n"); return; }
    Check(ok, what, fmt, v);
}

static const double PI = 3.14159265358979323846;

// the same curve the page draws (src\AudioFx.cs, Response): each band as its analogue prototype, frequency warped
static double Analytic(const RtaParams& p, double f, double rate)
{
    std::complex<double> h = rtd_db2lin(p.preampDb);
    for (int i = 0; i < RTA_BANDS; i++)
    {
        const RtaBand& b = p.bands[i];
        if (b.type == RTA_OFF) continue;
        /* 21.11: the band's sections by its slope (rtd_plan) */
        RtdSec sec[RTD_STAGES]; int n = rtd_plan(b.type, b.freq, b.gainDb, b.q, rate, sec);
        for (int j = 0; j < n; j++)
        {
        double q = sec[j].q;
        double A = pow(10.0, sec[j].g / 40.0), g = tan(PI * sec[j].f / rate), k = 1.0 / q, m0 = 1, m1 = 0, m2 = 0;
        switch (sec[j].type)
        {
        case RTA_BELL: k = 1.0 / (q * A); m1 = k * (A * A - 1); break;
        case RTA_LOWSHELF: g /= sqrt(A); m1 = k * (A - 1); m2 = A * A - 1; break;
        case RTA_HIGHSHELF: g *= sqrt(A); m0 = A * A; m1 = k * (1 - A) * A; m2 = 1 - A * A; break;
        case RTA_HIGHPASS: m1 = -k; m2 = -1; break;
        case RTA_LOWPASS: m0 = 0; m2 = 1; break;
        case RTA_NOTCH: m1 = -k; break;
        }
        std::complex<double> s(0, tan(PI * f / rate) / g), d = s * s + k * s + 1.0;
        h *= m0 + (m1 * s + m2) / d;
        }
    }
    return 20 * log10(abs(h));
}

static RtaParams Blank()
{
    RtaParams p; memset(&p, 0, sizeof(p));
    p.ratio = 1; p.mixPct = 100; p.attackMs = 10; p.releaseMs = 150; p.ceilDb = -0.1f; p.kneeDb = 0;
    for (int i = 0; i < RTA_BANDS; i++) { p.bands[i].freq = 1000; p.bands[i].q = 0.707f; }
    for (int i = 0; i < 2; i++) { p.cb[i].ratio = 1; p.cb[i].attackMs = 10; p.cb[i].releaseMs = 150; }
    return p;
}

// every part of the compressor gets the settings of the first (the two-part compressor as it was before the parts had their own)
static RtaParams Alike(RtaParams p)
{
    for (int i = 0; i < 2; i++) { RtaCompBand b = { p.thrDb, p.ratio, p.attackMs, p.releaseMs, p.kneeDb, p.makeupDb }; p.cb[i] = b; }
    return p;
}

// the level of a tone of frequency f in the last second of a stereo recording, dB
static double ToneDb(const std::vector<float>& buf, int n, double f)
{
    double re = 0, im = 0; for (int i = n - 48000; i < n; i++) { double ph = 2 * PI * f * i / 48000.0; re += buf[2 * i] * cos(ph); im += buf[2 * i] * sin(ph); }
    return 20 * log10(sqrt(re * re + im * im) / 24000 + 1e-12);
}

static void Band(RtaParams& p, int i, uint32_t type, float f, float g, float q) { p.bands[i].type = type; p.bands[i].freq = f; p.bands[i].gainDb = g; p.bands[i].q = q; }

// steady level of a sine through the effect, dB re the input level (stereo, both channels alike)
static double SineGain(RtDsp& d, double f, double amp, double seconds, double* outPeakDb = NULL)
{
    int n = (int)(d.rate * seconds), settle = n / 2;
    std::vector<float> buf(2 * 512);
    double sum = 0, peak = 0; int count = 0, t = 0;
    while (t < n)
    {
        int k = n - t < 512 ? n - t : 512;
        for (int i = 0; i < k; i++) { float v = (float)(amp * sin(2 * PI * f * (t + i) / d.rate)); buf[2 * i] = buf[2 * i + 1] = v; }
        d.Process(buf.data(), buf.data(), k);
        for (int i = 0; i < k; i++) if (t + i >= settle) { sum += (double)buf[2 * i] * buf[2 * i]; count++; if (fabs(buf[2 * i]) > peak) peak = fabs(buf[2 * i]); }
        t += k;
    }
    if (outPeakDb) *outPeakDb = 20 * log10(peak > 1e-12 ? peak : 1e-12);
    return 10 * log10(sum / count * 2 / (amp * amp));
}

static unsigned g_rng = 12345;
static float Noise() { g_rng = g_rng * 1664525u + 1013904223u; return (float)((int)(g_rng >> 8) - 8388608) / 8388608.f; }

static void TestDsp()
{
    printf("--- the processing ---\n");
    const double rates[] = { 44100, 48000, 96000, 192000 };
    const double freqs[] = { 20, 50, 100, 200, 500, 1000, 2000, 3000, 5000, 8000, 12000, 16000 };
    RtaParams eq = Blank(); eq.flags = RTA_ON | RTA_EQ; eq.preampDb = -3;
    Band(eq, 0, RTA_HIGHPASS, 30, 0, 0.707f); Band(eq, 1, RTA_LOWSHELF, 100, 6, 0.7f); Band(eq, 2, RTA_BELL, 1000, -8, 2);
    Band(eq, 3, RTA_BELL, 3000, 5, 1); Band(eq, 4, RTA_HIGHSHELF, 8000, 4, 0.7f); Band(eq, 5, RTA_NOTCH, 5000, 0, 8);
    for (double rate : rates)
    {
        double worst = 0;
        for (double f : freqs)
        {
            RtDsp* d = new RtDsp; d->Init(rate, 2); d->Set(&eq);
            double got = SineGain(*d, f, 0.05, 1.0), want = Analytic(eq, f, rate);
            if (want > -60 && fabs(got - want) > worst) worst = fabs(got - want);
            if (rate == 48000) printf("      %6.0f Hz  measured %7.2f dB  formula %7.2f dB\n", f, got, want);
            delete d;
        }
        char name[64]; sprintf(name, "equalizer vs formula at %.0f Hz", rate);
        Check(worst < 0.05, name, "(worst %.3f dB)", worst);
    }

    // 21.11, the slopes of the cuts: 12..48 dB/oct against the formula, and an 8th-order Butterworth where it must be
    {
        double worst = 0, at500 = 0, at1k = 0, lp = 0;
        for (int n = 1; n <= 4; n++)
            for (double f : freqs)
            {
                uint32_t sl = (uint32_t)(n - 1) << 8;
                RtaParams c = Blank(); c.flags = RTA_ON | RTA_EQ; Band(c, 0, RTA_HIGHPASS | sl, 1000, 0, 0.707f); Band(c, 1, RTA_LOWPASS | sl, 12000, 0, 0.707f);
                Band(c, 2, RTA_LOWSHELF | sl, 150, 6, 0.707f); Band(c, 3, RTA_BELL | sl, 2500, -7, 1.2f); Band(c, 4, RTA_HIGHSHELF | sl, 6000, 4, 0.707f); Band(c, 5, RTA_NOTCH | sl, 400, 0, 6);
                RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&c);
                double got = SineGain(*d, f, 0.05, 1.0), want = Analytic(c, f, 48000);
                if (want > -70 && fabs(got - want) > worst) worst = fabs(got - want);
                delete d;
            }
        RtaParams c = Blank(); Band(c, 0, RTA_HIGHPASS | (3u << 8), 1000, 0, 0.707f);
        at500 = Analytic(c, 500, 48000); at1k = Analytic(c, 1000, 48000);
        Band(c, 0, RTA_LOWPASS | (2u << 8), 1000, 0, 0.707f); lp = Analytic(c, 2000, 48000);
        Check(worst < 0.05, "slopes 1..4 of every kind vs formula (cuts, shelves, a bell, a notch)", "(worst %.3f dB)", worst);
        Check(fabs(at500 + 48.2) < 0.3 && fabs(at1k + 3.01) < 0.05 && fabs(lp + 36.1) < 0.3, "cut slopes: Butterworth shapes",
              "48 dB/oct: %.1f dB an octave under, %.2f dB at the frequency; 36 dB/oct low cut %.1f dB an octave over", at500, at1k, lp);
        /* a slope changed while sound plays: no NaN, no burst */
        /* the straight bell: the frequency keeps the gain, the top is flatter and the sides steeper than the smooth bell's */
        {
            bool ok = true; double worstPeak = 0, edgeS = 0, edgeT = 0, farS = 0, farT = 0;
            for (int n = 2; n <= 4; n++) for (int gs = -1; gs <= 1; gs += 2)
            {
                RtaParams b = Blank(); Band(b, 0, RTA_BELL | ((uint32_t)(n - 1) << 8), 1000, 12.f * gs, 1.f);
                double pk = Analytic(b, 1000, 48000); if (fabs(pk - 12 * gs) > worstPeak) worstPeak = fabs(pk - 12 * gs);
                if (n == 3 && gs == 1)
                {
                    RtaParams sm = Blank(); Band(sm, 0, RTA_BELL, 1000, 12, 1.f);
                    edgeS = Analytic(sm, 1000 * pow(2, 0.4), 48000); edgeT = Analytic(b, 1000 * pow(2, 0.4), 48000);
                    farS = Analytic(sm, 1000 * pow(2, 2.0), 48000); farT = Analytic(b, 1000 * pow(2, 2.0), 48000);
                }
            }
            ok = worstPeak < 0.6 && edgeT > edgeS + 1 && farT < farS - 0.5;
            Check(ok, "straight bell: the gain at the frequency, a flat top, steep sides", "peak off by %.2f dB at most; 0.4 oct out %.1f vs %.1f dB (smooth); 2 oct out %.1f vs %.1f dB", worstPeak, edgeT, edgeS, farT, farS);
        }
        RtaParams s = Blank(); s.flags = RTA_ON | RTA_EQ; Band(s, 0, RTA_HIGHPASS, 200, 0, 0.707f);
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&s); double pk = 0;
        std::vector<float> buf(2 * 480); g_rng = 3;
        for (int t = 0; t < 100; t++)
        {
            if (t % 20 == 10) { s.bands[0].type = RTA_HIGHPASS | ((uint32_t)((t / 20) % 4) << 8); d->Set(&s); }
            for (int i = 0; i < 480; i++) buf[2 * i] = buf[2 * i + 1] = 0.25f * Noise();
            d->Process(buf.data(), buf.data(), 480);
            for (float v : buf) { if (!(v == v)) pk = 1e9; else if (fabs(v) > pk) pk = fabs(v); }
        }
        delete d;
        Check(pk < 1.0, "cut slopes: switching the slope while noise plays stays in range", "peak %.2f", pk);
    }

    // compressor: the level that comes out for the level that goes in
    for (int kneeCase = 0; kneeCase < 2; kneeCase++)
    {
        RtaParams c = Blank(); c.flags = RTA_ON | RTA_COMP; c.thrDb = -20; c.ratio = 4; c.kneeDb = kneeCase ? 6.f : 0.f; c.attackMs = 1; c.releaseMs = 800; c.makeupDb = 3;
        double worst = 0;
        for (int in = -40; in <= 0; in += 5)
        {
            RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&c);
            double outPeak; SineGain(*d, 1000, pow(10.0, in / 20.0), 3.0, &outPeak);
            double over = in - c.thrDb, gr;
            if (2 * over < -c.kneeDb) gr = 0; else if (c.kneeDb > 0 && 2 * over <= c.kneeDb) gr = (1 / c.ratio - 1) * (over + c.kneeDb / 2) * (over + c.kneeDb / 2) / (2 * c.kneeDb); else gr = (1 / c.ratio - 1) * over;
            double want = in + gr + c.makeupDb;
            if (fabs(outPeak - want) > worst) worst = fabs(outPeak - want);
            if (!kneeCase) printf("      in %4d dB  out %7.2f dB  expected %7.2f dB\n", in, outPeak, want);
            delete d;
        }
        Check(worst < 0.6, kneeCase ? "compressor curve, knee 6 dB" : "compressor curve, hard knee", "(worst %.2f dB)", worst);
    }

    // limiter: a +15 dB boost on full-scale noise and on single spikes never leaves above the ceiling
    {
        RtaParams l = Blank(); l.flags = RTA_ON | RTA_EQ | RTA_LIMIT; l.preampDb = 15; l.ceilDb = -1; Band(l, 0, RTA_BELL, 100, 12, 1);
        const double ceil = pow(10.0, -1 / 20.0);
        for (double rate : rates)
        {
            RtDsp* d = new RtDsp; d->Init(rate, 2); d->Set(&l);
            std::vector<float> buf(2 * 480); double top = 0; long over = 0;
            const int total = (int)(rate * 6 / 480);
            for (int blk = 0; blk < total; blk++)
            {
                for (int i = 0; i < 480; i++)
                {
                    float v = blk < total / 2 ? Noise() : ((blk * 480 + i) % 997 == 0 ? 1.f : 0.001f * Noise());
                    buf[2 * i] = v; buf[2 * i + 1] = blk % 2 ? -v : Noise();
                }
                d->Process(buf.data(), buf.data(), 480);
                if (blk * 480 > rate * 0.5) for (float v : buf) { if (fabs(v) > top) top = fabs(v); if (fabs(v) > ceil * 1.00001) over++; }
            }
            char name[64]; sprintf(name, "limiter ceiling at %.0f Hz", rate);
            Check(over == 0 && top > ceil * 0.7, name, "(top %.5f, ceiling %.5f, over %ld)", top, ceil, over);
            delete d;
        }
    }

    // 19.11: the compressor's level is taken before the equalizer: a raised bass does not change how much is taken off
    {
        RtaParams c = Blank(); c.flags = RTA_ON | RTA_EQ | RTA_COMP; c.thrDb = -30; c.ratio = 2; c.kneeDb = 0; c.attackMs = 5; c.releaseMs = 60;
        Band(c, 0, RTA_LOWSHELF, 400, 12, 0.7f);
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&c);
        double outPeak; SineGain(*d, 100, 0.1, 3.0, &outPeak);
        double eq = Analytic(c, 100, 48000), want = -20 + eq - 5; // 10 dB over the threshold at 2:1 = 5 dB off, whatever the equalizer adds
        Check(fabs(outPeak - want) < 0.15, "the equalizer does not drive the compressor", "(out %.2f dB, expected %.2f dB, equalizer %+.2f dB)", outPeak, want, eq);
        delete d;
    }

    // a jump from quiet to loud: thanks to the look-ahead the loud sound does not get through at full level first, and
    // low notes are not bent (a steady 50 Hz tone under compression keeps its harmonics low)
    {
        RtaParams c = Blank(); c.flags = RTA_ON | RTA_COMP; c.thrDb = -36; c.ratio = 2; c.kneeDb = 0; c.attackMs = 3; c.releaseMs = 40;
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&c);
        const int n = 48000 * 2; std::vector<float> buf(2 * n);
        for (int i = 0; i < n; i++) { double a = i < 48000 ? 0.01 : 0.316; buf[2 * i] = buf[2 * i + 1] = (float)(a * sin(2 * PI * 1000 * i / 48000.0)); }
        d->Process(buf.data(), buf.data(), n);
        int lat = d->Latency(); double top = 0, settled = 0;
        for (int i = 48000 + lat - 480; i < 48000 + lat + 4800; i++) if (fabs(buf[2 * i]) > top) top = fabs(buf[2 * i]);
        for (int i = n - 4800; i < n; i++) if (fabs(buf[2 * i]) > settled) settled = fabs(buf[2 * i]);
        double over = 20 * log10(top / settled), level = 20 * log10(settled);
        Check(over < 3.0 && fabs(level - (-10 - 13)) < 0.2, "a jump to loud: no burst through", "(the loudest moment is %.2f dB over the settled %.2f dB)", over, level);
        delete d;

        d = new RtDsp; d->Init(48000, 2); d->Set(&c);
        const int m = 48000 * 3; std::vector<float> low(2 * m);
        for (int i = 0; i < m; i++) low[2 * i] = low[2 * i + 1] = (float)(0.2 * sin(2 * PI * 50 * i / 48000.0));
        d->Process(low.data(), low.data(), m);
        // the tone and its 3rd harmonic over the last second (a whole number of periods)
        double amp[2];
        for (int h = 0; h < 2; h++) { double re = 0, im = 0, f = h ? 150 : 50; for (int i = m - 48000; i < m; i++) { double p = 2 * PI * f * i / 48000.0; re += low[2 * i] * cos(p); im += low[2 * i] * sin(p); } amp[h] = sqrt(re * re + im * im) / 24000; }
        double h3 = 20 * log10(amp[1] / amp[0]);
        Check(h3 < -60, "a 50 Hz tone under compression stays clean", "(3rd harmonic %.0f dB)", h3);
        delete d;
    }

    // 19.11: the compressor in two parts (the bass apart)
    {
        RtaParams c = Blank(); c.flags = RTA_ON | RTA_COMP; c.thrDb = 0; c.ratio = 2; c.kneeDb = 0; c.splitHz = 280; c = Alike(c);
        double worst = 0;
        for (double f : { 40.0, 100.0, 200.0, 280.0, 400.0, 1000.0, 5000.0, 15000.0 })
        {
            RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&c);
            double g = SineGain(*d, f, 0.05, 1.0); if (fabs(g) > worst) worst = fabs(g);
            delete d;
        }
        Check(worst < 0.02, "in two parts: the halves add back to an even level", "(worst %.4f dB)", worst);

        for (int parts = 1; parts <= 2; parts++)
        {
            RtaParams p = Blank(); p.flags = RTA_ON | RTA_COMP; p.thrDb = -36; p.ratio = 2; p.kneeDb = 0; p.attackMs = 3; p.releaseMs = 40; p.splitHz = parts == 2 ? 280.f : 0.f; p = Alike(p);
            RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&p);
            const int n = 48000 * 3; std::vector<float> buf(2 * n);
            for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(0.1 * sin(2 * PI * 1000 * i / 48000.0) + 0.5 * sin(2 * PI * 60 * i / 48000.0));
            d->Process(buf.data(), buf.data(), n);
            double lvl[2];
            for (int h = 0; h < 2; h++) { double re = 0, im = 0, f = h ? 60 : 1000; for (int i = n - 48000; i < n; i++) { double ph = 2 * PI * f * i / 48000.0; re += buf[2 * i] * cos(ph); im += buf[2 * i] * sin(ph); } lvl[h] = 20 * log10(sqrt(re * re + im * im) / 24000); }
            // one part: both tones under one gain, set by their sum (-5.85 dB -> 15.07 dB off). Two parts: 1 kHz at -20 loses 8 dB, 60 Hz at -6.02 loses 14.99 dB
            double want1k = parts == 2 ? -28.0 : -20 - 15.07, want60 = parts == 2 ? -6.02 - 14.99 : -6.02 - 15.07;
            Check(fabs(lvl[0] - want1k) < 0.3 && fabs(lvl[1] - want60) < 0.3, parts == 2 ? "in two parts: a loud bass note leaves the 1 kHz tone alone" : "in one part: the bass note turns the 1 kHz tone down too",
                "(1 kHz %.2f dB, expected %.2f; 60 Hz %.2f dB, expected %.2f)", lvl[0], want1k, lvl[1], want60);
            delete d;
        }
    }

    // the bass part keeps under the limiter's ceiling by its own gain: a bass note raised 12 dB by the equalizer does not
    // make the limiter work, and a 1 kHz tone beside it is not turned down with it
    {
        RtaParams p = Blank(); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT; p.thrDb = -10; p.ratio = 2; p.kneeDb = 0; p.attackMs = 3; p.releaseMs = 40; p.splitHz = 280; p.ceilDb = -1; p = Alike(p);
        Band(p, 0, RTA_LOWSHELF, 300, 12, 0.7f);
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&p);
        const int n = 48000 * 12; std::vector<float> buf(2 * n);
        for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(0.05 * sin(2 * PI * 1000 * i / 48000.0) + 0.5 * sin(2 * PI * 60 * i / 48000.0));
        d->Process(buf.data(), buf.data(), n);
        double lvl[2];
        for (int h = 0; h < 2; h++) { double re = 0, im = 0, f = h ? 60 : 1000; for (int i = n - 48000; i < n; i++) { double ph = 2 * PI * f * i / 48000.0; re += buf[2 * i] * cos(ph); im += buf[2 * i] * sin(ph); } lvl[h] = 20 * log10(sqrt(re * re + im * im) / 24000); }
        double eq1k = Analytic(p, 1000, 48000);
        // 60 Hz: -6 dB + 12 dB of equalizer would be +6 dB; held at the ceiling (-1) less 2 dB. 1 kHz at -26 dB is under the threshold: only the equalizer's own change
        Check(fabs(lvl[1] - (-3.0)) < 0.4 && fabs(lvl[0] - (-26.02 + eq1k)) < 0.3 && d->limMin > -0.5f, "in two parts: the bass part keeps under the ceiling by itself",
            "(60 Hz %.2f dB, expected -3.00; 1 kHz %.2f dB, expected %.2f; the limiter took %.2f dB)", lvl[1], lvl[0], -26.02 + eq1k, d->limMin);
        delete d;
    }

    // 19.11: three parts with settings of their own
    {
        // with nothing to squeeze the three parts add back to the whole sound at an even level, the splits included
        RtaParams c = Blank(); c.flags = RTA_ON | RTA_COMP; c.splitHz = 250; c.split2Hz = 4000;
        double worst = 0;
        for (double f : { 40.0, 120.0, 250.0, 500.0, 1000.0, 2500.0, 4000.0, 6000.0, 15000.0 })
        {
            RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&c);
            double g = SineGain(*d, f, 0.05, 1.0); if (fabs(g) > worst) worst = fabs(g);
            delete d;
        }
        Check(worst < 0.02, "in three parts: they add back to an even level", "(worst %.4f dB)", worst);

        // three tones, one in each part, each part with its own threshold and ratio (no knee, a sine reads as its peak):
        //   (tones well away from the splits, and 100 Hz fits the 20 ms window a whole number of times)
        //   100 Hz at -6.02: bass -20, 4:1 -> 13.98 over, 10.49 off;  1 kHz at -20: middle -30, 2:1 -> 10 over, 5 off, +3 after;
        //   12 kHz at -26.02: top -40, 3:1 -> 13.98 over, 9.32 off
        RtaParams p = Blank(); p.flags = RTA_ON | RTA_COMP; p.splitHz = 300; p.split2Hz = 4000; p.attackMs = 3; p.releaseMs = 40;
        p.thrDb = -20; p.ratio = 4; p.kneeDb = 0;
        RtaCompBand mid = { -30, 2, 3, 40, 0, 3 }, top = { -40, 3, 3, 40, 0, 0 }; p.cb[0] = mid; p.cb[1] = top;
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&p);
        const int n = 48000 * 3; std::vector<float> buf(2 * n);
        for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(0.5 * sin(2 * PI * 100 * i / 48000.0) + 0.1 * sin(2 * PI * 1000 * i / 48000.0) + 0.05 * sin(2 * PI * 12000 * i / 48000.0));
        d->Process(buf.data(), buf.data(), n);
        double l60 = ToneDb(buf, n, 100), l1k = ToneDb(buf, n, 1000), l9k = ToneDb(buf, n, 12000);
        Check(fabs(l60 - (-6.02 - 10.49)) < 0.3 && fabs(l1k - (-20 - 5 + 3)) < 0.3 && fabs(l9k - (-26.02 - 9.32)) < 0.3, "in three parts: each part by its own settings",
            "(100 Hz %.2f dB, expected -16.51; 1 kHz %.2f dB, expected -22.00; 12 kHz %.2f dB, expected -35.34)", l60, l1k, l9k);
        // the meters per part: what each part takes off and the level it reads
        Check(fabs(d->bandGr[0] + 10.49) < 0.4 && fabs(d->bandGr[1] + 5) < 0.4 && fabs(d->bandGr[2] + 9.32) < 0.4 && fabs(d->bandLv[0] + 6.02) < 0.4 && fabs(d->bandLv[1] + 20) < 0.4 && fabs(d->bandLv[2] + 26.02) < 0.4,
            "in three parts: the meters of each part", "(gain %.2f / %.2f / %.2f dB, level %.2f / %.2f / %.2f dB)", d->bandGr[0], d->bandGr[1], d->bandGr[2], d->bandLv[0], d->bandLv[1], d->bandLv[2]);
        delete d;

        // two parts: the half above the split goes by the top's settings, the middle's are not used
        RtaParams q = Blank(); q.flags = RTA_ON | RTA_COMP; q.splitHz = 250; q.attackMs = 3; q.releaseMs = 40; q.thrDb = -20; q.ratio = 4; q.kneeDb = 0;
        RtaCompBand unused = { -60, 20, 3, 40, 0, 0 }; q.cb[0] = unused; q.cb[1] = mid;
        d = new RtDsp; d->Init(48000, 2); d->Set(&q);
        for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(0.5 * sin(2 * PI * 60 * i / 48000.0) + 0.1 * sin(2 * PI * 1000 * i / 48000.0));
        d->Process(buf.data(), buf.data(), n);
        l60 = ToneDb(buf, n, 60); l1k = ToneDb(buf, n, 1000);
        Check(fabs(l60 - (-6.02 - 10.49)) < 0.3 && fabs(l1k - (-20 - 5 + 3)) < 0.3, "in two parts: the upper half by the top's settings",
            "(60 Hz %.2f dB, expected -16.51; 1 kHz %.2f dB, expected -22.00)", l60, l1k);
        delete d;

        // 19.11, listening to the parts. Two parts, the top silent: the bass as before, of the 1 kHz tone only what the
        // crossover lets into the bass part (two octaves above the split: about 48 dB down)
        RtaParams qm = q; qm.mute = 4;
        d = new RtDsp; d->Init(48000, 2); d->Set(&qm);
        for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(0.5 * sin(2 * PI * 60 * i / 48000.0) + 0.1 * sin(2 * PI * 1000 * i / 48000.0));
        d->Process(buf.data(), buf.data(), n);
        l60 = ToneDb(buf, n, 60); l1k = ToneDb(buf, n, 1000);
        Check(fabs(l60 - (-16.51)) < 0.3 && l1k < -60, "listening, two parts: the top silent, the bass as before", "(60 Hz %.2f dB, expected -16.51; 1 kHz %.2f dB, under -60 expected)", l60, l1k);
        delete d;

        // three parts with the three tones of before: the middle silent (mute), then only the top left (solo of the top)
        for (int pass = 0; pass < 2; pass++)
        {
            RtaParams pm = p; pm.mute = pass == 0 ? 2u : 3u;
            d = new RtDsp; d->Init(48000, 2); d->Set(&pm);
            for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(0.5 * sin(2 * PI * 100 * i / 48000.0) + 0.1 * sin(2 * PI * 1000 * i / 48000.0) + 0.05 * sin(2 * PI * 12000 * i / 48000.0));
            d->Process(buf.data(), buf.data(), n);
            double a = ToneDb(buf, n, 100), b = ToneDb(buf, n, 1000), c9 = ToneDb(buf, n, 12000);
            if (pass == 0) Check(fabs(a - (-16.51)) < 0.3 && b < -55 && fabs(c9 - (-35.34)) < 0.3 && fabs(d->bandLv[1] + 20) < 0.4, "listening, three parts: the middle silent, the bass and the top as before, the middle's meter lives on",
                "(100 Hz %.2f dB, expected -16.51; 1 kHz %.2f dB, under -55 expected; 12 kHz %.2f dB, expected -35.34; the middle reads %.2f dB)", a, b, c9, d->bandLv[1]);
            else Check(a < -80 && b < -55 && fabs(c9 - (-35.34)) < 0.3, "listening, three parts: only the top left",
                "(100 Hz %.2f dB, under -80 expected; 1 kHz %.2f dB, under -55 expected; 12 kHz %.2f dB, expected -35.34)", a, b, c9);
            delete d;
        }

        // one part: there is nothing to make silent, whatever the bits say
        {
            RtaParams one = Blank(); one.flags = RTA_ON | RTA_COMP; one.mute = 7;
            RtDsp* d1 = new RtDsp; d1->Init(48000, 2); d1->Set(&one);
            double g = SineGain(*d1, 1000, 0.05, 1.0);
            Check(fabs(g) < 0.02, "listening, one part: the bits change nothing", "(1 kHz %.4f dB)", g);
            delete d1;
        }

        // switching a part off while the sound plays: no click — the 1 kHz tone goes down smoothly (no step between two
        // samples larger than the tone's own steepest slope)
        {
            RtaParams on = p; on.thrDb = 0; on.cb[0].thrDb = 0; on.cb[1].thrDb = 0; on.cb[0].makeupDb = 0;
            RtDsp* d2 = new RtDsp; d2->Init(48000, 2); d2->Set(&on);
            const int half = 48000; std::vector<float> t(2 * 2 * half);
            for (int i = 0; i < 2 * half; i++) t[2 * i] = t[2 * i + 1] = (float)(0.5 * sin(2 * PI * 1000 * i / 48000.0));
            d2->Process(t.data(), t.data(), half);
            on.mute = 2; d2->Set(&on);
            d2->Process(t.data() + 2 * half, t.data() + 2 * half, half);
            double step = 0; for (int i = half - 2000; i < 2 * half - 1; i++) { double s2 = fabs(t[2 * (i + 1)] - t[2 * i]); if (s2 > step) step = s2; }
            // (what is left: the part of the tone the crossovers let into the bass and the top, about 40 dB under it)
            double slope = 0.5 * 2 * PI * 1000 / 48000.0, tail = 0; for (int i = 2 * half - 4800; i < 2 * half; i++) if (fabs(t[2 * i]) > tail) tail = fabs(t[2 * i]);
            Check(step < slope * 1.1 && tail < 0.008, "listening: a part switched off while it plays goes down without a click",
                "(the largest step %.4f, the tone's own %.4f; what is left of it %.5f)", step, slope, tail);
            delete d2;
        }
    }

    // off: the same samples, late by the look-ahead
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); RtaParams off = Blank(); d->Set(&off);
        int n = 4800, lat = d->Latency(); std::vector<float> in(2 * n), out(2 * n);
        for (auto& v : in) v = Noise();
        d->Process(in.data(), out.data(), n);
        bool same = true; for (int i = lat; i < n && same; i++) same = out[2 * i] == in[2 * (i - lat)] && out[2 * i + 1] == in[2 * (i - lat) + 1];
        Check(same, "switched off: bit for bit", "(late by %d samples)", lat);
        delete d;
    }

    // settings that move while the sound plays: no step bigger than the sine's own steepest step, with a margin
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2);
        RtaParams a = Blank(); a.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT; d->Set(&a);
        const double f = 100, amp = 0.2; std::vector<float> buf(2 * 480); double prev = 0, worst = 0; bool first = true; long t = 0;
        // steepest step of the loudest the sine gets (+12 dB): 2*pi*f/rate*amp*4
        double steady = 2 * PI * f / 48000 * amp * 4;
        for (int blk = 0; blk < 300; blk++)
        {
            RtaParams p = Blank(); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT;
            int phase = blk / 30;
            if (phase % 2) Band(p, 0, RTA_BELL, 100, 12, 1);
            if (phase % 3 == 1) Band(p, 1, RTA_LOWSHELF, 200, -9, 0.7f);
            if (phase == 5) p.flags = 0; if (phase == 7) p.flags = RTA_ON | RTA_EQ;
            if (phase == 8) { p.preampDb = 6; Band(p, 2, RTA_HIGHSHELF, 4000, 8, 0.7f); }
            if (phase == 2 || phase == 3 || phase == 9) { p.splitHz = phase == 3 ? 400.f : 250.f; p.thrDb = -30; p.ratio = 3; p = Alike(p); }
            // 19.11: the third part comes, moves and goes while the sound plays, with settings of its own
            if (phase == 3 || phase == 4) { p.splitHz = 250; p.split2Hz = phase == 3 ? 3000.f : 5000.f; p.thrDb = -30; p.ratio = 3; p.cb[0].thrDb = -40; p.cb[0].ratio = 4; p.cb[1].thrDb = -20; p.cb[1].ratio = 2; p.cb[1].makeupDb = 3; }
            d->Set(&p);
            for (int i = 0; i < 480; i++, t++) buf[2 * i] = buf[2 * i + 1] = (float)(amp * sin(2 * PI * f * t / 48000));
            d->Process(buf.data(), buf.data(), 480);
            for (int i = 0; i < 480; i++) { if (!first && fabs(buf[2 * i] - prev) > worst) worst = fabs(buf[2 * i] - prev); prev = buf[2 * i]; first = false; }
        }
        Check(worst < steady * 1.5, "moving settings: no clicks", "(biggest step %.5f, a plain sine's %.5f)", worst, steady);
        delete d;
    }

    // a bad number in the input does not stay
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); RtaParams p = Blank(); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT; Band(p, 0, RTA_BELL, 500, 6, 1); d->Set(&p);
        std::vector<float> buf(2 * 480); bool finite = true;
        for (int blk = 0; blk < 50; blk++)
        {
            for (auto& v : buf) v = 0.1f * Noise();
            if (blk == 10) { buf[100] = (float)NAN; buf[201] = (float)INFINITY; }
            d->Process(buf.data(), buf.data(), 480);
            for (float v : buf) if (!(v == v) || fabs(v) > 4) finite = false;
        }
        double g = SineGain(*d, 500, 0.05, 1.0);
        Check(finite && d->faults >= 1 && fabs(g - 6) < 0.1, "a broken sample: silence, then back to work", "(faults %u, gain after %.2f dB)", d->faults, g);
        delete d;
    }

    // bad settings from the file
    {
        RtaParams p; memset(&p, 0xFF, sizeof(p)); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT;
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&p);
        std::vector<float> buf(2 * 480); bool finite = true;
        for (int blk = 0; blk < 20; blk++) { for (auto& v : buf) v = 0.5f * Noise(); d->Process(buf.data(), buf.data(), 480); for (float v : buf) if (!(v == v) || fabs(v) > 1.001f) finite = false; }
        Check(finite, "garbage settings: still sane sound");
        delete d;
    }

    // how much of one core it takes
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); RtaParams p = Blank(); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT; p.thrDb = -24; p.ratio = 4;
        for (int i = 0; i < 10; i++) Band(p, i, RTA_BELL, 50.f * (float)pow(2.0, i), (i % 2 ? 4.f : -4.f), 1.4f);
        d->Set(&p);
        std::vector<float> buf(2 * 480); for (auto& v : buf) v = 0.3f * Noise();
        LARGE_INTEGER f, t0, t1; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
        const int blocks = 6000; // 60 s of sound
        for (int blk = 0; blk < blocks; blk++) { buf[0] = 0.3f * Noise(); d->Process(buf.data(), buf.data(), 480); }
        QueryPerformanceCounter(&t1);
        double sec = (double)(t1.QuadPart - t0.QuadPart) / f.QuadPart;
        CheckCost(sec < 6, "speed: 10 bands + compressor + limiter, stereo 48 kHz", "(%.2f%% of one core)", sec / 60 * 100);
        p.splitHz = 250; p.split2Hz = 4000; p = Alike(p); d->Set(&p);
        QueryPerformanceCounter(&t0);
        for (int blk = 0; blk < blocks; blk++) { buf[0] = 0.3f * Noise(); d->Process(buf.data(), buf.data(), 480); }
        QueryPerformanceCounter(&t1);
        sec = (double)(t1.QuadPart - t0.QuadPart) / f.QuadPart;
        CheckCost(sec < 6, "speed: the same with the compressor in three parts", "(%.2f%% of one core)", sec / 60 * 100);
        delete d;
    }
}

// How clean the limiter is, in numbers (19.11). A limiter is heard when its gain moves with the wave itself: a bass
// note pushed over the ceiling comes out bent (harmonics), and everything else playing with it gets a rough edge
// (the bass note's rhythm printed on a high tone: side tones around it). And a ceiling kept only at the samples lets
// the real wave between them go over it, which the converter then clips.
static double Level(const std::vector<float>& x, int from, int to, double f, double rate)
{
    double re = 0, im = 0; for (int i = from; i < to; i++) { double p = 2 * PI * f * i / rate; re += x[2 * i] * cos(p); im += x[2 * i] * sin(p); }
    return sqrt(re * re + im * im) / (to - from) * 2;
}

// the highest point of the real wave (16 points per sample, a long windowed sinc), left channel, samples [from, to)
static double TruePeak(const std::vector<float>& x, int from, int to)
{
    const int half = 32; double top = 0;
    for (int i = from + half; i < to - half; i++)
        for (int p = 0; p < 16; p++)
        {
            double s = 0, frac = p / 16.0;
            for (int k = -half + 1; k <= half; k++)
            {
                double d = k - frac, w = 0.5 + 0.5 * cos(PI * d / half), sinc = fabs(d) < 1e-9 ? 1 : sin(PI * d) / (PI * d);
                s += x[2 * (i + k)] * sinc * w;
            }
            if (fabs(s) > top) top = fabs(s);
        }
    return top;
}

static void TestLimiterQuality()
{
    printf("--- how clean the limiter is ---\n");
    const double rate = 48000, ceilDb = -1, ceil = pow(10.0, ceilDb / 20);
    RtaParams l = Blank(); l.flags = RTA_ON | RTA_LIMIT; l.ceilDb = (float)ceilDb;
    const int n = 48000 * 4;

    // a 50 Hz note 6 dB over the ceiling: its harmonics 2..12 against the note
    {
        RtDsp* d = new RtDsp; d->Init(rate, 2); d->Set(&l);
        std::vector<float> buf(2 * n); for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(2.0 * ceil * sin(2 * PI * 50 * i / rate));
        d->Process(buf.data(), buf.data(), n);
        double f1 = Level(buf, n - 48000, n, 50, rate), h = 0; for (int k = 2; k <= 12; k++) { double a = Level(buf, n - 48000, n, 50.0 * k, rate); h += a * a; }
        double thd = 20 * log10(sqrt(h) / f1);
        Check(thd < -55, "a bass note 6 dB over the ceiling comes out unbent", "(harmonics %.1f dB under the note, the note at %.2f dB)", -thd, 20 * log10(f1));
        delete d;
    }
    // the same note with a quiet 3 kHz tone beside it: the side tones the limiter prints around the 3 kHz
    {
        RtDsp* d = new RtDsp; d->Init(rate, 2); d->Set(&l);
        std::vector<float> buf(2 * n); for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(1.8 * ceil * sin(2 * PI * 50 * i / rate) + 0.1 * sin(2 * PI * 3000 * i / rate));
        d->Process(buf.data(), buf.data(), n);
        double f1 = Level(buf, n - 48000, n, 3000, rate), s = 0; for (int k = 1; k <= 6; k++) for (int sg = -1; sg <= 1; sg += 2) { double a = Level(buf, n - 48000, n, 3000 + sg * 50.0 * k, rate); s += a * a; }
        double side = 20 * log10(sqrt(s) / f1);
        Check(side < -50, "a tone beside that bass note keeps a clean edge", "(side tones %.1f dB under the tone)", -side);
        delete d;
    }
    // the real wave between the samples: a tone at a quarter of the rate whose samples miss its tops by 3 dB, and loud noise
    {
        RtDsp* d = new RtDsp; d->Init(rate, 2); d->Set(&l);
        std::vector<float> buf(2 * n); for (int i = 0; i < n; i++) buf[2 * i] = buf[2 * i + 1] = (float)(1.5 * sin(2 * PI * 12000 * i / rate + PI / 4));
        d->Process(buf.data(), buf.data(), n);
        double tp = 20 * log10(TruePeak(buf, n - 9600, n));
        double tpn[2];
        for (int white = 0; white < 2; white++)
        {
            RtaParams q = l; if (!white) { q.flags |= RTA_EQ; Band(q, 0, RTA_LOWPASS, 16000, 0, 0.54f); Band(q, 1, RTA_LOWPASS, 16000, 0, 1.31f); }
            RtDsp* e = new RtDsp; e->Init(rate, 2); e->Set(&q);
            std::vector<float> nz(2 * n); for (int i = 0; i < n; i++) { nz[2 * i] = 3 * Noise(); nz[2 * i + 1] = 3 * Noise(); }
            e->Process(nz.data(), nz.data(), n);
            tpn[white] = 20 * log10(TruePeak(nz, n - 24000, n));
            delete e;
        }
        Check(tp < ceilDb + 0.1 && tpn[0] < ceilDb + 0.3, "the wave between the samples stays under the ceiling too", "(ceiling %.1f dB; tone at a quarter of the rate %.2f dB, loud noise up to 16 kHz %.2f dB; white noise up to 24 kHz %.2f dB)", ceilDb, tp, tpn[0], tpn[1]);
        delete d;
    }
    // two seconds of a bass note 6 dB over the ceiling, then quiet sound: the limiter lets go of it
    {
        RtDsp* d = new RtDsp; d->Init(rate, 2); d->Set(&l);
        const int m = 48000 * 10; std::vector<float> buf(2 * m);
        for (int i = 0; i < m; i++) buf[2 * i] = buf[2 * i + 1] = (float)(i < 96000 ? 2.0 * ceil * sin(2 * PI * 50 * i / rate) : 0.1 * sin(2 * PI * 1000 * i / rate));
        d->Process(buf.data(), buf.data(), m);
        double a1 = 20 * log10(Level(buf, 96000 + 48000, 96000 + 48000 + 4800, 1000, rate) / 0.1), a6 = 20 * log10(Level(buf, 96000 + 6 * 48000, 96000 + 6 * 48000 + 4800, 1000, rate) / 0.1);
        Check(a1 > -4.0 && a6 > -0.2, "the limiter lets go after a loud passage", "(the quiet sound is %.2f dB down 1 s after it, %.2f dB after 6 s)", a1, a6);
        delete d;
    }
    // one loud hit in quiet sound: how soon the quiet sound is back
    {
        RtDsp* d = new RtDsp; d->Init(rate, 2); d->Set(&l);
        std::vector<float> buf(2 * n);
        for (int i = 0; i < n; i++) { double a = (i >= 96000 && i < 96240) ? 3.0 : 0.1; buf[2 * i] = buf[2 * i + 1] = (float)(a * sin(2 * PI * 1000 * i / rate)); }
        d->Process(buf.data(), buf.data(), n);
        int lat = d->Latency();
        double a50 = 20 * log10(Level(buf, 96240 + lat + 2400 - 240, 96240 + lat + 2400 + 240, 1000, rate) / 0.1);
        double a200 = 20 * log10(Level(buf, 96240 + lat + 9600 - 240, 96240 + lat + 9600 + 240, 1000, rate) / 0.1);
        Check(a50 > -3.5 && a200 > -1.3, "after one loud hit the quiet sound is back at once", "(%.2f dB after 50 ms, %.2f dB after 200 ms)", a50, a200);
        delete d;
    }
}

// {E4B6C2A1-7F3D-4C58-9B1E-5A2D8C6F0E37}
static const GUID CLSID_RedTuneApo = { 0xe4b6c2a1, 0x7f3d, 0x4c58, { 0x9b, 0x1e, 0x5a, 0x2d, 0x8c, 0x6f, 0x0e, 0x37 } };
static const GUID kTestEndpoint = { 0x11112222, 0x3333, 0x4444, { 1, 2, 3, 4, 5, 6, 7, 8 } };

static void WriteParams(RtaSlot* s, const RtaParams& p)
{
    InterlockedIncrement(&s->seq); s->p = p; InterlockedIncrement(&s->seq);
}

static void TestApo(const wchar_t* dll, const wchar_t* dir)
{
    printf("--- the effect as Windows loads it ---\n");
    HMODULE m = LoadLibraryW(dll);
    if (!m) { Check(false, "RedTuneApo.dll loads", "(error %lu)", GetLastError()); return; }
    typedef HRESULT(__stdcall* GetClass)(REFCLSID, REFIID, void**);
    typedef void(__stdcall* Hook)(const wchar_t*, const GUID*);
    GetClass getClass = (GetClass)GetProcAddress(m, "DllGetClassObject"); Hook hook = (Hook)GetProcAddress(m, "RtaTestHook");
    Check(getClass && hook, "exports");
    if (!getClass || !hook) return;

    // the shared file, as the program makes it
    wchar_t path[MAX_PATH]; swprintf(path, MAX_PATH, L"%s\\audio-test.bin", dir);
    HANDLE file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, CREATE_ALWAYS, 0, NULL);
    HANDLE map = file != INVALID_HANDLE_VALUE ? CreateFileMappingW(file, NULL, PAGE_READWRITE, 0, sizeof(RtaShared), NULL) : NULL;
    RtaShared* sh = map ? (RtaShared*)MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(RtaShared)) : NULL;
    Check(sh != NULL, "shared file", "(%u bytes, slot %u)", (unsigned)sizeof(RtaShared), (unsigned)sizeof(RtaSlot));
    if (!sh) return;
    memset(sh, 0, sizeof(*sh)); sh->magic = RTA_MAGIC; sh->layout = RTA_LAYOUT; sh->slotSize = sizeof(RtaSlot); sh->slotCount = RTA_SLOTS;
    RtaSlot* slot = &sh->slots[3]; memcpy(slot->endpoint, &kTestEndpoint, 16);
    RtaParams p = Blank(); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT; p.thrDb = -18; p.ratio = 3; Band(p, 0, RTA_BELL, 1000, 6, 1); Band(p, 1, RTA_LOWSHELF, 120, 4, 0.7f);
    WriteParams(slot, p);
    hook(path, &kTestEndpoint);

    IClassFactory* cf = NULL; HRESULT hr = getClass(CLSID_RedTuneApo, __uuidof(IClassFactory), (void**)&cf);
    Check(SUCCEEDED(hr) && cf, "class factory", "(0x%08lX)", hr); if (!cf) return;
    IAudioProcessingObject* apo = NULL; hr = cf->CreateInstance(NULL, __uuidof(IAudioProcessingObject), (void**)&apo);
    Check(SUCCEEDED(hr) && apo, "the object", "(0x%08lX)", hr); if (!apo) return;
    IAudioProcessingObjectRT* rt = NULL; IAudioProcessingObjectConfiguration* cfg = NULL; IAudioSystemEffects2* fx = NULL; IUnknown* unk = NULL;
    apo->QueryInterface(__uuidof(IAudioProcessingObjectRT), (void**)&rt); apo->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration), (void**)&cfg);
    apo->QueryInterface(__uuidof(IAudioSystemEffects2), (void**)&fx); apo->QueryInterface(__uuidof(IUnknown), (void**)&unk);
    Check(rt && cfg && fx && unk, "interfaces");
    if (!rt || !cfg) return;
    LPGUID ids = (LPGUID)1; UINT count = 9; if (fx) fx->GetEffectsList(&ids, &count, NULL);
    Check(ids == NULL && count == 0, "effects list is empty");

    APO_REG_PROPERTIES* reg = NULL; hr = apo->GetRegistrationProperties(&reg);
    Check(SUCCEEDED(hr) && reg && reg->clsid == CLSID_RedTuneApo, "registration properties", reg ? "(\"%ls\", flags 0x%X)" : "", reg ? reg->szFriendlyName : L"", reg ? reg->Flags : 0);
    if (reg) CoTaskMemFree(reg);

    APOInitSystemEffects2 init; memset(&init, 0, sizeof(init)); init.APOInit.cbSize = sizeof(init); init.APOInit.clsid = CLSID_RedTuneApo;
    hr = apo->Initialize(sizeof(init), (BYTE*)&init);
    Check(SUCCEEDED(hr), "Initialize", "(0x%08lX)", hr);

    UNCOMPRESSEDAUDIOFORMAT f; memset(&f, 0, sizeof(f)); f.guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT; f.dwSamplesPerFrame = 2; f.dwBytesPerSampleContainer = 4; f.dwValidBitsPerSample = 32; f.fFramesPerSecond = 48000; f.dwChannelMask = 3;
    IAudioMediaType* mt = NULL; hr = CreateAudioMediaTypeFromUncompressedAudioFormat(&f, &mt);
    Check(SUCCEEDED(hr) && mt, "media type", "(0x%08lX)", hr); if (!mt) return;
    IAudioMediaType* sup = NULL; hr = apo->IsInputFormatSupported(NULL, mt, &sup);
    Check(hr == S_OK, "float 48 kHz stereo is accepted", "(0x%08lX)", hr); if (sup) sup->Release(); sup = NULL;
    hr = apo->IsOutputFormatSupported(mt, mt, &sup);
    Check(hr == S_OK, "the same format out", "(0x%08lX)", hr); if (sup) sup->Release(); sup = NULL;
    UNCOMPRESSEDAUDIOFORMAT pcm = f; pcm.guidFormatType = KSDATAFORMAT_SUBTYPE_PCM; pcm.dwBytesPerSampleContainer = 2; pcm.dwValidBitsPerSample = 16;
    IAudioMediaType* mt16 = NULL; CreateAudioMediaTypeFromUncompressedAudioFormat(&pcm, &mt16);
    if (mt16) { hr = apo->IsInputFormatSupported(NULL, mt16, &sup); Check(hr == S_FALSE && sup, "16-bit integer is refused with a float offer", "(0x%08lX)", hr); if (sup) sup->Release(); sup = NULL; mt16->Release(); }
    HNSTIME lat = 0; apo->GetLatency(&lat);
    Check(lat > 140000 && lat < 165000, "latency", "(%.2f ms)", lat / 10000.0);

    const int N = 480;
    float* inBuf = (float*)_aligned_malloc(sizeof(float) * 2 * N, 32); float* outBuf = (float*)_aligned_malloc(sizeof(float) * 2 * N, 32);
    APO_CONNECTION_DESCRIPTOR din, dout; memset(&din, 0, sizeof(din)); memset(&dout, 0, sizeof(dout));
    din.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL; din.pBuffer = (UINT_PTR)inBuf; din.u32MaxFrameCount = N; din.pFormat = mt; din.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
    dout = din; dout.pBuffer = (UINT_PTR)outBuf;
    APO_CONNECTION_DESCRIPTOR* pin = &din; APO_CONNECTION_DESCRIPTOR* pout = &dout;
    hr = cfg->LockForProcess(1, &pin, 1, &pout);
    Check(SUCCEEDED(hr), "LockForProcess", "(0x%08lX)", hr); if (FAILED(hr)) return;

    // the same sound through the object and through the processing called directly: equal to the bit
    RtDsp* ref = new RtDsp; ref->Init(48000, 2); ref->Set(&p);
    APO_CONNECTION_PROPERTY cin, cout; memset(&cin, 0, sizeof(cin)); memset(&cout, 0, sizeof(cout));
    cin.pBuffer = (UINT_PTR)inBuf; cin.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE; cout.pBuffer = (UINT_PTR)outBuf; cout.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE;
    APO_CONNECTION_PROPERTY* pcin = &cin; APO_CONNECTION_PROPERTY* pcout = &cout;
    std::vector<float> want(2 * N); bool same = true; unsigned beat0 = slot->m.beat;
    unsigned csr = _mm_getcsr(); _mm_setcsr(csr | 0x8040);
    for (int blk = 0; blk < 200; blk++)
    {
        for (int i = 0; i < 2 * N; i++) inBuf[i] = 0.4f * Noise();
        ref->Process(inBuf, want.data(), N);
        cin.u32ValidFrameCount = N; cin.u32BufferFlags = BUFFER_VALID; cout.u32BufferFlags = BUFFER_INVALID; cout.u32ValidFrameCount = 0;
        rt->APOProcess(1, &pcin, 1, &pcout);
        if (cout.u32BufferFlags != BUFFER_VALID || cout.u32ValidFrameCount != N || memcmp(outBuf, want.data(), sizeof(float) * 2 * N) != 0) same = false;
    }
    Check(same, "APOProcess = the processing, bit for bit");
    Check(slot->m.beat - beat0 == 200 && slot->m.rate == 48000 && slot->m.channels == 2 && slot->m.pid == GetCurrentProcessId(), "meters come back", "(beat +%u, in %.3f, out %.3f, comp %.1f dB, limiter %.1f dB)", slot->m.beat - beat0, slot->m.inPeak, slot->m.outPeak, slot->m.grDb, slot->m.limDb);
    bool ring = false; for (int i = 0; i < RTA_RING; i++) if (slot->ring[i] != 0) ring = true;
    Check(ring && slot->m.ringPos == 200u * N, "the output for the spectrum is written");

    // new settings while it runs, in place (one buffer for in and out)
    RtaParams q = p; q.flags = RTA_ON | RTA_EQ; Band(q, 0, RTA_BELL, 1000, -12, 2); WriteParams(slot, q); ref->Set(&q);
    same = true; cout.pBuffer = cin.pBuffer;
    for (int blk = 0; blk < 100; blk++)
    {
        for (int i = 0; i < 2 * N; i++) inBuf[i] = 0.4f * Noise();
        ref->Process(inBuf, want.data(), N);
        cin.u32ValidFrameCount = N; cin.u32BufferFlags = BUFFER_VALID;
        rt->APOProcess(1, &pcin, 1, &pcout);
        if (memcmp(inBuf, want.data(), sizeof(float) * 2 * N) != 0) same = false;
    }
    Check(same, "new settings are picked up; in-place buffers");

    // silence coming in: the tail plays out, then the effect reports silence itself
    int validBlocks = 0, silentBlocks = 0;
    for (int blk = 0; blk < 100; blk++)
    {
        for (int i = 0; i < 2 * N; i++) inBuf[i] = 123.f; // rubbish that must not be played
        cin.u32ValidFrameCount = N; cin.u32BufferFlags = BUFFER_SILENT;
        rt->APOProcess(1, &pcin, 1, &pcout);
        if (cout.u32BufferFlags == BUFFER_VALID) { validBlocks++; for (int i = 0; i < 2 * N; i++) if (fabs(inBuf[i]) > 1.f) same = false; } else silentBlocks++;
    }
    Check(same && validBlocks >= 40 && validBlocks <= 60 && silentBlocks > 0, "silence in: the tail, then silence out", "(%d blocks of tail, %d silent)", validBlocks, silentBlocks);
    _mm_setcsr(csr);

    // the device's slot is taken away: the sound goes through untouched
    memset(slot->endpoint, 0, 16);
    same = true; int lat0 = ref->Latency(); std::vector<float> hist;
    for (int blk = 0; blk < 300; blk++)
    {
        for (int i = 0; i < 2 * N; i++) inBuf[i] = 0.4f * Noise();
        hist.insert(hist.end(), inBuf, inBuf + 2 * N);
        cin.u32ValidFrameCount = N; cin.u32BufferFlags = BUFFER_VALID;
        rt->APOProcess(1, &pcin, 1, &pcout);
        if (blk >= 250) for (int i = 0; i < N; i++) { size_t at = (size_t)blk * N + i - lat0; if (inBuf[2 * i] != hist[2 * at]) same = false; }
    }
    Check(same, "slot removed: untouched sound");

    hr = cfg->UnlockForProcess(); Check(SUCCEEDED(hr), "UnlockForProcess");
    delete ref;
    if (unk) unk->Release(); if (fx) fx->Release(); cfg->Release(); rt->Release();
    ULONG left = apo->Release();
    Check(left == 0, "released", "(%lu references left)", left);
    typedef HRESULT(__stdcall* CanUnload)(); CanUnload can = (CanUnload)GetProcAddress(m, "DllCanUnloadNow");
    Check(can && can() == S_OK, "DllCanUnloadNow");
    mt->Release(); _aligned_free(inBuf); _aligned_free(outBuf);
    UnmapViewOfFile(sh); CloseHandle(map); CloseHandle(file); DeleteFileW(path);
}

// 21.11, the noise gate: noise at about -50 dB, a -20 dB tone over it, the noise again. The noise must go down by the
// range, the tone pass as it is, from its very first milliseconds (the detector reads 10 ms ahead), and the gate must
// close again after the hold and the release.
static void TestGate()
{
    RtDsp d; d.Init(48000, 2);
    RtaParams p = Blank(); p.flags = RTA_ON | RTA_GATE; p.gateThrDb = -35; p.gateRangeDb = -40; p.gateReleaseMs = 100;
    d.Set(&p);
    const int R = 48000, n1 = R, n2 = R / 2, n3 = R, N = n1 + n2 + n3, L = d.Latency();
    std::vector<float> in(2 * N), out(2 * N);
    g_rng = 777;
    for (int i = 0; i < N; i++)
    {
        float v = 0.003f * Noise();
        if (i >= n1 && i < n1 + n2) v += (float)(0.1 * sin(2 * PI * 1000.0 * (i - n1) / R));
        in[2 * i] = in[2 * i + 1] = v;
    }
    for (int t = 0; t < N; t += 480) { int k = N - t < 480 ? N - t : 480; memcpy(&out[2 * t], &in[2 * t], k * 2 * sizeof(float)); d.Process(&out[2 * t], &out[2 * t], k); }
    // the level of out against in over [a, b) of the input's time (out is L samples late)
    auto gain = [&](int a, int b) { double si = 0, so = 0; for (int i = a; i < b && i + L < N; i++) { si += (double)in[2 * i] * in[2 * i]; so += (double)out[2 * (i + L)] * out[2 * (i + L)]; } return 10 * log10((so + 1e-20) / (si + 1e-20)); };
    double gNoise = gain(R / 2, n1 - R / 20);
    Check(gNoise < -36 && gNoise > -44, "gate: the noise under the threshold goes down by the range", "%.1f dB (range -40)", gNoise);
    double gTone = gain(n1 + R / 10, n1 + n2 - R / 10);
    Check(fabs(gTone) < 0.3, "gate: the tone over the threshold passes as it is", "%.2f dB", gTone);
    double gOnset = gain(n1, n1 + R / 200);
    Check(gOnset > -1.5, "gate: the first 5 ms of the tone are not cut (the detector reads ahead)", "%.2f dB", gOnset);
    double gHold = gain(n1 + n2 + R / 50, n1 + n2 + R / 10);
    Check(gHold > -6, "gate: right after the tone it is still open (the hold)", "%.1f dB", gHold);
    double gAfter = gain(n1 + n2 + R * 7 / 10, N - R / 20);
    Check(gAfter < -36, "gate: closed again after the hold and the release", "%.1f dB", gAfter);
    // switched off: the noise passes
    RtDsp e; e.Init(48000, 2); RtaParams q = p; q.flags = RTA_ON; e.Set(&q);
    std::vector<float> o2(in.begin(), in.begin() + 2 * n1);
    for (int t = 0; t < n1; t += 480) e.Process(&o2[2 * t], &o2[2 * t], 480);
    double si = 0, so = 0; int L2 = e.Latency();
    for (int i = R / 2; i < n1 - L2; i++) { si += (double)in[2 * i] * in[2 * i]; so += (double)o2[2 * (i + L2)] * o2[2 * (i + L2)]; }
    double gOff = 10 * log10(so / si);
    Check(fabs(gOff) < 0.1, "gate off: the noise passes as it is", "%.2f dB", gOff);
    Check(d.gateMin < -30, "gate: its meter shows what it takes off", "%.1f dB", d.gateMin);
}

// 21.11, the denoiser (RNNoise): steady noise must go down a lot at full strength, pass as it is at strength 0 (only
// late), work at 44.1 kHz through the resampling without running dry, and cost little (it runs in Windows' sound process)
static double NsRun(double rate, int strength, double seconds, int kind, double* cpuShare, uint32_t* under, float* vadOut, uint32_t extra = 0)
{
    RtDsp d; d.Init(rate, 2); d.ns = rnnoise_create(NULL);
    RtaParams p = Blank(); p.flags = RTA_ON | RTA_DENOISE | extra; p.mute = (uint32_t)strength << 8; d.Set(&p);
    int N = (int)(rate * seconds), from = (int)(rate * 1.0);
    std::vector<float> buf(2 * 480);
    double si = 0, so = 0; g_rng = 4242;
    LARGE_INTEGER f0, t0, t1; QueryPerformanceFrequency(&f0); QueryPerformanceCounter(&t0);
    for (int t = 0; t < N; t += 480)
    {
        int k = N - t < 480 ? N - t : 480;
        for (int i = 0; i < k; i++)
        {
            double tt = (t + i) / rate, v;
            if (kind == 0) v = 0.05 * Noise();
            else
            {
                /* something like a voice: a 140 Hz buzz with two formants, in syllables, over quiet noise */
                double env = 0.5 + 0.5 * sin(2 * PI * 3.0 * tt); env *= env;
                double h = 0; for (int n = 1; n < 30; n++) { double fr = 140.0 * n; double a = exp(-pow((fr - 700) / 250, 2)) + 0.6 * exp(-pow((fr - 1200) / 300, 2)) + 0.05; h += a * sin(2 * PI * fr * tt) / n; }
                v = 0.2 * env * h + 0.003 * Noise();
            }
            buf[2 * i] = buf[2 * i + 1] = (float)v;
            if (t + i >= from) si += v * v;
        }
        d.Process(buf.data(), buf.data(), k);
        for (int i = 0; i < k; i++) if (t + i >= from) so += (double)buf[2 * i] * buf[2 * i];
    }
    QueryPerformanceCounter(&t1);
    if (cpuShare) *cpuShare = (double)(t1.QuadPart - t0.QuadPart) / f0.QuadPart / seconds;
    if (under) *under = d.nsUnder;
    if (vadOut) *vadOut = d.vadMeter;
    rnnoise_destroy(d.ns);
    return 10 * log10((so + 1e-20) / (si + 1e-20));
}

static void TestDenoise()
{
    double cpu; uint32_t under; float vad;
    double g48 = NsRun(48000, 100, 4, 0, &cpu, &under, &vad);
    Check(g48 < -15, "denoise: steady noise at full strength goes down a lot (48 kHz)", "%.1f dB", g48);
    CheckCost(cpu < 0.10, "denoise: it costs little", "%.1f %% of one core, the whole effect", cpu * 100);
    double g0 = NsRun(48000, 0, 3, 0, NULL, NULL, NULL);
    Check(fabs(g0) < 0.1, "denoise: at strength 0 the noise passes as it is", "%.2f dB", g0);
    double g44 = NsRun(44100, 100, 4, 0, NULL, &under, NULL);
    Check(g44 < -15, "denoise: works at 44.1 kHz through the resampling", "%.1f dB", g44);
    Check(under == 0, "denoise: at 44.1 kHz the cleaned sound never runs dry", "%u gaps", under);
    double gv = NsRun(48000, 100, 4, 1, NULL, NULL, &vad);
    Check(gv > -12, "denoise: a voice-like sound is kept (not taken for noise)", "%.1f dB, voice probability %.2f", gv, vad);
}

// 27.11, the own network as the denoiser's third model (RTA_OWN): the same demands as RNNoise's
#ifdef RTD_OWN
static void TestOwn()
{
    double cpu; uint32_t under; float vad;
    double g48 = NsRun(48000, 100, 4, 0, &cpu, &under, &vad, RTA_OWN);
    Check(g48 < -15, "own network: steady noise at full strength goes down a lot (48 kHz)", "%.1f dB", g48);
    CheckCost(cpu < 0.10, "own network: it costs little", "%.1f %% of one core, the whole effect", cpu * 100);
    double g0 = NsRun(48000, 0, 3, 0, NULL, NULL, NULL, RTA_OWN);
    Check(fabs(g0) < 0.1, "own network: at strength 0 the noise passes as it is", "%.2f dB", g0);
    double g44 = NsRun(44100, 100, 4, 0, NULL, &under, NULL, RTA_OWN);
    Check(g44 < -15, "own network: works at 44.1 kHz through the resampling", "%.1f dB", g44);
    Check(under == 0, "own network: at 44.1 kHz the cleaned sound never runs dry", "%u gaps", under);
    /* The made-up «voice» of the RNNoise check (a buzz with two formants) is noise to this network, which was trained
       on real speech (-24 dB). A real voice instead: testvoice.f32 beside this file (5 s, 48 kHz, mono floats; a
       held-back speaker of the training's test), alone and in steady noise. Without the file the two checks are skipped. */
    std::string path = __FILE__; path = path.substr(0, path.find_last_of("\\/") + 1) + "testvoice.f32";
    std::vector<float> voice;
    if (FILE* f = fopen(path.c_str(), "rb")) { voice.resize(5 * 48000); voice.resize(fread(voice.data(), 4, voice.size(), f)); fclose(f); }
    if (voice.size() < 3 * 48000) { printf("skip  own network: a real voice is kept (no %s)\n", path.c_str()); return; }
    for (int pass = 0; pass < 2; pass++)
    {
        RtDsp d; d.Init(48000, 2);
        RtaParams p = Blank(); p.flags = RTA_ON | RTA_DENOISE | RTA_OWN; p.mute = 100u << 8; d.Set(&p);
        const double noise = pass ? 0.02 : 0;       /* the second pass: steady noise under the voice */
        std::vector<float> buf(2 * 480); double sv = 0, sm = 0, so = 0; float top = 0; g_rng = 99;
        const int N = (int)voice.size() / 480 * 480, from = 48000;
        for (int t = 0; t < N; t += 480)
        {
            for (int i = 0; i < 480; i++)
            {
                double v = voice[t + i], m = v + noise * Noise();
                buf[2 * i] = buf[2 * i + 1] = (float)m;
                if (t + i >= from) { sv += v * v; sm += m * m; }
            }
            d.Process(buf.data(), buf.data(), 480);
            for (int i = 0; i < 480; i++) if (t + i >= from) so += (double)buf[2 * i] * buf[2 * i];
            if (d.vadMeter > top) top = d.vadMeter;
        }
        const double kept = 10 * log10((so + 1e-20) / (sv + 1e-20)), mixed = 10 * log10((sm + 1e-20) / (sv + 1e-20));
        if (!pass) Check(kept > -1.5 && kept < 0.5 && top > 0.5, "own network: a real voice alone is kept", "%.2f dB against what went in, the most it lets through %.2f", kept, top);
        else Check(kept > -2.5 && kept < mixed * 0.5, "own network: a real voice in steady noise comes out near its own level", "out %.2f dB against the voice alone (the mix was %+.2f dB)", kept, mixed);
    }
}
#endif

// 21.11, the 48 kHz path and the reverb remover.
// Something like a voice at 48 kHz: a 140 Hz buzz with two formants; in words of 150 ms with 300 ms of quiet between
// (words=true), or in syllables without pauses
static double Voice(double tt, bool words)
{
    double env;
    if (words) { double ph = fmod(tt, 0.45); env = ph < 0.15 ? sin(PI * ph / 0.15) : 0; env = sqrt(env); }
    else { env = 0.5 + 0.5 * sin(2 * PI * 3.0 * tt); env *= env; }
    if (env == 0) return 0;
    double h = 0; for (int n = 1; n < 30; n++) { double fr = 140.0 * n; double a = exp(-pow((fr - 700) / 250, 2)) + 0.6 * exp(-pow((fr - 1200) / 300, 2)) + 0.05; h += a * sin(2 * PI * fr * tt) / n; }
    return 0.2 * env * h;
}

// a room: Schroeder's reverb (four combs, two all-passes) with a reverb time of t60 seconds, at rate
struct Room
{
    std::vector<float> c[4], a[2]; int ci[4], ai[2]; double g[4];
    void Init(double rate, double t60)
    {
        const double cd[4] = { 0.0297, 0.0371, 0.0411, 0.0437 }, ad[2] = { 0.005, 0.0017 };
        for (int i = 0; i < 4; i++) { c[i].assign((size_t)(cd[i] * rate), 0.f); ci[i] = 0; g[i] = pow(10.0, -3.0 * cd[i] / t60); }
        for (int i = 0; i < 2; i++) { a[i].assign((size_t)(ad[i] * rate), 0.f); ai[i] = 0; }
    }
    double Run(double x)
    {
        double s = 0;
        for (int i = 0; i < 4; i++) { float& d = c[i][ci[i]]; double o = d; d = (float)(x + g[i] * o); s += o; if (++ci[i] >= (int)c[i].size()) ci[i] = 0; }
        s *= 0.25;
        for (int i = 0; i < 2; i++) { float& d = a[i][ai[i]]; double o = d; double v = s + 0.7 * o; d = (float)v; s = o - 0.7 * v; if (++ai[i] >= (int)a[i].size()) ai[i] = 0; }
        return s;
    }
};

// the whole effect with only the path on: the mono output; fills lat with the path's delay at that rate
static std::vector<float> PathRun(double rate, const std::vector<float>& x, uint32_t flags, int dn, int dr, bool noNet, int* lat, double* t60, double* cpu)
{
    RtDsp d; d.Init(rate, 2); d.ns = rnnoise_create(NULL); d.nsNoNet = noNet;
    RtaParams p = Blank(); p.flags = RTA_ON | flags; p.mute = ((uint32_t)dn << 8) | ((uint32_t)dr << 16); d.Set(&p);
    int N = (int)x.size(); std::vector<float> y(N), buf(2 * 480);
    LARGE_INTEGER f0, t0, t1; QueryPerformanceFrequency(&f0); QueryPerformanceCounter(&t0);
    for (int t = 0; t < N; t += 480)
    {
        int k = N - t < 480 ? N - t : 480;
        for (int i = 0; i < k; i++) buf[2 * i] = buf[2 * i + 1] = x[t + i];
        d.Process(buf.data(), buf.data(), k);
        for (int i = 0; i < k; i++) y[t + i] = buf[2 * i];
    }
    QueryPerformanceCounter(&t1);
    if (cpu) *cpu = (double)(t1.QuadPart - t0.QuadPart) / f0.QuadPart / (N / rate);
    if (lat) *lat = d.nsLat + d.Latency();   /* the path, then the look-aheads of the compressor and the limiter */
    if (t60) *t60 = d.drTn >= 3 ? d.drT60 : 0;
    rnnoise_destroy(d.ns);
    return y;
}

// y against x delayed by lag, from "from" on: what differs, dB under x
static double DiffDb(const std::vector<float>& x, const std::vector<float>& y, int lag, int from)
{
    double e = 0, s = 0;
    for (int i = from; i < (int)y.size(); i++) { double r = x[i - lag]; e += (y[i] - r) * (y[i] - r); s += r * r; }
    return 10 * log10((e + 1e-20) / (s + 1e-20));
}

static int BestLag(const std::vector<float>& x, const std::vector<float>& y, int lo, int hi, int from)
{
    int best = lo; double bv = -1e30;
    for (int L = lo; L <= hi; L++) { double c = 0; for (int i = from; i < (int)y.size(); i++) c += (double)y[i] * x[i - L]; if (c > bv) { bv = c; best = L; } }
    return best;
}

static void TestPath()
{
    /* RNNoise itself: its output is the block from two blocks before */
    {
        DenoiseState* st = rnnoise_create(NULL);
        int N = 48000 * 2; std::vector<float> x(N), y(N); g_rng = 99;
        for (int i = 0; i < N; i++) x[i] = (float)(Voice(i / 48000.0, false) + 0.002 * Noise());
        float a[480], b[480];
        for (int t = 0; t + 480 <= N; t += 480) { for (int i = 0; i < 480; i++) a[i] = x[t + i] * 32768.f; rnnoise_process_frame(st, b, a); for (int i = 0; i < 480; i++) y[t + i] = b[i] / 32768.f; }
        rnnoise_destroy(st);
        int L = BestLag(x, y, 0, 1500, 24000);
        Check(L == 960, "path: RNNoise gives back the block from two blocks before", "%d samples late", L);
    }
    /* the path without the network is a pure delay of nsLat: the untouched sound and the processed one meet exactly */
    {
        int N = 48000 * 2; std::vector<float> x(N); g_rng = 5;
        for (int i = 0; i < N; i++) x[i] = (float)(Voice(i / 48000.0, false) + 0.01 * Noise());
        int lat; std::vector<float> y = PathRun(48000, x, RTA_DENOISE, 50, 0, true, &lat, NULL, NULL);
        double e = DiffDb(x, y, lat, 24000);
        Check(e < -100, "path: at 48 kHz it is exactly its delay, the strength mix in line", "%d samples, the rest %.0f dB", lat, e);
        y = PathRun(48000, x, RTA_DEREVERB, 0, 0, true, &lat, NULL, NULL);
        e = DiffDb(x, y, lat, 24000);
        Check(e < -90, "path: the reverb remover at strength 0 gives the sound back as it was", "the rest %.0f dB", e);
    }
    {
        double r = 44100; int N = (int)r * 2; std::vector<float> x(N);
        for (int i = 0; i < N; i++) x[i] = (float)(0.3 * sin(2 * PI * 300 * i / r) + 0.2 * sin(2 * PI * 1100 * i / r));
        int lat; std::vector<float> y = PathRun(r, x, RTA_DENOISE, 50, 0, true, &lat, NULL, NULL);
        int L = BestLag(x, y, lat - 20, lat + 20, 22050);
        double e = DiffDb(x, y, lat, 22050);
        Check(L == lat && e < -20, "path: at 44.1 kHz the delay it counts on is the real one (the rest: the linear resampling)", "counted %d, real %d, the rest %.0f dB", lat, L, e);
    }
    /* the denoiser at half strength: the voice is not doubled (the old path mixed it 10 ms off) */
    {
        int N = 48000 * 3; std::vector<float> x(N); g_rng = 7;
        for (int i = 0; i < N; i++) x[i] = (float)(Voice(i / 48000.0, false) + 0.003 * Noise());
        int lat; std::vector<float> y = PathRun(48000, x, RTA_DENOISE, 50, 0, false, &lat, NULL, NULL);
        double sxy = 0, sxx = 0, syy = 0; for (int i = 48000; i < N; i++) { double a = x[i - lat]; sxy += a * y[i]; sxx += a * a; syy += (double)y[i] * y[i]; }
        double c = sxy / sqrt(sxx * syy + 1e-30);
        Check(c > 0.97, "path: the denoiser at 50 % keeps the voice whole (in line with the untouched one)", "correlation %.3f", c);
    }
}

// the reverb remover on words in a room: the tail between the words goes down, the words stay
static void DrMeasure(double rate, double t60, bool dry, double* tailDb, double* wordDb, double* heard, double* cpu)
{
    int N = (int)(rate * 9); std::vector<float> x(N); Room room; room.Init(rate, t60 > 0 ? t60 : 0.3); g_rng = 31;
    for (int i = 0; i < N; i++) { double v = Voice(i / rate, true); x[i] = (float)(v + (dry ? 0 : 1.2 * room.Run(v)) + 0.0003 * Noise()); }
    int lat; double t; std::vector<float> y1 = PathRun(rate, x, RTA_DEREVERB, 0, 100, false, &lat, &t, cpu);
    std::vector<float> y0 = PathRun(rate, x, RTA_DEREVERB, 0, 0, false, NULL, NULL, NULL);
    double tw0 = 0, tw1 = 0, ww0 = 0, ww1 = 0;
    for (int i = (int)(3 * rate); i + lat < N; i++)
    {
        double ph = fmod(i / rate, 0.45); int o = i + lat;
        if (ph > 0.02 && ph < 0.13) { ww0 += (double)y0[o] * y0[o]; ww1 += (double)y1[o] * y1[o]; }
        if (ph > 0.23 && ph < 0.43) { tw0 += (double)y0[o] * y0[o]; tw1 += (double)y1[o] * y1[o]; }
    }
    *tailDb = 10 * log10((tw1 + 1e-20) / (tw0 + 1e-20)); *wordDb = 10 * log10((ww1 + 1e-20) / (ww0 + 1e-20)); *heard = t;
}

static void TestDereverb()
{
    double tail, word, heard, cpu;
    DrMeasure(48000, 0.6, false, &tail, &word, &heard, &cpu);
    printf("      room 0.6 s: tail %.1f dB, words %.1f dB, heard %.2f s\n", tail, word, heard);
    Check(tail < -6, "dereverb: the room's tail between words goes down (0.6 s room)", "%.1f dB", tail);
    Check(word > -3, "dereverb: the words themselves stay", "%.1f dB", word);
    Check(heard > 0.35 && heard < 0.95, "dereverb: it hears the room's time", "%.2f s for 0.6 s", heard);
    CheckCost(cpu < 0.05, "dereverb: it costs little", "%.1f %% of one core, the whole effect", cpu * 100);
    DrMeasure(48000, 1.0, false, &tail, &word, &heard, NULL);
    printf("      room 1.0 s: tail %.1f dB, words %.1f dB, heard %.2f s\n", tail, word, heard);
    Check(heard > 0.6, "dereverb: a longer room is heard longer", "%.2f s for 1.0 s", heard);
    DrMeasure(48000, 0, true, &tail, &word, &heard, NULL);
    printf("      dry: words %.1f dB, heard %.2f s\n", word, heard);
    Check(word > -2, "dereverb: a dry voice is left almost alone", "%.1f dB, heard %.2f s", word, heard);
    DrMeasure(44100, 0.6, false, &tail, &word, &heard, NULL);
    Check(tail < -6 && word > -3, "dereverb: works at 44.1 kHz", "tail %.1f dB, words %.1f dB, heard %.2f s", tail, word, heard);
}

#ifdef RTD_DFN
// 21.11, DeepFilterNet as the denoiser's other model: the path with it is still exactly its delay, the model takes steady
// noise down and keeps a voice, its strength is its limit, and on its own thread at the pace of real sound it keeps up
static std::vector<float> DfRun(RtDsp& d, const std::vector<float>& x, int strength, int* lat, uint32_t* misses)
{
    RtaParams p = Blank(); p.flags = RTA_ON | RTA_DENOISE | RTA_DFN; p.mute = (uint32_t)strength << 8; d.Set(&p);
    int N = (int)x.size(); std::vector<float> y(N), buf(2 * 480);
    for (int t = 0; t < N; t += 480)
    {
        int k = N - t < 480 ? N - t : 480;
        for (int i = 0; i < k; i++) buf[2 * i] = buf[2 * i + 1] = x[t + i];
        d.Process(buf.data(), buf.data(), k);
        for (int i = 0; i < k; i++) y[t + i] = buf[2 * i];
    }
    if (lat) *lat = d.nsLat + d.Latency();
    if (misses) *misses = d.dfMiss;
    return y;
}

struct DfThread { RtDsp* d; HANDLE ev; volatile bool stop; };
static DWORD WINAPI DfThreadMain(void* p)
{
    DfThread* t = (DfThread*)p;
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    while (!t->stop) { WaitForSingleObject(t->ev, 100); if (!t->stop) t->d->DfWork(); }
    return 0;
}
static void WakeEv(void* ev) { SetEvent((HANDLE)ev); }

static void TestDfn()
{
    int lat; uint32_t miss;
    /* the path without the model (the worker hands each block back as it is): exactly its delay, at 48 and 44.1 kHz */
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->ns = NULL; d->dfSync = true; d->dfNoNet = true;
        int N = 48000 * 2; std::vector<float> x(N); g_rng = 5;
        for (int i = 0; i < N; i++) x[i] = (float)(Voice(i / 48000.0, false) + 0.01 * Noise());
        std::vector<float> y = DfRun(*d, x, 100, &lat, &miss);
        double e = DiffDb(x, y, lat, 24000);
        Check(e < -100 && miss <= 1, "dfn: the path at 48 kHz is exactly its delay", "%d samples (%.0f ms), the rest %.0f dB, %u missed", lat, lat / 48.0, e, miss);
        delete d;
    }
    {
        double r = 44100; RtDsp* d = new RtDsp; d->Init(r, 2); d->ns = NULL; d->dfSync = true; d->dfNoNet = true;
        int N = (int)r * 2; std::vector<float> x(N);
        g_rng = 21; for (int i = 0; i < N; i++) x[i] = (float)(Voice(i / r, true) + 0.05 * Noise());   /* not periodic: a lag cannot hide */
        std::vector<float> y = DfRun(*d, x, 100, &lat, NULL);
        int L = BestLag(x, y, lat - 20, lat + 20, 22050);
        Check(L == lat, "dfn: at 44.1 kHz the delay it counts on is the real one", "counted %d, real %d", lat, L);
        delete d;
    }
    /* the model itself (made here; in the effect its worker makes it) */
    void* model = rtdf_create(100.f);
    Check(model != NULL, "dfn: the model is made");
    if (!model) return;
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->ns = NULL; d->dfSync = true; d->df = model;
        int N = 48000 * 5; std::vector<float> x(N); g_rng = 11; for (int i = 0; i < N; i++) x[i] = 0.05f * Noise();
        LARGE_INTEGER f0, t0, t1; QueryPerformanceFrequency(&f0); QueryPerformanceCounter(&t0);
        std::vector<float> y = DfRun(*d, x, 100, &lat, &miss);
        QueryPerformanceCounter(&t1); double cpu = (double)(t1.QuadPart - t0.QuadPart) / f0.QuadPart / 5.0;
        double si = 0, so = 0; for (int i = 48000 * 2; i < N; i++) { si += (double)x[i] * x[i]; so += (double)y[i] * y[i]; }
        double g = 10 * log10(so / si);
        Check(g < -20, "dfn: steady noise goes down a lot", "%.1f dB", g);
        CheckCost(cpu < 0.10, "dfn: it costs", "%.1f %% of one core, the whole effect", cpu * 100);
        delete d;
    }
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->ns = NULL; d->dfSync = true; d->df = model;
        int N = 48000 * 5; std::vector<float> x(N); g_rng = 11; for (int i = 0; i < N; i++) x[i] = 0.05f * Noise();
        std::vector<float> y = DfRun(*d, x, 50, NULL, NULL);
        double si = 0, so = 0; for (int i = 48000 * 2; i < N; i++) { si += (double)x[i] * x[i]; so += (double)y[i] * y[i]; }
        double g = 10 * log10(so / si);
        Check(g > -8 && g < -4, "dfn: at 50 % it goes no deeper than 6 dB (its limit)", "%.1f dB", g);
        delete d;
    }
    /* 21.11 (the user: «it cuts my voice off» on a quiet microphone): the sound goes to the model lifted to an ordinary
       level and comes back put down by exactly the same gain. Checked here on the path without the model (exact, since
       a synthetic buzz is not a voice to the model itself); on a real voice the model then keeps the voice at every
       level: RedTune-devtools\denoise-compare (compare.cpp, the level test: 14.7..14.9 dB SI-SDR from -10 to -40 dBFS
       peaks, where without the lift it was -15 at -30 and below). */
    {
        int N = 48000 * 6; std::vector<float> x(N); g_rng = 17;
        for (int i = 0; i < N; i++) x[i] = (float)(0.01 * (Voice(i / 48000.0, true) + 0.02 * Noise()));
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->ns = NULL; d->dfSync = true; d->dfNoNet = true;
        std::vector<float> y = DfRun(*d, x, 100, &lat, NULL);
        double e = DiffDb(x, y, lat, 48000 * 3), lift = 20 * log10(d->dfLift);
        Check(e < -100 && lift > 30 && lift < 45, "dfn: a quiet microphone (40 dB down) is lifted for the model and put back exactly", "lifted +%.0f dB, the rest %.0f dB", lift, e);
        delete d;
    }
    /* on its own thread, at the pace of real sound, in the calls the user's microphone gets (44.1 kHz, 448 frames, so a
       call now and then finishes two blocks): it keeps up */
    {
        const double rr = 44100; const int F = 448;
        RtDsp* d = new RtDsp; d->Init(rr, 2); d->ns = NULL; d->df = model;
        DfThread t = { d, CreateEventW(NULL, FALSE, FALSE, NULL), false };
        d->dfWake = WakeEv; d->dfWakeCtx = t.ev;
        HANDLE th = CreateThread(NULL, 0, DfThreadMain, &t, 0, NULL);
        timeBeginPeriod(1);
        RtaParams p = Blank(); p.flags = RTA_ON | RTA_DENOISE | RTA_DFN; p.mute = 100u << 8; d->Set(&p);
        std::vector<float> buf(2 * F); g_rng = 13; double outE = 0; uint32_t missStart = 0;
        LARGE_INTEGER f0, start, now; QueryPerformanceFrequency(&f0); QueryPerformanceCounter(&start);
        for (int b = 0; b < 400; b++)
        {
            for (int i = 0; i < F; i++) buf[2 * i] = buf[2 * i + 1] = (float)(Voice((b * F + i) / rr, false) + 0.02 * Noise());
            d->Process(buf.data(), buf.data(), F);
            if (b > 50) for (int i = 0; i < F; i++) outE += (double)buf[2 * i] * buf[2 * i];
            if (b == 50) missStart = d->dfMiss;
            /* the next call when real time says so: slept to within 2 ms, then waited out exactly, as Windows' engine keeps
               its period (Sleep alone oversleeps and the calls then come in bunches, which the engine does not do) */
            for (;;)
            {
                QueryPerformanceCounter(&now); double left = (b + 1) * F / rr - (now.QuadPart - start.QuadPart) / (double)f0.QuadPart;
                if (left <= 0) break;
                if (left > 0.002) Sleep(1); else YieldProcessor();
            }
        }
        timeEndPeriod(1);
        t.stop = true; SetEvent(t.ev); WaitForSingleObject(th, 2000); CloseHandle(th); CloseHandle(t.ev);
        Check(d->dfMiss <= 9 && outE > 1e-6,   /* up to 2 %: a block late goes out untouched; a busy machine (a build running beside) costs a few */ "dfn: on its own thread at real pace it keeps up (44.1 kHz, calls of 448 frames)", "%u of ~440 blocks not back in time (%u in the first half second); the model at most %.1f ms a block, a block back at most %.1f ms after it went", d->dfMiss, missStart, d->dfRunMax / 1000, d->dfTurnMax / 1000);
        delete d;
    }
    /* 21.11: the denoiser switched off while DeepFilterNet stays picked: the model is not asked for and does not run */
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); d->ns = NULL; d->dfSync = true; d->df = model;
        int N = 48000; std::vector<float> x(N); g_rng = 3; for (int i = 0; i < N; i++) x[i] = 0.05f * Noise();
        DfRun(*d, x, 100, NULL, NULL); uint32_t seqOn = d->dfSeq;
        RtaParams p = Blank(); p.flags = RTA_ON | RTA_DFN | RTA_DEREVERB; p.mute = (100u << 8) | (0u << 16); d->Set(&p);   /* the path stays (the reverb remover), the denoiser off */
        std::vector<float> buf(2 * 480); for (int b = 0; b < 100; b++) { for (int i = 0; i < 960; i++) buf[i] = 0.05f * Noise(); d->Process(buf.data(), buf.data(), 480); }
        Check(seqOn > 50 && d->dfSeq == seqOn && d->dfWantA.load() == 0, "dfn: with the denoiser off the model is not asked for and runs no block", "blocks run before %u, after %u", seqOn, d->dfSeq - seqOn);
        delete d;
    }
    rtdf_free(model);
}
#endif

// 21.11, the de-esser: a hiss like an "s" is turned down by its range, a vowel is left alone, a quiet microphone gets
// the same, and switched off it changes nothing
static double DsRun(int kind, double scale, bool on, double* meter, float top = 0)
{
    RtDsp* d = new RtDsp; d->Init(48000, 2);
    RtaParams p = Blank(); p.flags = RTA_ON | (on ? RTA_DEESS : 0); p.dsHz = 6500; p.dsThrDb = -15; p.dsRangeDb = -8; p.dsHz2 = top; d->Set(&p);
    int N = 48000 * 2; std::vector<float> buf(2 * 480); double si = 0, so = 0; float n1 = 0, n2 = 0; g_rng = 23;
    std::vector<float> x(N); for (int i = 0; i < N; i++)
    {
        double v;
        if (kind == 0) { float n = Noise(); v = 0.25 * (n - 2 * n1 + n2); n2 = n1; n1 = n; }   /* hiss: the noise's second difference, mostly above 8 kHz */
        else if (kind == 2) v = 0.3 * sin(2 * PI * 7000 * i / 48000.0) * (fmod(i / 48000.0, 0.3) < 0.15 ? 1 : 0);   /* a whistle at 7 kHz, in bursts */
        else if (kind == 3) v = 0.3 * sin(2 * PI * 16000 * i / 48000.0);                                             /* "air" at 16 kHz */
        else v = Voice(i / 48000.0, false);                                                    /* a vowel: harmonics up to ~4 kHz */
        x[i] = (float)(v * scale);
    }
    int lat = d->Latency();
    std::vector<float> y(N);
    for (int t = 0; t < N; t += 480) { for (int i = 0; i < 480; i++) buf[2 * i] = buf[2 * i + 1] = x[t + i]; d->Process(buf.data(), buf.data(), 480); for (int i = 0; i < 480; i++) y[t + i] = buf[2 * i]; }
    for (int i = 48000; i < N; i++) { si += (double)x[i - lat] * x[i - lat]; so += (double)y[i] * y[i]; }
    if (meter) *meter = d->dsMin;
    double diff = 0; for (int i = 48000; i < N; i++) { double e = y[i] - x[i - lat]; diff += e * e; }
    delete d;
    return on ? 10 * log10(so / si) : 10 * log10((diff + 1e-30) / si);
}

static void TestDeEss()
{
    double m1, m2;
    double hiss = DsRun(0, 1, true, &m1), vowel = DsRun(1, 1, true, NULL), quiet = DsRun(0, 0.01, true, &m2), off = DsRun(0, 1, false, NULL);
    Check(hiss < -6 && hiss > -9, "de-esser: a hiss like an \"s\" goes down by its range (8 dB)", "%.1f dB, the meter %.1f dB", hiss, m1);
    Check(fabs(vowel) < 0.5, "de-esser: a vowel is left alone", "%.2f dB", vowel);
    Check(fabs(quiet - hiss) < 1, "de-esser: a quiet microphone (40 dB down) gets the same", "%.1f dB", quiet);
    Check(off < -100, "de-esser: switched off it changes nothing", "the rest %.0f dB", off);
    /* 21.11, the band's top edge at 10 kHz: a 7 kHz burst in the band is turned down, a 16 kHz "air" tone above it is not */
    double inBand = DsRun(2, 1, true, NULL, 10000), air = DsRun(3, 1, true, NULL, 10000), airOpen = DsRun(3, 1, true, NULL, 0);
    Check(inBand < -3 && fabs(air) < 0.5 && airOpen < -6, "de-esser: with a top edge at 10 kHz the band is turned down and what is above it left alone",
          "7 kHz in the band %.1f dB, 16 kHz above it %.2f dB (without the edge %.1f dB)", inBand, air, airOpen);
}

// 21.11, the compressor's characters and the colour after it
static RtaParams CharParams(int chr, float thr, float ratio, float att, float rel)
{
    RtaParams p = Blank(); p.flags = RTA_ON | RTA_COMP; p.thrDb = thr; p.ratio = ratio; p.attackMs = att; p.releaseMs = rel; p.kneeDb = 0; p.compChar = (float)chr;
    return Alike(p);
}
/* a 1 kHz tone of amp for n samples through d, the envelope (dB) after each block of 48 */
static void ToneEnv(RtDsp& d, double amp, int n, std::vector<double>* envs)
{
    std::vector<float> buf(2 * 48);
    static long long ph = 0;
    for (int t = 0; t < n; t += 48)
    {
        for (int i = 0; i < 48; i++) { double v = amp * sin(2 * PI * 1000 * (ph++) / 48000.0); buf[2 * i] = buf[2 * i + 1] = (float)v; }
        d.Process(buf.data(), buf.data(), 48);
        if (envs) envs->push_back(d.env);
    }
}
static void TestCharacter()
{
    /* FET against clean, the same 10 ms attack: the time to reach 63 % of the reduction */
    double t63[2];
    for (int k = 0; k < 2; k++)
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); RtaParams p = CharParams(k ? 1 : 0, -30, 4, 10, 200); d->Set(&p);
        ToneEnv(*d, 0.001, 48000, NULL);
        std::vector<double> e; ToneEnv(*d, 0.5, 48000, &e);
        double fin = e.back(); int i = 0; while (i < (int)e.size() && e[i] > 0.63 * fin) i++;
        t63[k] = i * 1.0; delete d;   /* in ms (a block of 48 is 1 ms) */
    }
    Check(t63[1] < t63[0] * 0.6, "character FET: the attack is faster than the clean one's", "63 %% of the reduction in %.0f ms (clean %.0f ms)", t63[1], t63[0]);

    /* optical: after a long loud stretch half of the reduction comes back fast, the rest slowly, and slower after a
       longer stretch (its memory) */
    double back100[2], left1s[2];
    for (int k = 0; k < 2; k++)
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); RtaParams p = CharParams(2, -30, 4, 10, 200); d->Set(&p);
        ToneEnv(*d, 0.001, 24000, NULL);
        ToneEnv(*d, 0.5, k ? 48000 * 4 : 48000 / 2, NULL);
        double start = d->env; std::vector<double> e; ToneEnv(*d, 0.001, 48000 * 2, &e);
        back100[k] = 1 - e[100] / start; left1s[k] = e[1000] / start; delete d;
    }
    Check(back100[1] > 0.35 && left1s[1] > 0.1 && left1s[1] > left1s[0], "character optical: a release in two stages, with a memory",
          "after 4 s of squeezing: %.0f %% back in 100 ms, %.0f %% still on after 1 s (after 0.5 s of squeezing: %.0f %%)", back100[1] * 100, left1s[1] * 100, left1s[0] * 100);

    /* vari-mu: the ratio grows with the level */
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); RtaParams p = CharParams(3, -30, 4, 5, 100); d->Set(&p);
        double gr[3]; const double lv[3] = { -24, -18, 0 };   /* dB of the tone's peak: 6, 12 and 30 dB over the threshold */
        for (int k = 0; k < 3; k++) { ToneEnv(*d, pow(10, lv[k] / 20), 48000, NULL); gr[k] = d->env; }
        double rLow = (lv[1] - lv[0]) / ((lv[1] + gr[1]) - (lv[0] + gr[0])), rHigh = (lv[2] - lv[1]) / ((lv[2] + gr[2]) - (lv[1] + gr[1]));
        Check(rLow < 2.5 && rHigh > rLow + 1 && rHigh < 4.5, "character vari-mu: the ratio grows with the level", "%.1f:1 just over the threshold, %.1f:1 high over it (4:1 set)", rLow, rHigh);
        delete d;
    }
}

/* harmonics of a tone through the colour: the fundamental and the 2nd and 3rd, dB of the fundamental */
static void ColorRun(int type, double f, double amp, double* h2, double* h3, double* fund, double probe = 0, double* atProbe = NULL)
{
    RtDsp* d = new RtDsp; d->Init(48000, 2);
    RtaParams p = Blank(); p.flags = RTA_ON | RTA_COMP; p.ratio = 1; p.colorType = (float)type; p.colorAmt = 100; d->Set(&p);
    int N = 48000 * 2; std::vector<float> buf(2 * 480), y(N);
    for (int t = 0; t < N; t += 480) { for (int i = 0; i < 480; i++) { double v = amp * sin(2 * PI * f * (t + i) / 48000.0); buf[2 * i] = buf[2 * i + 1] = (float)v; } d->Process(buf.data(), buf.data(), 480); for (int i = 0; i < 480; i++) y[t + i] = buf[2 * i]; }
    auto Lv = [&](double fr) { double re = 0, im = 0; for (int i = N / 2; i < N; i++) { double w = 0.5 - 0.5 * cos(2 * PI * (i - N / 2) / (N / 2)); re += y[i] * w * cos(2 * PI * fr * i / 48000.0); im += y[i] * w * sin(2 * PI * fr * i / 48000.0); } return 20 * log10(sqrt(re * re + im * im) + 1e-12); };
    double a1 = Lv(f); *fund = a1; *h2 = Lv(2 * f) - a1; *h3 = Lv(3 * f) - a1;
    if (atProbe) *atProbe = Lv(probe) - a1;
    delete d;
}
static void TestColor()
{
    double h2, h3, a, al;
    ColorRun(1, 1000, 0.5, &h2, &h3, &a);
    Check(h2 > -45 && h2 > h3 + 6, "colour tube: the 2nd harmonic leads", "2nd %.0f dB, 3rd %.0f dB", h2, h3);
    ColorRun(2, 1000, 0.5, &h2, &h3, &a);
    Check(h2 > -45 && h3 > -45, "colour transistor: the 2nd and the 3rd", "2nd %.0f dB, 3rd %.0f dB", h2, h3);
    ColorRun(3, 1000, 0.5, &h2, &h3, &a);
    Check(h3 > -45 && h3 > h2 + 10, "colour tape: the 3rd, symmetric", "3rd %.0f dB, 2nd %.0f dB", h3, h2);
    double lowA, hiA, x2, x3;
    ColorRun(3, 80, 0.01, &x2, &x3, &lowA); ColorRun(3, 1000, 0.01, &x2, &x3, &hiA);
    Check(lowA - hiA > 1, "colour tape: the bump at 80 Hz", "+%.1f dB at 80 Hz against 1 kHz, quiet", lowA - hiA);
    double t1k2, t1k3, t60_2, t60_3;
    ColorRun(4, 1000, 0.5, &t1k2, &t1k3, &a); ColorRun(4, 60, 0.5, &t60_2, &t60_3, &a);
    Check(t60_3 > -40 && t1k3 < t60_3 - 15, "colour transformer: the bass saturates, the rest stays clean", "60 Hz: 3rd %.0f dB; 1 kHz: 3rd %.0f dB", t60_3, t1k3);
    /* 15 kHz through the transistor: its 3rd (45 kHz) must not fold back to 3 kHz */
    ColorRun(2, 15000, 0.7, &h2, &h3, &a, 3000, &al);
    Check(al < -60, "colour: no folding back (15 kHz through the transistor)", "at 3 kHz %.0f dB", al);
    /* strength 0: the sound as it was, only later */
    {
        RtDsp* d = new RtDsp; d->Init(48000, 2); RtaParams p = Blank(); p.flags = RTA_ON | RTA_COMP; p.ratio = 1; p.colorType = 1; p.colorAmt = 0; d->Set(&p);
        int N = 48000; std::vector<float> x(N), y(N), buf(2 * 480); g_rng = 9; for (int i = 0; i < N; i++) x[i] = 0.3f * Noise();
        for (int t = 0; t < N; t += 480) { for (int i = 0; i < 480; i++) buf[2 * i] = buf[2 * i + 1] = x[t + i]; d->Process(buf.data(), buf.data(), 480); for (int i = 0; i < 480; i++) y[t + i] = buf[2 * i]; }
        double e = DiffDb(x, y, d->Latency(), 24000);
        Check(e < -100, "colour at 0 %: the sound as it was, only its delay later", "the rest %.0f dB (delay %d samples)", e, d->Latency());
        delete d;
    }
}

// 21.11, the second compressor (first in the chain, in parts): alone it follows its formula, before the main one the main
// one squeezes what it lets out, its parts squeeze apart, its mix is a parallel compression, and off it changes nothing
static RtaParams C2Params(bool first, bool second, float mix, float thr2, float split, float ratioTop)
{
    RtaParams p = Blank(); p.flags = RTA_ON | (first ? RTA_COMP : 0) | (second ? RTA_COMP2 : 0);
    p.thrDb = -30; p.ratio = 4; p.attackMs = 1; p.releaseMs = 300; p.kneeDb = 0; p = Alike(p);
    for (int i = 0; i < 3; i++) { RtaCompBand q = { thr2, 3, 1, 300, 0, 0 }; p.c2b[i] = q; }
    p.c2b[2].ratio = ratioTop; p.c2Split = split; p.c2Split2 = 0; p.c2Mix = mix; p.c2Char = 0;
    return p;
}
static double C2Run(const RtaParams& p, double f, double inDb, double* gr0)
{
    RtDsp* d = new RtDsp; d->Init(48000, 2); d->Set(&p);
    double outPeak; SineGain(*d, f, pow(10.0, inDb / 20.0), 3.0, &outPeak);
    if (gr0) *gr0 = d->c2E[0];
    delete d; return outPeak;
}
static void TestComp2()
{
    double worst = 0;
    for (int in = -40; in <= 0; in += 5)
    {
        double out = C2Run(C2Params(false, true, 100, -20, 0, 3), 1000, in, NULL), want = in + rtd_want(in, -20, 1.0 / 3 - 1, 0, 0);
        if (fabs(out - want) > worst) worst = fabs(out - want);
    }
    Check(worst < 0.6, "second compressor alone: its curve", "(worst %.2f dB)", worst);
    /* before the main one: 0 dB in, the second (-20, 3:1) lets out -13.3; the main one (-30, 4:1) then takes 3/4 of 16.7 dB */
    double sec = C2Run(C2Params(false, true, 100, -20, 0, 3), 1000, 0, NULL), both = C2Run(C2Params(true, true, 100, -20, 0, 3), 1000, 0, NULL);
    double wantBoth = sec + rtd_want(sec, -30, 1.0 / 4 - 1, 0, 0);
    Check(fabs(both - wantBoth) < 0.6, "second compressor first: the main one squeezes what it lets out", "second alone %.1f dB, both %.1f dB (%.1f expected)", sec, both, wantBoth);
    /* in two parts at 500 Hz, the top at 1:1: the bass is squeezed, the top is not */
    double bass = C2Run(C2Params(false, true, 100, -20, 500, 1), 100, 0, NULL), top = C2Run(C2Params(false, true, 100, -20, 500, 1), 4000, 0, NULL);
    double wantBass = rtd_want(0, -20, 1.0 / 3 - 1, 0, 0);
    Check(fabs(bass - wantBass) < 1 && fabs(top) < 0.3, "second compressor in parts: each part by its own settings", "100 Hz %.1f dB (%.1f expected), 4 kHz %.2f dB (1:1)", bass, wantBass, top);
    double full = C2Run(C2Params(false, true, 100, -20, 0, 3), 1000, 0, NULL), half = C2Run(C2Params(false, true, 50, -20, 0, 3), 1000, 0, NULL);
    double gl = pow(10.0, full / 20.0), wantHalf = 20 * log10(0.5 + 0.5 * gl);
    Check(fabs(half - wantHalf) < 0.6, "second compressor mix 50 %: the plain sound beside (parallel)", "%.1f dB (%.1f expected; all squeezed %.1f)", half, wantHalf, full);
    double off = C2Run(C2Params(false, false, 100, -20, 0, 3), 1000, -6, NULL);
    Check(fabs(off + 6) < 0.05, "second compressor off: nothing changes", "%.2f dB", off);
}

// 21.11, what the chains cost (apotest --cost): each one fed 30 s of a voice with noise at the rate and in the calls the
// real device gets, the best of three runs, as a share of one core (the model of DeepFilterNet run on the same thread,
// so its share is in the sum; in the effect it runs on its own thread)
static double CostOf(RtaParams p, double rate, int frames, bool dfn)
{
    double best = 1e9;
    for (int run = 0; run < 3; run++)
    {
        RtDsp* d = new RtDsp; d->Init(rate, 2);
#ifdef RTD_DFN
        static void* model = NULL;
        if (dfn) { if (!model) model = rtdf_create(100.f); d->dfSync = true; d->df = model; }
#endif
        d->Set(&p);
        const int secs = 30, total = (int)(rate * secs) / frames * frames; std::vector<float> buf(2 * frames), x(total); g_rng = 7;
        for (int i = 0; i < total; i++) x[i] = (float)(0.3 * Voice(i / rate, true) + 0.01 * Noise());   /* made before the clock starts */
        LARGE_INTEGER f0, t0, t1; QueryPerformanceFrequency(&f0); QueryPerformanceCounter(&t0);
        for (int t = 0; t < total; t += frames)
        {
            for (int i = 0; i < frames; i++) buf[2 * i] = buf[2 * i + 1] = x[t + i];
            d->Process(buf.data(), buf.data(), frames);
        }
        QueryPerformanceCounter(&t1);
        double c = (double)(t1.QuadPart - t0.QuadPart) / f0.QuadPart / secs;
        if (c < best) best = c;
        delete d;
    }
    return best;
}
static void CostRun()
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    auto eq = [](RtaParams& p) {
        Band(p, 0, RTA_HIGHPASS | (3u << 8), 80, 0, 0.707f);   /* a cut at 48 dB/oct */
        for (int i = 1; i < 12; i++) Band(p, i, RTA_BELL | (i == 6 ? (2u << 8) : 0), 50.f * (float)pow(1.75, i), (i % 2 ? 3.f : -3.f), 1.4f);
    };
    auto comp3 = [](RtaParams& p, float chr, float col) {
        p.thrDb = -24; p.ratio = 4; p.attackMs = 10; p.releaseMs = 150; p.kneeDb = 6; p = Alike(p);
        p.splitHz = 250; p.split2Hz = 4000; p.compChar = chr; p.colorType = col; p.colorAmt = col > 0 ? 50.f : 0.f; p.mixPct = 70;
    };
    struct Row { const char* name; RtaParams p; double rate; int frames; bool dfn; };
    std::vector<Row> rows;
    RtaParams p;
    /* the output (headphones / speakers): 48 kHz, 480 frames */
    p = Blank(); p.flags = 0; rows.push_back({ "output: the effect attached, switched off", p, 48000, 480, false });
    p = Blank(); p.flags = RTA_ON; rows.push_back({ "output: on, every effect off", p, 48000, 480, false });
    p = Blank(); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT; eq(p); comp3(p, 0, 0); rows.push_back({ "output: EQ 12 + compressor in 3 parts + limiter", p, 48000, 480, false });
    p = Blank(); p.flags = RTA_ON | RTA_EQ | RTA_COMP | RTA_LIMIT; eq(p); comp3(p, 3, 3); rows.push_back({ "output: the same, tube character + tape colour", p, 48000, 480, false });
    /* the microphone: 44.1 kHz, 448 frames (as the user's) */
    auto mic = [&](uint32_t extra) {
        RtaParams q = Blank(); q.flags = RTA_ON | RTA_GATE | RTA_DENOISE | RTA_DEREVERB | RTA_EQ | RTA_DEESS | RTA_COMP2 | RTA_COMP | RTA_LIMIT | extra;
        q.mute = (100u << 8) | (60u << 16); q.gateThrDb = -50; q.gateRangeDb = -20; q.gateReleaseMs = 150;
        eq(q); q.dsHz = 5500; q.dsHz2 = 10000; q.dsThrDb = -20; q.dsRangeDb = -8; comp3(q, 3, 3);
        for (int i = 0; i < 3; i++) { RtaCompBand b = { -20, 3, 5, 200, 6, 0 }; q.c2b[i] = b; }
        q.c2Split = 250; q.c2Split2 = 4000; q.c2Mix = 100; q.c2Char = 1;
        return q;
    };
    rows.push_back({ "mic: everything, RNNoise", mic(0), 44100, 448, false });
#ifdef RTD_OWN
    rows.push_back({ "mic: everything, the own network", mic(RTA_OWN), 44100, 448, false });
#endif
#ifdef RTD_DFN
    rows.push_back({ "mic: everything, DeepFilterNet 3", mic(RTA_DFN), 44100, 448, true });
#endif
    for (auto& r : rows) printf("  %-52s %6.2f %% of one core\n", r.name, CostOf(r.p, r.rate, r.frames, r.dfn) * 100);
}

int wmain(int argc, wchar_t** argv)
{
    _mm_setcsr(_mm_getcsr() | 0x8040);
    if (argc >= 2 && wcscmp(argv[1], L"--cost") == 0) { CostRun(); return 0; }
    TestDsp();
    TestLimiterQuality();
    TestGate();
    TestDenoise();
#ifdef RTD_OWN
    TestOwn();
#else
    printf("skip  the own network: built without its weights (rtnet_weights.h)\n");
#endif
    TestPath();
    TestDereverb();
    TestDeEss();
    TestCharacter();
    TestComp2();
    TestColor();
#ifdef RTD_DFN
    TestDfn();
#else
    printf("      (DeepFilterNet not built in: its checks skipped)\n");
#endif
    if (argc >= 3) { CoInitializeEx(NULL, COINIT_MULTITHREADED); TestApo(argv[1], argv[2]); }
    printf(g_fail ? "\n%d CHECK(S) FAILED\n" : "\nall checks passed\n", g_fail);
    return g_fail;
}
