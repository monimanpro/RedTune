using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RedTune
{
    // 18.11: the sound. Red Tune's own effect (native\apo -> audio\RedTuneApo.dll) sits in Windows' chain of sound
    // effects of an output device, where Nahimic sits: pre-gain, parametric equalizer, compressor, limiter. This file is
    // the program's side of it:
    //   - the settings of every device (SoundParams) and the presets, kept in %APPDATA%\RedTune\sound.txt
    //   - the file both sides map into memory (%ProgramData%\RedTune\audio.bin, layout in native\apo\shared.h): the
    //     settings go in, the meters and a stretch of the processed sound come back
    //   - attaching the effect to a device and taking it off again (the device's FxProperties in the registry), the
    //     dll's registration, the restart of the Windows Audio service that makes Windows load or drop it
    //   - the curve of the equalizer (the same formulas as native\apo\rtdsp.h) and the spectrum for the page
    // Nothing here writes to the system unless the page asks: Attach, Detach, Register.

    class EqBand
    {
        public int Type = 1; public float Freq = 1000, Gain, Q = 1f;
        // 21.11 (the user): the slope 1..4 of every kind (the dial «Крутизна»): a cut 12..48 dB/oct, a shelf a steeper
        // turn, a bell from smooth to straight, a notch deeper (rtd_plan in native\apo\rtdsp.h). Written as a fifth number
        // of the band, and to the effect in bits 8..15 of its type.
        public int Slope = 1;
        public const int MaxSlope = 4;
        public EqBand Clone() { return (EqBand)MemberwiseClone(); }
        public bool HasGain { get { return Type >= 1 && Type <= 3; } }
        public bool IsCut { get { return Type == 4 || Type == 5; } }
        // 21.11: the sections of this band as AudioFx.Plan made them last, for the settings they were made for (the curve
        // asks for them at every point it draws; a straight bell takes a few passes to make). A clone shares them: they
        // are never changed, only replaced.
        internal List<double[]> planOf; internal double planKey0, planKey1, planKey2, planRate; internal int planType = -1, planSlope;
    }

    // the compressor's settings of one part of the sound
    class CompPart
    {
        public float Thr = -18, Ratio = 3, Attack = 10, Release = 150, Knee = 6, Makeup = 0;
        public CompPart Clone() { return (CompPart)MemberwiseClone(); }
        public override string ToString() { return string.Join(",", new[] { Thr, Ratio, Attack, Release, Knee, Makeup }.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))); }
    }

    class SoundParams
    {
        public const int MaxBands = 12;
        public bool On, Eq = true, Comp, Limit = true;
        public float Preamp;
        public List<EqBand> Bands = new List<EqBand>();
        public float Mix = 100, Ceil = -0.1f;
        // 19.11: the compressor works on the sound in one part, or in two or three parts with settings of their own:
        //   Split  > 0: the bass under this frequency apart (Hz)
        //   Split2 > 0: and above the second split the top apart; between the two is the middle
        // Parts: [0] the bass (and the whole sound while it is in one part), [1] the middle, [2] the top. With two parts
        // the half above the split goes by the top's settings, [1] is not used. The same order as in native\apo\shared.h.
        public float Split, Split2;
        public CompPart[] Parts = { new CompPart(), new CompPart(), new CompPart() };
        // the first part by its own names: the settings of the compressor while it is in one part, as they always were
        public float Thr { get { return Parts[0].Thr; } set { Parts[0].Thr = value; } }
        public float Ratio { get { return Parts[0].Ratio; } set { Parts[0].Ratio = value; } }
        public float Attack { get { return Parts[0].Attack; } set { Parts[0].Attack = value; } }
        public float Release { get { return Parts[0].Release; } set { Parts[0].Release = value; } }
        public float Knee { get { return Parts[0].Knee; } set { Parts[0].Knee = value; } }
        public float Makeup { get { return Parts[0].Makeup; } set { Parts[0].Makeup = value; } }
        public int PartCount { get { return Split <= 0 ? 1 : Split2 > 0 ? 3 : 2; } }
        // which of Parts are at work, from the lowest up
        public int[] ActiveParts { get { int n = PartCount; return n == 1 ? new[] { 0 } : n == 2 ? new[] { 0, 2 } : new[] { 0, 1, 2 }; } }

        // 19.11, listening to the bands on the page «Звук» (solo and mute): bit b = part b of the compressor is silent.
        // Not a setting: never saved, not part of a preset or of a step back; the page sets it before every write.
        public int Mute;

        // 21.11, the noise gate (microphones; native\apo: RTA_GATE, the three words after mute): under GateThr (dB) the
        // sound is turned down by GateRange (dB, <= 0) and comes back down over GateRelease (ms) after a 150 ms hold
        public bool Gate;
        public float GateThr = -50, GateRange = -40, GateRelease = 150;
        // 21.11, the denoiser (RNNoise in the effect; RTA_DENOISE, the strength in bits 8..15 of mute): how much of the
        // cleaned sound replaces the untouched one, 0..100 %
        public bool Denoise;
        public float DenoiseAmount = 100;
        // 21.11 (the user): the denoiser's model: 0 RNNoise, 1 DeepFilterNet 3 (RTA_DFN); with it the strength is how deep the noise may go
        public int DenoiseModel;
        // 21.11, the reverb remover (RTA_DEREVERB, the strength in bits 16..23 of mute): how deep the room's tail is taken off
        public bool Dereverb;
        public float DereverbAmount = 70;
        // 21.11 (the user): the de-esser after the equalizer (RTA_DEESS): the top above DeEssHz turned down while it stands
        // out of the whole sound by more than DeEssThr, by at most DeEssRange (<= 0)
        public bool DeEss;
        public float DeEssHz = 6500, DeEssThr = -15, DeEssRange = -8;
        public float DeEssHz2 = 10000;   // 21.11 (the user): the band's top edge; 20000 = none (all of the top)
        // 21.11 (the user): the compressor's character (0 clean, 1 FET, 2 optical, 3 vari-mu tube) and its colour: by the
        // character (ColorAuto) or one picked (ColorType 0 none, 1 tube, 2 transistor, 3 tape, 4 transformer), its strength
        public int CompChar;
        public bool ColorAuto = true;
        public int ColorType;
        public float ColorAmt = 50;
        public static readonly int[] ColorOfChar = { 0, 2, 4, 1 };   // clean: none; FET: transistor; optical: transformer; tube: tube
        public int ColorNow { get { return ColorAuto ? ColorOfChar[Math.Max(0, Math.Min(3, CompChar))] : ColorType; } }
        // 21.11 (the user): the second compressor (microphones), first in the chain, in up to three parts as the main one
        // (RTA_COMP2): C2Parts [0] the bass or the whole sound, [1] the middle, [2] the top; C2Split / C2Split2 as Split / Split2
        public bool Comp2;
        public CompPart[] C2Parts = { C2Start(), C2Start(), C2Start() };
        static CompPart C2Start() { return new CompPart { Thr = -20, Ratio = 3, Attack = 10, Release = 150, Knee = 6, Makeup = 0 }; }
        public CompPart C2 { get { return C2Parts[0]; } }
        public float C2Split, C2Split2;
        public float C2Mix = 100;
        public float C2Thr { get { return C2.Thr; } set { C2.Thr = value; } }
        public float C2Ratio { get { return C2.Ratio; } set { C2.Ratio = value; } }
        public float C2Att { get { return C2.Attack; } set { C2.Attack = value; } }
        public float C2Rel { get { return C2.Release; } set { C2.Release = value; } }
        public float C2Knee { get { return C2.Knee; } set { C2.Knee = value; } }
        public float C2Mk { get { return C2.Makeup; } set { C2.Makeup = value; } }
        public int C2Char = 2;

        public SoundParams Clone()
        {
            var p = (SoundParams)MemberwiseClone();
            p.Bands = Bands.Select(b => b.Clone()).ToList();
            p.Parts = Parts.Select(q => q.Clone()).ToArray();
            p.C2Parts = C2Parts.Select(q => q.Clone()).ToArray();
            return p;
        }

        // where a new device starts: the twelve points of EvenBands
        public static SoundParams Start()
        {
            var p = new SoundParams();
            p.Bands = EvenBands();
            return p;
        }

        // 21.11 (the user): the points at rest: twelve bells of no gain spread evenly along the whole line, 20 Hz .. 20 kHz
        // (the middles of twelve equal steps in octaves: 27, 47, 84 Hz .. 15 kHz), the width of one step
        public static List<EqBand> EvenBands()
        {
            var list = new List<EqBand>();
            for (int i = 0; i < MaxBands; i++)
            {
                double f = 20 * Math.Pow(1000, (i + 0.5) / MaxBands);
                f = f < 100 ? Math.Round(f) : f < 1000 ? Math.Round(f / 5) * 5 : f < 10000 ? Math.Round(f / 50) * 50 : Math.Round(f / 500) * 500;
                list.Add(new EqBand { Type = 1, Freq = (float)f, Gain = 0, Q = 1.4f });
            }
            return list;
        }

        // 21.11 (the user): «Сбросить»: every effect off and every setting as it comes, the points at rest; the processing
        // itself (On) stays as it is, so the device keeps its effect
        public static SoundParams Defaults(bool on)
        {
            var p = Start();
            p.On = on; p.Eq = false; p.Comp = false; p.Limit = false; p.Gate = false; p.Denoise = false; p.Dereverb = false;
            return p;
        }

        // 21.11 (the user): the equalizer always has all twelve points, there is no «+» any more. Settings with fewer get
        // bells of no gain (they leave the sound exactly as it is) in the widest gaps, and only then the points are put in
        // order of frequency, once. Settings that have all twelve keep their order as saved (21.11, the user: a point
        // dragged past another must keep its strip after a restart too).
        public void FillBands()
        {
            if (Bands.Count >= MaxBands) return;
            while (Bands.Count < MaxBands)
            {
                var fs = new List<double> { 20 }; fs.AddRange(Bands.Select(b => (double)b.Freq).OrderBy(x => x)); fs.Add(20000);
                int at = 0; double widest = -1;
                for (int i = 0; i + 1 < fs.Count; i++) { double gap = Math.Log(Math.Max(fs[i + 1], fs[i] + 1e-3) / fs[i]); if (gap > widest) { widest = gap; at = i; } }
                double f = Math.Sqrt(fs[at] * fs[at + 1]);
                f = f < 100 ? Math.Round(f) : f < 1000 ? Math.Round(f / 5) * 5 : f < 10000 ? Math.Round(f / 50) * 50 : Math.Round(f / 500) * 500;
                Bands.Add(new EqBand { Type = 1, Freq = (float)f, Gain = 0, Q = 1f });
            }
            Bands = Bands.OrderBy(b => b.Freq).ToList();
        }

        static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("on=").Append(On ? 1 : 0).Append(" eq=").Append(Eq ? 1 : 0).Append(" comp=").Append(Comp ? 1 : 0).Append(" lim=").Append(Limit ? 1 : 0);
            sb.Append(" pre=").Append(F(Preamp)).Append(" thr=").Append(F(Thr)).Append(" ratio=").Append(F(Ratio)).Append(" att=").Append(F(Attack)).Append(" rel=").Append(F(Release));
            sb.Append(" knee=").Append(F(Knee)).Append(" mk=").Append(F(Makeup)).Append(" mix=").Append(F(Mix)).Append(" ceil=").Append(F(Ceil));
            if (Split > 0) sb.Append(" xo=").Append(F(Split));
            if (Split > 0 && Split2 > 0) sb.Append(" xo2=").Append(F(Split2));
            sb.Append(" gate=").Append(Gate ? 1 : 0).Append(" gthr=").Append(F(GateThr)).Append(" grange=").Append(F(GateRange)).Append(" grel=").Append(F(GateRelease));
            sb.Append(" dn=").Append(Denoise ? 1 : 0).Append(" dns=").Append(F(DenoiseAmount)).Append(" dnm=").Append(DenoiseModel);
            sb.Append(" dr=").Append(Dereverb ? 1 : 0).Append(" drs=").Append(F(DereverbAmount));
            sb.Append(" ds=").Append(DeEss ? 1 : 0).Append(" dshz=").Append(F(DeEssHz)).Append(" dsthr=").Append(F(DeEssThr)).Append(" dsrange=").Append(F(DeEssRange)).Append(" dshz2=").Append(F(DeEssHz2));
            sb.Append(" cch=").Append(CompChar).Append(" cauto=").Append(ColorAuto ? 1 : 0).Append(" ctype=").Append(ColorType).Append(" camt=").Append(F(ColorAmt));
            sb.Append(" c2=").Append(Comp2 ? 1 : 0).Append(" c2thr=").Append(F(C2Thr)).Append(" c2ratio=").Append(F(C2Ratio)).Append(" c2att=").Append(F(C2Att)).Append(" c2rel=").Append(F(C2Rel))
              .Append(" c2knee=").Append(F(C2Knee)).Append(" c2mk=").Append(F(C2Mk)).Append(" c2mix=").Append(F(C2Mix)).Append(" c2ch=").Append(C2Char);
            sb.Append(" c2p2=").Append(C2Parts[1]).Append(" c2p3=").Append(C2Parts[2]);
            if (C2Split > 0) sb.Append(" c2xo=").Append(F(C2Split));
            if (C2Split > 0 && C2Split2 > 0) sb.Append(" c2xo2=").Append(F(C2Split2));
            // the settings of the upper parts: kept also while they are not at work, so a choice of three parts is not lost by trying one
            sb.Append(" p2=").Append(Parts[1]).Append(" p3=").Append(Parts[2]);
            foreach (var b in Bands)
            {
                sb.Append(" b=").Append(b.Type).Append(',').Append(F(b.Freq)).Append(',').Append(F(b.Gain)).Append(',').Append(F(b.Q));
                if (b.Slope > 1) sb.Append(',').Append(b.Slope);
            }
            return sb.ToString();
        }

        public static SoundParams Parse(string text)
        {
            var p = new SoundParams(); bool own2 = false, own3 = false;
            foreach (var part in (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('='); if (eq <= 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1); float f;
                if (k == "c2p2" || k == "c2p3")
                {
                    var x = v.Split(','); var n = new float[6]; bool ok = x.Length == 6;
                    for (int i = 0; ok && i < 6; i++) ok = Num(x[i], out n[i]);
                    if (ok) p.C2Parts[k == "c2p2" ? 1 : 2] = new CompPart { Thr = n[0], Ratio = n[1], Attack = n[2], Release = n[3], Knee = n[4], Makeup = n[5] };
                    continue;
                }
                if (k == "p2" || k == "p3")
                {
                    var x = v.Split(','); var n = new float[6]; bool ok = x.Length == 6;
                    for (int i = 0; ok && i < 6; i++) ok = Num(x[i], out n[i]);
                    if (ok) { p.Parts[k == "p2" ? 1 : 2] = new CompPart { Thr = n[0], Ratio = n[1], Attack = n[2], Release = n[3], Knee = n[4], Makeup = n[5] }; if (k == "p2") own2 = true; else own3 = true; }
                    continue;
                }
                if (k == "b")
                {
                    var x = v.Split(','); int t, sl = 1; float fr, g, q;
                    if ((x.Length == 4 || x.Length == 5) && int.TryParse(x[0], out t) && Num(x[1], out fr) && Num(x[2], out g) && Num(x[3], out q) && p.Bands.Count < MaxBands
                        && (x.Length == 4 || int.TryParse(x[4], out sl)))
                        p.Bands.Add(new EqBand { Type = Math.Max(1, Math.Min(6, t)), Freq = fr, Gain = g, Q = q, Slope = Math.Max(1, Math.Min(EqBand.MaxSlope, sl)) });
                    continue;
                }
                if (!Num(v, out f)) continue;
                switch (k)
                {
                    case "on": p.On = f != 0; break; case "eq": p.Eq = f != 0; break; case "comp": p.Comp = f != 0; break; case "lim": p.Limit = f != 0; break;
                    case "pre": p.Preamp = f; break; case "thr": p.Thr = f; break; case "ratio": p.Ratio = f; break; case "att": p.Attack = f; break; case "rel": p.Release = f; break;
                    case "knee": p.Knee = f; break; case "mk": p.Makeup = f; break; case "mix": p.Mix = f; break; case "ceil": p.Ceil = f; break; case "xo": p.Split = f; break; case "xo2": p.Split2 = f; break;
                    case "gate": p.Gate = f != 0; break; case "gthr": p.GateThr = f; break; case "grange": p.GateRange = f; break; case "grel": p.GateRelease = f; break;
                    case "dn": p.Denoise = f != 0; break; case "dns": p.DenoiseAmount = f; break; case "dnm": p.DenoiseModel = f >= 1 ? 1 : 0; break;
                    case "dr": p.Dereverb = f != 0; break; case "drs": p.DereverbAmount = f; break;
                    case "ds": p.DeEss = f != 0; break; case "dshz": p.DeEssHz = f; break; case "dsthr": p.DeEssThr = f; break; case "dsrange": p.DeEssRange = f; break; case "dshz2": p.DeEssHz2 = f; break;
                    case "cch": p.CompChar = (int)f; break; case "cauto": p.ColorAuto = f != 0; break; case "ctype": p.ColorType = (int)f; break; case "camt": p.ColorAmt = f; break;
                    case "c2": p.Comp2 = f != 0; break; case "c2thr": p.C2Thr = f; break; case "c2ratio": p.C2Ratio = f; break; case "c2att": p.C2Att = f; break;
                    case "c2rel": p.C2Rel = f; break; case "c2knee": p.C2Knee = f; break; case "c2mk": p.C2Mk = f; break; case "c2mix": p.C2Mix = f; break; case "c2xo": p.C2Split = f; break; case "c2xo2": p.C2Split2 = f; break; case "c2ch": p.C2Char = (int)f; break;
                }
            }
            // settings written before the parts had their own (19.11): every part as the first, which is what they did then
            if (!own2) p.Parts[1] = p.Parts[0].Clone();
            if (!own3) p.Parts[2] = p.Parts[0].Clone();
            p.Fit();
            p.FillBands();
            return p;
        }

        static bool Num(string s, out float f) { return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f); }

        // the same limits the effect applies (rtd_sanitize), so the page never shows what will not be played
        public void Fit()
        {
            Preamp = Clamp(Preamp, -24, 24);
            foreach (var b in Bands) { b.Freq = Clamp(b.Freq, 20, 20000); b.Gain = Clamp(b.Gain, -24, 24); b.Q = Clamp(b.Q, 0.1f, 20); }
            foreach (var q in Parts)
            {
                q.Thr = Clamp(q.Thr, -60, 0); q.Ratio = Clamp(q.Ratio, 1, 30); q.Attack = Clamp(q.Attack, 0.1f, 300); q.Release = Clamp(q.Release, 5, 3000);
                q.Knee = Clamp(q.Knee, 0, 24); q.Makeup = Clamp(q.Makeup, -12, 24);
            }
            Mix = Clamp(Mix, 0, 100); Ceil = Clamp(Ceil, -12, 0);
            Split = float.IsNaN(Split) || Split < 40 ? 0 : Clamp(Split, 60, 1000);
            Split2 = Split <= 0 || float.IsNaN(Split2) || Split2 < 200 ? 0 : Clamp(Split2, Math.Max(400, Split * 2), 12000);
            GateThr = Clamp(GateThr, -90, 0); GateRange = Clamp(GateRange, -90, 0); GateRelease = Clamp(GateRelease, 10, 2000);
            DenoiseAmount = Clamp(DenoiseAmount, 0, 100); DereverbAmount = Clamp(DereverbAmount, 0, 100);
            DeEssHz = Clamp(DeEssHz, 2000, 16000); DeEssThr = Clamp(DeEssThr, -40, 0); DeEssRange = Clamp(DeEssRange, -24, 0);
            DeEssHz2 = Clamp(DeEssHz2, DeEssHz * 1.25f, 20000);
            CompChar = Math.Max(0, Math.Min(3, CompChar)); ColorType = Math.Max(0, Math.Min(4, ColorType)); ColorAmt = Clamp(ColorAmt, 0, 100);
            foreach (var q in C2Parts)
            {
                q.Thr = Clamp(q.Thr, -60, 0); q.Ratio = Clamp(q.Ratio, 1, 30); q.Attack = Clamp(q.Attack, 0.1f, 300); q.Release = Clamp(q.Release, 5, 3000);
                q.Knee = Clamp(q.Knee, 0, 24); q.Makeup = Clamp(q.Makeup, -12, 24);
            }
            C2Split = float.IsNaN(C2Split) || C2Split < 40 ? 0 : Clamp(C2Split, 60, 1000);
            C2Split2 = C2Split <= 0 || float.IsNaN(C2Split2) || C2Split2 < 200 ? 0 : Clamp(C2Split2, Math.Max(400, C2Split * 2), 12000);
            C2Mix = Clamp(C2Mix, 0, 100); C2Char = Math.Max(0, Math.Min(3, C2Char));
        }

        static float Clamp(float v, float lo, float hi) { return float.IsNaN(v) ? lo : Math.Max(lo, Math.Min(hi, v)); }
    }

    class SoundDevice
    {
        public Guid Id; public string Name, Adapter; public bool Default;
        public bool Capture; // 21.11: a microphone or another input (MMDevices\Audio\Capture)
        public string Full { get { return string.IsNullOrEmpty(Adapter) ? Name : Name + " (" + Adapter + ")"; } }
    }

    class SoundMeters
    {
        public uint Beat, Rate, Channels, Pid, Faults, RingPos, Version; public float InPeak, OutPeak, GrDb, LimDb;
        public float GateDb; // 21.11: what the gate takes off, dB (<= 0); an effect older than version 2 has no gate and leaves 0
        public float Vad;    // 21.11: the denoiser's voice probability 0..1 (version 3 and newer)
        public float Room;   // 21.11: the room's reverb time the reverb remover hears, s (version 4; 0 while off or not heard yet)
        public float DsDb;   // 21.11: what the de-esser takes off the top, dB (<= 0; version 6, layout 3)
        public float[] C2Gr = new float[3], C2Lv = { -140, -140, -140 };   // 21.11: the second compressor's parts: what each takes off and reads, dB (version 8, layout 5)
        public float C2Db { get { return Math.Min(C2Gr[0], Math.Min(C2Gr[1], C2Gr[2])); } }
        // per part of the compressor (the order of SoundParams.Parts): the gain it takes off and the level it reads, dB
        public float[] PartGr = new float[3], PartLv = new float[3];
    }

    // 21.11: Mic — a preset of the microphone tab (the two tabs have lists of their own)
    class SoundPreset { public string Name; public SoundParams P; public bool BuiltIn, Mic; }

    static class AudioFx
    {
        public const string Clsid = "{E4B6C2A1-7F3D-4C58-9B1E-5A2D8C6F0E37}";
        const string Fx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}", Modes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}", DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
        const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        // 21.11: microphones and other inputs, the same effect with settings of their own (a device id is unique across both)
        const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        static readonly Dictionary<Guid, bool> kinds = new Dictionary<Guid, bool>();
        public static bool IsCapture(Guid id)
        {
            bool c;
            lock (kinds) if (kinds.TryGetValue(id, out c)) return c;
            try { using (var k = Registry.LocalMachine.OpenSubKey(CaptureKey + "\\" + id.ToString("B"))) c = k != null; } catch { c = false; }
            lock (kinds) kinds[id] = c;
            return c;
        }
        static string RootOf(Guid id) { return IsCapture(id) ? CaptureKey : RenderKey; }
        const string OwnKey = @"SOFTWARE\RedTune\Audio"; // what Attach created, to take exactly that away again

        // ---- the shared file (native\apo\shared.h) ----
        const uint Magic = 0x55415452, Layout = 5;   // 3 (21.11): the de-esser, spare words; 4, 5: the second compressor (native\apo\shared.h)
        const int Slots = 16, MaxBands = 12, Ring = 4096;
        // layout 2 (19.11): the settings 304 bytes (the compressor's parts), the meters 80 (per part)
        const int Header = 64, SlotSize = 16980, OffSeq = 16, OffParams = 24, OffMeters = 480, OffRing = 596, Total = Header + Slots * SlotSize;

        public static string SharedPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RedTune", "audio.bin"); } }
        public static string SettingsPath { get { return Path.Combine(Store.DataDir, "sound.txt"); } }
        public static string TracePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RedTune", "apo.log"); } }

        // the end of the effect's journal: what Windows asked of it last
        public static string TraceTail(int lines)
        {
            try
            {
                if (!File.Exists(TracePath)) return "  (no journal file)";
                using (var fs = new FileStream(TracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    var all = r.ReadToEnd().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    return all.Length == 0 ? "  (empty: Windows has not called the effect since this build was registered)" : string.Join("\r\n", all.Skip(Math.Max(0, all.Length - lines)).Select(l => "  " + l));
                }
            }
            catch (Exception ex) { return "  " + ex.Message; }
        }

        static readonly object gate = new object();
        static MemoryMappedFile map; static MemoryMappedViewAccessor view; static unsafe byte* mem;

        // create: make the file when it is not there (only when a device is being attached); otherwise it is opened if it exists
        static unsafe bool Open(bool create)
        {
            lock (gate)
            {
                if (mem != null) return true;
                string path = SharedPath;
                if (!File.Exists(path) && !create) return false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    bool fresh = !File.Exists(path) || new FileInfo(path).Length != Total;
                    var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    // another size: an older layout. Windows does not let it change while an effect has it mapped (after an
                    // update, until Windows' sound restarts): then not now (Init writes again after the restart)
                    if (fs.Length != Total) { try { fs.SetLength(Total); } catch (IOException) { fs.Dispose(); return false; } }
                    map = MemoryMappedFile.CreateFromFile(fs, null, Total, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, false);
                    view = map.CreateViewAccessor(0, Total, MemoryMappedFileAccess.ReadWrite);
                    byte* p = null; view.SafeMemoryMappedViewHandle.AcquirePointer(ref p); mem = p;
                    if (fresh || *(uint*)mem != Magic || *(uint*)(mem + 4) != Layout)
                    {
                        for (int i = 0; i < Total; i++) mem[i] = 0;
                        *(uint*)(mem + 8) = SlotSize; *(uint*)(mem + 12) = Slots; *(uint*)(mem + 4) = Layout; *(uint*)mem = Magic;
                    }
                    if (create) Grant(path);
                    return true;
                }
                catch (Exception ex) { Store.Log("звук: общий файл: " + ex.Message); Close(); return false; }
            }
        }

        // the effect's own notes in the header (RtaDiag in shared.h): the last calls Windows made to it, on any device
        public static unsafe string DiagText()
        {
            if (!Open(false)) return "no shared file";
            lock (gate)
            {
                uint* d = (uint*)(mem + 16);
                uint created = d[0], inits = d[1], size = d[2], info = d[3], locks = d[8], lockHr = d[9], formatHr = d[10], pid = d[11];
                var g = new byte[16]; for (int i = 0; i < 16; i++) g[i] = mem[32 + i];
                if (pid == 0) return "the effect has written nothing yet (Windows has not made it since this build was registered)";
                return "sound process " + pid + ": objects made " + created + ", Initialize " + inits + " (last: data " + size + " bytes, devices " + (info & 0xff)
                    + ((info & 0x100) != 0 ? ", device " + new Guid(g).ToString("B") : ", DEVICE NOT READ") + ((info & 0x200) != 0 ? ", only to list the effects" : "") + ((info & 0x400) != 0 ? ", THE BASE CLASS REFUSED" : "")
                    + "), LockForProcess " + locks + " (last returned 0x" + lockHr.ToString("X8") + "), last format refused: " + (formatHr == 0 ? "none" : "0x" + formatHr.ToString("X8"));
            }
        }

        static unsafe void Close()
        {
            lock (gate)
            {
                try { if (mem != null) view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                mem = null;
                try { if (view != null) view.Dispose(); } catch { }
                try { if (map != null) map.Dispose(); } catch { }
                view = null; map = null;
            }
        }

        // Windows' sound process runs as LOCAL SERVICE: it reads the file as any user does and needs the right to write for the meters
        static void Grant(string path)
        {
            try
            {
                var sec = File.GetAccessControl(path);
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.Modify, AccessControlType.Allow));
                File.SetAccessControl(path, sec);
            }
            catch (Exception ex) { Store.Log("звук: права на общий файл: " + ex.Message); }
        }

        static unsafe byte* Slot(Guid id, bool make)
        {
            if (mem == null) return null;
            var want = id.ToByteArray(); byte* free = null;
            for (int s = 0; s < Slots; s++)
            {
                byte* p = mem + Header + s * SlotSize; bool same = true, empty = true;
                for (int i = 0; i < 16; i++) { if (p[i] != want[i]) same = false; if (p[i] != 0) empty = false; }
                if (same) return p;
                if (empty && free == null) free = p;
            }
            if (!make || free == null) return null;
            // the settings first, the name last: the effect starts reading a slot only once it carries the device's id
            for (int i = 16; i < OffRing; i++) free[i] = 0;
            for (int i = 0; i < 16; i++) free[i] = want[i];
            return free;
        }

        // the settings into the device's slot; the effect picks them up on its next block of sound (10 ms)
        public static unsafe bool Write(Guid id, SoundParams sp, bool create = false)
        {
            if (!Open(create)) return false;
            lock (gate)
            {
                byte* s = Slot(id, create); if (s == null) return false;
                Interlocked.Increment(ref *(int*)(s + OffSeq)); // odd: being written
                byte* p = s + OffParams;
                *(uint*)p = (sp.On ? 1u : 0) | (sp.Eq ? 2u : 0) | (sp.Comp ? 4u : 0) | (sp.Limit ? 8u : 0) | (sp.Gate ? 16u : 0) | (sp.Denoise ? 32u : 0) | (sp.Dereverb ? 64u : 0) | (sp.Denoise && sp.DenoiseModel == 1 ? 128u : 0) | (sp.DeEss && IsCapture(id) ? 256u : 0) | (sp.Comp2 && IsCapture(id) ? 512u : 0);   // 21.11 (the user): the de-esser is the microphone's only
                *(float*)(p + 4) = sp.Preamp;
                for (int i = 0; i < MaxBands; i++)
                {
                    byte* b = p + 8 + i * 16; var band = i < sp.Bands.Count ? sp.Bands[i] : null;
                    *(uint*)b = band == null ? 0u : (uint)band.Type | ((uint)(Math.Max(1, Math.Min(EqBand.MaxSlope, band.Slope)) - 1) << 8); *(float*)(b + 4) = band == null ? 1000f : band.Freq; // 21.11: the slope in bits 8..15 of the type
                    *(float*)(b + 8) = band == null || !band.HasGain ? 0f : band.Gain; *(float*)(b + 12) = band == null ? 0.707f : band.Q;
                }
                float* c = (float*)(p + 8 + MaxBands * 16);
                c[0] = sp.Thr; c[1] = sp.Ratio; c[2] = sp.Attack; c[3] = sp.Release; c[4] = sp.Knee; c[5] = sp.Makeup; c[6] = sp.Mix; c[7] = sp.Ceil; c[8] = sp.Split;
                c[9] = sp.Split > 0 ? sp.Split2 : 0;
                c[11] = sp.GateThr; c[12] = sp.GateRange; c[13] = sp.GateRelease; // 21.11: the gate (the words that were reserved)
                *(uint*)(c + 10) = (uint)(sp.Mute & 7) | ((uint)Math.Round(Math.Max(0, Math.Min(100, sp.DenoiseAmount))) << 8)
                    | ((uint)Math.Round(Math.Max(0, Math.Min(100, sp.DereverbAmount))) << 16); // RtaParams.mute (21.11: the denoiser's strength in bits 8..15, the reverb remover's in 16..23)
                for (int k = 1; k <= 2; k++)
                {
                    var q = sp.Parts[k]; float* b = c + 14 + (k - 1) * 6;
                    b[0] = q.Thr; b[1] = q.Ratio; b[2] = q.Attack; b[3] = q.Release; b[4] = q.Knee; b[5] = q.Makeup;
                }
                c[26] = sp.DeEssHz; c[27] = sp.DeEssThr; c[28] = sp.DeEssRange; c[29] = sp.DeEssHz2; c[30] = sp.CompChar; c[31] = sp.ColorNow; c[32] = sp.ColorAmt; c[33] = 0;   // 21.11: the de-esser, the character and colour, a spare word
                for (int k = 0; k < 3; k++)   // the second compressor's parts (RtaParams.c2b), its splits, mix and character
                {
                    var q = sp.C2Parts[k]; float* b = c + 34 + k * 6;
                    b[0] = q.Thr; b[1] = q.Ratio; b[2] = q.Attack; b[3] = q.Release; b[4] = q.Knee; b[5] = q.Makeup;
                }
                c[52] = sp.C2Split; c[53] = sp.C2Split2; c[54] = sp.C2Mix; c[55] = sp.C2Char;
                for (int k = 56; k < 64; k++) c[k] = 0;
                Interlocked.Increment(ref *(int*)(s + OffSeq));
                return true;
            }
        }

        public static unsafe void Free(Guid id)
        {
            if (!Open(false)) return;
            lock (gate) { byte* s = Slot(id, false); if (s != null) for (int i = 0; i < 16; i++) s[i] = 0; }
        }

        public static unsafe SoundMeters Meters(Guid id)
        {
            if (!Open(false)) return null;
            lock (gate)
            {
                byte* s = Slot(id, false); if (s == null) return null;
                byte* m = s + OffMeters;
                var sm = new SoundMeters
                {
                    Beat = *(uint*)m, Rate = *(uint*)(m + 4), Channels = *(uint*)(m + 8), InPeak = *(float*)(m + 16), OutPeak = *(float*)(m + 20),
                    GrDb = *(float*)(m + 24), LimDb = *(float*)(m + 28), Pid = *(uint*)(m + 32), Version = *(uint*)(m + 36), Faults = *(uint*)(m + 40), RingPos = *(uint*)(m + 44),
                    GateDb = *(float*)(m + 72), Vad = *(float*)(m + 76),
                    Room = (*(uint*)(m + 12) >> 16) / 1000f, // RtaMeters.frames, high 16 bits (an older effect leaves them 0)
                    DsDb = *(float*)(m + 80), C2Gr = new[] { *(float*)(m + 84), *(float*)(m + 88), *(float*)(m + 92) }, C2Lv = new[] { *(float*)(m + 96), *(float*)(m + 100), *(float*)(m + 104) },
                };
                for (int k = 0; k < 3; k++) { sm.PartGr[k] = *(float*)(m + 48 + k * 4); sm.PartLv[k] = *(float*)(m + 60 + k * 4); }
                return sm;
            }
        }

        // the last `count` samples of what the device plays (mono), oldest first; null when there is no slot
        public static unsafe float[] Recent(Guid id, int count)
        {
            if (!Open(false)) return null;
            lock (gate)
            {
                byte* s = Slot(id, false); if (s == null) return null;
                uint pos = *(uint*)(s + OffMeters + 44); float* ring = (float*)(s + OffRing);
                var r = new float[count];
                for (int i = 0; i < count; i++) r[i] = ring[(pos - (uint)count + (uint)i) & (Ring - 1)];
                return r;
            }
        }

        // --audio-record <device|default> <seconds> <file.wav> <report>: what the effect puts out for the device, mono, as a
        // float wav: the stretch of sound in the shared file is read every 10 ms and the pieces are joined by the effect's
        // sample counter. With the processing switched off it is what reaches the effect, i.e. the output of every effect
        // before it (Nahimic): a way to measure those. Silence is not recorded (the effect is not called while nothing plays).
        public static unsafe string Record(Guid id, double seconds, string wav)
        {
            if (!Open(false)) return "no shared file";
            byte* s; lock (gate) s = Slot(id, false);
            if (s == null) return "no slot for the device (the effect is not attached)";
            uint* ppos = (uint*)(s + OffMeters + 44); float* ring = (float*)(s + OffRing);
            var data = new List<float>((int)(seconds * 48000) + Ring);
            const uint Safe = Ring - 600; // the oldest samples may be under the effect's pen
            uint last = *ppos; long lost = 0; int rate = 48000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(10);
                uint pos = *ppos, n = pos - last;
                if (n > 0x80000000u) { last = pos; continue; } // the counter went back: Windows made the effect anew (its sound service restarted)
                if (n > Safe) { lost += n - Safe; last = pos - Safe; n = Safe; }
                for (uint i = 0; i < n; i++) data.Add(ring[(last + i) & (Ring - 1)]);
                last = pos;
                uint r = *(uint*)(s + OffMeters + 4); if (r > 8000) rate = (int)r;
            }
            using (var f = new BinaryWriter(File.Create(wav)))
            {
                int bytes = data.Count * 4;
                f.Write(Encoding.ASCII.GetBytes("RIFF")); f.Write(36 + bytes); f.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); f.Write(16);
                f.Write((short)3); f.Write((short)1); f.Write(rate); f.Write(rate * 4); f.Write((short)4); f.Write((short)32);
                f.Write(Encoding.ASCII.GetBytes("data")); f.Write(bytes);
                foreach (var v in data) f.Write(v);
            }
            return data.Count + " samples, " + rate + " Hz, " + (data.Count / (double)rate).ToString("0.00", CultureInfo.InvariantCulture) + " s of sound, lost " + lost;
        }

        // the width of section j of a cut of n sections (rtd_cutq in native\apo\rtdsp.h): a Butterworth of order 2n, the
        // band's own Q scaling the narrowest section
        public static double CutQ(int n, int j, double q)
        {
            if (n <= 1) return q;
            double w = 1.0 / (2.0 * Math.Sin((2 * j + 1) * Math.PI / (4.0 * n)));
            return j == 0 ? w * q / 0.7071067811865476 : w;
        }

        // one section's response at f (the analogue prototype, frequency warped), multiplied into re/im
        static void SecMul(int type, double fc, double gain, double q, double f, double rate, ref double re, ref double im)
        {
            double A = Math.Pow(10, gain / 40), g = Math.Tan(Math.PI * fc / rate), k = 1 / q, m0 = 1, m1 = 0, m2 = 0;
            switch (type)
            {
                case 1: k = 1 / (q * A); m1 = k * (A * A - 1); break;
                case 2: g /= Math.Sqrt(A); m1 = k * (A - 1); m2 = A * A - 1; break;
                case 3: g *= Math.Sqrt(A); m0 = A * A; m1 = k * (1 - A) * A; m2 = 1 - A * A; break;
                case 4: m1 = -k; m2 = -1; break;
                case 5: m0 = 0; m2 = 1; break;
                case 6: m1 = -k; break;
                default: return;
            }
            // s = j*w; H = m0 + (m1*s + m2) / (s*s + k*s + 1)
            double w = Math.Tan(Math.PI * Math.Min(f, rate * 0.499) / rate) / g;
            double dr = 1 - w * w, di = k * w, nr = m2, ni = m1 * w, dd = dr * dr + di * di;
            double hr = m0 + (nr * dr + ni * di) / dd, hi = (ni * dr - nr * di) / dd;
            double tr = re * hr - im * hi; im = re * hi + im * hr; re = tr;
        }

        static double SecDb(int type, double fc, double gain, double q, double f, double rate)
        {
            double re = 1, im = 0; SecMul(type, fc, gain, q, f, rate, ref re, ref im);
            return 10 * Math.Log10(re * re + im * im + 1e-30);
        }

        // 21.11: the sections a band is made of by its slope, as rtd_plan in native\apo\rtdsp.h (see there): {type, f, gain, q}
        public static bool PlanCache = true;   // the check measures the curve with and without it
        public static List<double[]> Plan(EqBand b, double rate)
        {
            if (PlanCache && b.planOf != null && b.planType == b.Type && b.planSlope == b.Slope && b.planKey0 == b.Freq && b.planKey1 == b.Gain && b.planKey2 == b.Q && b.planRate == rate)
                return b.planOf;
            var made = MakePlan(b, rate);
            b.planOf = made; b.planType = b.Type; b.planSlope = b.Slope; b.planKey0 = b.Freq; b.planKey1 = b.Gain; b.planKey2 = b.Q; b.planRate = rate;
            return made;
        }
        static List<double[]> MakePlan(EqBand b, double rate)
        {
            var s = new List<double[]>();
            double f = Math.Max(10, Math.Min(Math.Min(22000, rate * 0.45), b.Freq)), g = b.HasGain ? Math.Max(-24, Math.Min(24, b.Gain)) : 0, q = Math.Max(0.1, Math.Min(20, b.Q));
            int n = Math.Max(1, Math.Min(EqBand.MaxSlope, b.Slope)), k = b.Type;
            if (k < 1 || k > 6) return s;
            if (n <= 1) { s.Add(new double[] { k, f, g, q }); return s; }
            if (k == 1)
            {
                double bw = Math.Max(0.6, 2.0 * Asinh(1.0 / (2.0 * q)) / Math.Log(2)), r = Math.Pow(2, bw / 2), top = rate * 0.45;
                double f2 = Math.Min(top, f * r), f1 = Math.Max(10, f / r), gg = g / n;
                for (int it = 0; it < 3 && Math.Abs(g) > 1e-4; it++)
                {
                    double c = 0; for (int j = 0; j < n; j++) { double cq = CutQ(n, j, 0.7071067811865476); c += SecDb(2, f2, gg, cq, f, rate) + SecDb(2, f1, -gg, cq, f, rate); }
                    if (Math.Abs(c) < 1e-6) break;
                    gg *= Math.Max(0.5, Math.Min(3, g / c));
                }
                for (int j = 0; j < n; j++) { double cq = CutQ(n, j, 0.7071067811865476); s.Add(new double[] { 2, f2, gg, cq }); s.Add(new double[] { 2, f1, -gg, cq }); }
                return s;
            }
            for (int j = 0; j < n; j++) s.Add(new double[] { k, f, k == 2 || k == 3 ? g / n : 0, k == 6 ? q : CutQ(n, j, q) });
            return s;
        }
        static double Asinh(double x) { return Math.Log(x + Math.Sqrt(x * x + 1)); }

        // ---- the curve: the same filters as rtd_coef in native\apo\rtdsp.h, as their analogue prototypes ----
        public static double Response(SoundParams p, double f, double rate, int only = -1)
        {
            double re = 1, im = 0;
            for (int i = 0; i < p.Bands.Count && i < MaxBands; i++)
            {
                if (only >= 0 && i != only) continue;
                foreach (var sec in Plan(p.Bands[i], rate)) SecMul((int)sec[0], sec[1], sec[2], sec[3], f, rate, ref re, ref im);
            }
            double mag = Math.Sqrt(re * re + im * im);
            return 20 * Math.Log10(Math.Max(1e-9, mag)) + (only >= 0 ? 0 : p.Preamp);
        }

        // ---- the spectrum of what plays: 2048 samples, Hann window, dB re full scale per frequency bin ----
        const int FftN = 2048;
        static readonly double[] hann = Enumerable.Range(0, FftN).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftN)).ToArray();

        public static double[] Spectrum(Guid id)
        {
            var x = Recent(id, FftN); if (x == null) return null;
            var re = new double[FftN]; var im = new double[FftN];
            for (int i = 0; i < FftN; i++) re[i] = x[i] * hann[i];
            Fft(re, im);
            var db = new double[FftN / 2];
            for (int i = 0; i < db.Length; i++) db[i] = 10 * Math.Log10(Math.Max(1e-12, (re[i] * re[i] + im[i] * im[i]) * 16.0 / ((double)FftN * FftN))); // a full-scale sine reads 0 dB
            return db;
        }
        public static int SpectrumSize { get { return FftN; } }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                    }
                }
            }
        }

        // ---- the output devices ----
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr n);
            int UnregisterControlChangeNotify(IntPtr n);
            int GetChannelCount(out int c);
            int SetMasterVolumeLevel(float level, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
            int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            int GetChannelVolumeLevel(uint ch, out float level);
            int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // the interface the Windows sound settings use to pick the default device (not in the SDK, the same since Windows 7)
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

        [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string dev, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string dev, int def, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string dev);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string dev, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string dev, int def, IntPtr defPeriod, IntPtr minPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string dev, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string dev, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string dev, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string dev, int fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string dev, int fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string dev, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string dev, int visible);
        }

        static string EndpointId(Guid id) { return (IsCapture(id) ? "{0.0.1.00000000}." : "{0.0.0.00000000}.") + id.ToString("B"); }

        // capture: the default microphone (eCapture) instead of the default output (eRender)
        public static Guid DefaultDevice(bool capture = false)
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator(); IMMDevice d; string id;
                if (en.GetDefaultAudioEndpoint(capture ? 1 : 0, 1, out d) != 0 || d == null || d.GetId(out id) != 0) return Guid.Empty;
                Guid g; return Guid.TryParse(id.Substring(id.LastIndexOf('{')), out g) ? g : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        // the output devices that are plugged in and switched on (DeviceState 1), the default one first
        // capture (21.11): the inputs instead of the outputs
        public static List<SoundDevice> Devices(bool capture = false)
        {
            var list = new List<SoundDevice>(); Guid def = DefaultDevice(capture);
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(capture ? CaptureKey : RenderKey))
                {
                    if (root == null) return list;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        Guid g; if (!Guid.TryParse(name, out g)) continue;
                        using (var k = root.OpenSubKey(name))
                        {
                            if (k == null || !(k.GetValue("DeviceState") is int) || (int)k.GetValue("DeviceState") != 1) continue;
                            using (var pk = k.OpenSubKey("Properties"))
                                list.Add(new SoundDevice
                                {
                                    Id = g, Default = g == def, Capture = capture,
                                    Name = (pk == null ? null : pk.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string) ?? name,
                                    Adapter = pk == null ? null : pk.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string,
                                });
                        }
                    }
                }
            }
            catch (Exception ex) { Store.Log("звук: список устройств: " + ex.Message); }
            return list.OrderByDescending(d => d.Default).ThenBy(d => d.Full).ToList();
        }

        // 21.11: a COM interface of the device, on the calling thread (MicListen: the recording client)
        public static object Activate(Guid id, Guid iid)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator(); IMMDevice d; object o;
            if (en.GetDevice(EndpointId(id), out d) != 0 || d == null) return null;
            return d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0 ? o : null;
        }

        static IAudioEndpointVolume Volume(Guid id)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator(); IMMDevice d; object o;
            if (en.GetDevice(EndpointId(id), out d) != 0 || d == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            return d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0 ? (IAudioEndpointVolume)o : null;
        }

        // 0..100, or -1 when the device does not answer
        public static int GetVolume(Guid id)
        {
            try { var v = Volume(id); float f; return v != null && v.GetMasterVolumeLevelScalar(out f) == 0 ? (int)Math.Round(f * 100) : -1; } catch { return -1; }
        }

        public static void SetVolume(Guid id, int percent)
        {
            try { var v = Volume(id); var ctx = Guid.Empty; if (v != null) v.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, percent)) / 100f, ref ctx); } catch (Exception ex) { Store.Quiet("звук: громкость", ex); }
        }

        public static bool? GetMute(Guid id)
        {
            try { var v = Volume(id); bool m; return v != null && v.GetMute(out m) == 0 ? m : (bool?)null; } catch { return null; }
        }

        public static void SetMute(Guid id, bool mute)
        {
            try { var v = Volume(id); var ctx = Guid.Empty; if (v != null) v.SetMute(mute, ref ctx); } catch (Exception ex) { Store.Quiet("звук: без звука", ex); }
        }

        // the device every program plays to (all three roles, as the Windows settings do)
        public static string SetDefault(Guid id)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigClient();
                for (int role = 0; role < 3; role++) { int hr = pc.SetDefaultEndpoint(EndpointId(id), role); if (hr != 0) return "0x" + hr.ToString("X8"); }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---- the effect in a device's chain ----
        public class FxState
        {
            public bool HasKey, Attached, Composite, EnhancementsOff, Nahimic, NahimicOff, OtherEndEffect;
            public bool ViaMode;      // attached in the mode slot (a device that takes no end effect)
            public bool EndUnread;    // only in the end slot of a device that declares none: Windows does not read it there (MoveToMode)
            public string[] Stream = new string[0], Mode = new string[0], End = new string[0]; // the effects Windows runs for the device, by stage
        }

        static RegistryKey FxKey(Guid id, bool write)
        {
            // an administrator may set values here but not create keys, so exactly these rights are asked for
            return Registry.LocalMachine.OpenSubKey(RootOf(id) + "\\" + id.ToString("B") + "\\FxProperties", write ? RegistryKeyPermissionCheck.ReadWriteSubTree : RegistryKeyPermissionCheck.ReadSubTree,
                write ? RegistryRights.QueryValues | RegistryRights.SetValue : RegistryRights.QueryValues);
        }

        static string[] List(RegistryKey k, string name)
        {
            var v = k.GetValue(name);
            var many = v as string[]; if (many != null) return many.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            var one = v as string; return string.IsNullOrWhiteSpace(one) || one.Trim('{', '}', '0', '-').Length == 0 ? new string[0] : new[] { one };
        }

        static bool Ours(string clsid) { return string.Equals(clsid, Clsid, StringComparison.OrdinalIgnoreCase); }

        public static FxState State(Guid id)
        {
            var st = new FxState();
            try
            {
                using (var k = FxKey(id, false))
                {
                    if (k == null) return st;
                    st.HasKey = true;
                    var names = k.GetValueNames();
                    st.Composite = names.Any(n => n.Equals(Fx + ",13", StringComparison.OrdinalIgnoreCase) || n.Equals(Fx + ",14", StringComparison.OrdinalIgnoreCase) || n.Equals(Fx + ",15", StringComparison.OrdinalIgnoreCase));
                    st.Stream = List(k, Fx + (st.Composite ? ",13" : ",5")); st.Mode = List(k, Fx + (st.Composite ? ",14" : ",6")); st.End = List(k, Fx + (st.Composite ? ",15" : ",7"));
                    st.ViaMode = !st.Composite && st.Mode.Any(Ours);
                    st.Attached = st.End.Any(Ours) || st.ViaMode;
                    st.OtherEndEffect = !st.Composite && st.End.Length > 0 && !st.End.Any(Ours);
                    if (!st.Composite && !st.ViaMode && st.End.Any(Ours))
                        using (var own = Registry.LocalMachine.OpenSubKey(OwnKey + "\\" + id.ToString("B")))
                            st.EndUnread = own != null && own.GetValue("CreatedModes") != null;
                    st.EnhancementsOff = Convert.ToInt32(k.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5") ?? 0) != 0;
                    st.Nahimic = NahimicSlots.Any(n => List(k, Fx + n).Any(IsNahimic));
                }
                if (!st.Nahimic)
                    using (var keep = Registry.LocalMachine.OpenSubKey(OwnKey + "\\" + id.ToString("B") + "\\Nahimic"))
                        st.NahimicOff = keep != null && keep.ValueCount > 0;
            }
            catch (Exception ex) { Store.Log("звук: состояние эффектов: " + ex.Message); }
            return st;
        }

        // ---- Nahimic's effects on a device ----
        // Nahimic's three effects ({670173E3/E4/E5-78CF-11E5-A837-0800200C9A66}: stream, mode, end) stand in the same
        // FxProperties, in the lists (13, 14, 15, and 19, 20 for hardware-offloaded streams) or in the single slots
        // (5, 6, 7, 11, 12). Off: every value that names one of them is kept as it was under the program's own key, then
        // written without them (a value left empty is deleted). On: the kept values go back, with Red Tune's own effect
        // where it stands now. Nahimic itself (its service, its app, its virtual devices) is not touched.
        static readonly string[] NahimicSlots = { ",13", ",14", ",15", ",19", ",20", ",5", ",6", ",7", ",11", ",12" };
        static bool IsNahimic(string clsid) { return clsid != null && clsid.StartsWith("{670173", StringComparison.OrdinalIgnoreCase) && clsid.EndsWith("-78CF-11E5-A837-0800200C9A66}", StringComparison.OrdinalIgnoreCase); }

        public static string SetNahimic(Guid id, bool on)
        {
            try
            {
                string keepPath = OwnKey + "\\" + id.ToString("B") + "\\Nahimic";
                using (var k = FxKey(id, true))
                {
                    if (k == null) return "у устройства нет раздела эффектов Windows";
                    bool present = NahimicSlots.Any(n => List(k, Fx + n).Any(IsNahimic));
                    if (!on)
                    {
                        if (!present) return null;
                        using (var keep = Registry.LocalMachine.CreateSubKey(keepPath))
                            foreach (var n in NahimicSlots)
                            {
                                var v = k.GetValue(Fx + n); if (v == null || !List(k, Fx + n).Any(IsNahimic)) continue;
                                keep.SetValue(n, v, k.GetValueKind(Fx + n));
                                var rest = List(k, Fx + n).Where(c => !IsNahimic(c)).ToArray();
                                if (rest.Length == 0) k.DeleteValue(Fx + n, false);
                                else if (v is string[]) k.SetValue(Fx + n, rest, RegistryValueKind.MultiString);
                                else k.SetValue(Fx + n, rest[0], RegistryValueKind.String);
                            }
                        Store.Log("звук: эффекты производителя убраны из цепочки устройства " + id.ToString("B"));
                        return null;
                    }
                    using (var keep = Registry.LocalMachine.OpenSubKey(keepPath))
                    {
                        if (keep == null) return present ? null : "исходные списки эффектов не сохранены";
                        // when they are back already (a driver update wrote its lists again) the kept copy is just dropped
                        if (!present)
                            foreach (var n in keep.GetValueNames())
                            {
                                var v = keep.GetValue(n); var many = v as string[];
                                if (many == null) { if (k.GetValue(Fx + n) == null) k.SetValue(Fx + n, (string)v, RegistryValueKind.String); continue; }
                                bool ours = List(k, Fx + n).Any(Ours);
                                var back = many.Where(c => !Ours(c) && !string.IsNullOrWhiteSpace(c));
                                // what another program put into the list meanwhile stays, after the kept ones
                                var added = List(k, Fx + n).Where(c => !Ours(c) && !many.Contains(c, StringComparer.OrdinalIgnoreCase));
                                k.SetValue(Fx + n, (ours ? new[] { Clsid } : new string[0]).Concat(back).Concat(added).ToArray(), RegistryValueKind.MultiString);
                            }
                    }
                }
                try { Registry.LocalMachine.DeleteSubKeyTree(keepPath, false); } catch { }
                Store.Log("звук: эффекты производителя возвращены в цепочку устройства " + id.ToString("B"));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string EffectName(string clsid)
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + clsid)) return (k == null ? null : k.GetValue("FriendlyName") as string) ?? clsid; }
            catch { return clsid; }
        }

        // where the dll Windows is told to load lies now (null = not registered or the file is gone)
        public static string RegisteredDll()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + Clsid + @"\InprocServer32"))
                {
                    var p = k == null ? null : k.GetValue(null) as string;
                    return p != null && File.Exists(p) ? p : null;
                }
            }
            catch { return null; }
        }

        public static string SourceDll { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio", "RedTuneApo.dll"); } }
        public static bool Available { get { return File.Exists(SourceDll); } }

        static string Hash(string file)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) using (var f = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(f).Take(6).Select(b => b.ToString("x2")));
        }

        // The dll goes where only administrators can write (Windows' sound process loads it, so it must not lie in a
        // folder any program could change): %ProgramFiles%\RedTune\Audio. The name carries the file's fingerprint, so a
        // new build never has to overwrite the one that is loaded; the old ones are deleted when they are free.
        public static string Register()
        {
            try
            {
                string src = SourceDll; if (!File.Exists(src)) return "нет файла " + src;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RedTune", "Audio");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, "RedTuneApo-" + Hash(src) + ".dll");
                if (!File.Exists(dst)) File.Copy(src, dst);
                bool fresh = !string.Equals(RegisteredDll(), dst, StringComparison.OrdinalIgnoreCase);
                if (fresh)
                {
                    var why = Regsvr(dst, false); if (why != null) return why;
                }
                // the effect's journal (Trace in apo.cpp): the file is made here, the sound process may only write into
                // it; a new build starts it empty
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(TracePath));
                    bool had = File.Exists(TracePath);
                    if (!had || fresh) using (new FileStream(TracePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                    if (!had) Grant(TracePath);
                }
                catch (Exception ex) { Store.Log("звук: журнал обработчика: " + ex.Message); }
                foreach (var old in Directory.GetFiles(dir, "RedTuneApo-*.dll")) if (!old.Equals(dst, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static string Regsvr(string dll, bool unregister)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "regsvr32.exe"), (unregister ? "/u " : "") + "/s \"" + dll + "\"") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return "regsvr32 не ответил"; }
                return p.ExitCode == 0 ? null : "regsvr32: код " + p.ExitCode;
            }
        }

        // Puts the effect into the device's chain, first among the end-stage effects: it gets the mixed sound as Windows
        // made it and hands it on in the same form, so whatever stood there (Realtek's speaker protection) works as before.
        // Nothing of another vendor is overwritten: on a device with effect lists the id is added to the list, on a device
        // with single slots it takes the end slot only when that is empty. Windows loads it after RestartAudio.
        public static string Attach(Guid id)
        {
            try
            {
                var why = Register(); if (why != null) return "обработчик не зарегистрирован: " + why;
                var st = State(id);
                if (!st.HasKey) return "у устройства нет раздела эффектов Windows";
                if (st.Attached) return null;
                if (st.OtherEndEffect) return "конечный эффект устройства занят другим обработчиком (" + EffectName(st.End[0]) + ")";
                using (var k = FxKey(id, true))
                using (var own = Registry.LocalMachine.CreateSubKey(OwnKey + "\\" + id.ToString("B")))
                {
                    if (st.Composite)
                    {
                        if (k.GetValue(Fx + ",15") == null) own.SetValue("CreatedList", 1);
                        // 21.11: on an input the end-stage effects work on the microphone's own sound before it goes on; the
                        // effect goes LAST among them, after the vendor's (Realtek's recording effect: its echo and noise
                        // handling sees the sound as the microphone gave it), on an output first, as before
                        bool input = IsCapture(id);
                        k.SetValue(Fx + ",15", (input ? st.End.Concat(new[] { Clsid }) : new[] { Clsid }.Concat(st.End)).ToArray(), RegistryValueKind.MultiString);
                    }
                    else if (st.End.Length == 0 && k.GetValue(Modes + ",7") == null)
                    {
                        // 19.11, Bluetooth headphones (Microsoft's A2DP driver): a device with single slots that declares
                        // no end effect. Windows reads neither the end slot nor the lists there (seen in the effect's
                        // journal: it was never made), only the stream and the mode slot. The effect takes the mode slot;
                        // what stood there (Windows' own «APO звука WM») is kept and put back by Detach.
                        ToModeSlot(k, own);
                        Store.Log("звук: обработчик подключён к устройству " + id.ToString("B") + " (в слот эффекта режима: конечный эффект устройство не принимает)");
                        return null;
                    }
                    else k.SetValue(Fx + ",7", Clsid, RegistryValueKind.String);
                    if (k.GetValue(Modes + ",7") == null) { k.SetValue(Modes + ",7", new[] { DefaultMode }, RegistryValueKind.MultiString); own.SetValue("CreatedModes", 1); }
                }
                Store.Log("звук: обработчик подключён к устройству " + id.ToString("B") + (st.Composite ? " (в список конечных эффектов)" : " (в слот конечного эффекта)"));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // the mode slot: ours in, the device's own kept under the program's key (an empty text = the slot was empty)
        static void ToModeSlot(RegistryKey k, RegistryKey own)
        {
            var was = k.GetValue(Fx + ",6") as string;
            if (!Ours(was)) { own.SetValue("ModeWas", was ?? "", RegistryValueKind.String); k.SetValue(Fx + ",6", Clsid, RegistryValueKind.String); }
            if (k.GetValue(Modes + ",6") == null) { k.SetValue(Modes + ",6", new[] { DefaultMode }, RegistryValueKind.MultiString); own.SetValue("CreatedModeModes", 1); }
        }

        // A device attached before 19.11 through the end slot it does not declare (Windows never read it): the effect
        // moves to the mode slot and what Attach had put into the end slot goes. True = something was changed, Windows'
        // sound has to be restarted for it.
        public static bool MoveToMode(Guid id)
        {
            try
            {
                if (!State(id).EndUnread) return false;
                using (var k = FxKey(id, true))
                using (var own = Registry.LocalMachine.CreateSubKey(OwnKey + "\\" + id.ToString("B")))
                {
                    ToModeSlot(k, own);
                    if (Ours(k.GetValue(Fx + ",7") as string)) k.DeleteValue(Fx + ",7", false);
                    k.DeleteValue(Modes + ",7", false); own.DeleteValue("CreatedModes", false);
                }
                Store.Log("звук: обработчик устройства " + id.ToString("B") + " перенесён в слот эффекта режима (конечный эффект устройство не принимает)");
                return true;
            }
            catch (Exception ex) { Store.Log("звук: перенос обработчика: " + ex.Message); return false; }
        }

        // takes away exactly what Attach put there
        public static string Detach(Guid id)
        {
            try
            {
                using (var k = FxKey(id, true))
                {
                    if (k == null) return null;
                    bool createdList = false, createdModes = false, createdModeModes = false; string modeWas = null;
                    using (var own = Registry.LocalMachine.OpenSubKey(OwnKey + "\\" + id.ToString("B")))
                        if (own != null)
                        {
                            createdList = own.GetValue("CreatedList") != null; createdModes = own.GetValue("CreatedModes") != null;
                            createdModeModes = own.GetValue("CreatedModeModes") != null; modeWas = own.GetValue("ModeWas") as string;
                        }
                    // the mode slot gets back what the device had there
                    if (Ours(k.GetValue(Fx + ",6") as string))
                    {
                        if (string.IsNullOrEmpty(modeWas)) k.DeleteValue(Fx + ",6", false); else k.SetValue(Fx + ",6", modeWas, RegistryValueKind.String);
                        if (createdModeModes) k.DeleteValue(Modes + ",6", false);
                    }
                    var list = k.GetValue(Fx + ",15") as string[];
                    if (list != null && list.Any(Ours))
                    {
                        var rest = list.Where(c => !Ours(c)).ToArray();
                        if (rest.Length == 0 && createdList) k.DeleteValue(Fx + ",15", false); else k.SetValue(Fx + ",15", rest, RegistryValueKind.MultiString);
                    }
                    if (Ours(k.GetValue(Fx + ",7") as string)) k.DeleteValue(Fx + ",7", false);
                    if (createdModes) k.DeleteValue(Modes + ",7", false);
                }
                // the notes of Attach go; what SetNahimic keeps under the same key (the lists as they were) stays
                try
                {
                    bool empty;
                    using (var own = Registry.LocalMachine.OpenSubKey(OwnKey + "\\" + id.ToString("B"), true))
                    {
                        if (own != null) foreach (var n in new[] { "CreatedList", "CreatedModes", "CreatedModeModes", "ModeWas" }) own.DeleteValue(n, false);
                        empty = own != null && own.SubKeyCount == 0 && own.ValueCount == 0;
                    }
                    if (empty) Registry.LocalMachine.DeleteSubKey(OwnKey + "\\" + id.ToString("B"), false);
                }
                catch { }
                Free(id);
                Store.Log("звук: обработчик снят с устройства " + id.ToString("B"));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // every device that has the effect in its chain, plugged in or not
        public static List<Guid> AttachedDevices()
        {
            var r = new List<Guid>();
            try
            {
                foreach (var key in new[] { RenderKey, CaptureKey }) // 21.11: the inputs too
                    using (var root = Registry.LocalMachine.OpenSubKey(key))
                        if (root != null) foreach (var name in root.GetSubKeyNames()) { Guid g; if (Guid.TryParse(name, out g) && State(g).Attached) r.Add(g); }
            }
            catch { }
            return r;
        }

        // 19.11, for the uninstaller (RedTune.exe --audio-uninstall): everything the sound page put into Windows is
        // taken back — Nahimic's effects where the page switched them off, the effect off every device and its
        // registration, and, after Windows Audio has let go of the file, the effect's files and the shared settings
        // file. Windows Audio is restarted only when something was actually attached or switched off. Returns what
        // was done, a line each, for the journal. Not run on the PC it was written on (that would have taken the
        // effect off the user's devices): it is made of the page's own steps, each tried there by hand.
        public static string Uninstall()
        {
            var done = new List<string>(); bool touched = false;
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(RenderKey))
                    if (root != null)
                        foreach (var name in root.GetSubKeyNames())
                        {
                            Guid g; if (!Guid.TryParse(name, out g)) continue;
                            FxState st; try { st = State(g); } catch { continue; }
                            if (st.NahimicOff) { var why = SetNahimic(g, true); done.Add("Nahimic back on " + name + (why != null ? ": " + why : "")); touched = true; }
                        }
            }
            catch (Exception ex) { done.Add("devices: " + ex.Message); }
            try { if (AttachedDevices().Count > 0 || RegisteredDll() != null) touched = true; } catch { }
            var first = RemoveAll(); done.Add("effect off the devices and unregistered" + (first != null ? ": " + first : ""));
            if (touched) { var why = RestartAudio(); done.Add("Windows Audio restarted" + (why != null ? ": " + why : "")); }
            foreach (var dirPath in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RedTune"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RedTune") })
                try { if (Directory.Exists(dirPath)) { Directory.Delete(dirPath, true); done.Add("removed " + dirPath); } } catch (Exception ex) { done.Add(dirPath + ": " + ex.Message); }
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\RedTune", false); } catch (Exception ex) { done.Add("registry: " + ex.Message); }
            return string.Join("\r\n", done);
        }

        // off every device, then the dll's registration; the file stays until Windows lets go of it (RestartAudio)
        public static string RemoveAll()
        {
            string first = null;
            foreach (var g in AttachedDevices()) { var why = Detach(g); if (why != null && first == null) first = why; }
            try { var dll = RegisteredDll(); if (dll != null) Regsvr(dll, true); } catch (Exception ex) { first = first ?? ex.Message; }
            return first;
        }

        // Windows reads a device's effects when its sound service starts: the service is stopped and started again
        // (sound stops for a second or two; programs that were playing pick it up again or need a pause and play)
        public static string RestartAudio()
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController("Audiosrv"))
                {
                    var deps = sc.DependentServices.Where(d => d.Status == System.ServiceProcess.ServiceControllerStatus.Running).Select(d => d.ServiceName).ToList();
                    if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                    sc.Start(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    foreach (var d in deps) try { using (var dc = new System.ServiceProcess.ServiceController(d)) { dc.Refresh(); if (dc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) dc.Start(); } } catch { }
                }
                Store.Log("звук: служба Windows Audio перезапущена");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---- settings of the devices and the presets ----
        public static readonly Dictionary<Guid, SoundParams> ByDevice = new Dictionary<Guid, SoundParams>();
        public static readonly List<SoundPreset> Presets = new List<SoundPreset>();
        static bool loaded; static DateTime stamp;

        static DateTime FileStamp() { try { return File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath) : DateTime.MinValue; } catch { return DateTime.MinValue; } }

        // the settings file was written by another copy of the program (a check from the command line): read it again.
        // Called when the page shows, before it takes a device's settings.
        public static void Refresh() { if (loaded && FileStamp() != stamp) { loaded = false; ByDevice.Clear(); } Load(); }

        static SoundPreset Built(string name, string text, bool mic = false) { return new SoundPreset { Name = name, P = SoundParams.Parse(text), BuiltIn = true, Mic = mic }; }

        public static List<SoundPreset> BuiltIn()
        {
            return new List<SoundPreset>
            {
                Built("Ровно", "eq=1 comp=0 lim=1 b=2,80,0,0.7 b=1,250,0,1 b=1,1000,0,1 b=1,4000,0,1 b=3,10000,0,0.7"),
                Built("Больше баса", "eq=1 comp=0 lim=1 pre=-3 b=2,110,6,0.7 b=1,250,-1.5,1 b=1,1000,0,1 b=1,4000,0,1 b=3,10000,1,0.7"),
                Built("Голос", "eq=1 comp=1 lim=1 thr=-22 ratio=3 att=8 rel=120 knee=8 mk=4 b=4,90,0,0.7 b=1,300,-2.5,1 b=1,2500,3.5,1 b=1,5000,2,1.4 b=3,10000,1.5,0.7"),
                Built("Динамики ноутбука", "eq=1 comp=1 lim=1 pre=-4 thr=-20 ratio=2.5 att=10 rel=160 knee=8 mk=3 b=4,70,0,0.7 b=2,180,6,0.7 b=1,450,-3,1.2 b=1,3000,2.5,1 b=3,9000,3.5,0.7"),
                Built("Ночной режим", "eq=1 comp=1 lim=1 thr=-30 ratio=5 att=5 rel=250 knee=10 mk=10 b=2,100,-4,0.7 b=1,250,0,1 b=1,1000,0,1 b=1,3000,2,1 b=3,10000,0,0.7"),
                // 21.11, the microphone's own: a low cut against rumble and breath, presence for clarity, an even level, and
                // the gate (its threshold depends on the microphone and the room: a start to move with the level in sight)
                Built("Ровно", "eq=1 comp=0 lim=1 ceil=-1 gate=0 gthr=-50 grange=-40 grel=150 b=4,80,0,0.7 b=1,250,0,1 b=1,1000,0,1 b=1,4000,0,1 b=3,10000,0,0.7", true),
                Built("Голос", "eq=1 comp=1 lim=1 ceil=-1 thr=-24 ratio=3 att=5 rel=120 knee=8 mk=6 gate=1 gthr=-50 grange=-30 grel=150 dn=1 dns=80 ds=1 dshz=6500 dsthr=-15 dsrange=-6 b=4,90,0,0.7 b=1,300,-2,1 b=1,1000,0,1 b=1,3000,3,1 b=3,10000,1.5,0.7", true),
                Built("Тихая комната", "eq=1 comp=1 lim=1 ceil=-1 thr=-26 ratio=2.5 att=8 rel=150 knee=8 mk=4 gate=1 gthr=-55 grange=-20 grel=250 b=4,70,0,0.7 b=1,250,-1,1 b=1,1000,0,1 b=1,3500,2,1 b=3,10000,1,0.7", true),
                Built("Шумно вокруг", "eq=1 comp=1 lim=1 ceil=-1 thr=-22 ratio=4 att=4 rel=100 knee=6 mk=5 gate=1 gthr=-40 grange=-40 grel=100 dn=1 dns=100 b=4,120,0,0.7 b=1,400,-3,1 b=1,1000,0,1 b=1,3000,3.5,1 b=3,8000,-2,0.7", true),
            };
        }

        public static void Load()
        {
            if (loaded) return; loaded = true;
            Presets.Clear(); Presets.AddRange(BuiltIn()); stamp = FileStamp();
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    var x = line.Split('\t'); Guid g;
                    if (x.Length >= 3 && x[0] == "device" && Guid.TryParse(x[1], out g)) ByDevice[g] = SoundParams.Parse(x[2]);
                    else if (x.Length >= 3 && (x[0] == "preset" || x[0] == "micpreset"))
                    {
                        bool mic = x[0] == "micpreset"; // 21.11: the microphone's presets on lines of their own
                        if (!Presets.Any(p => p.Name == x[1] && p.Mic == mic)) Presets.Add(new SoundPreset { Name = x[1], P = SoundParams.Parse(x[2]), Mic = mic });
                    }
                }
            }
            catch (Exception ex) { Store.Log("звук: настройки: " + ex.Message); }
        }

        public static void Save()
        {
            try
            {
                var lines = ByDevice.Select(kv => "device\t" + kv.Key.ToString("B") + "\t" + kv.Value).Concat(Presets.Where(p => !p.BuiltIn).Select(p => (p.Mic ? "micpreset\t" : "preset\t") + p.Name.Replace('\t', ' ') + "\t" + p.P));
                Directory.CreateDirectory(Store.DataDir);
                File.WriteAllLines(SettingsPath, lines, new UTF8Encoding(true)); stamp = FileStamp();
            }
            catch (Exception ex) { Store.Log("звук: сохранение: " + ex.Message); }
        }

        public static SoundParams For(Guid id)
        {
            Load();
            SoundParams p;
            if (!ByDevice.TryGetValue(id, out p))
            {
                // 21.11: a microphone starts from its own «Ровно» (a low cut, the gate ready but off)
                var start = IsCapture(id) ? Presets.FirstOrDefault(x => x.Mic && x.BuiltIn) : null;
                p = start != null ? start.P.Clone() : SoundParams.Start(); ByDevice[id] = p;
            }
            return p;
        }

        // at the program's start: the saved settings go into the shared file for every device the effect is attached to
        // (the file may be new, or hold what another copy left), nothing in the system is changed
        public static void Init()
        {
            try
            {
                Load();
                var attached = AttachedDevices();
                foreach (var g in attached) Write(g, For(g), true);
                // 19.11: the program came with a newer effect file than the one Windows has loaded (an update): it is
                // registered and Windows' sound is restarted once, so the devices that use the effect get the new one.
                // Off the window's thread: the restart takes a second or two.
                if (attached.Count > 0 && Available)
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            // a device attached the way Windows does not read on it gets the way it does (once)
                            bool moved = false; foreach (var g in attached) if (MoveToMode(g)) moved = true;
                            string have = RegisteredDll(), want = "RedTuneApo-" + Hash(SourceDll) + ".dll";
                            if (have != null && Path.GetFileName(have).Equals(want, StringComparison.OrdinalIgnoreCase))
                            {
                                if (moved) { var w = RestartAudio(); if (w != null) Store.Log("звук: перезапуск Windows Audio: " + w); }
                                return;
                            }
                            var why = Register(); if (why == null) why = RestartAudio();
                            Store.Log("звук: обработчик обновлён до новой сборки" + (why == null ? "" : ": не удалось: " + why));
                            // 21.11: a new layout of the shared file could not be made while the old effect held it: now
                            // that Windows let go of it, the settings once more (the new effect finds the file by itself)
                            foreach (var g in attached) Write(g, For(g), true);
                        }
                        catch (Exception ex) { Store.Log("звук: обновление обработчика: " + ex.Message); }
                    });
            }
            catch (Exception ex) { Store.Log("звук: запуск: " + ex.Message); }
        }

        // --audio-status <file>: what the program sees, a check (reads only)
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("dll in the program: " + SourceDll + (Available ? "" : "  (MISSING)"));
            sb.AppendLine("registered dll: " + (RegisteredDll() ?? "none"));
            sb.AppendLine("shared file: " + SharedPath + (File.Exists(SharedPath) ? "  (" + new FileInfo(SharedPath).Length + " bytes)" : "  (none)"));
            sb.AppendLine("settings: " + SettingsPath + (File.Exists(SettingsPath) ? "" : "  (none)"));
            sb.AppendLine("last calls: " + DiagText());
            Load();
            foreach (var d in Devices())
            {
                var st = State(d.Id); var m = Meters(d.Id);
                sb.AppendLine();
                sb.AppendLine((d.Default ? "* " : "  ") + d.Full + "  " + d.Id.ToString("B") + "  volume " + GetVolume(d.Id) + " %");
                sb.AppendLine("    effects key: " + (st.HasKey ? (st.Composite ? "lists" : "single slots") : "NONE") + (st.EnhancementsOff ? ", enhancements switched off" : "") + (st.Nahimic ? ", Nahimic" : "") + (st.Attached ? ", RED TUNE ATTACHED" + (st.ViaMode ? " (mode slot)" : st.EndUnread ? " (IN AN END SLOT WINDOWS DOES NOT READ HERE)" : "") : ""));
                sb.AppendLine("    stream: " + string.Join(" | ", st.Stream.Select(EffectName)));
                sb.AppendLine("    mode:   " + string.Join(" | ", st.Mode.Select(EffectName)));
                sb.AppendLine("    end:    " + string.Join(" | ", st.End.Select(EffectName)));
                if (m != null) sb.AppendLine("    meters: beat " + m.Beat + ", " + m.Rate + " Hz, " + m.Channels + " ch, in " + m.InPeak.ToString("0.000") + ", out " + m.OutPeak.ToString("0.000") + ", comp " + m.GrDb.ToString("0.0") + " dB, limiter " + m.LimDb.ToString("0.0") + " dB, pid " + m.Pid + ", faults " + m.Faults);
                if (m != null) sb.AppendLine("    compressor by parts (bass or whole, middle, top): gain " + string.Join(" / ", m.PartGr.Select(v => v.ToString("0.0"))) + " dB, level " + string.Join(" / ", m.PartLv.Select(v => v.ToString("0.0"))) + " dB");
                SoundParams p; if (ByDevice.TryGetValue(d.Id, out p)) sb.AppendLine("    settings: " + p);
            }
            sb.AppendLine();
            sb.AppendLine("the effect's journal (" + TracePath + "), the last lines:");
            sb.AppendLine(TraceTail(60));
            return sb.ToString();
        }

        // the checks of Program.cs (--audio-attach, --audio-detach, --audio-set, --audio-meters): what was done, as text
        public static string Cli(string cmd, string device, string text)
        {
            var sb = new StringBuilder();
            try
            {
                Load();
                if (cmd == "--audio-detach" && device == "all")
                {
                    sb.AppendLine("remove from all devices: " + (RemoveAll() ?? "ok"));
                    sb.AppendLine("restart of Windows Audio: " + (RestartAudio() ?? "ok"));
                    return sb.ToString();
                }
                Guid id; if (device == "default") id = DefaultDevice(); else if (!Guid.TryParse(device, out id)) return "bad device id" + Environment.NewLine;
                sb.AppendLine("device: " + id.ToString("B"));
                if (cmd == "--audio-attach")
                {
                    var p = For(id); p.On = true;
                    var why = Attach(id); sb.AppendLine("attach: " + (why ?? "ok"));
                    if (why == null) { sb.AppendLine("settings into the shared file: " + (Write(id, p, true) ? "ok" : "FAILED")); Save(); sb.AppendLine("restart of Windows Audio: " + (RestartAudio() ?? "ok")); }
                    sb.AppendLine("registered dll: " + (RegisteredDll() ?? "none"));
                }
                else if (cmd == "--audio-detach")
                {
                    SoundParams p; if (ByDevice.TryGetValue(id, out p)) { p.On = false; Save(); }
                    sb.AppendLine("detach: " + (Detach(id) ?? "ok"));
                    sb.AppendLine("restart of Windows Audio: " + (RestartAudio() ?? "ok"));
                }
                else if (cmd == "--audio-set")
                {
                    var p = SoundParams.Parse(text);
                    sb.AppendLine("settings: " + p);
                    sb.AppendLine("written: " + (Write(id, p) ? "ok" : "NO (no shared file or no slot: the effect is not attached)"));
                }
                else if (cmd == "--audio-nahimic")
                {
                    sb.AppendLine("Nahimic " + text + ": " + (SetNahimic(id, text == "on") ?? "ok"));
                    sb.AppendLine("restart of Windows Audio: " + (RestartAudio() ?? "ok"));
                    var s2 = State(id);
                    sb.AppendLine("stream: " + string.Join(" | ", s2.Stream.Select(EffectName)));
                    sb.AppendLine("mode:   " + string.Join(" | ", s2.Mode.Select(EffectName)));
                    sb.AppendLine("Nahimic present: " + s2.Nahimic + ", kept for return: " + s2.NahimicOff);
                }
                else
                {
                    var m = Meters(id);
                    if (m == null) sb.AppendLine("no slot");
                    else
                    {
                        uint b0 = m.Beat; Thread.Sleep(500); m = Meters(id);
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "beat {0} (+{1} in 0.5 s), {2} Hz, {3} ch, in {4:0.0000} ({5:0.00} dB), out {6:0.0000} ({7:0.00} dB), comp {8:0.00} dB, limiter {9:0.00} dB, pid {10}, faults {11}",
                            m.Beat, m.Beat - b0, m.Rate, m.Channels, m.InPeak, 20 * Math.Log10(Math.Max(1e-9, m.InPeak)), m.OutPeak, 20 * Math.Log10(Math.Max(1e-9, m.OutPeak)), m.GrDb, m.LimDb, m.Pid, m.Faults));
                        try { sb.AppendLine("process " + m.Pid + ": " + System.Diagnostics.Process.GetProcessById((int)m.Pid).ProcessName); } catch { sb.AppendLine("process " + m.Pid + ": gone"); }
                    }
                }
                var st = State(id);
                sb.AppendLine("end effects now: " + string.Join(" | ", st.End.Select(EffectName)) + (st.Attached ? "  (attached)" : "  (not attached)"));
            }
            catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }
            return sb.ToString();
        }

        // --audio-response <file>: the curve of the bench's test equalizer at the bench's frequencies (native\apo\apotest.cpp
        // prints the same table from the effect itself), a check that the page draws what the effect plays
        public static string ResponseCheck()
        {
            var p = SoundParams.Parse("pre=-3 b=4,30,0,0.707 b=2,100,6,0.7 b=1,1000,-8,2 b=1,3000,5,1 b=3,8000,4,0.7 b=6,5000,0,8");
            var sb = new StringBuilder();
            foreach (var f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 3000, 5000, 8000, 12000, 16000 })
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,6} Hz  {1,8:0.00} dB", f, Response(p, f, 48000)));
            return sb.ToString();
        }
    }
}
