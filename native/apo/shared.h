// What Red Tune (the page «Звук») and the sound effect (RedTuneApo.dll inside Windows' audiodg.exe) share: one file,
// %ProgramData%\RedTune\audio.bin, mapped into memory by both. The program writes the settings, the effect reads them
// on every block of sound and writes back the meters and a short stretch of the processed sound (for the spectrum).
// One slot per output device. src\AudioFx.cs holds the same layout: change both together and raise RTA_LAYOUT.
#pragma once
#include <stdint.h>

#define RTA_MAGIC   0x55415452u /* "RTAU" */
#define RTA_LAYOUT  5u          /* 2 (19.11): the compressor in up to three parts with settings of their own; meters per part;
                                   3 (21.11): the de-esser's settings and meter, and spare words in both for what comes next;
                                   4 (21.11): the second compressor (microphones), and eight more spare words;
                                   5 (21.11): the second compressor in up to three parts, first in the chain, meters per part */
#define RTA_SLOTS   16
#define RTA_BANDS   12
#define RTA_RING    4096        /* mono samples of the output, for the spectrum */

/* flags */
#define RTA_ON      1u          /* the whole effect */
#define RTA_EQ      2u
#define RTA_COMP    4u
#define RTA_LIMIT   8u
#define RTA_GATE    16u         /* 21.11: the noise gate (microphones), first in the chain */
#define RTA_DENOISE 32u         /* 21.11: RNNoise before the gate; its strength (0..100 %) in bits 8..15 of mute */
#define RTA_DEREVERB 64u        /* 21.11: the room's tail taken off after the denoiser; its strength (0..100 %) in bits 16..23 of mute */
#define RTA_DFN     128u        /* 21.11: the denoiser is DeepFilterNet 3 instead of RNNoise (its strength: how deep the noise may go) */
#define RTA_DEESS   256u        /* 21.11: the de-esser, after the equalizer (dsHz, dsThrDb, dsRangeDb) */
#define RTA_COMP2   512u        /* 21.11: the second compressor, first in the chain (before the equalizer), c2b/c2Split */

/* band types */
enum { RTA_OFF = 0, RTA_BELL = 1, RTA_LOWSHELF = 2, RTA_HIGHSHELF = 3, RTA_HIGHPASS = 4, RTA_LOWPASS = 5, RTA_NOTCH = 6 };

#pragma pack(push, 4)
/* 21.11: type = the kind (below) | (slope - 1) << 8, the slope 1..4 for every kind (the dial «Крутизна»): a cut of
   12..48 dB/oct, a shelf of one to four shelves in a row (a steeper turn), a bell from smooth to straight (a flat top
   and steep sides), a notch of one to four in a row. 0 in those bits is slope 1, as written before; an older effect
   turns a band with them off. */
typedef struct RtaBand { uint32_t type; float freq, gainDb, q; } RtaBand;            /* 16 bytes */

/* the compressor's settings of one part of the sound */
typedef struct RtaCompBand { float thrDb, ratio, attackMs, releaseMs, kneeDb, makeupDb; } RtaCompBand;   /* 24 bytes */

typedef struct RtaParams                                                             /* 456 bytes */
{
    uint32_t flags;
    float preampDb;
    RtaBand bands[RTA_BANDS];
    float thrDb, ratio, attackMs, releaseMs, kneeDb, makeupDb, mixPct;               /* the compressor */
    float ceilDb;                                                                    /* the limiter */
    float splitHz;              /* the compressor in parts, the bass under this frequency apart; 0 = one part */
    float split2Hz;             /* a second split above the first: three parts (bass, middle, top); 0 = two parts at most */
    /* 19.11, listening to the parts (solo and mute on the page): bit b set = part b of the compressor ([0] the bass,
       [1] the middle, [2] the top) does not go out. Only while the sound is in parts; the program never saves it.
       21.11: bits 8..15 = the denoiser's strength, 0..100 % (an older effect masks them away); bits 16..23 = the
       strength of the reverb remover, 0..100 % */
    uint32_t mute;
    /* 21.11, the noise gate (in the three words that were reserved, so the layout stays 2 and an older effect simply
       ignores them): it lets the sound through only while it is above gateThrDb; under it the sound is turned down by
       gateRangeDb (<= 0) and comes back down over gateReleaseMs once it has stayed under for the hold (150 ms). */
    float gateThrDb, gateRangeDb, gateReleaseMs;
    /* The compressor's settings of the parts above the bass: [0] the middle (used with three parts), [1] the top (with
       two parts it is everything above the split). thrDb .. makeupDb above are the bass part's, and the whole sound's
       while it is in one part. */
    RtaCompBand cb[2];
    /* 21.11 (layout 3), the de-esser: the top above dsHz is turned down while it stands out of the whole sound by more
       than dsThrDb (a level relative to the sound's own, so a quiet microphone is treated as a loud one), by at most
       dsRangeDb (<= 0) */
    float dsHz, dsThrDb, dsRangeDb;
    float dsHz2;                /* 21.11 (the user): the band's top edge; what is above it is left alone (0: no top edge) */
    /* 21.11 (the user): the compressor's character (0 clean VCA, 1 FET, 2 optical, 3 vari-mu tube) and its colour after
       it (0 none, 1 tube, 2 transistor, 3 tape, 4 transformer) with its strength 0..100; colour only with the compressor on */
    float compChar, colorType, colorAmt;
    float spare[1];             /* zero; for settings to come without another layout */
    /* 21.11 (layout 5, the user: «both alike, the main one with the limiter last»): the second compressor, first in the
       chain (on the sound as it comes, before the gate, the equalizer and the main compressor, which then reads what it
       lets out), in up to three parts as the main one: c2b [0] the bass (or the whole sound), [1] the middle, [2] the top;
       c2Split / c2Split2 as splitHz / split2Hz; mix (100 = all squeezed, less = the plain sound beside: a parallel
       compression) and character (as compChar) */
    RtaCompBand c2b[3];
    float c2Split, c2Split2, c2Mix, c2Char;
    float spare2[8];
} RtaParams;

typedef struct RtaMeters                                                             /* 116 bytes, written by the effect */
{
    uint32_t beat;              /* +1 on every block: the program sees the effect is alive */
    uint32_t rate, channels, frames;   /* frames: low 16 bits the last block's frames; 21.11 (version 4): high 16 bits the
                                          room's reverb time the reverb remover hears, ms (0 while it is off) */
    float inPeak, outPeak;      /* linear, the highest of the last ~0.3 s */
    float grDb, limDb;          /* gain taken off by the compressor and by the limiter, dB (<= 0), the deepest of the last ~0.3 s */
    uint32_t pid, version, faults, ringPos;
    /* per part of the compressor ([0] the bass or the whole sound, [1] the middle, [2] the top): the gain it takes off,
       dB (<= 0, the deepest of the last ~0.3 s), and the level it reads, dB (the highest of the last ~0.3 s) */
    float bandGr[3], bandLv[3];
    float gateDb;               /* 21.11: what the gate takes off, dB (<= 0, the deepest of the last ~0.3 s); 0 while it is off */
    float vad;                  /* 21.11: the denoiser's voice probability, 0..1 (the highest of the last ~0.3 s); 0 while it is off */
    float dsDb;                 /* 21.11 (layout 3): what the de-esser takes off the top, dB (<= 0, the deepest of the last ~0.3 s) */
    float c2Gr[3];              /* 21.11 (layout 5): what each part of the second compressor takes off, dB (<= 0, the deepest of ~0.3 s) */
    float c2Lv[3];              /* ...and the level each part reads, dB (-140 while off or not in parts) */
    float spareM[2];
} RtaMeters;

typedef struct RtaSlot
{
    uint8_t endpoint[16];       /* the device's GUID ({0.0.0.00000000}.{this}); all zero = a free slot */
    volatile long seq;          /* odd while the program is writing p; the effect takes p only at an even, unchanged value */
    uint32_t pad;
    RtaParams p;
    RtaMeters m;
    float ring[RTA_RING];
} RtaSlot;

/* 48 bytes in the header (they were reserved): the last calls Windows made to the effect, whatever the device.
   Written by the effect off the sound thread, shown by --audio-status: on which step a device does not take it. */
typedef struct RtaDiag
{
    uint32_t created;           /* objects Windows made since its sound process started */
    uint32_t inits;             /* Initialize calls */
    uint32_t initSize;          /* the size of the data of the last one */
    uint32_t initInfo;          /* low byte: devices in its collection; 0x100 the device was read, 0x200 "only to list the effects", 0x400 the base class refused */
    uint8_t  endpoint[16];      /* the device of the last Initialize */
    uint32_t locks;             /* LockForProcess calls */
    uint32_t lockHr;            /* what the last one returned */
    uint32_t formatHr;          /* the last refusal of a sound format (0 = none) */
    uint32_t pid;               /* the sound process that wrote this */
} RtaDiag;

typedef struct RtaShared
{
    uint32_t magic, layout, slotSize, slotCount;
    RtaDiag diag;
    RtaSlot slots[RTA_SLOTS];
} RtaShared;
#pragma pack(pop)
