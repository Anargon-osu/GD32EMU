// Small synthetic programs exercise the emulator without vendor firmware or a test framework.
using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace Gd32Emu.Tests
{
    public static class EmulatorTests
    {
        static int passed;
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++;
            Console.WriteLine("ok: " + name);
        }

        static byte[] Blank()
        {
            byte[] image = new byte[65536];
            for (int i = 0; i < image.Length; i++) image[i] = 255;
            Put(image, 0, 0x20002000);
            Put(image, 4, 0x08000101);
            return image;
        }

        static void Put(byte[] image, int offset, int value)
        {
            for (int i = 0; i < 4; i++) image[offset + i] = (byte)(value >> (8 * i));
        }

        static void Unlock(Machine machine)
        {
            machine.Write(0x40022004, 4, 0x45670123);
            machine.Write(0x40022004, 4, unchecked((int)0xCDEF89AB));
        }

        static void Core()
        {
            Check(P.IsNaN(Double.NaN) && !P.IsNaN(0) && !P.IsNaN(Double.PositiveInfinity) && !P.IsNaN(Double.NegativeInfinity),
                "shared NaN check distinguishes finite values and infinities");
            byte[] image = Blank();
            // MOVS r0,#1; ADDS r0,#2; SUBS r0,#1; B .
            byte[] program = new byte[] { 1, 32, 2, 48, 1, 56, 254, 231 };
            Array.Copy(program, 0, image, 256, program.Length);
            Machine machine = new Machine(image, false, null, false);
            machine.SystemReset();
            for (int i = 0; i < 4; i++) machine.cpu.Step();
            Check(machine.cpu.r[0] == 2 && machine.cpu.r[15] == 0x08000106, "Thumb arithmetic and branch execute");
            machine.Write(0x20000000, 4, 0x12345678);
            Check(machine.Read(0x20000000, 4) == 0x12345678, "SRAM read/write");
            bool fault = false;
            try { machine.Read(0x20001FFF, 4); } catch (Fault) { fault = true; }
            Check(fault, "SRAM boundary faults");
            fault = false;
            try { machine.Read(0x40010800, 4); } catch (Fault) { fault = true; }
            Check(fault, "unsupported peripheral faults");
            machine.Write(unchecked((int)0xE000ED08), 4, 0x08003800);
            Check(machine.vtor == 0x08003800, "vector-table relocation");
            machine.SystemReset();
            Check(machine.vtor == 0x08000000 && machine.cpu.r[15] == 0x08000100, "system reset restores vectors");
            Check((machine.Read(0x40021024, 4) & (1 << 28)) != 0, "software-reset cause is recorded");
            Check(machine.Read(0x20000000, 4) == 0x12345678, "system reset preserves SRAM");
        }

        static void Flash()
        {
            Machine machine = new Machine(Blank(), false, null, false);
            bool fault = false;
            try { machine.Write(0x08004000, 4, 0x12345678); } catch (Fault) { fault = true; }
            Check(fault && machine.Read(0x08004000, 4) == -1, "locked flash refuses programming");
            Unlock(machine);
            machine.Write(0x40022010, 4, 1);
            machine.Write(0x08004000, 4, 0x12345678);
            Check(machine.Read(0x08004000, 4) == 0x12345678, "unlocked erased word programs");
            machine.Write(0x08004000, 4, 0);
            Check(machine.Read(0x08004000, 4) == 0x12345678 && (machine.fmcStat & 4) != 0, "non-erased word raises PGERR without changing flash");
            machine.Write(0x4002200C, 4, 4);
            Check((machine.fmcStat & 4) == 0, "flash status is write-one-to-clear");
            machine.Write(0x40022014, 4, 0x08004000);
            machine.Write(0x40022010, 4, 0x42);
            Check(machine.Read(0x08004000, 4) == -1, "page erase restores erased bytes");
            Machine.bootWp = true;
            try
            {
                machine.Write(0x40022010, 4, 1);
                machine.Write(0x08002800, 4, 0);
                Check(machine.Read(0x08002800, 4) == -1 && (machine.fmcStat & 16) != 0, "protected boot page raises WPERR");
            }
            finally { Machine.bootWp = false; }
        }

        static byte[] Configuration(byte[] descriptors, int interfaces)
        {
            byte[] config = new byte[9 + descriptors.Length];
            config[0] = 9; config[1] = 2; config[2] = (byte)config.Length; config[3] = (byte)(config.Length >> 8);
            config[4] = (byte)interfaces; config[5] = 1; config[7] = 0x80; config[8] = 50;
            Array.Copy(descriptors, 0, config, 9, descriptors.Length);
            return config;
        }

        static void RejectConfiguration(byte[] config, string name)
        {
            bool rejected = false;
            try { UsbConfiguration.Parse(config); } catch (Stop error) { rejected = !String.IsNullOrEmpty(error.msg); }
            Check(rejected, name);
        }

        static void QueueReport(Machine machine, int endpoint)
        {
            machine.usb.txbuf[endpoint].Add(endpoint);
            machine.periph.Set(Machine.DIEPCTL(endpoint), unchecked((int)0x80000000));
            machine.periph.Set(Machine.DIEPLEN(endpoint), 1);
        }

        static void UsbDescriptors()
        {
            byte[] subset = Configuration(new byte[] {
                9, 4, 0, 0, 2, 0xFF, 0, 0, 0,
                7, 5, 0x81, 3, 8, 0, 3,
                7, 5, 0x83, 3, 12, 0, 1 }, 1);
            Machine machine = new Machine(Blank(), false, null, false);
            machine.AttachUsb(false); machine.ConfigureUsb(UsbConfiguration.Parse(subset));
            Check(machine.hostInterval == 0 && machine.bint82 == -1, "missing EP82 has no fallback polling interval");
            Check(machine.hostIntervals[1] == 2 && machine.hostIntervals[3] == 1, "advertised IN endpoints retain separate model intervals");
            QueueReport(machine, 1); QueueReport(machine, 2); QueueReport(machine, 3);
            machine.UsbFrame();
            Check(machine.reports.n == 1 && machine.reports.a[0].ep == 3, "only the advertised endpoint due in this frame is polled");
            machine.UsbFrame();
            Check(machine.reports.n == 2 && machine.reports.a[1].ep == 1 && machine.usb.txbuf[2].n == 1,
                "unadvertised EP2 is never polled while valid endpoint subsets work");
            machine.HostPoll(2);
            Check(machine.reports.n == 3, "explicit supported endpoint polling remains available");
            byte[] outOnly = Configuration(new byte[] { 9, 4, 0, 0, 1, 0xFF, 0, 0, 0, 7, 5, 3, 3, 10, 0, 1 }, 1);
            machine.ConfigureUsb(UsbConfiguration.Parse(outOnly)); QueueReport(machine, 3); machine.UsbFrame();
            Check(machine.reports.n == 3 && machine.usb.txbuf[3].n == 1, "OUT endpoints do not schedule IN polls");
            byte[] bulk = Configuration(new byte[] { 9, 4, 0, 0, 1, 0xFF, 0, 0, 0, 7, 5, 0x82, 2, 64, 0, 0 }, 1);
            machine.ConfigureUsb(UsbConfiguration.Parse(bulk)); QueueReport(machine, 2); machine.UsbFrame();
            Check(machine.hostIntervals[2] == 0 && machine.reports.n == 3, "non-interrupt endpoints do not schedule interrupt polls");
            byte[] alternates = Configuration(new byte[] {
                9, 4, 0, 0, 1, 0xFF, 0, 0, 0, 7, 5, 0x81, 3, 8, 0, 3,
                9, 4, 0, 1, 1, 0xFF, 0, 0, 0, 7, 5, 0x81, 3, 16, 0, 1 }, 1);
            UsbConfiguration parsed = UsbConfiguration.Parse(alternates);
            Check(parsed.maxPacket[0x81] == 8 && parsed.interval[0x81] == 3, "inactive alternate settings do not replace or duplicate active endpoints");
            byte[] bad = (byte[])subset.Clone(); bad[20] = 0x84;
            RejectConfiguration(bad, "endpoint addresses outside the model are rejected");
            bad = (byte[])subset.Clone(); bad[20] = 0x92;
            RejectConfiguration(bad, "reserved endpoint address bits are rejected");
            bad = (byte[])subset.Clone(); bad[20] = 0x80;
            RejectConfiguration(bad, "endpoint zero cannot appear as an interface endpoint");
            bad = (byte[])subset.Clone(); bad[22] = 0;
            RejectConfiguration(bad, "zero max packet sizes are rejected");
            bad = (byte[])subset.Clone(); bad[24] = 0;
            RejectConfiguration(bad, "zero interrupt intervals are rejected");
            bad = (byte[])subset.Clone(); bad[18] = 6;
            RejectConfiguration(bad, "short endpoint descriptors are rejected");
            bad = (byte[])subset.Clone(); bad[18] = 1;
            RejectConfiguration(bad, "descriptor lengths below two are rejected");
            bad = (byte[])subset.Clone(); bad[2]--;
            RejectConfiguration(bad, "configuration total length must match received bytes");
            bad = (byte[])subset.Clone(); bad[13] = 1;
            RejectConfiguration(bad, "declared endpoint counts must match the descriptor layout");
            byte[] duplicate = Configuration(new byte[] {
                9, 4, 0, 0, 1, 0xFF, 0, 0, 0, 7, 5, 0x81, 3, 8, 0, 1,
                9, 4, 1, 0, 1, 0xFF, 0, 0, 0, 7, 5, 0x81, 3, 8, 0, 1 }, 2);
            RejectConfiguration(duplicate, "duplicate active endpoint addresses are rejected");
            bool rejected = false;
            try { machine.HostPoll(4); } catch (Stop) { rejected = true; }
            Check(rejected, "manual polling rejects unsupported endpoint numbers");
        }

        static void Cli()
        {
            string work = Path.Combine(Path.GetTempPath(), "gd32emu-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string path = Path.Combine(work, "test image.bin");
                byte[] image = Blank();
                image[256] = 254; image[257] = 231; // B .
                File.WriteAllBytes(path, image);
                Check(EmuProgram.Main(new string[] { path, "--steps", "4" }) == 0, "CLI executes a valid synthetic image");
                image[4] = 0; image[5] = 0; image[6] = 0; image[7] = 0x60; // invalid bus address
                File.WriteAllBytes(path, image);
                Check(EmuProgram.Main(new string[] { path, "--steps", "1" }) != 0, "CLI faults return a failing exit code");
                Check(EmuProgram.Main(new string[] { path, "--steps", "-1" }) != 0, "CLI refuses negative run lengths");
                File.WriteAllText(path, ":00000001FE\n");
                Check(EmuProgram.Main(new string[] { path, "--steps", "1" }) != 0, "CLI refuses malformed HEX checksums");
                Check(EmuProgram.Main(new string[] { path, "--device", "STM32F103" }) != 0, "CLI refuses unsupported devices");
                Check(EmuProgram.Main(new string[] { path, "--device", "GD32F350CB" }) != 0, "CLI refuses unmodelled 128 KB parts");
                Check(Devices.Find("GD32F350CB") == null && Devices.Find("GD32F330CB") == null, "device list matches modelled flash and SRAM sizes");
            }
            finally
            {
                foreach (string path in Directory.GetFiles(work)) File.Delete(path);
                Directory.Delete(work);
            }
        }

        static FieldInfo HidField(string name) { return typeof(P).GetField(name, BindingFlags.NonPublic | BindingFlags.Static); }
        static object HidCall(string name, params object[] arguments)
        {
            return typeof(P).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, arguments);
        }
        class BlockingStream : FileStream
        {
            public ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false);
            public BlockingStream(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) { }
            public override int Read(byte[] bytes, int offset, int count) { entered.Set(); release.WaitOne(); return 0; }
        }
        static void HidSessions()
        {
            // Only local files and reflected managed helpers are used. No HID device is opened.
            P.HidClose();
            int generation = (int)HidField("hidGeneration").GetValue(null);
            HidField("hidRun").SetValue(null, true);
            HidField("hidN").SetValue(null, 0);
            byte[] report = new byte[10]; report[0] = 7;
            HidCall("HidStore", report, report.Length, generation - 1);
            Check((int)HidField("hidN").GetValue(null) == 0, "late reports from an old reader are discarded");
            HidCall("HidStore", report, report.Length, generation);
            Check((int)HidField("hidN").GetValue(null) == 1, "current session receives ID-7 reports");
            P.HidClose();
            HidCall("HidStore", report, report.Length, generation);
            Check((int)HidField("hidN").GetValue(null) == 1, "closed sessions discard late reports");
            generation = (int)HidField("hidGeneration").GetValue(null);
            HidField("hidRun").SetValue(null, true); HidField("hidN").SetValue(null, 0);
            HidField("hidFailure").SetValue(null, "");
            report[0] = 2;
            HidCall("HidStore", report, report.Length, generation);
            Check(P.HidWhy().Contains("ID 2"), "other report IDs remain diagnostic and are not treated as a pen");
            // R41: a Raw Input block (dwCount reports of dwSizeHid bytes) is split into single reports
            byte[] block = new byte[30];
            block[0] = 7; block[10] = 7; block[20] = 7; block[11] = 0xA0;
            int before = (int)HidField("hidN").GetValue(null);
            HidCall("HidStoreBlock", block, 10, 3, generation);
            Check((int)HidField("hidN").GetValue(null) == before + 3, "a Raw Input block of three pen reports stores three reports");
            HidCall("HidStoreBlock", block, 10, 4, generation);
            Check((int)HidField("hidN").GetValue(null) == before + 3, "a Raw Input block shorter than its count claims is ignored");
            string pen = (string)HidCall("WinCollectionLabel", @"\\?\hid#vid_28bd&pid_2904&mi_01#b&1db16c6e&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x0D, 2, true);
            string mouse = (string)HidCall("WinCollectionLabel", @"\\?\HID#VID_28BD&PID_2904&MI_00&Col02#b&625d154&1&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}", 1, 2, false);
            Check(pen == "interface 1 pen (000D:0002)" && mouse == "interface 0 collection 2 mouse", "Windows collections are named by interface, collection and usage");
            Check(((string)HidCall("WinOpenError", 32)).Contains("sharing violation") && ((string)HidCall("WinOpenError", 5)).Contains("access denied"), "failed opens name the Windows error");
            HidField("hidN").SetValue(null, 0); // the reader-failure checks below need a session without pen reports
            string path = Path.GetTempFileName();
            try
            {
                HidCall("HidLoop", new FileStream(path, FileMode.Open, FileAccess.Read), 10, generation);
                Check(P.HidWhy().Contains("stream closed"), "EOF stops the reader and explains the failure");
                BlockingStream stream = new BlockingStream(path);
                int capturedGeneration = generation;
                Thread reader = new Thread(delegate() { HidCall("HidLoop", stream, 10, capturedGeneration); });
                reader.IsBackground = true;
                ((FileStream[])HidField("hidStreams").GetValue(null))[0] = stream;
                ((Thread[])HidField("hidThreads").GetValue(null))[0] = reader;
                HidField("hidOpen").SetValue(null, 1);
                reader.Start();
                Check(stream.entered.WaitOne(1000), "blocked reader fixture entered Read");
                Stopwatch stopping = Stopwatch.StartNew();
                P.HidClose();
                Check(stopping.ElapsedMilliseconds < 2500 && (bool)HidField("hidClosingFailed").GetValue(null), "close is bounded when a driver ignores cancellation");
                Check(P.HidWhy().Contains("did not stop"), "an incomplete close is reported explicitly");
                stream.release.Set();
                Check(reader.Join(2000), "stopped session releases its stream when Read returns");
                P.HidClose();
                Check(!(bool)HidField("hidClosingFailed").GetValue(null), "a completed close clears the previous shutdown failure");
                stream.entered.Close(); stream.release.Close();
            }
            finally { P.HidClose(); File.Delete(path); }
        }

        public static int Main()
        {
            try { Core(); Flash(); UsbDescriptors(); Cli(); HidSessions(); Console.WriteLine(passed + " emulator regression checks passed"); return 0; }
            catch (Exception error) { Console.WriteLine(error.Message); return 1; }
        }
    }
}
