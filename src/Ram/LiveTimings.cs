using System;
using System.Collections.Generic;
using System.Globalization;

namespace RedTune.Ram
{
    // The memory timings and clocks the controller is really running (what ZenTimings shows), the app's own way
    // (17.11; before: ZenStates-Core in a separate PowerShell, 0.4 s per reading). They sit in the registers of the
    // memory controller (UMC) of the first channel that has a module: SMN 0x50000 + channel × 0x100000, read through
    // PawnIO (Smu.Registers). The register map and the rules are ZenStates-Core's (1.79, GPL-3.0). Reads only.
    static class LiveTimings
    {
        struct Field
        {
            public readonly uint Reg; public readonly string Name; public readonly int Hi, Lo;
            public Field(uint reg, string name, int hi, int lo) { Reg = reg; Name = name; Hi = hi; Lo = lo; }
        }

        // the same on DDR4 and DDR5
        static readonly Field[] Fields =
        {
            new Field(0x204, "RCDWR", 29, 24), new Field(0x204, "RCDRD", 21, 16), new Field(0x204, "RAS", 14, 8), new Field(0x204, "CL", 5, 0),
            new Field(0x208, "RC", 7, 0), new Field(0x208, "RP", 21, 16),
            new Field(0x20C, "RTP", 28, 24), new Field(0x20C, "RRDL", 12, 8), new Field(0x20C, "RRDS", 4, 0),
            new Field(0x210, "FAW", 7, 0),
            new Field(0x214, "WTRL", 22, 16), new Field(0x214, "WTRS", 12, 8), new Field(0x214, "CWL", 5, 0),
            new Field(0x218, "WR", 7, 0),
            new Field(0x220, "RDRDSCL", 29, 24), new Field(0x220, "RDRDSC", 19, 16), new Field(0x220, "RDRDSD", 11, 8), new Field(0x220, "RDRDDD", 3, 0),
            new Field(0x224, "WRWRSCL", 29, 24), new Field(0x224, "WRWRSC", 19, 16), new Field(0x224, "WRWRSD", 11, 8), new Field(0x224, "WRWRDD", 3, 0),
            new Field(0x228, "RDWR", 13, 8), new Field(0x228, "WRRD", 3, 0),
            new Field(0x230, "REFI", 15, 0),
        };

        // the registers of one channel, in this order
        static readonly uint[] Regs = { 0x200, 0x204, 0x208, 0x20C, 0x210, 0x214, 0x218, 0x220, 0x224, 0x228, 0x230, 0x260, 0x264, 0x268, 0x26C, 0x2C0, 0x2C4, 0x2C8, 0x2CC };

        static uint Bits(uint v, int hi, int lo) { return (v >> lo) & (uint)((1L << (hi - lo + 1)) - 1); }

        // «CL» → «40», plus Frequency, GDM, Cmd2T, Type and the clocks FCLK / UCLK / MCLK ("" when unread)
        public static Dictionary<string, string> Read()
        {
            if (!Tctl.Available) throw new Exception("Нужен PawnIO (вкладка «Компоненты»)");

            // the kind of memory, and for each of the 12 possible channels: is it on, is there a module in either slot
            var probe = new uint[1 + 12 * 3];
            probe[0] = 0x50100;
            for (uint ch = 0; ch < 12; ch++) { probe[1 + ch * 3] = ch << 20 | 0x50DF0; probe[2 + ch * 3] = ch << 20 | 0x50000; probe[3 + ch * 3] = ch << 20 | 0x50008; }
            int unread;
            var p = Smu.Registers(probe, out unread);
            if (p == null || unread == probe.Length) throw new Exception("не читаются регистры контроллера памяти");
            int type = (int)(p[0] & 3);
            bool ddr5 = type >= 1; // LPDDR5 reads like DDR5
            uint offset = 0;
            for (uint ch = 0; ch < 12; ch++)
                if ((p[1 + ch * 3] >> 19 & 1) == 0 && ((p[2 + ch * 3] & 1) == 1 || (p[3 + ch * 3] & 1) == 1)) { offset = ch << 20; break; }

            var addr = new uint[Regs.Length];
            for (int i = 0; i < Regs.Length; i++) addr[i] = offset | 0x50000 | Regs[i];
            var v = Smu.Registers(addr, out unread);
            if (v == null || unread > 0) throw new Exception("не читаются регистры контроллера памяти");
            Func<uint, uint> reg = r => v[Array.IndexOf(Regs, r)];

            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string, uint> put = (k, x) => d[k] = x.ToString(CultureInfo.InvariantCulture);

            // the memory clock: DDR5 keeps it in MHz / 2 × 100, DDR4 as a ratio in thirds of 200 MHz
            uint head = reg(0x200);
            double mhz = ddr5 ? Bits(head, 15, 0) / 100.0 * 200 : Bits(head, 6, 0) / 3.0 * 200;
            put("Frequency", (uint)Math.Round(mhz));
            d["GDM"] = Bits(head, ddr5 ? 18 : 11, ddr5 ? 18 : 11) == 1 ? "Enabled" : "Disabled";
            d["Cmd2T"] = Bits(head, ddr5 ? 17 : 10, ddr5 ? 17 : 10) == 1 ? "2T" : "1T";
            foreach (var f in Fields) put(f.Name, Bits(reg(f.Reg), f.Hi, f.Lo));

            // tRFC: one register per module, the ones without a module keep the reset value
            uint rfc = 0;
            if (ddr5) { foreach (uint r in new uint[] { 0x260, 0x264, 0x268, 0x26C }) if (reg(r) != 0x00C00138) { rfc = reg(r); break; } }
            else { uint a = reg(0x260), b = reg(0x264); rfc = a == b || a != 0x21060138 ? a : b; }
            put("RFC", rfc != 0 ? Bits(rfc, ddr5 ? 15 : 10, 0) : 0);
            put("RFC2", rfc != 0 ? Bits(rfc, ddr5 ? 31 : 21, ddr5 ? 16 : 11) : 0);
            if (ddr5)
            {
                uint sb = 0;
                foreach (uint r in new uint[] { 0x2C0, 0x2C4, 0x2C8, 0x2CC }) { sb = Bits(reg(r), 10, 0); if (sb != 0) break; }
                put("RFCsb", sb);
            }
            else d["RFCsb"] = "";

            d["Type"] = type == 0 ? "DDR4" : type == 1 ? "DDR5" : type == 2 ? "LPDDR5" : type.ToString();
            var clk = Smu.MemClocks();
            d["FCLK"] = clk != null ? clk[0].ToString() : "";
            d["UCLK"] = clk != null ? clk[1].ToString() : "";
            d["MCLK"] = clk != null ? clk[2].ToString() : "";
            return d;
        }
    }
}
