// Red Tune's own denoiser: a small recurrent network trained by us (RedTune-devtools\denoiser), run by this code.
// 27.11.2026. Plain C++ with nothing of Windows in it; nothing here allocates, locks or waits, so it runs on the
// sound thread. The weights come from rtnet_weights.h, written by the training folder's export.py.
//
// How it works, one block (480 samples, 10 ms at 48 kHz) at a time:
//   - the block and the one before make a frame of 960 under a square-root Hann window, padded to 1024 and transformed
//     (the reverb remover's framing in rtdsp.h: with all gains at 1 the sound comes out exactly as it went in);
//   - the frame's power is gathered into RTN_NB bands (triangles between the centres RTN_CENTRE: two bins wide up to
//     about 1.7 kHz, on the ear's scale above), each band's level in dB is measured against its own running mean of
//     one second: the picture is the same on a quiet microphone and on a loud one;
//   - the network sees three frames of that picture (the one before, the one in question, the one after: 10 ms of
//     look-ahead), remembers what the noise has been like in two recurrent layers and answers with a gain 0..1 for
//     every band of the frame in question;
//   - the gains slide linearly from one band's centre to the next across the bins of that frame's spectrum, which is
//     transformed back, windowed again and overlap-added.
// What comes out of Process() is the block from two calls before (one for the overlap, one for the look-ahead): the
// same delay RNNoise has in rtdsp.h.
// The PyTorch side (rtn\dsp.py, rtn\model.py) does the same sums; RedTune-devtools\denoiser\cpp\netcheck.cpp runs both on
// one recording and compares them sample for sample.
#pragma once
#include <math.h>
#include <stdint.h>
#include <string.h>
#include <immintrin.h>
#include "rtnet_weights.h"

#define RTN_BLOCK 480
#define RTN_WIN   960
#define RTN_FFT   1024
#define RTN_BINS  513

struct RtNet
{
    /* tables, made by Init (off the sound thread) */
    float win[RTN_WIN], cosT[RTN_FFT / 2], sinT[RTN_FFT / 2]; uint16_t rev[RTN_FFT];
    /* the sound: the block before, the half frame waiting for its other half, the spectrum waiting for its gains */
    float prev[RTN_BLOCK], olap[RTN_BLOCK], heldRe[RTN_BINS], heldIm[RTN_BINS];
    float re[RTN_FFT], im[RTN_FFT];
    /* the picture: three frames of it (oldest first), the bands' running means */
    float feat[3 * RTN_NB], mean[RTN_NB];
    /* the network's memory */
    float h1[RTN_H], h2[RTN_H];
    float gain[RTN_NB];             /* the last answer (for meters and the bench) */
    uint32_t frames;

    void Init()
    {
        memset(this, 0, sizeof(*this));
        const double pi = 3.14159265358979323846;
        for (int j = 0; j < RTN_WIN; j++) win[j] = (float)sqrt(0.5 - 0.5 * cos(2 * pi * j / RTN_WIN));
        for (int j = 0; j < RTN_FFT / 2; j++) { cosT[j] = (float)cos(2 * pi * j / RTN_FFT); sinT[j] = (float)sin(2 * pi * j / RTN_FFT); }
        for (int i = 0; i < RTN_FFT; i++) { int r = 0; for (int b = 0; b < 10; b++) if (i & (1 << b)) r |= 1 << (9 - b); rev[i] = (uint16_t)r; }
    }

    /* the sound starts anew (the tables stay) */
    void Reset()
    {
        memset(prev, 0, sizeof(prev)); memset(olap, 0, sizeof(olap)); memset(heldRe, 0, sizeof(heldRe)); memset(heldIm, 0, sizeof(heldIm));
        memset(feat, 0, sizeof(feat)); memset(mean, 0, sizeof(mean)); memset(h1, 0, sizeof(h1)); memset(h2, 0, sizeof(h2));
        memset(gain, 0, sizeof(gain)); frames = 0;
    }

    void Fft(float* r, float* i, bool inverse) const
    {
        for (int a = 0; a < RTN_FFT; a++) { int b = rev[a]; if (a < b) { float t = r[a]; r[a] = r[b]; r[b] = t; t = i[a]; i[a] = i[b]; i[b] = t; } }
        for (int len = 2; len <= RTN_FFT; len <<= 1)
        {
            int half = len >> 1, step = RTN_FFT / len;
            for (int a = 0; a < RTN_FFT; a += len)
                for (int j = 0; j < half; j++)
                {
                    float wr = cosT[j * step], wi = inverse ? sinT[j * step] : -sinT[j * step];
                    float* ar = r + a + j; float* ai = i + a + j; float* br = ar + half; float* bi = ai + half;
                    float xr = *br * wr - *bi * wi, xi = *br * wi + *bi * wr;
                    *br = *ar - xr; *bi = *ai - xi; *ar += xr; *ai += xi;
                }
        }
    }

    /* y = W x + b, W row by row (rows x cols). Eight products at a time (AVX2 with FMA: every Ryzen has both). */
    static void MatVec(const float* w, const float* b, const float* x, int rows, int cols, float* y)
    {
        for (int r = 0; r < rows; r++, w += cols)
        {
            __m256 s0 = _mm256_setzero_ps(), s1 = _mm256_setzero_ps();
            int c = 0;
            for (; c + 16 <= cols; c += 16)
            {
                s0 = _mm256_fmadd_ps(_mm256_loadu_ps(w + c), _mm256_loadu_ps(x + c), s0);
                s1 = _mm256_fmadd_ps(_mm256_loadu_ps(w + c + 8), _mm256_loadu_ps(x + c + 8), s1);
            }
            for (; c + 8 <= cols; c += 8) s0 = _mm256_fmadd_ps(_mm256_loadu_ps(w + c), _mm256_loadu_ps(x + c), s0);
            s0 = _mm256_add_ps(s0, s1);
            __m128 q = _mm_add_ps(_mm256_castps256_ps128(s0), _mm256_extractf128_ps(s0, 1));
            q = _mm_add_ps(q, _mm_movehl_ps(q, q)); q = _mm_add_ss(q, _mm_shuffle_ps(q, q, 1));
            float s = _mm_cvtss_f32(q);
            for (; c < cols; c++) s += w[c] * x[c];
            y[r] = s + b[r];
        }
        _mm256_zeroupper();
    }

    static inline float Sig(float v) { return 1.0f / (1.0f + expf(-v)); }

    /* one step of a recurrent layer (a GRU as PyTorch counts it: reset, update, new) */
    static void Gru(const float* wi, const float* wh, const float* bi, const float* bh, const float* x, int nx, float* h)
    {
        float a[3 * RTN_H], b[3 * RTN_H];
        MatVec(wi, bi, x, 3 * RTN_H, nx, a);
        MatVec(wh, bh, h, 3 * RTN_H, RTN_H, b);
        for (int i = 0; i < RTN_H; i++)
        {
            float r = Sig(a[i] + b[i]), z = Sig(a[RTN_H + i] + b[RTN_H + i]);
            float n = tanhf(a[2 * RTN_H + i] + r * b[2 * RTN_H + i]);
            h[i] = (1.0f - z) * n + z * h[i];
        }
    }

    /* one block in, the block from two calls before out (in and out may be the same buffer) */
    void Process(const float* in, float* out)
    {
        /* the frame and its spectrum */
        for (int j = 0; j < RTN_BLOCK; j++) { re[j] = prev[j] * win[j]; re[RTN_BLOCK + j] = in[j] * win[RTN_BLOCK + j]; }
        for (int j = RTN_WIN; j < RTN_FFT; j++) re[j] = 0;
        memset(im, 0, sizeof(im));
        memcpy(prev, in, sizeof(prev));
        Fft(re, im, false);

        /* the picture of this frame, behind the two before it */
        memmove(feat, feat + RTN_NB, 2 * RTN_NB * sizeof(float));
        float* x = feat + 2 * RTN_NB;
        {
            double e[RTN_NB]; for (int b = 0; b < RTN_NB; b++) e[b] = 0;
            for (int b = 0; b + 1 < RTN_NB; b++)
            {
                const int lo = RTN_CENTRE[b], hi = RTN_CENTRE[b + 1];
                for (int k = lo; k < hi; k++)
                {
                    const float t = (float)((double)(k - lo) / (hi - lo));
                    const double p = (double)re[k] * re[k] + (double)im[k] * im[k];
                    e[b] += (1.0f - t) * p; e[b + 1] += t * p;
                }
            }
            e[RTN_NB - 1] += (double)re[RTN_BINS - 1] * re[RTN_BINS - 1] + (double)im[RTN_BINS - 1] * im[RTN_BINS - 1];
            for (int b = 0; b < RTN_NB; b++)
            {
                float db = (float)(10.0 * log10(e[b] + 1e-20)); if (db < RTN_FLOOR_DB) db = RTN_FLOOR_DB;
                if (!frames) mean[b] = db;
                mean[b] = RTN_MEAN_KEEP * mean[b] + (1.0f - RTN_MEAN_KEEP) * db;
                x[b] = (db - mean[b]) * (1.0f / 40.0f);
            }
        }

        if (frames)
        {
            /* the network's answer for the frame before this one */
            float cat[3 * RTN_H];
            MatVec(RTN_IN_W, RTN_IN_B, feat, RTN_H, 3 * RTN_NB, cat);
            for (int i = 0; i < RTN_H; i++) cat[i] = tanhf(cat[i]);
            Gru(RTN_G1_WI, RTN_G1_WH, RTN_G1_BI, RTN_G1_BH, cat, RTN_H, h1);
            Gru(RTN_G2_WI, RTN_G2_WH, RTN_G2_BI, RTN_G2_BH, h1, RTN_H, h2);
            memcpy(cat + RTN_H, h1, sizeof(h1)); memcpy(cat + 2 * RTN_H, h2, sizeof(h2));
            MatVec(RTN_OUT_W, RTN_OUT_B, cat, RTN_NB, 3 * RTN_H, gain);
            for (int b = 0; b < RTN_NB; b++) gain[b] = Sig(gain[b]);

            /* onto that frame's spectrum, and back to sound */
            float wr[RTN_FFT], wi[RTN_FFT];
            for (int b = 0; b + 1 < RTN_NB; b++)
            {
                const int lo = RTN_CENTRE[b], hi = RTN_CENTRE[b + 1];
                for (int k = lo; k < hi; k++)
                {
                    const float t = (float)((double)(k - lo) / (hi - lo));
                    const float g = (1.0f - t) * gain[b] + t * gain[b + 1];
                    wr[k] = heldRe[k] * g; wi[k] = heldIm[k] * g;
                }
            }
            wr[RTN_BINS - 1] = heldRe[RTN_BINS - 1] * gain[RTN_NB - 1]; wi[RTN_BINS - 1] = heldIm[RTN_BINS - 1] * gain[RTN_NB - 1];
            for (int k = 1; k < RTN_FFT / 2; k++) { wr[RTN_FFT - k] = wr[k]; wi[RTN_FFT - k] = -wi[k]; }
            Fft(wr, wi, true);
            for (int j = 0; j < RTN_BLOCK; j++)
            {
                const float a = wr[j] * (1.0f / RTN_FFT) * win[j] + olap[j];
                olap[j] = wr[RTN_BLOCK + j] * (1.0f / RTN_FFT) * win[RTN_BLOCK + j];
                out[j] = a;
            }
        }
        else memset(out, 0, RTN_BLOCK * sizeof(float));

        memcpy(heldRe, re, sizeof(heldRe)); memcpy(heldIm, im, sizeof(heldIm));
        frames++;
    }
};
