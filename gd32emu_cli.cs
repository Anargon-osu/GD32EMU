// gd32emu_cli.cs - command line of the general-purpose GD32 emulator.
using System;
using System.Text;

namespace Gd32Emu
{
    public static class EmuProgram
    {
        public const string HELP = "gd32emu - a general-purpose GigaDevice GD32F3x0 emulator (GD32 only: STM32 parts are not modelled)\n"
            + "usage: gd32emu IMAGE [--device NAME] [--vtor ADDR] [--steps N | --ms N] [--speed uncapped|72mhz] [--trace N]\n"
            + "  IMAGE          .bin (whole flash from 0x08000000) or Intel .hex\n"
            + "  --device NAME  one of: GD32F350C8 (default), GD32F350G8, GD32F350R8, GD32F330C8 (64 KB flash, 8 KB SRAM)\n"
            + "  --vtor ADDR    vector table to start from (default 0x08000000)\n"
            + "  --steps N      run N instructions without interrupts (default 2000000)\n"
            + "  --ms N         run N ms of chip time with timers and interrupts\n"
            + "  --trace N      print the first N instructions' addresses\n"
            + "  --list-devices";

        public static int Main(string[] args)
        {
            string image = null, devName = "GD32F350C8", speed = "uncapped";
            long steps = 2000000; double ms = 0; int vtor = 0x08000000, trace = 0;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (P.Eq(a, "-h") || P.Eq(a, "--help")) { P.Line(HELP); return 0; }
                    if (P.Eq(a, "--list-devices")) { for (int k = 0; k < Devices.ALL.Length; k++) P.Line(Devices.ALL[k].name + "  " + F.Str(Devices.ALL[k].flashKb) + " KB flash, " + F.Str(Devices.ALL[k].sramKb) + " KB SRAM"); return 0; }
                    if (P.StartsWith(a, "--"))
                    {
                        if (i + 1 >= args.Length) throw new Stop(a + " needs a value");
                        string v = args[++i];
                        if (P.Eq(a, "--device")) devName = v;
                        else if (P.Eq(a, "--vtor")) vtor = (int)F.ParseInt(v);
                        else if (P.Eq(a, "--steps")) steps = F.ParseInt(v);
                        else if (P.Eq(a, "--ms")) ms = P.ParseDouble(v);
                        else if (P.Eq(a, "--speed")) speed = v;
                        else if (P.Eq(a, "--trace")) trace = (int)F.ParseInt(v);
                        else throw new Stop("unknown option " + a);
                    }
                    else image = a;
                }
                if (image == null) { P.Line(HELP); return 2; }
                // x-x is zero only for finite x; this form also translates to Java.
                if (steps < 0 || ms < 0 || ms - ms != 0.0 || trace < 0)
                    throw new Stop("Run length and trace count must be finite, non-negative numbers.");
                if (!P.Eq(speed, "uncapped") && !P.Eq(speed, "72mhz"))
                    throw new Stop("--speed must be uncapped or 72mhz.");
                Device dev = Devices.Find(devName);
                if (dev == null) throw new Stop(devName + " is not a supported part. Only GigaDevice GD32F3x0 parts are modelled (--list-devices); STM32 parts are not, by design.");
                byte[] img = Load(image);
                string refuse = StmGuard.Check(img);
                if (refuse != null) { P.Line(refuse); return 3; }
                Machine mc = new Machine(img, ms > 0, null, P.Eq(speed, "72mhz"));
                mc.dev = dev;
                mc.vtor = vtor;
                Cpu c = mc.cpu;
                int vo = vtor - 0x08000000;
                if (vo < 0 || vo > img.Length - 8 || (vo & 3) != 0)
                    throw new Stop("--vtor must point to a word-aligned vector table inside flash.");
                c.r[13] = Machine.LE(img, vo, 4); c.r[15] = Machine.LE(img, vo + 4, 4) & ~1; c.r[14] = -1;
                P.Line("== " + image + " on " + dev.name + " (" + F.Str(dev.flashKb) + " KB flash, " + F.Str(dev.sramKb) + " KB SRAM), reset at " + F.Hx(c.r[15], 8));
                string why = "ran " + F.Str(steps) + " instructions";
                int result = 0;
                try
                {
                    if (ms > 0) { mc.RunMs(ms); why = "ran " + F.Fx(ms, 3) + " ms"; }
                    else
                        for (long n = 0; n < steps; n++)
                        {
                            if (n < trace) P.Line("   " + F.Hx(c.r[15], 8));
                            c.Step();
                        }
                }
                catch (Fault e) { why = "stopped: " + e.msg; result = 1; }
                catch (Stop e) { why = "stopped: " + e.msg; result = 1; }
                P.Line("   " + why);
                P.Line("   pc " + F.Hx(c.r[15], 8) + ", sp " + F.Hx(c.r[13], 8) + ", " + F.Str(c.icount) + " instructions, " + F.Str(c.cycles) + " cycles (" + Fus(c.cycles) + " at 72 MHz), flash writes " + F.Str(mc.flashWrites));
                StringBuilder regs = new StringBuilder("   ");
                for (int k = 0; k < 13; k++) regs.Append("r" + F.Str(k) + "=" + F.Hx(c.r[k], 8) + (k == 6 ? "\n   " : " "));
                P.Line(regs.ToString());
                return result;
            }
            catch (Stop e) { P.Line(e.msg); return 1; }
            catch (Exception e) { P.Line("Could not run the image: " + P.Msg(e)); return 1; }
        }

        static string Fus(long cycles) { return F.Fx(cycles / 72.0, 6) + " us"; }

        public static byte[] Load(string path)
        {
            byte[] flash = new byte[0x10000];
            for (int i = 0; i < flash.Length; i++) flash[i] = (byte)0xFF;
            byte[] raw = P.ReadAllBytes(path);
            if (raw.Length > 0 && raw[0] == (byte)':')
            {
                string[] lines = P.Lines(P.Ascii(raw));
                int bs = 0;
                bool ended = false, hasData = false;
                for (int ln = 0; ln < lines.Length; ln++)
                {
                    string line = P.Trim(lines[ln]);
                    if (P.Len(line) == 0) continue;
                    if (ended) throw new Stop("Intel HEX contains records after its end marker.");
                    if (P.Len(line) < 11 || P.CharAt(line, 0) != ':' || (P.Len(line) & 1) == 0)
                        throw new Stop("Invalid Intel HEX record at line " + F.Str(ln + 1) + ".");
                    int nrec = (P.Len(line) - 1) / 2;
                    int[] rec = new int[nrec];
                    for (int k = 0; k < nrec; k++) rec[k] = (int)F.ParseInt("0x" + P.Sub(line, 1 + 2 * k, 2));
                    int n = rec[0], a = (rec[1] << 8) | rec[2], t = rec[3], checksum = 0;
                    for (int k = 0; k < nrec; k++) checksum += rec[k];
                    if (nrec != n + 5 || (checksum & 255) != 0)
                        throw new Stop("Invalid Intel HEX length or checksum at line " + F.Str(ln + 1) + ".");
                    if (t == 0)
                    {
                        long start = (bs & 0xFFFFFFFFL) + a - 0x08000000L;
                        if (start < 0 || start + n > flash.Length) throw new Stop("Intel HEX data is outside the modelled flash.");
                        for (int k = 0; k < n; k++) flash[(int)start + k] = (byte)rec[4 + k];
                        hasData = hasData || n > 0;
                    }
                    else if (t == 1 && n == 0 && a == 0) ended = true;
                    else if (t == 4 && n == 2 && a == 0) bs = ((rec[4] << 8) | rec[5]) << 16;
                    else if (t == 5 && n == 4 && a == 0) { } // start address is supplied by the vector table
                    else throw new Stop("Unsupported or malformed Intel HEX record at line " + F.Str(ln + 1) + ".");
                }
                if (!ended || !hasData) throw new Stop("Intel HEX must contain flash data and an end marker.");
                return flash;
            }
            if (raw.Length < 8 || raw.Length > flash.Length) throw new Stop("A binary image must contain 8 to 65536 bytes.");
            P.Copy(raw, flash, raw.Length);
            return flash;
        }
    }
}
