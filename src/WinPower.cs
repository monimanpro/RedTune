using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace RedTune
{
    class PowerPlan { public Guid Id; public string Name; public bool Active; }

    // Windows power plans (powrprof)
    static class WinPower
    {
        [DllImport("powrprof.dll")] static extern uint PowerEnumerate(IntPtr root, IntPtr sub, IntPtr setting, uint access, uint index, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, IntPtr buffer, ref uint size);
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        public static Guid Active()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return Guid.Empty;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); } finally { LocalFree(p); }
        }

        public static List<PowerPlan> List()
        {
            var list = new List<PowerPlan>();
            var active = Active();
            for (uint i = 0; ; i++)
            {
                var buf = new byte[16]; uint size = 16;
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buf, ref size) != 0) break;
                var id = new Guid(buf);
                list.Add(new PowerPlan { Id = id, Name = Name(id), Active = id == active });
            }
            return list;
        }

        static string Name(Guid id)
        {
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size == 0) return id.ToString();
            var p = Marshal.AllocHGlobal((int)size);
            try { return PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, p, ref size) == 0 ? Marshal.PtrToStringUni(p) : id.ToString(); }
            finally { Marshal.FreeHGlobal(p); }
        }

        public static void SetActive(Guid id)
        {
            if (id == Guid.Empty || id == Active()) return;
            PowerSetActiveScheme(IntPtr.Zero, ref id);
        }

        public static void OpenControlPanel() { Process.Start("control.exe", "/name Microsoft.PowerOptions"); }

        // ---------- 17.11: a plan for tuning ----------
        // A plan of its own for the auto-tune and for checks of the curve. It is a copy of Windows' «High performance»,
        // so the power mode of Windows' slider is not laid over it: on 17.11 «best power efficiency» over the Balance plan
        // held every core at 4500 MHz on mains power, and a check of the curve ran 1 GHz under what the auto-tune tests.
        // With it the processor may go to the very bottom and to the very top: minimum state 5 % (the copy has 100 %, the
        // clock never falls), maximum 100 %, no clock cap, boost aggressive, the energy preference of the Balance plan
        // (a light load then runs the middle clocks instead of the top one, and the middle of the curve is tested too).
        // Made when the user presses the button on the power page; nothing else in the program creates plans.
        [DllImport("powrprof.dll")] static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, ref IntPtr destination);
        [DllImport("powrprof.dll")] static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, uint size);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDescription(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, uint size);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

        static readonly Guid HighPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        static Guid Processor = new Guid("54533251-82be-4824-96c1-47b60b740d00");

        // what the plan sets, on mains power and on battery alike. The settings come in threes: one for each efficiency
        // class of cores; a class the processor does not have is skipped by Windows
        public class PlanValue { public string Name, What; public Guid Id; public uint Value; }
        public static readonly PlanValue[] TuningValues =
        {
            new PlanValue { Name = "PROCTHROTTLEMIN",  What = "минимальное состояние процессора, %", Id = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"), Value = 5 },
            new PlanValue { Name = "PROCTHROTTLEMIN1", What = null, Id = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d"), Value = 5 },
            new PlanValue { Name = "PROCTHROTTLEMIN2", What = null, Id = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964e"), Value = 5 },
            new PlanValue { Name = "PROCTHROTTLEMAX",  What = "максимальное состояние процессора, %", Id = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), Value = 100 },
            new PlanValue { Name = "PROCTHROTTLEMAX1", What = null, Id = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed"), Value = 100 },
            new PlanValue { Name = "PROCTHROTTLEMAX2", What = null, Id = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ee"), Value = 100 },
            new PlanValue { Name = "PROCFREQMAX",      What = "потолок частоты, МГц (0 — нет)", Id = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100"), Value = 0 },
            new PlanValue { Name = "PROCFREQMAX1",     What = null, Id = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101"), Value = 0 },
            new PlanValue { Name = "PROCFREQMAX2",     What = null, Id = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e102"), Value = 0 },
            new PlanValue { Name = "PERFBOOSTMODE",    What = "буст (2 — агрессивный)", Id = new Guid("be337238-0d82-4146-a960-4f3749d470c7"), Value = 2 },
            new PlanValue { Name = "PERFEPP",          What = "предпочтение энергии, % (как в «Балансе»)", Id = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), Value = 25 },
            new PlanValue { Name = "PERFEPP1",         What = null, Id = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), Value = 33 },
            new PlanValue { Name = "PERFEPP2",         What = null, Id = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6865"), Value = 33 },
        };

        // Makes the plan (a copy of «High performance»; of the active plan when Windows has no such plan to copy), or, when
        // the plan made before is still there, only writes the values into it again. Returns its id; report: what the plan
        // holds now, read back, and whether a power mode of Windows is laid over the plan that is active.
        public static Guid MakeTuningPlan(string name, string description, Guid existing, out string report)
        {
            var lines = new List<string>();
            Guid id = existing; bool made = false;
            if (id == Guid.Empty || !List().Exists(p => p.Id == id))
            {
                Guid from = HighPerformance; IntPtr dest = IntPtr.Zero;
                uint rc = PowerDuplicateScheme(IntPtr.Zero, ref from, ref dest);
                if (rc != 0) { from = Active(); dest = IntPtr.Zero; rc = PowerDuplicateScheme(IntPtr.Zero, ref from, ref dest); lines.Add("Копия сделана с активной схемы: «Высокой производительности» в этой Windows нет"); }
                if (rc != 0 || dest == IntPtr.Zero) throw new Exception("Windows не дала скопировать схему (код " + rc + ")");
                try { id = (Guid)Marshal.PtrToStructure(dest, typeof(Guid)); } finally { LocalFree(dest); }
                made = true;
            }
            var nm = System.Text.Encoding.Unicode.GetBytes(name + "\0"); PowerWriteFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, nm, (uint)nm.Length);
            var ds = System.Text.Encoding.Unicode.GetBytes(description + "\0"); PowerWriteDescription(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, ds, (uint)ds.Length);
            foreach (var v in TuningValues)
            {
                var setting = v.Id;
                uint a = PowerWriteACValueIndex(IntPtr.Zero, ref id, ref Processor, ref setting, v.Value), d = PowerWriteDCValueIndex(IntPtr.Zero, ref id, ref Processor, ref setting, v.Value);
                uint now; bool read = PowerReadACValueIndex(IntPtr.Zero, ref id, ref Processor, ref setting, out now) == 0;
                if (v.What != null) lines.Add(v.What + ": " + (read ? now.ToString() : "не читается") + (a != 0 || d != 0 || (read && now != v.Value) ? "  (не записалось)" : ""));
            }
            if (id == Active()) { PowerSetActiveScheme(IntPtr.Zero, ref id); } // the values take effect
            try { Guid o; if (id == Active() && PowerGetEffectiveOverlayScheme(out o) == 0 && o != Guid.Empty) lines.Add("Внимание: поверх схемы действует режим питания Windows, он может держать свой потолок частоты"); } catch { }
            lines.Insert(0, made ? "Схема создана" : "Схема уже была, значения записаны заново");
            Store.Log("схема «" + name + "» " + id + ": " + string.Join("; ", lines));
            report = string.Join("\n", lines);
            return id;
        }

        // ---------- 18.11: the processor settings of a plan, read and written on the power page ----------
        // Five settings the power page shows for a plan, on mains power and on battery apart. A setting has a copy for every
        // efficiency class of cores (the ids differ in the last digit). The page shows the value of class 0 and writes its
        // value to every class the plan has, so a processor with two kinds of cores gets the same value on both.
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadACDefaultIndex(IntPtr root, ref Guid personality, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCDefaultIndex(IntPtr root, ref Guid personality, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);

        public enum Cpu { FreqMax, Boost, Epp, MinState, MinCores }
        static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        static readonly Guid[][] CpuIds =
        {
            new[] { new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100"), new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101"), new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e102") }, // PROCFREQMAX: MHz, 0 is no cap
            new[] { new Guid("be337238-0d82-4146-a960-4f3749d470c7") },                                                                                               // PERFBOOSTMODE: 0..6
            new[] { new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6865") }, // PERFEPP: 0 performance .. 100 saving
            new[] { new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"), new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d"), new Guid("893dee8e-2bef-41e0-89c6-b55d0929964e") }, // PROCTHROTTLEMIN: %
            new[] { new Guid("0cc5b647-c1df-4637-891a-dec35c318583"), new Guid("0cc5b647-c1df-4637-891a-dec35c318584") },                                             // CPMINCORES: % of cores never parked
        };

        static bool ReadIndex(Guid plan, Guid setting, bool dc, out uint v)
        {
            return (dc ? PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref Processor, ref setting, out v)
                       : PowerReadACValueIndex(IntPtr.Zero, ref plan, ref Processor, ref setting, out v)) == 0;
        }

        // the value of class 0: the one the page shows
        public static bool ReadCpu(Guid plan, Cpu what, bool dc, out uint v) { return ReadIndex(plan, CpuIds[(int)what][0], dc, out v); }

        // the value of every class; null where the plan has no such setting
        public static uint?[] ReadCpuClasses(Guid plan, Cpu what, bool dc)
        {
            return CpuIds[(int)what].Select(id => { uint v; return ReadIndex(plan, id, dc, out v) ? v : (uint?)null; }).ToArray();
        }

        // Writes the values class by class (null: that class is left alone); a class the plan does not have is skipped.
        // The active plan is set again, so the values take effect at once. Returns Windows' error code, 0 when written.
        public static uint WriteCpuClasses(Guid plan, Cpu what, bool dc, uint?[] values)
        {
            uint rc = 0; var ids = CpuIds[(int)what]; var done = new List<string>();
            for (int k = 0; k < ids.Length && k < values.Length; k++)
            {
                uint was; var id = ids[k];
                if (values[k] == null || !ReadIndex(plan, id, dc, out was)) continue;
                uint r = dc ? PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref Processor, ref id, values[k].Value)
                            : PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref Processor, ref id, values[k].Value);
                if (r != 0) rc = r;
                done.Add(was + " → " + values[k].Value + (r != 0 ? " (код " + r + ")" : ""));
            }
            if (done.Count == 0) rc = 2; // the plan has no such setting
            if (plan == Active()) PowerSetActiveScheme(IntPtr.Zero, ref plan);
            Store.Log("схема " + plan + ", " + what + (dc ? ", от батареи: " : ", от сети: ") + (done.Count > 0 ? string.Join(", ", done) : "такой настройки нет"));
            return rc;
        }

        public static uint WriteCpu(Guid plan, Cpu what, bool dc, uint value)
        {
            return WriteCpuClasses(plan, what, dc, CpuIds[(int)what].Select(id => (uint?)value).ToArray());
        }

        // what Windows' own «Balanced» plan has for the setting out of the box, class by class
        public static uint?[] DefaultCpuClasses(Cpu what, bool dc)
        {
            return CpuIds[(int)what].Select(id =>
            {
                uint v; var s = id; var b = Balanced;
                uint r = dc ? PowerReadDCDefaultIndex(IntPtr.Zero, ref b, ref Processor, ref s, out v) : PowerReadACDefaultIndex(IntPtr.Zero, ref b, ref Processor, ref s, out v);
                return r == 0 ? v : (uint?)null;
            }).ToArray();
        }

        // a power mode of Windows («best power efficiency», «best performance») is laid over the plan, which is the active
        // one: the mode can hold values of its own over the plan's (17.11: a 4500 MHz cap on mains power)
        public static bool Overlaid(Guid plan)
        {
            try { Guid o; return plan == Active() && PowerGetEffectiveOverlayScheme(out o) == 0 && o != Guid.Empty; } catch { return false; }
        }

        // 19.11: the kind of a plan («Тип схемы электропитания», a setting of the plan outside the subgroups: 0 power saver,
        // 1 high performance, 2 balanced). Windows lays its power mode only over plans of the balanced kind; a plan made
        // from «High performance» is not touched by it. Read on this PC with powercfg: «Сбалансированная» and «Balance»
        // 2, «Max» and «Тюнинг» 1. A plan whose kind cannot be read counts as balanced (the mode's row then stays).
        static Guid NoSubgroup = new Guid("fea3413e-7e05-4911-9a71-700331f1c294"), PlanKind = new Guid("245d8541-3943-4422-b025-13a784f679b7");
        public static bool TakesMode(Guid plan, bool dc)
        {
            try
            {
                uint v;
                uint rc = dc ? PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref NoSubgroup, ref PlanKind, out v) : PowerReadACValueIndex(IntPtr.Zero, ref plan, ref NoSubgroup, ref PlanKind, out v);
                return rc != 0 || v == 2;
            }
            catch { return true; }
        }

        // 19.11, later (the user's wish): the kind of a plan read and written from the plan block. One kind for the plan:
        // the same value goes to its mains side and to its battery side (Windows keeps one for each; the plans seen here
        // have them equal), the mains one is what is shown. Besides the power mode, Windows takes from the kind the
        // values of the settings the plan itself does not hold.
        public static bool ReadKind(Guid plan, out uint kind)
        {
            kind = 0;
            try { return PowerReadACValueIndex(IntPtr.Zero, ref plan, ref NoSubgroup, ref PlanKind, out kind) == 0; } catch { return false; }
        }
        public static uint WriteKind(Guid plan, uint kind)
        {
            uint was; bool read = ReadKind(plan, out was);
            uint a = PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref NoSubgroup, ref PlanKind, kind), d = PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref NoSubgroup, ref PlanKind, kind);
            if (plan == Active()) PowerSetActiveScheme(IntPtr.Zero, ref plan); // takes effect at once
            Store.Log("схема " + plan + ", тип схемы: " + (read ? was.ToString() : "?") + " → " + kind + (a != 0 || d != 0 ? " (код " + (a != 0 ? a : d) + ")" : ""));
            return a != 0 ? a : d;
        }

        // the plan made for tuning no longer holds the values it was made with (TuningValues): they were changed in
        // Windows' own settings or by another program. A setting the plan does not have is not counted.
        public static bool TuningDrift(Guid plan)
        {
            foreach (var v in TuningValues)
                for (int d = 0; d < 2; d++) { uint now; if (ReadIndex(plan, v.Id, d == 1, out now) && now != v.Value) return true; }
            return false;
        }

        // for the checks: a copy of the active plan to write into, and its removal
        public static Guid MakeTestCopy()
        {
            Guid from = Active(); IntPtr dest = IntPtr.Zero;
            uint rc = PowerDuplicateScheme(IntPtr.Zero, ref from, ref dest);
            if (rc != 0 || dest == IntPtr.Zero) throw new Exception("the active plan could not be copied (code " + rc + ")");
            Guid id;
            try { id = (Guid)Marshal.PtrToStructure(dest, typeof(Guid)); } finally { LocalFree(dest); }
            var nm = System.Text.Encoding.Unicode.GetBytes("Red Tune проба\0"); PowerWriteFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, nm, (uint)nm.Length);
            return id;
        }
        public static bool DeleteTestCopy(Guid id) { return id != Active() && PowerDeleteScheme(IntPtr.Zero, ref id) == 0 && !List().Exists(p => p.Id == id); }

        // ---------- 18.11: Windows' power mode ----------
        // «Режим питания» of Windows' settings: a set of values laid over every plan based on «Balanced» (a plan based on
        // «High performance» is not touched by it). Since Windows 11 24H2 there is one for mains power and one for
        // battery; an older Windows has one for both, and for it the two functions of the overlay are used instead (that
        // path is written by the documentation and LLT's code and was not run here).
        [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid mode);

        public static readonly Guid ModeEfficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), ModeBalanced = Guid.Empty, ModePerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        public static string ModeName(Guid m) { return m == ModeEfficiency ? "Эффективность" : m == ModeBalanced ? "Сбалансированный" : m == ModePerformance ? "Производительность" : m.ToString(); }

        public static bool ReadMode(bool dc, out Guid mode)
        {
            mode = Guid.Empty;
            try { return (dc ? PowerGetUserConfiguredDCPowerMode(out mode) : PowerGetUserConfiguredACPowerMode(out mode)) == 0; }
            catch (EntryPointNotFoundException) { }
            try { return PowerGetEffectiveOverlayScheme(out mode) == 0; } catch { return false; }
        }

        public static uint WriteMode(bool dc, Guid mode)
        {
            uint rc;
            try { rc = dc ? PowerSetUserConfiguredDCPowerMode(ref mode) : PowerSetUserConfiguredACPowerMode(ref mode); }
            catch (EntryPointNotFoundException) { rc = PowerSetActiveOverlayScheme(mode); }
            PowerModeWatch.Own();
            Store.Log("режим питания Windows, " + (dc ? "от батареи" : "от сети") + ": " + ModeName(mode) + (rc != 0 ? " (код " + rc + ")" : ""));
            return rc;
        }

        // what a mode holds over a plan (the balanced mode holds nothing: the plan's own values act)
        public static bool ReadModeValue(Guid mode, Cpu what, bool dc, out uint v) { v = 0; return mode != Guid.Empty && ReadIndex(mode, CpuIds[(int)what][0], dc, out v); }

        // for --power-mode-check: the mode now and what each mode holds (read); then the mode of the power source the
        // computer is NOT on is changed and put back, to see that the write works without changing what acts now
        public static string PowerModeCheck()
        {
            var sb = new System.Text.StringBuilder();
            Guid a, d; bool ra = ReadMode(false, out a), rd = ReadMode(true, out d);
            sb.AppendLine("mains: " + (ra ? ModeName(a) : "not read") + "   battery: " + (rd ? ModeName(d) : "not read"));
            foreach (var m in new[] { ModeEfficiency, ModePerformance })
                for (int k = 0; k < 2; k++)
                {
                    uint f, e, mn, c;
                    sb.AppendLine("  " + ModeName(m).PadRight(18) + (k == 1 ? "battery" : "mains  ") + ": clock cap " + (ReadModeValue(m, Cpu.FreqMax, k == 1, out f) ? f.ToString() : "-") + ", energy preference " + (ReadModeValue(m, Cpu.Epp, k == 1, out e) ? e.ToString() : "-")
                        + ", minimum state " + (ReadModeValue(m, Cpu.MinState, k == 1, out mn) ? mn.ToString() : "-") + ", minimum cores " + (ReadModeValue(m, Cpu.MinCores, k == 1, out c) ? c.ToString() : "-"));
                }
            bool onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
            bool side = !onBattery; // true: the battery side is written (the computer is on mains power)
            Guid was; if (!ReadMode(side, out was)) { sb.AppendLine("the mode is not read: no write test"); return sb.ToString(); }
            Guid other = was == ModePerformance ? ModeBalanced : ModePerformance, got, back, untouched;
            uint w1 = WriteMode(side, other); ReadMode(side, out got); ReadMode(!side, out untouched);
            uint w2 = WriteMode(side, was); ReadMode(side, out back);
            bool ok = w1 == 0 && w2 == 0 && got == other && back == was && untouched == (side ? a : d);
            sb.AppendLine("the " + (side ? "battery" : "mains") + " side (not in use now): " + ModeName(was) + " -> " + ModeName(got) + " -> " + ModeName(back) + ", codes " + w1 + " " + w2 + ", the other side stayed " + ModeName(untouched) + (ok ? "  ok" : "  FAILED"));
            return sb.ToString();
        }

        // ---------- 18.11: the screen brightness a plan keeps ----------
        static Guid Video = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        static readonly Guid Brightness = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb"); // VIDEONORMALLEVEL, %

        public static bool ReadBrightness(Guid plan, bool dc, out uint v)
        {
            var b = Brightness;
            return (dc ? PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref Video, ref b, out v) : PowerReadACValueIndex(IntPtr.Zero, ref plan, ref Video, ref b, out v)) == 0;
        }

        // The brightness into the plans, for mains power and for battery; a plan that has it already is not written, a plan
        // without the setting is skipped. The active plan is not set again: the screen is at this brightness already.
        // Returns how many plans were changed.
        public static int SetBrightness(IEnumerable<Guid> plans, uint percent)
        {
            int n = 0;
            foreach (var p in plans)
            {
                var id = p; var b = Brightness; uint now; bool changed = false;
                if (ReadBrightness(id, false, out now) && now != percent) changed |= PowerWriteACValueIndex(IntPtr.Zero, ref id, ref Video, ref b, percent) == 0;
                if (ReadBrightness(id, true, out now) && now != percent) changed |= PowerWriteDCValueIndex(IntPtr.Zero, ref id, ref Video, ref b, percent) == 0;
                if (changed) n++;
            }
            return n;
        }

        // for --brightness-check: the brightness now and in every plan (read), then a write into a copy of the active plan
        // made for the check and deleted after it; no plan of the user is written
        public static string BrightnessCheck()
        {
            var sb = new System.Text.StringBuilder();
            Func<Guid, string> show = id => { uint a, d; return (ReadBrightness(id, false, out a) ? a.ToString() : "-") + " / " + (ReadBrightness(id, true, out d) ? d.ToString() : "-"); };
            sb.AppendLine("the screen now: " + BrightnessSync.Current() + " %  (-1: Windows gives no brightness for this screen)");
            var before = List().ToDictionary(p => p.Id, p => show(p.Id));
            foreach (var p in List()) sb.AppendLine("  " + p.Id + "  mains / battery " + before[p.Id].PadRight(10) + p.Name + (p.Active ? "  (active)" : ""));
            Guid copy = MakeTestCopy(), active = Active();
            try
            {
                string was = show(copy);
                int n = SetBrightness(new[] { copy }, 37);
                bool ok = n == 1 && show(copy) == "37 / 37";
                sb.AppendLine("a copy of the active plan: " + was + " -> write 37 -> " + show(copy) + ", plans changed " + n + (ok ? "  ok" : "  FAILED"));
                int again = SetBrightness(new[] { copy }, 37);
                sb.AppendLine("the same value again: plans changed " + again + (again == 0 ? "  ok (nothing is written twice)" : "  FAILED"));
                bool same = List().Where(p => p.Id != copy).All(p => before.ContainsKey(p.Id) && before[p.Id] == show(p.Id));
                sb.AppendLine("the other plans: " + (same ? "as they were  ok" : "CHANGED  FAILED"));
            }
            finally { sb.AppendLine("the copy deleted: " + (DeleteTestCopy(copy) ? "yes" : "NO") + "; the active plan: " + (Active() == active ? "the same" : "CHANGED")); }
            return sb.ToString();
        }

        static string Show(uint?[] v) { return string.Join("/", v.Select(x => x == null ? "-" : x.ToString())); }

        // for --plan-values: the five settings of every plan as the page reads them, class by class; nothing is written
        public static string CpuValuesText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in List())
            {
                sb.AppendLine(p.Id + "  " + p.Name + (p.Active ? "  (active" + (Overlaid(p.Id) ? ", a power mode of Windows is laid over it" : "") + ")" : ""));
                foreach (Cpu c in Enum.GetValues(typeof(Cpu)))
                    sb.AppendLine("  " + c.ToString().PadRight(9) + " AC " + Show(ReadCpuClasses(p.Id, c, false)).PadRight(12) + " DC " + Show(ReadCpuClasses(p.Id, c, true)));
            }
            sb.AppendLine("«Balanced» out of the box, MinCores: AC " + Show(DefaultCpuClasses(Cpu.MinCores, false)) + "  DC " + Show(DefaultCpuClasses(Cpu.MinCores, true)));
            return sb.ToString();
        }

        // for --plan-roundtrip: a copy of the active plan is made, every setting is written into it on mains power and on
        // battery and read back, and the copy is deleted. No plan of the user is changed and the active plan stays active.
        public static string CpuRoundTrip()
        {
            var sb = new System.Text.StringBuilder();
            Guid from = Active(), id; IntPtr dest = IntPtr.Zero;
            uint rc = PowerDuplicateScheme(IntPtr.Zero, ref from, ref dest);
            if (rc != 0 || dest == IntPtr.Zero) return "the active plan could not be copied (code " + rc + ")";
            try { id = (Guid)Marshal.PtrToStructure(dest, typeof(Guid)); } finally { LocalFree(dest); }
            int bad = 0;
            try
            {
                var nm = System.Text.Encoding.Unicode.GetBytes("Red Tune проба\0"); PowerWriteFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, nm, (uint)nm.Length);
                sb.AppendLine("copy " + id + " of the active plan " + from);
                var test = new uint[][] { new uint[] { 3300, 2700 }, new uint[] { 4, 0 }, new uint[] { 70, 33 }, new uint[] { 37, 12 }, new uint[] { 100, 50 } };
                foreach (Cpu c in Enum.GetValues(typeof(Cpu)))
                    for (int d = 0; d < 2; d++)
                    {
                        bool dc = d == 1; uint v = test[(int)c][d];
                        var before = ReadCpuClasses(id, c, dc); var other = ReadCpuClasses(id, c, !dc);
                        uint w = WriteCpu(id, c, dc, v);
                        var after = ReadCpuClasses(id, c, dc); var other2 = ReadCpuClasses(id, c, !dc);
                        bool ok = w == 0 && after[0] == v && after.All(x => x == null || x == v) && Show(other) == Show(other2);
                        if (!ok) bad++;
                        sb.AppendLine("  " + c.ToString().PadRight(9) + (dc ? " DC " : " AC ") + Show(before).PadRight(12) + " -> write " + v.ToString().PadRight(5) + " code " + w + " -> " + Show(after).PadRight(14)
                            + (dc ? " AC" : " DC") + " stayed " + Show(other2).PadRight(12) + (ok ? " ok" : " FAILED"));
                    }
                // the classes apart, as «allow parking» puts back what the plan had
                var back = new uint?[] { 17, 0 };
                uint wb = WriteCpuClasses(id, Cpu.MinCores, false, back);
                var got = ReadCpuClasses(id, Cpu.MinCores, false);
                bool okb = wb == 0 && got[0] == 17 && (got[1] == null || got[1] == 0);
                if (!okb) bad++;
                sb.AppendLine("  MinCores  AC classes apart: write 17/0 code " + wb + " -> " + Show(got) + (okb ? " ok" : " FAILED"));
            }
            finally
            {
                rc = PowerDeleteScheme(IntPtr.Zero, ref id);
                sb.AppendLine("copy deleted: " + (rc == 0 ? "yes" : "NO, code " + rc) + (List().Exists(p => p.Id == id) ? "  (STILL IN THE LIST)" : ""));
                sb.AppendLine("active plan now: " + Active() + (Active() == from ? " (the same)" : " (CHANGED)"));
            }
            sb.AppendLine(bad == 0 ? "all written and read back" : bad + " FAILED");
            return sb.ToString();
        }

        // for --plan-dry: what the button would write, without writing anything
        public static string TuningPlanText()
        {
            var sb = new System.Text.StringBuilder();
            Guid hp = HighPerformance;
            sb.AppendLine("copy of " + hp + " («High performance»), then on mains power and on battery:");
            foreach (var v in TuningValues)
            {
                var setting = v.Id; var active = Active(); uint now;
                bool read = PowerReadACValueIndex(IntPtr.Zero, ref active, ref Processor, ref setting, out now) == 0;
                sb.AppendLine("  " + v.Name.PadRight(18) + v.Id + "  = " + v.Value + "   (the active plan has " + (read ? now.ToString() : "no such setting") + ")");
            }
            try { Guid o; if (PowerGetEffectiveOverlayScheme(out o) == 0) sb.AppendLine("power mode laid over the active plan now: " + (o == Guid.Empty ? "none" : o.ToString())); } catch { }
            return sb.ToString();
        }
    }

    // 18.11: one screen brightness for all power plans (the user's wish; LLT has the same switch). A plan keeps a
    // brightness of its own, for mains power and for battery, and Windows sets it when the plan becomes active: with
    // several plans the screen jumps on a change of the mode, and a newly made plan brings the brightness it was copied
    // with. With the switch on, each change of the brightness is written into every plan, so a change of the plan
    // changes nothing on the screen. Off by default; while it is off nothing is watched and nothing is written.
    static class BrightnessSync
    {
        static Settings settings;
        static System.Management.ManagementEventWatcher watcher;
        static System.Threading.Timer timer;
        static int wanted = -1;
        static readonly object gate = new object();

        // at the start of the program and when the switch is turned; off the UI thread (WMI takes its time)
        public static void Init(Settings s) { settings = s; System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { Apply(); } catch (Exception ex) { Store.Log("яркость для всех схем: " + ex.Message); } }); }

        // the watch begins or ends; switched on, the brightness as it is now goes to the plans at once
        static void Apply()
        {
            lock (gate)
            {
                if (settings == null || !settings.SyncBrightness) { Stop(); return; }
                if (watcher == null)
                    try
                    {
                        var w = new System.Management.ManagementEventWatcher(new System.Management.ManagementScope(@"root\WMI"), new System.Management.WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                        w.EventArrived += (o, e) => { try { Changed(Convert.ToInt32(e.NewEvent["Brightness"])); } catch { } };
                        w.Start(); watcher = w;
                        Store.Log("яркость для всех схем: слежение включено");
                    }
                    catch (Exception ex) { Store.Log("яркость для всех схем: слежение не запустилось: " + ex.Message); }
            }
            Now();
        }

        public static void Stop()
        {
            lock (gate)
            {
                if (watcher != null) { try { watcher.Stop(); watcher.Dispose(); } catch { } watcher = null; }
                if (timer != null) { timer.Dispose(); timer = null; }
            }
        }

        // the brightness as it is now into the plans (the switch was turned on, a plan was made)
        public static void Now()
        {
            if (settings == null || !settings.SyncBrightness) return;
            int now = Current();
            if (now >= 0) Changed(now);
        }

        // the brightness changes many times while the slider or the keys move it: the last value is written when it has
        // stood for a moment
        static void Changed(int percent)
        {
            if (percent < 0 || percent > 100) return;
            lock (gate)
            {
                wanted = percent;
                if (timer == null) timer = new System.Threading.Timer(_ => Write(), null, 700, System.Threading.Timeout.Infinite);
                else timer.Change(700, System.Threading.Timeout.Infinite);
            }
        }

        static void Write()
        {
            int v;
            lock (gate) { if (settings == null || !settings.SyncBrightness) return; v = wanted; }
            try
            {
                int n = WinPower.SetBrightness(WinPower.List().Select(p => p.Id), (uint)v);
                if (n > 0) Store.Log("яркость " + v + " % записана в схемы питания: " + n);
            }
            catch (Exception ex) { Store.Log("яркость для всех схем: " + ex.Message); }
        }

        // the brightness of the built-in screen now; -1 when Windows does not give it (a desktop monitor)
        public static int Current()
        {
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness"))
                    foreach (System.Management.ManagementObject o in q.Get())
                        if (Convert.ToBoolean(o["Active"])) return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return -1;
        }
    }

    // 18.11: who changes Windows' power mode. On 18.11 the mode for mains power, set to «balanced», was found back at
    // «best power efficiency» half an hour later, and nothing in Red Tune writes it by itself. The mode is read every
    // five seconds; a change that Red Tune did not make goes to the log with what was running, to find the one who did.
    static class PowerModeWatch
    {
        static System.Threading.Timer timer;
        static Guid ac, dc; static bool have;
        static DateTime own = DateTime.MinValue;

        public static void Own() { own = DateTime.Now; }
        public static void Start() { if (timer == null) timer = new System.Threading.Timer(_ => Tick(), null, 3000, 5000); }
        public static void Stop() { if (timer != null) { timer.Dispose(); timer = null; } }

        static void Tick()
        {
            try
            {
                Guid a, d;
                if (!WinPower.ReadMode(false, out a) || !WinPower.ReadMode(true, out d)) return;
                if (!have) Store.Log("режим питания Windows при запуске: от сети " + WinPower.ModeName(a) + ", от батареи " + WinPower.ModeName(d));
                else if ((a != ac || d != dc) && (DateTime.Now - own).TotalSeconds > 8)
                {
                    var who = new List<string>();
                    try
                    {
                        foreach (var p in Process.GetProcesses())
                        {
                            string n = p.ProcessName;
                            if (n.IndexOf("Legion", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Lenovo", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Vantage", StringComparison.OrdinalIgnoreCase) >= 0
                                || n.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) || n.Equals("powercfg", StringComparison.OrdinalIgnoreCase)) who.Add(n);
                            p.Dispose();
                        }
                    }
                    catch { }
                    Store.Log("режим питания Windows изменён не из Red Tune:"
                        + (a != ac ? " от сети " + WinPower.ModeName(ac) + " → " + WinPower.ModeName(a) + ";" : "") + (d != dc ? " от батареи " + WinPower.ModeName(dc) + " → " + WinPower.ModeName(d) + ";" : "")
                        + " активная схема " + WinPower.Active() + "; запущены: " + (who.Count > 0 ? string.Join(", ", who.Distinct()) : "ни Legion Toolkit, ни параметры Windows"));
                }
                ac = a; dc = d; have = true;
            }
            catch (Exception ex) { Store.Quiet("режим питания Windows: слежение", ex); }
        }
    }
}
