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

static int g_fail = 0;
static void Check(bool ok, const char* what, const char* fmt = "", ...)
{
    char more[256] = ""; va_list va; va_start(va, fmt); vsnprintf(more, sizeof(more), fmt, va); va_end(va);
    printf("%s  %s  %s\n", ok ? "ok  " : "FAIL", what, more);
    if (!ok) g_fail++;
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
        double A = pow(10.0, b.gainDb / 40.0), g = tan(PI * b.freq / rate), k = 1.0 / b.q, m0 = 1, m1 = 0, m2 = 0;
        switch (b.type)
        {
        case RTA_BELL: k = 1.0 / (b.q * A); m1 = k * (A * A - 1); break;
        case RTA_LOWSHELF: g /= sqrt(A); m1 = k * (A - 1); m2 = A * A - 1; break;
        case RTA_HIGHSHELF: g *= sqrt(A); m0 = A * A; m1 = k * (1 - A) * A; m2 = 1 - A * A; break;
        case RTA_HIGHPASS: m1 = -k; m2 = -1; break;
        case RTA_LOWPASS: m0 = 0; m2 = 1; break;
        case RTA_NOTCH: m1 = -k; break;
        }
        std::complex<double> s(0, tan(PI * f / rate) / g), d = s * s + k * s + 1.0;
        h *= m0 + (m1 * s + m2) / d;
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
        Check(sec < 6, "speed: 10 bands + compressor + limiter, stereo 48 kHz", "(%.2f%% of one core)", sec / 60 * 100);
        p.splitHz = 250; p.split2Hz = 4000; p = Alike(p); d->Set(&p);
        QueryPerformanceCounter(&t0);
        for (int blk = 0; blk < blocks; blk++) { buf[0] = 0.3f * Noise(); d->Process(buf.data(), buf.data(), 480); }
        QueryPerformanceCounter(&t1);
        sec = (double)(t1.QuadPart - t0.QuadPart) / f.QuadPart;
        Check(sec < 6, "speed: the same with the compressor in three parts", "(%.2f%% of one core)", sec / 60 * 100);
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

int wmain(int argc, wchar_t** argv)
{
    _mm_setcsr(_mm_getcsr() | 0x8040);
    TestDsp();
    TestLimiterQuality();
    if (argc >= 3) { CoInitializeEx(NULL, COINIT_MULTITHREADED); TestApo(argv[1], argv[2]); }
    printf(g_fail ? "\n%d CHECK(S) FAILED\n" : "\nall checks passed\n", g_fail);
    return g_fail;
}
