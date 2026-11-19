// What Red Tune (the page «Звук») and the sound effect (RedTuneApo.dll inside Windows' audiodg.exe) share: one file,
// %ProgramData%\RedTune\audio.bin, mapped into memory by both. The program writes the settings, the effect reads them
// on every block of sound and writes back the meters and a short stretch of the processed sound (for the spectrum).
// One slot per output device. src\AudioFx.cs holds the same layout: change both together and raise RTA_LAYOUT.
#pragma once
#include <stdint.h>

#define RTA_MAGIC   0x55415452u /* "RTAU" */
#define RTA_LAYOUT  2u          /* 2 (19.11): the compressor in up to three parts with settings of their own; meters per part */
#define RTA_SLOTS   16
#define RTA_BANDS   12
#define RTA_RING    4096        /* mono samples of the output, for the spectrum */

/* flags */
#define RTA_ON      1u          /* the whole effect */
#define RTA_EQ      2u
#define RTA_COMP    4u
#define RTA_LIMIT   8u

/* band types */
enum { RTA_OFF = 0, RTA_BELL = 1, RTA_LOWSHELF = 2, RTA_HIGHSHELF = 3, RTA_HIGHPASS = 4, RTA_LOWPASS = 5, RTA_NOTCH = 6 };

#pragma pack(push, 4)
typedef struct RtaBand { uint32_t type; float freq, gainDb, q; } RtaBand;            /* 16 bytes */

/* the compressor's settings of one part of the sound */
typedef struct RtaCompBand { float thrDb, ratio, attackMs, releaseMs, kneeDb, makeupDb; } RtaCompBand;   /* 24 bytes */

typedef struct RtaParams                                                             /* 304 bytes */
{
    uint32_t flags;
    float preampDb;
    RtaBand bands[RTA_BANDS];
    float thrDb, ratio, attackMs, releaseMs, kneeDb, makeupDb, mixPct;               /* the compressor */
    float ceilDb;                                                                    /* the limiter */
    float splitHz;              /* the compressor in parts, the bass under this frequency apart; 0 = one part */
    float split2Hz;             /* a second split above the first: three parts (bass, middle, top); 0 = two parts at most */
    /* 19.11, listening to the parts (solo and mute on the page): bit b set = part b of the compressor ([0] the bass,
       [1] the middle, [2] the top) does not go out. Only while the sound is in parts; the program never saves it. */
    uint32_t mute;
    uint32_t reserved[3];
    /* The compressor's settings of the parts above the bass: [0] the middle (used with three parts), [1] the top (with
       two parts it is everything above the split). thrDb .. makeupDb above are the bass part's, and the whole sound's
       while it is in one part. */
    RtaCompBand cb[2];
} RtaParams;

typedef struct RtaMeters                                                             /* 80 bytes, written by the effect */
{
    uint32_t beat;              /* +1 on every block: the program sees the effect is alive */
    uint32_t rate, channels, frames;
    float inPeak, outPeak;      /* linear, the highest of the last ~0.3 s */
    float grDb, limDb;          /* gain taken off by the compressor and by the limiter, dB (<= 0), the deepest of the last ~0.3 s */
    uint32_t pid, version, faults, ringPos;
    /* per part of the compressor ([0] the bass or the whole sound, [1] the middle, [2] the top): the gain it takes off,
       dB (<= 0, the deepest of the last ~0.3 s), and the level it reads, dB (the highest of the last ~0.3 s) */
    float bandGr[3], bandLv[3];
    uint32_t reserved[2];
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
