// gd32emu.cs - a general-purpose GigaDevice GD32F3x0 emulator (Cortex-M4F core, GD32F3x0 peripherals, USBFS).
//
// A C# port of deco640_sim.py (the reference twin) that must give IDENTICAL results: same reports,
// same cycle counts, same text. It needs nothing but Windows: deco640_patcher.py compiles it with
// the C# compiler that ships with every Windows 10/11 (.NET Framework 4,
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe)
// and runs deco640_twin.exe. Elsewhere it builds with mono (mcs) or any newer C# compiler.
//
//   deco640_twin.exe IMAGE [--checks boot,usb,agc,sens,scan] [--scan MS] [--speed uncapped|72mhz]
//   deco640_twin.exe IMAGE --hover --watch 0x20000EC0
//   deco640_twin.exe IMAGE --run MS --ramp "0:h=1;150:h=12" --every 10 --watch 0x20000EC0
//   deco640_twin.exe --selftest
//
// --speed 72mhz paces the emulation to the real chip's clock; uncapped (default) runs flat out.
// Both give the same results. Written in plain C# 5 (no LINQ, no generics, no unsigned types) so the
// same source compiles everywhere and can be checked against the Python twin line by line.
using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gd32Emu
{
//@@PLATFORM-BEGIN
    public static class P
    {
        public static void Line(string s) { Console.Out.Write(s + "\n"); Console.Out.Flush(); }
        public static void Write(string s) { Console.Out.Write(s); Console.Out.Flush(); }
        public static bool FileExists(string p) { return File.Exists(p); }
        public static bool DirExists(string p) { return Directory.Exists(p); }
        public static void MakeDirs(string p) { try { Directory.CreateDirectory(p); } catch (Exception) { } }
        public static void WriteAllBytes(string p, byte[] b) { File.WriteAllBytes(p, b); }
        public static void AppendText(string p, string s) { File.AppendAllText(p, s, new UTF8Encoding(false)); }
        public static void DeleteFile(string p) { try { File.Delete(p); } catch (Exception) { } }
        static bool inputEnded;
        public static string ReadLine() { string l = Console.ReadLine(); inputEnded = l == null; return l == null ? "" : l; }
        public static bool InputEnded() { return inputEnded; }
        // Enter = false, Shift+Enter = true (also "n" + Enter, for terminals that cannot report Shift, and piped input)
        public static bool ReadShiftEnter()
        {
            if (Console.IsInputRedirected) { string l = ReadLine().Trim().ToLowerInvariant(); return l == "n" || l == "next"; }
            bool n = false;
            while (true)
            {
                ConsoleKeyInfo k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return n || (k.Modifiers & ConsoleModifiers.Shift) != 0; }
                if (k.KeyChar == 'n' || k.KeyChar == 'N') { n = true; Console.Write("n"); }
                else if (k.Key == ConsoleKey.Backspace && n) { n = false; Console.Write("\b \b"); }
            }
        }
        // R44: true once Enter was pressed (never waits; other keys are dropped; false with piped input)
        public static bool KeyHit()
        {
            try
            {
                if (Console.IsInputRedirected) return false;
                while (Console.KeyAvailable) if (Console.ReadKey(true).Key == ConsoleKey.Enter) return true;
            }
            catch (Exception) { }
            return false;
        }
        // R44: the console's QuickEdit mode (Windows): a click in the window pauses the program's output until a key is
        // pressed, and a pen used as a mouse clicks. Returns the previous mode (-1: not changed); QuickEditRestore puts it back.
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out int mode);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, int mode);
        public static int QuickEditOff()
        {
            try
            {
                if (!IsWindows() || Console.IsInputRedirected) return -1;
                IntPtr h = GetStdHandle(-10);
                int m;
                if (!GetConsoleMode(h, out m)) return -1;
                if ((m & 0x40) == 0) return -1;
                return SetConsoleMode(h, (m & ~0x40) | 0x80) ? m : -1;
            }
            catch (Exception) { return -1; }
        }
        public static void QuickEditRestore(int mode)
        {
            if (mode < 0) return;
            try { SetConsoleMode(GetStdHandle(-10), mode); } catch (Exception) { }
        }
        public static string Env(string k) { return Environment.GetEnvironmentVariable(k); }
        public static string Now(string fmt) { return DateTime.Now.ToString(fmt == "date" ? "yyyyMMdd" : "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture); }
        public static string PathJoin(string a, string b) { return Path.Combine(a, b); }
        public static string FullPath(string p) { return Path.GetFullPath(p); }
        public static void AtomicWriteAllBytes(string path, byte[] bytes)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static string FileName(string p) { return Path.GetFileName(p); }
        public static string DirName(string p) { string d = Path.GetDirectoryName(p); return d == null ? "" : d; }
        public static string ExeDir() { return AppDomain.CurrentDomain.BaseDirectory; }
        public static int Cpus() { return Environment.ProcessorCount; }
        public static void StartBackground(Job job)
        {
            lock (childLock)
            {
                if (childrenStopping) throw new InvalidOperationException("The program is shutting down.");
                Thread thread = new Thread(new ThreadStart(job.Run));
                thread.IsBackground = true;
                thread.Start();
            }
        }
        public static bool StdinIsTty() { return !Console.IsInputRedirected; }
        public static string Upper(string s) { return s.ToUpperInvariant(); }
        public static bool EndsWith(string s, string p) { return s.EndsWith(p, StringComparison.Ordinal); }
        public static string Replace(string s, string a, string b) { return s.Replace(a, b); }
        public static void RunParallel(Job[] jobs, int limit)
        {
            Thread[] ts = new Thread[jobs.Length];
            for (int i = 0; i < jobs.Length; i++)
            {
                ts[i] = new Thread(new ThreadStart(jobs[i].Run), 64 * 1024 * 1024);
                ts[i].Start();
                if (i + 1 - limit >= 0) ts[i + 1 - limit].Join();
            }
            for (int i = 0; i < jobs.Length; i++) ts[i].Join();
        }
        // Keep a Windows job handle only in this process. Assign the parent before creating any
        // child: descendants inherit the job atomically, including when this process is killed
        // between Process.Start and bookkeeping. The OS closes the handle on parent death.
        // Do not explicitly close it while running: this process is itself a job member.
        static readonly object childLock = new object();
        static readonly System.Collections.ArrayList children = new System.Collections.ArrayList();
        static bool childrenReady, childrenStopping;
        static IntPtr childJob;
        [StructLayout(LayoutKind.Sequential)] struct JOB_BASIC_LIMIT
        {
            public long processTime, jobTime; public uint flags;
            public UIntPtr minimumWorkingSet, maximumWorkingSet;
            public uint activeProcessLimit; public UIntPtr affinity;
            public uint priorityClass, schedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] struct JOB_IO_COUNTERS
        {
            public ulong readOperations, writeOperations, otherOperations, readBytes, writeBytes, otherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] struct JOB_EXTENDED_LIMIT
        {
            public JOB_BASIC_LIMIT basic; public JOB_IO_COUNTERS io;
            public UIntPtr processMemoryLimit, jobMemoryLimit, peakProcessMemory, peakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int kind, ref JOB_EXTENDED_LIMIT info, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

        static void EnsureChildLifetime()
        {
            if (childrenReady) return;
            if (IsWindows())
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create child-process lifetime job.");
                JOB_EXTENDED_LIMIT info = new JOB_EXTENDED_LIMIT();
                info.basic.flags = 0x2000;
                if (!SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(JOB_EXTENDED_LIMIT))))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseHandle(job);
                    throw new System.ComponentModel.Win32Exception(error, "Could not configure child-process lifetime job.");
                }
                childJob = job;
            }
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs args) { StopChildren(); };
            AppDomain.CurrentDomain.ProcessExit += delegate(object sender, EventArgs args) { StopChildren(); };
            childrenReady = true;
        }

        static Process StartChild(ProcessStartInfo info)
        {
            lock (childLock)
            {
                if (childrenStopping) throw new InvalidOperationException("The program is shutting down; no further tool commands will run.");
                EnsureChildLifetime();
                Process process = Process.Start(info);
                if (process == null) throw new InvalidOperationException("Could not start " + info.FileName + ".");
                if (childJob != IntPtr.Zero && !AssignProcessToJobObject(childJob, process.Handle))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 5)
                    {
                        try { process.Kill(); } catch { }
                        process.Dispose();
                        throw new System.ComponentModel.Win32Exception(error, "Could not attach child-process lifetime protection.");
                    }
                }
                children.Add(process);
                return process;
            }
        }

        static void KillChild(Process process)
        {
            try
            {
                if (process.HasExited) return;
                // .NET 10 also terminates descendants on Unix. Framework's Windows job owns the
                // whole tree even though its Process API only offers Kill() on the direct child.
                System.Reflection.MethodInfo killTree = typeof(Process).GetMethod("Kill", new Type[] { typeof(bool) });
                if (killTree != null) killTree.Invoke(process, new object[] { true });
                else process.Kill();
            }
            catch (Exception) { try { process.Kill(); } catch (Exception) { } } // exit races are expected during shutdown
        }

        static void StopChildren()
        {
            lock (childLock)
            {
                childrenStopping = true;
                for (int i = 0; i < children.Count; i++) KillChild((Process)children[i]);
            }
        }

        static void ReleaseChild(Process process)
        {
            if (process == null) return;
            lock (childLock)
            {
                KillChild(process); // also clean up if a stream or wait failed
                children.Remove(process);
                process.Dispose();
            }
        }

        public static string[] RunProcess(string exe, string args)
        {
            // stdout and stderr are drained at the same time and echoed as they arrive (reading one stream to the end
            // first can deadlock once the other fills its pipe: that stalled the second flash attempt)
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                p = StartChild(psi);
                p.StandardInput.Close();
                StringBuilder sb = new StringBuilder();
                Pump a = new Pump(p.StandardOutput, sb), b = new Pump(p.StandardError, sb);
                Thread ta = new Thread(new ThreadStart(a.Run)), tb = new Thread(new ThreadStart(b.Run));
                ta.Start(); tb.Start();
                p.WaitForExit(); ta.Join(); tb.Join();
                return new string[] { p.ExitCode.ToString(CultureInfo.InvariantCulture), sb.ToString() };
            }
            catch (Exception e) { return new string[] { "-1", e.Message }; }
            finally { ReleaseChild(p); }
        }
        public class Pump
        {
            StreamReader r; StringBuilder sb;
            public Pump(StreamReader rd, StringBuilder s) { r = rd; sb = s; }
            public void Run()
            {
                int ch;
                while ((ch = r.Read()) >= 0) { lock (sb) { Console.Write((char)ch); sb.Append((char)ch); } }
            }
        }
        public static void Clear() { try { if (!Console.IsOutputRedirected) Console.Clear(); } catch (Exception) { } }
        public static bool IsWindows() { return Path.DirectorySeparatorChar == '\\'; }
        public static bool IsMac() { return !IsWindows() && File.Exists("/System/Library/CoreServices/SystemVersion.plist"); }
        public static int CursorTop() { try { if (Console.IsOutputRedirected) return -1; return Console.CursorTop; } catch (Exception) { return -1; } }
        // visible columns of the console window; -1 when output is not a console window (piped, tests)
        public static int WindowWidth() { try { if (Console.IsOutputRedirected) return -1; return Console.WindowWidth; } catch (Exception) { return -1; } }
        public static int WindowHeight() { try { return Console.WindowHeight; } catch (Exception) { return 0; } }
        public static void SetCursor(int l, int t) { try { Console.SetCursorPosition(l, t); } catch (Exception) { } }
        public static string[] ListDirs(string p) { try { return Directory.GetDirectories(p); } catch (Exception) { return new string[0]; } }
        public static string[] ListFiles(string p) { try { string[] f = Directory.GetFiles(p); Array.Sort(f, StringComparer.Ordinal); return f; } catch (Exception) { return new string[0]; } }
        public static long FileSize(string p) { return new FileInfo(p).Length; }

        // runs a tool and shows its output under INDENT; progress lines ("[13.6%] ...") overwrite each other
        public static string RunProcessProgress(string exe, string args, string indent) { return RunIn(null, exe, args, indent); }

        // runs EXE in DIR; its prompts appear at once and you answer them here (stdin is passed through); progress lines
        // ("[13.6%] ...") overwrite each other
        public static string RunIn(string dir, string exe, string args, string indent)
        {
            return RunInResult(dir, exe, args, indent, true)[1];
        }

        // Keep the exit status as well as the output. Firmware tools must never infer success from text alone.
        // echo = interactive (dfu, the dump before a flash, update): the tool runs in THIS console with this console's
        // input, exactly like typing the command by hand, so its typed confirmation ("Type exactly: UPDATE ...")
        // reads the user's keyboard. CreateNoWindow must stay off here: on Windows it gives the tool its own hidden
        // console, and a prompt that reads that console can never be answered (R37: nothing could be typed at the
        // UPDATE confirmation). Quiet commands (the background startup dump) keep a hidden console and a closed stdin,
        // so they can never take the menu's input; Ctrl+C and exit still end them (StopChildren, Windows job).
        public static ProcessStartInfo ToolStart(string dir, string exe, string args, bool echo)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            if (dir != null && P.Len(dir) > 0) psi.WorkingDirectory = dir;
            psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.CreateNoWindow = !echo;
            psi.RedirectStandardInput = !echo;
            return psi;
        }
        public static string[] RunInResult(string dir, string exe, string args, string indent, bool echo)
        {
            Process p = null;
            try
            {
                ProcessStartInfo psi = ToolStart(dir, exe, args, echo);
                p = StartChild(psi);
                if (!echo) p.StandardInput.Close();
                LinePump lp = new LinePump(indent); lp.echo = echo;
                CharPump a = new CharPump(p.StandardOutput, lp, 0), b = new CharPump(p.StandardError, lp, 1);
                PromptTicker tk = new PromptTicker(); tk.lp = lp;
                Thread ta = new Thread(new ThreadStart(a.Run)), tb = new Thread(new ThreadStart(b.Run)), tt = new Thread(new ThreadStart(tk.Run));
                tt.IsBackground = true;
                ta.Start(); tb.Start(); if (echo) tt.Start();
                p.WaitForExit(); ta.Join(); tb.Join();
                tk.stop = true; if (echo) tt.Join();
                lp.Done();
                string[] result = new string[] { p.ExitCode.ToString(CultureInfo.InvariantCulture), lp.all.ToString() };
                return result;
            }
            catch (Exception e)
            {
                string error = "could not run " + exe + ": " + e.Message;
                if (echo) Console.WriteLine(indent + error);
                return new string[] { "-1", error };
            }
            finally { ReleaseChild(p); }
        }
        // Echoes a tool's stdout (stream 0) and stderr (stream 1) under INDENT, forward-only (no cursor movement up).
        // Each stream keeps its own partial line, so a log line on one can never be glued into a prompt on the other
        // (R37 showed "> [INFO] Device: ..."). A prompt (a partial line ending in "> " or ": ") is shown once the tool
        // has been quiet for QUIET ms (at most LATEST ms after it appeared): log lines the tool flushes right after its
        // prompt - a Python tool flushes stderr when it starts reading input - are printed first, and the prompt is
        // the last thing on screen, where the user types. Progress lines ("[13.6%] ...") overwrite each other.
        public class LinePump
        {
            public const int QUIET = 150, LATEST = 1000;
            public StringBuilder all = new StringBuilder();
            public bool echo = true;
            string indent; bool progress = false;
            StringBuilder[] line = new StringBuilder[] { new StringBuilder(), new StringBuilder() };
            int[] shown = new int[2];
            int open = -1;                       // the stream whose prompt the cursor is right after; -1: none
            bool[] waiting = new bool[2]; int[] waitingAt = new int[2];   // a prompt not shown yet, per stream, since when
            int lastAt = 0;                      // the tool's last output
            public LinePump(string ind) { indent = ind; }
            public void Put(char c) { Put(0, c); }
            static bool IsPrompt(string t) { return t.EndsWith("> ", StringComparison.Ordinal) || t.EndsWith(": ", StringComparison.Ordinal); }
            // ends the screen line a progress line or prompt is on, so the next line starts on its own row
            void EndScreenLine()
            {
                if (progress) { Console.WriteLine(); progress = false; }
                if (open >= 0) { Console.WriteLine(); shown[open] = 0; open = -1; }
            }
            public void Put(int s, char c)
            {
                lock (this)
                {
                    all.Append(c);
                    if (!echo) return;
                    lastAt = Environment.TickCount;
                    if (c != '\n' && c != '\r')
                    {
                        line[s].Append(c);
                        string sofar = line[s].ToString();
                        if (!IsPrompt(sofar)) { waiting[s] = false; return; }
                        if (open == s) { Console.Write(sofar.Substring(shown[s])); shown[s] = sofar.Length; return; }   // same screen line
                        if (!waiting[s]) { waiting[s] = true; waitingAt[s] = lastAt; }
                        return;
                    }
                    waiting[s] = false;                  // the line went on: it was not waiting for input
                    string l = line[s].ToString().TrimEnd();
                    int had = shown[s];
                    bool onScreen = open == s && had > 0;
                    line[s].Length = 0; shown[s] = 0;
                    if (onScreen)
                    {
                        // the prompt was answered (the Enter already moved the cursor down): the rest is a new line
                        open = -1;
                        l = l.Length > had ? l.Substring(had).Trim() : "";
                        if (l.Length == 0) { Console.WriteLine(); return; }
                    }
                    if (l.Length == 0) return;
                    bool pr = l.StartsWith("[", StringComparison.Ordinal) && l.IndexOf("%]", StringComparison.Ordinal) > 0;
                    if (pr)
                    {
                        if (open >= 0) { Console.WriteLine(); shown[open] = 0; open = -1; }
                        Console.Write("\r" + indent + l + "        "); progress = true; return;
                    }
                    EndScreenLine();
                    Console.WriteLine(indent + l);
                }
            }
            // called every few ms while the tool runs: shows a waiting prompt once the output has gone quiet
            public void Tick()
            {
                lock (this)
                {
                    if (!echo) return;
                    int now = Environment.TickCount, s = -1;
                    for (int k = 0; k < 2; k++) if (waiting[k] && (s < 0 || waitingAt[k] - waitingAt[s] < 0)) s = k;
                    if (s < 0 || (now - lastAt < QUIET && now - waitingAt[s] < LATEST)) return;
                    waiting[s] = false;
                    if (open != s) EndScreenLine();
                    else if (progress) { Console.WriteLine(); progress = false; }
                    string sofar = line[s].ToString();
                    Console.Write(shown[s] == 0 ? indent + sofar : sofar.Substring(shown[s]));
                    shown[s] = sofar.Length; open = s;
                }
            }
            public void Done() { lock (this) { waiting[0] = waiting[1] = false; for (int s = 0; s < 2; s++) if (line[s].Length > 0) Put(s, '\n'); if (progress) Console.WriteLine(); progress = false; } }
        }
        public class CharPump
        {
            StreamReader r; LinePump lp; int stream;
            public CharPump(StreamReader rd, LinePump l) : this(rd, l, 0) { }
            public CharPump(StreamReader rd, LinePump l, int s) { r = rd; lp = l; stream = s; }
            // whole chunks under one lock: text the tool wrote in one go stays together on screen
            public void Run()
            {
                char[] chunk = new char[4096]; int n;
                while ((n = r.Read(chunk, 0, chunk.Length)) > 0) lock (lp) { for (int i = 0; i < n; i++) lp.Put(stream, chunk[i]); }
            }
        }
        public class PromptTicker
        {
            public LinePump lp; public volatile bool stop;
            public void Run() { while (!stop) { Thread.Sleep(25); lp.Tick(); } }
        }

        // ---- tablet HID reading on Windows: Windows' own HID driver plus Raw Input; nothing is installed.
        // How Windows presents the Deco 640 (VID 28BD, PID 2904), measured on Anargon's PC with the tablet (R41, read-only probe):
        //   MI_00 Col01/Col02 mouse 0001:0002, Col03 keyboard 0001:0006 - opened exclusively by Windows: ACCESS_DENIED (5)
        //   MI_01 pen digitizer 000D:0002, 10-byte reports, report ID 7 (report mode 2: pen reports and calibration telemetry)
        //         - held by Windows' pen input: CreateFile(GENERIC_READ) fails with SHARING_VIOLATION (32)
        //   MI_02 vendor FF0A:0001, 12-byte reports, report ID 2 - opens, but silent until a host selects report mode 4
        // Up to R40 only CreateFile/ReadFile was used and failed opens were skipped silently, so on Windows only the silent MI_02
        // was read ("no reports at all from 1 open collection(s)") whatever firmware was installed. Raw Input
        // (RegisterRawInputDevices, RIDEV_INPUTSINK) hands this program the pen collection's reports byte for byte from
        // Windows' own reader: 3199 ID-7 reports in 8 s (400/s) on the real tablet, where ReadFile received none. So digitizer
        // collections (usage page 000D) are read through Raw Input and every other usable collection through ReadFile; each
        // collection is read one way only, and every collection is listed with its result (HidDescribe), never skipped silently.
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        // HidD_* return a one-byte BOOLEAN: without U1 the undefined upper bytes of the return register are read too
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetIndexedString(SafeFileHandle h, int index, byte[] buf, int len);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr di, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }
        [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS
        {
            public short Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public short[] Reserved;
            public short a, b, c, d, e, f, g, h, i, j;
        }
        // Raw Input (user32): a message-only window on its own thread receives WM_INPUT for the registered digitizer usages
        delegate IntPtr WndProcFn(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WNDCLASSEX
        {
            public int cbSize, style; public WndProcFn lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
        }
        [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public int dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public int time, x, y, priv; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, int n, int size);
        [DllImport("user32.dll")] static extern int GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref int size, int header);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetRawInputDeviceInfo(IntPtr dev, uint cmd, StringBuilder data, ref int size);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjects(int n, IntPtr[] h, bool all, int ms, uint mask);

        // One reader per opened stream: ReadFile collections on Windows, hidraw nodes on Linux. Reports of every ID are
        // counted (diagnosis); ID-7 reports are kept from whichever delivers them.
        public class HidReader
        {
            public FileStream s; public int len, generation, fd = -1;
            public void Run() { if (fd >= 0) HidLoopLinux(fd, generation); else HidLoop(s, len, generation); }
        }
        static FileStream[] hidStreams = new FileStream[16];
        static Thread[] hidThreads = new Thread[16];
        static int hidOpen = 0;
        static volatile int hidOther = 0, hidOtherId = -1;
        static volatile bool hidRun = false;
        static volatile int hidGeneration = 0;
        static string hidFailure = "";
        static bool hidClosingFailed = false;
        static int[] hidRec = new int[11 * 65536];
        static int[] hidReportCounts = new int[256];
        static int hidN = 0;
        static double hidT0 = 0;
        static string hidSummary = "";                 // Windows: what is being read and how, for HidWhy
        static string[] hidDescribe = new string[0];   // Windows: one line per tablet collection, for --hid-check

        // device interface paths of every present HID collection of VID:PID (Windows)
        static System.Collections.ArrayList WinHidPaths(int vid, int pid)
        {
            System.Collections.ArrayList paths = new System.Collections.ArrayList();
            string want1 = "vid_" + vid.ToString("x4"), want2 = "pid_" + pid.ToString("x4");
            IntPtr set = IntPtr.Zero;
            try
            {
                Guid g; HidD_GetHidGuid(out g);
                set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
                if (set == IntPtr.Zero || set == new IntPtr(-1)) return paths;
                for (int idx = 0; idx < 1024; idx++)
                {
                    SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, idx, ref d)) break;
                    int req;
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req + 8);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                        if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    if (path == null) continue;
                    string lp = path.ToLowerInvariant();
                    if (lp.IndexOf(want1, StringComparison.Ordinal) >= 0 && lp.IndexOf(want2, StringComparison.Ordinal) >= 0) paths.Add(path);
                }
            }
            finally { if (set != IntPtr.Zero && set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        // USB string descriptor INDEX of the first device VID:PID that answers it, read through Windows' HID driver
        // (no access rights needed, works while another program reads the tablet); null if none answers or on
        // Linux / macOS (hidraw cannot request arbitrary strings).
        public static string HidIndexedString(int vid, int pid, int index)
        {
            if (IsMac()) return MacUsbString(vid, pid, index);
            if (!IsWindows()) return LinuxUsbString(vid, pid, index);
            try
            {
                System.Collections.ArrayList paths = WinHidPaths(vid, pid);
                for (int i = 0; i < paths.Count; i++)
                {
                    SafeFileHandle h = CreateFile((string)paths[i], 0u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero); // access 0: allowed on every collection
                    if (h.IsInvalid) continue;
                    try
                    {
                        byte[] sb = new byte[256];
                        if (HidD_GetIndexedString(h, index, sb, sb.Length))
                        {
                            string v = Encoding.Unicode.GetString(sb);
                            int z = v.IndexOf('\0');
                            return z >= 0 ? v.Substring(0, z) : v;
                        }
                    }
                    finally { h.Close(); }
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        // R44 (APIP training): one handle kept open for many reads of one string, so each read is only the USB request
        // (HidIndexedString finds and opens the collection every time). Windows: the first collection that answers INDEX;
        // elsewhere every read goes through HidIndexedString.
        static SafeFileHandle hidStr = null;
        static int hidStrVid, hidStrPid;
        public static bool HidStringOpen(int vid, int pid, int index)
        {
            HidStringClose();
            hidStrVid = vid; hidStrPid = pid;
            if (!IsWindows()) return HidIndexedString(vid, pid, index) != null;
            try
            {
                System.Collections.ArrayList paths = WinHidPaths(vid, pid);
                for (int i = 0; i < paths.Count; i++)
                {
                    SafeFileHandle h = CreateFile((string)paths[i], 0u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    byte[] b = new byte[256];
                    if (HidD_GetIndexedString(h, index, b, b.Length)) { hidStr = h; return true; }
                    h.Close();
                }
            }
            catch (Exception) { }
            return false;
        }
        // the string's characters as UTF-16LE bytes, zero-padded to 256 bytes; null when the tablet did not answer
        public static byte[] HidStringRead(int index)
        {
            try
            {
                if (hidStr == null)
                {
                    if (IsWindows()) return null;
                    string v = HidIndexedString(hidStrVid, hidStrPid, index);
                    if (v == null) return null;
                    byte[] r = new byte[256];
                    byte[] e = Encoding.Unicode.GetBytes(v);
                    Array.Copy(e, r, e.Length < 254 ? e.Length : 254);
                    return r;
                }
                byte[] b = new byte[256];
                return HidD_GetIndexedString(hidStr, index, b, b.Length) ? b : null;
            }
            catch (Exception) { return null; }
        }
        public static void HidStringClose() { if (hidStr != null) { try { hidStr.Close(); } catch (Exception) { } hidStr = null; } }

        // "interface 1 pen (000D:0002)" from a collection's device path and top-level usage (usage omitted when SHORT)
        static string WinCollectionLabel(string path, int page, int usage, bool full)
        {
            string lp = path.ToLowerInvariant(), where = "";
            int mi = lp.IndexOf("&mi_", StringComparison.Ordinal), col = lp.IndexOf("&col", StringComparison.Ordinal), n;
            if (mi >= 0 && mi + 6 <= lp.Length && int.TryParse(lp.Substring(mi + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
                where = "interface " + n.ToString(CultureInfo.InvariantCulture);
            if (col >= 0 && col + 6 <= lp.Length && int.TryParse(lp.Substring(col + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
                where += (where.Length > 0 ? " " : "") + "collection " + n.ToString(CultureInfo.InvariantCulture);
            string kind = page < 0 ? "collection (usage unreadable)" : page == 1 && usage == 2 ? "mouse" : page == 1 && usage == 6 ? "keyboard"
                : page == 0x0D && usage == 2 ? "pen" : page == 0x0D ? "digitizer" : page >= 0xFF00 ? "vendor" : "collection";
            string text = (where.Length > 0 ? where + " " : "") + kind;
            return full && page >= 0 ? text + " (" + page.ToString("X4") + ":" + usage.ToString("X4") + ")" : text;
        }
        static string WinOpenError(int error)
        {
            if (error == 5) return "held by Windows (access denied)";
            if (error == 32) return "held by Windows or another program (sharing violation)";
            return "cannot be opened (Windows error " + error.ToString(CultureInfo.InvariantCulture) + ")";
        }

        // opens the pen interface of the tablet (VID 28BD, PID 2904); "" = ok, else why not
        public static string HidOpenPen()
        {
            HidClose();
            if (hidClosingFailed) return "the previous tablet reader is still stopping; reconnect the tablet or restart this tool before trying again";
            lock (hidRec) { hidFailure = ""; Array.Clear(hidReportCounts, 0, hidReportCounts.Length); }
            hidSummary = ""; hidDescribe = new string[0];
            if (IsMac()) return HidOpenMac();
            if (!IsWindows()) return HidOpenLinux();
            try
            {
                string error = HidOpenWindows();
                if (error.Length > 0) HidClose();
                return error;
            }
            catch (Exception e) { HidClose(); return "HID error: " + e.Message; }
        }

        static string HidOpenWindows()
        {
            System.Collections.ArrayList paths = WinHidPaths(0x28BD, 0x2904);
            hidOpen = 0; hidOther = 0; hidOtherId = -1; hidN = 0; hidT0 = NowSec();
            if (paths.Count == 0) return "no Deco 640 found (VID 28BD, PID 2904): plug it in, in normal mode (not DFU)";
            hidRun = true;
            System.Collections.ArrayList lines = new System.Collections.ArrayList(), reading = new System.Collections.ArrayList(), rawUsages = new System.Collections.ArrayList(), rawLabels = new System.Collections.ArrayList(), rawShort = new System.Collections.ArrayList();
            for (int i = 0; i < paths.Count; i++)
            {
                string path = (string)paths[i];
                int page = -1, usage = -1, len = 0;
                SafeFileHandle info = CreateFile(path, 0u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero); // access 0: usage and sizes of any collection
                if (!info.IsInvalid)
                {
                    try
                    {
                        IntPtr pp;
                        if (HidD_GetPreparsedData(info, out pp))
                        {
                            HIDP_CAPS caps;
                            if (HidP_GetCaps(pp, out caps) == 0x00110000) // HIDP_STATUS_SUCCESS
                            { page = caps.UsagePage & 0xFFFF; usage = caps.Usage & 0xFFFF; len = caps.InputReportByteLength & 0xFFFF; }
                            HidD_FreePreparsedData(pp);
                        }
                    }
                    finally { info.Close(); }
                }
                string label = WinCollectionLabel(path, page, usage, true) + (len > 0 ? ", " + len.ToString(CultureInfo.InvariantCulture) + "-byte reports" : "");
                if (page == 0x0D)
                {
                    // Windows' pen input holds digitizer collections; Raw Input delivers their reports instead of ReadFile
                    int key = (page << 16) | usage;
                    if (!rawUsages.Contains(key)) rawUsages.Add(key);
                    rawLabels.Add(label); rawShort.Add(WinCollectionLabel(path, page, usage, false));
                    continue;
                }
                if (len > 0 && len < 10) { lines.Add(label + ": not read (too short for pen reports)"); continue; }
                SafeFileHandle h = CreateFile(path, 0x80000000u, 3u, IntPtr.Zero, 3u, 0x40000000u, IntPtr.Zero); // overlapped: close can cancel a pending read
                if (h.IsInvalid) { lines.Add(label + ": not read, " + WinOpenError(Marshal.GetLastWin32Error())); continue; }
                if (len < 10 || hidOpen >= hidStreams.Length) { h.Close(); lines.Add(label + ": not read (" + (len < 10 ? "report size unreadable" : "too many collections") + ")"); continue; }
                HidReader rd = new HidReader();
                rd.s = new FileStream(h, FileAccess.Read, len, true); rd.len = len; rd.generation = hidGeneration;
                hidStreams[hidOpen] = rd.s;
                Thread t = new Thread(new ThreadStart(rd.Run));
                hidThreads[hidOpen++] = t;
                t.IsBackground = true;
                t.Start();
                lines.Add(label + ": read through ReadFile");
                reading.Add(WinCollectionLabel(path, page, usage, false) + " via ReadFile");
            }
            if (rawUsages.Count > 0)
            {
                WinRawSession raw = new WinRawSession();
                raw.generation = hidGeneration;
                raw.usages = (int[])rawUsages.ToArray(typeof(int));
                raw.thread = new Thread(new ThreadStart(raw.Run)); raw.thread.IsBackground = true;
                winRaw = raw;
                raw.thread.Start();
                if (!raw.ready.WaitOne(3000)) raw.status = "Raw Input did not start within 3 seconds";
                string rawStatus = raw.status;
                for (int i = 0; i < rawLabels.Count; i++)
                {
                    lines.Insert(i, (string)rawLabels[i] + (rawStatus.Length == 0 ? ": read through Raw Input" : ": not read, Raw Input failed: " + rawStatus));
                    if (rawStatus.Length == 0) reading.Insert(i, (string)rawShort[i] + " via Raw Input");
                }
            }
            hidDescribe = (string[])lines.ToArray(typeof(string));
            if (reading.Count == 0) return "the tablet was found, but none of its HID collections can be read: " + string.Join("; ", hidDescribe) + " (close OpenTabletDriver / the XP-Pen driver and try again)";
            hidSummary = "reading " + string.Join(", ", (string[])reading.ToArray(typeof(string)));
            return "";
        }

        // Windows: one line per tablet HID collection and how this program reads it (empty on Linux / macOS)
        public static string[] HidDescribe()
        {
            string[] d = hidDescribe; string[] o = new string[d.Length];
            Array.Copy(d, o, d.Length);
            return o;
        }

        // ---- Windows Raw Input session: owns its thread, message-only window and registration; one per open
        const string WIN_RAW_CLASS = "Gd32EmuTabletRawInput";
        static readonly object winRawLock = new object();
        static bool winRawClass = false;
        static WndProcFn winRawProc;                   // stays referenced: Windows may call it for the life of the process
        static WinRawSession winRaw = null;
        static IntPtr WinRawWndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            if (msg == 0x00FF) { WinRawSession s = winRaw; if (s != null && s.hwnd == hwnd) s.Input(l); } // WM_INPUT
            return DefWindowProc(hwnd, msg, w, l);     // required after WM_INPUT: frees the input
        }
        class WinRawSession
        {
            public int generation; public int[] usages = new int[0];
            public volatile bool running = true; public Thread thread; public IntPtr hwnd = IntPtr.Zero;
            public readonly ManualResetEvent ready = new ManualResetEvent(false);
            public volatile string status = "";        // "" once registered; else why Raw Input could not start
            readonly System.Collections.Hashtable tablet = new System.Collections.Hashtable(); // raw device handle -> whether it is the Deco 640
            public void Run()
            {
                RAWINPUTDEVICE[] reg = null; bool registered = false;
                try
                {
                    IntPtr module = GetModuleHandle(null);
                    lock (winRawLock)
                    {
                        if (!winRawClass)
                        {
                            winRawProc = new WndProcFn(WinRawWndProc);
                            WNDCLASSEX wc = new WNDCLASSEX(); wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
                            wc.lpfnWndProc = winRawProc; wc.hInstance = module; wc.lpszClassName = WIN_RAW_CLASS;
                            if (RegisterClassEx(ref wc) == 0) { int e = Marshal.GetLastWin32Error(); if (e != 1410) throw new IOException("RegisterClassEx failed (error " + e.ToString() + ")"); } // 1410: already registered
                            winRawClass = true;
                        }
                    }
                    hwnd = CreateWindowEx(0, WIN_RAW_CLASS, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, module, IntPtr.Zero); // HWND_MESSAGE
                    if (hwnd == IntPtr.Zero) throw new IOException("CreateWindowEx failed (error " + Marshal.GetLastWin32Error().ToString() + ")");
                    reg = new RAWINPUTDEVICE[usages.Length];
                    for (int i = 0; i < reg.Length; i++)
                    {
                        reg[i].usUsagePage = (ushort)(usages[i] >> 16); reg[i].usUsage = (ushort)(usages[i] & 0xFFFF);
                        reg[i].dwFlags = 0x100; reg[i].hwndTarget = hwnd; // RIDEV_INPUTSINK: also while the console is in the background
                    }
                    if (!RegisterRawInputDevices(reg, reg.Length, Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                        throw new IOException("RegisterRawInputDevices failed (error " + Marshal.GetLastWin32Error().ToString() + ")");
                    registered = true;
                    ready.Set();
                    MSG m;
                    while (running && HidActive(generation))
                    {
                        MsgWaitForMultipleObjects(0, null, false, 50, 0x04FF); // QS_ALLINPUT, includes QS_RAWINPUT
                        while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); } // PM_REMOVE
                    }
                }
                catch (Exception e)
                {
                    if (!registered) status = e.Message;
                    else HidReadFailed(generation, "the Raw Input reader stopped: " + e.Message + "; reconnect the tablet and reopen calibration");
                }
                finally
                {
                    if (registered)
                    {
                        for (int i = 0; i < reg.Length; i++) { reg[i].dwFlags = 0x1; reg[i].hwndTarget = IntPtr.Zero; } // RIDEV_REMOVE
                        RegisterRawInputDevices(reg, reg.Length, Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                    }
                    if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
                    ready.Set();
                }
            }
            public void Input(IntPtr handle)
            {
                if (!running || !HidActive(generation)) return;
                int header = 8 + 2 * IntPtr.Size, size = 0;            // RAWINPUTHEADER: dwType, dwSize, hDevice, wParam
                if (GetRawInputData(handle, 0x10000003u, IntPtr.Zero, ref size, header) != 0 || size < header + 8) return; // RID_INPUT
                IntPtr data = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetRawInputData(handle, 0x10000003u, data, ref size, header) != size) return;
                    if (Marshal.ReadInt32(data, 0) != 2) return;                          // RIM_TYPEHID
                    if (!IsTablet(Marshal.ReadIntPtr(data, 8))) return;
                    int each = Marshal.ReadInt32(data, header), count = Marshal.ReadInt32(data, header + 4); // RAWHID.dwSizeHid, dwCount
                    if (each <= 0 || count <= 0 || (long)each * count > size - header - 8) return;
                    byte[] block = new byte[each * count];
                    Marshal.Copy(new IntPtr(data.ToInt64() + header + 8), block, 0, block.Length);
                    HidStoreBlock(block, each, count, generation);
                }
                finally { Marshal.FreeHGlobal(data); }
            }
            bool IsTablet(IntPtr device)
            {
                object known = tablet[device];
                if (known != null) return (bool)known;
                int size = 0; bool yes = false;
                GetRawInputDeviceInfo(device, 0x20000007u, null, ref size);        // RIDI_DEVICENAME; size in characters
                if (size > 0 && size < 4096)
                {
                    StringBuilder name = new StringBuilder(size + 1);
                    if (GetRawInputDeviceInfo(device, 0x20000007u, name, ref size) > 0)
                    {
                        string n = name.ToString().ToLowerInvariant();
                        yes = n.IndexOf("vid_28bd", StringComparison.Ordinal) >= 0 && n.IndexOf("pid_2904", StringComparison.Ordinal) >= 0;
                    }
                }
                tablet[device] = yes;
                return yes;
            }
        }
        // a Raw Input block carries COUNT reports of EACH bytes, each starting with its report ID
        static void HidStoreBlock(byte[] block, int each, int count, int generation)
        {
            if (each <= 0 || count <= 0 || block == null || block.Length < each * count) return;
            byte[] one = new byte[each];
            for (int k = 0; k < count; k++) { Array.Copy(block, k * each, one, 0, each); HidStore(one, each, generation); }
        }
        // Linux: every hidraw node of the tablet (one per HID interface) is read, like the Windows collections. Needs read
        // access to /dev/hidraw*: run with sudo, or install the udev rule (UdevRule()) once.
        static string HidOpenLinux()
        {
            if (!Directory.Exists("/sys/class/hidraw")) return "no hidraw devices found (is the tablet plugged in?)";
            hidOpen = 0; hidOther = 0; hidOtherId = -1; hidRun = true; hidN = 0; hidT0 = NowSec();
            bool saw = false; string denied = null;
            foreach (string dir in Directory.GetDirectories("/sys/class/hidraw"))
            {
                string ue;
                try { ue = File.ReadAllText(Path.Combine(Path.Combine(dir, "device"), "uevent")).ToUpperInvariant(); } catch (Exception) { continue; }
                if (ue.IndexOf("000028BD:00002904", StringComparison.Ordinal) < 0) continue;
                saw = true;
                string dev = "/dev/" + Path.GetFileName(dir);
                int fd = lx_open(dev, 0x800); // O_RDONLY | O_NONBLOCK: a stopped session never waits on a pen report
                if (fd < 0) { denied = dev + " (error " + Marshal.GetLastWin32Error().ToString() + ")"; continue; }
                if (hidOpen >= hidStreams.Length) { lx_close(fd); continue; }
                HidReader rd = new HidReader(); rd.fd = fd; rd.generation = hidGeneration;
                Thread t = new Thread(new ThreadStart(rd.Run)); t.IsBackground = true;
                hidThreads[hidOpen++] = t; t.Start();
            }
            if (hidOpen > 0) return "";
            hidRun = false;
            if (!saw) return "no Deco 640 found (VID 28BD, PID 2904): plug it in, in normal mode (not DFU)";
            return "cannot read the tablet: " + denied + ". Allow it once with a udev rule (shown by --linux-setup), or run with sudo";
        }
        public static string UdevRule()
        {
            return "SUBSYSTEM==\"hidraw\", ATTRS{idVendor}==\"28bd\", ATTRS{idProduct}==\"2904\", MODE=\"0666\"\n"
                + "SUBSYSTEM==\"usb\", ATTR{idVendor}==\"28bd\", MODE=\"0666\"\n";
        }

        // ---- Linux: one USB string descriptor through the kernel's usbfs (USBDEVFS_CONTROL); libc only
        [DllImport("libc", EntryPoint = "open", SetLastError = true)] static extern int lx_open(string path, int flags);
        [DllImport("libc", EntryPoint = "close")] static extern int lx_close(int fd);
        [DllImport("libc", EntryPoint = "read", SetLastError = true)] static extern IntPtr lx_read(int fd, byte[] buffer, UIntPtr count);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] static extern int lx_ioctl(int fd, UIntPtr req, IntPtr arg);
        static string LinuxUsbString(int vid, int pid, int index)
        {
            try
            {
                if (!Directory.Exists("/sys/bus/usb/devices")) return null;
                string want1 = vid.ToString("x4"), want2 = pid.ToString("x4");
                foreach (string dir in Directory.GetDirectories("/sys/bus/usb/devices"))
                {
                    string v = SysRead(Path.Combine(dir, "idVendor")), pr = SysRead(Path.Combine(dir, "idProduct"));
                    if (v != want1 || pr != want2) continue;
                    int bus = int.Parse(SysRead(Path.Combine(dir, "busnum"))), dev = int.Parse(SysRead(Path.Combine(dir, "devnum")));
                    string node = "/dev/bus/usb/" + bus.ToString("000") + "/" + dev.ToString("000");
                    int fd = lx_open(node, 2);                       // O_RDWR (control transfers need it)
                    if (fd < 0) continue;
                    try
                    {
                        // struct usbdevfs_ctrltransfer { u8 bRequestType, bRequest; u16 wValue, wIndex, wLength; u32 timeout; void *data; }
                        int ps = IntPtr.Size, size = ps == 8 ? 24 : 16, dataOff = ps == 8 ? 16 : 12;
                        IntPtr ct = Marshal.AllocHGlobal(size), data = Marshal.AllocHGlobal(256);
                        try
                        {
                            for (int k = 0; k < size; k++) Marshal.WriteByte(ct, k, 0);
                            Marshal.WriteByte(ct, 0, 0x80); Marshal.WriteByte(ct, 1, 6);
                            Marshal.WriteInt16(ct, 2, (short)(0x0300 | index)); Marshal.WriteInt16(ct, 4, 0x0409); Marshal.WriteInt16(ct, 6, 255);
                            Marshal.WriteInt32(ct, 8, 1000);
                            Marshal.WriteIntPtr(ct, dataOff, data);
                            uint req = (3u << 30) | ((uint)size << 16) | ((uint)'U' << 8) | 0u;      // _IOWR('U', 0, struct)
                            int n = lx_ioctl(fd, new UIntPtr(req), ct);
                            if (n < 4) continue;
                            byte[] b = new byte[n]; Marshal.Copy(data, b, 0, n);
                            if (b[1] != 3) continue;
                            int len = b[0] < n ? b[0] : n;
                            return Encoding.Unicode.GetString(b, 2, (len - 2) & ~1);
                        }
                        finally { Marshal.FreeHGlobal(ct); Marshal.FreeHGlobal(data); }
                    }
                    finally { lx_close(fd); }
                }
            }
            catch (Exception) { }
            return null;
        }
        static string SysRead(string f) { try { return File.ReadAllText(f).Trim().ToLowerInvariant(); } catch (Exception) { return ""; } }

        // ---- macOS: IOKit (system framework) - IOHIDManager for the pen reports, the USB device interface for strings.
        // The first run asks for "Input Monitoring" permission (System Settings > Privacy & Security) for the terminal.
        const string IOKIT = "/System/Library/Frameworks/IOKit.framework/IOKit";
        const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(CF)] static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint enc);
        [DllImport(CF)] static extern IntPtr CFNumberCreate(IntPtr alloc, int type, ref int value);
        [DllImport(CF)] static extern bool CFNumberGetValue(IntPtr num, int type, out int value);
        [DllImport(CF)] static extern IntPtr CFDictionaryCreateMutable(IntPtr alloc, IntPtr cap, IntPtr keyCb, IntPtr valCb);
        [DllImport(CF)] static extern void CFDictionarySetValue(IntPtr d, IntPtr k, IntPtr v);
        [DllImport(CF)] static extern void CFRelease(IntPtr o);
        [DllImport(CF)] static extern IntPtr CFSetGetCount(IntPtr set);
        [DllImport(CF)] static extern IntPtr CFRunLoopGetCurrent();
        [DllImport(CF)] static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, bool returnAfterSource);
        [DllImport(CF)] static extern IntPtr CFUUIDGetConstantUUIDWithBytes(IntPtr alloc, byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7, byte b8, byte b9, byte b10, byte b11, byte b12, byte b13, byte b14, byte b15);
        [DllImport(CF)] static extern CFUUIDBytes CFUUIDGetUUIDBytes(IntPtr uuid);
        [DllImport("/usr/lib/libSystem.dylib")] static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] static extern IntPtr dlsym(IntPtr h, string name);
        [DllImport(IOKIT)] static extern IntPtr IOHIDManagerCreate(IntPtr alloc, int options);
        [DllImport(IOKIT)] static extern void IOHIDManagerSetDeviceMatching(IntPtr mgr, IntPtr dict);
        [DllImport(IOKIT)] static extern int IOHIDManagerOpen(IntPtr mgr, int options);
        [DllImport(IOKIT)] static extern int IOHIDManagerClose(IntPtr mgr, int options);
        [DllImport(IOKIT)] static extern void IOHIDManagerRegisterInputReportCallback(IntPtr mgr, MacReportCb cb, IntPtr ctx);
        [DllImport(IOKIT)] static extern void IOHIDManagerScheduleWithRunLoop(IntPtr mgr, IntPtr rl, IntPtr mode);
        [DllImport(IOKIT)] static extern void IOHIDManagerUnscheduleFromRunLoop(IntPtr mgr, IntPtr rl, IntPtr mode);
        [DllImport(IOKIT)] static extern IntPtr IOHIDManagerCopyDevices(IntPtr mgr);
        [DllImport(IOKIT)] static extern IntPtr IOServiceMatching(string name);
        [DllImport(IOKIT)] static extern int IOServiceGetMatchingServices(uint port, IntPtr matching, out uint iter);
        [DllImport(IOKIT)] static extern uint IOIteratorNext(uint iter);
        [DllImport(IOKIT)] static extern int IOObjectRelease(uint obj);
        [DllImport(IOKIT)] static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr alloc, uint options);
        [DllImport(IOKIT)] static extern int IOCreatePlugInInterfaceForService(uint service, IntPtr pluginType, IntPtr interfaceType, out IntPtr plugin, out int score);
        [StructLayout(LayoutKind.Sequential)] public struct CFUUIDBytes { public ulong lo, hi; }
        public delegate void MacReportCb(IntPtr ctx, int result, IntPtr sender, int type, uint reportId, IntPtr report, IntPtr len);
        delegate int QueryInterfaceFn(IntPtr self, CFUUIDBytes iid, out IntPtr ppv);
        delegate uint ReleaseFn(IntPtr self);
        delegate int DeviceRequestFn(IntPtr self, IntPtr req);
        // Each loop owns its manager and callback. Reopening cannot replace a callback
        // that an older native run loop still holds.
        class MacSession
        {
            public IntPtr manager;
            public int generation;
            public volatile bool running = true;
            public Thread thread;
            public MacReportCb callback;
            public void Report(IntPtr ctx, int result, IntPtr sender, int type, uint reportId, IntPtr report, IntPtr len)
            {
                if (!running || !HidActive(generation)) return;
                if (result != 0) { HidReadFailed(generation, "macOS input error 0x" + result.ToString("x")); return; }
                int n = (int)len.ToInt64();
                if (n <= 0 || n > 64) return;
                byte[] b = new byte[n + 1];
                Marshal.Copy(report, b, 0, n);
                if (reportId != 0 && b[0] != (byte)reportId) { Array.Copy(b, 0, b, 1, n); b[0] = (byte)reportId; n++; }
                HidStore(b, n, generation);
            }
            public void Run()
            {
                IntPtr loop = IntPtr.Zero, mode = IntPtr.Zero;
                bool scheduled = false;
                try
                {
                    IntPtr modeSym = CFSym("kCFRunLoopDefaultMode");
                    if (modeSym == IntPtr.Zero) throw new IOException("the default macOS run-loop mode was not found");
                    mode = Marshal.ReadIntPtr(modeSym); loop = CFRunLoopGetCurrent();
                    IOHIDManagerRegisterInputReportCallback(manager, callback, IntPtr.Zero);
                    IOHIDManagerScheduleWithRunLoop(manager, loop, mode); scheduled = true;
                    while (running && HidActive(generation)) CFRunLoopRunInMode(mode, 0.1, false);
                }
                catch (Exception e) { HidReadFailed(generation, "macOS input loop stopped: " + e.Message); }
                finally
                {
                    if (scheduled) IOHIDManagerUnscheduleFromRunLoop(manager, loop, mode);
                    IOHIDManagerRegisterInputReportCallback(manager, null, IntPtr.Zero);
                    IOHIDManagerClose(manager, 0); CFRelease(manager);
                    callback = null;
                }
            }
        }
        static MacSession macSession = null;
        static IntPtr cfLibrary = IntPtr.Zero;
        static IntPtr CFStr(string s) { return CFStringCreateWithCString(IntPtr.Zero, s, 0x08000100); }
        static IntPtr CFSym(string name)
        {
            lock (hidRec) { if (cfLibrary == IntPtr.Zero) cfLibrary = dlopen(CF, 1); }
            return cfLibrary == IntPtr.Zero ? IntPtr.Zero : dlsym(cfLibrary, name);
        }
        static IntPtr CFMatch(int vid, int pid)
        {
            IntPtr d = CFDictionaryCreateMutable(IntPtr.Zero, IntPtr.Zero, CFSym("kCFTypeDictionaryKeyCallBacks"), CFSym("kCFTypeDictionaryValueCallBacks"));
            int v = vid, p = pid;
            IntPtr vk = CFStr("VendorID"), pk = CFStr("ProductID");
            IntPtr vn = CFNumberCreate(IntPtr.Zero, 9, ref v), pn = CFNumberCreate(IntPtr.Zero, 9, ref p);
            try { CFDictionarySetValue(d, vk, vn); CFDictionarySetValue(d, pk, pn); }
            finally { CFRelease(vk); CFRelease(pk); CFRelease(vn); CFRelease(pn); }
            return d;
        }
        static string HidOpenMac()
        {
            IntPtr manager = IntPtr.Zero;
            try
            {
                manager = IOHIDManagerCreate(IntPtr.Zero, 0);
                if (manager == IntPtr.Zero) return "macOS could not create a tablet reader";
                IntPtr matching = CFMatch(0x28BD, 0x2904);
                try { IOHIDManagerSetDeviceMatching(manager, matching); } finally { CFRelease(matching); }
                int r = IOHIDManagerOpen(manager, 0);
                if (r != 0) return "macOS refused to open the tablet (IOReturn 0x" + r.ToString("x") + "): allow your terminal under System Settings > Privacy & Security > Input Monitoring, then run again";
                IntPtr set = IOHIDManagerCopyDevices(manager);
                bool found = false;
                if (set != IntPtr.Zero) { found = CFSetGetCount(set).ToInt64() > 0; CFRelease(set); }
                if (!found) return "no Deco 640 found (VID 28BD, PID 2904): plug it in, in normal mode (not DFU)";
                hidOpen = 1; hidOther = 0; hidOtherId = -1; hidRun = true; hidN = 0; hidT0 = NowSec();
                MacSession session = new MacSession(); session.manager = manager; session.generation = hidGeneration;
                session.callback = new MacReportCb(session.Report);
                session.thread = new Thread(new ThreadStart(session.Run)); session.thread.IsBackground = true;
                macSession = session; session.thread.Start(); manager = IntPtr.Zero; // loop owns the retained manager
                return "";
            }
            catch (Exception e) { hidRun = false; hidOpen = 0; macSession = null; return "macOS HID error: " + e.Message; }
            finally { if (manager != IntPtr.Zero) { IOHIDManagerClose(manager, 0); CFRelease(manager); } }
        }
        static IntPtr Uuid(string hex)
        {
            byte[] u = new byte[16];
            for (int i = 0; i < 16; i++) u[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero, u[0], u[1], u[2], u[3], u[4], u[5], u[6], u[7], u[8], u[9], u[10], u[11], u[12], u[13], u[14], u[15]);
        }
        static T Vt<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);         // obj points to the interface pointer: *obj = the table {_reserved, QueryInterface, AddRef, Release, ...}
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, slot * IntPtr.Size), typeof(T)) as T;
        }
        static string MacUsbString(int vid, int pid, int index)
        {
            try
            {
                IntPtr userClient = Uuid("9DC7B7809EC011D4A54F000A27052861");    // kIOUSBDeviceUserClientTypeID
                IntPtr plugIf = Uuid("C244E858109C11D491D40050E4C6426F");        // kIOCFPlugInInterfaceID
                IntPtr devIf = Uuid("5C8187D09EF311D48B45000A27052861");         // kIOUSBDeviceInterfaceID
                string[] classes = new string[] { "IOUSBHostDevice", "IOUSBDevice" };
                for (int c = 0; c < classes.Length; c++)
                {
                    uint it;
                    if (IOServiceGetMatchingServices(0, IOServiceMatching(classes[c]), out it) != 0) continue;
                    try
                    {
                    uint svc;
                    while ((svc = IOIteratorNext(it)) != 0)
                    {
                        try
                        {
                            if (RegInt(svc, "idVendor") != vid || RegInt(svc, "idProduct") != pid) continue;
                            IntPtr plugin; int score;
                            if (IOCreatePlugInInterfaceForService(svc, userClient, plugIf, out plugin, out score) != 0 || plugin == IntPtr.Zero) continue;
                            IntPtr dev;
                            int qr = Vt<QueryInterfaceFn>(plugin, 1)(plugin, CFUUIDGetUUIDBytes(devIf), out dev);
                            Vt<ReleaseFn>(plugin, 3)(plugin);
                            if (qr != 0 || dev == IntPtr.Zero) continue;
                            // IOUSBDevRequest { u8 bmRequestType, bRequest; u16 wValue, wIndex, wLength; void *pData; u32 wLenDone; }
                            int ps = IntPtr.Size;
                            IntPtr req = Marshal.AllocHGlobal(8 + 2 * ps), data = Marshal.AllocHGlobal(256);
                            try
                            {
                                for (int k = 0; k < 8 + 2 * ps; k++) Marshal.WriteByte(req, k, 0);
                                Marshal.WriteByte(req, 0, 0x80); Marshal.WriteByte(req, 1, 6);
                                Marshal.WriteInt16(req, 2, (short)(0x0300 | index)); Marshal.WriteInt16(req, 4, 0x0409); Marshal.WriteInt16(req, 6, 255);
                                Marshal.WriteIntPtr(req, 8, data);
                                int rr = Vt<DeviceRequestFn>(dev, 26)(dev, req);       // DeviceRequest (default pipe; standard request)
                                int done = Marshal.ReadInt32(req, 8 + ps);
                                Vt<ReleaseFn>(dev, 3)(dev);
                                if (rr != 0 || done < 4) continue;
                                byte[] b = new byte[done]; Marshal.Copy(data, b, 0, done);
                                if (b[1] != 3) continue;
                                int len = b[0] < done ? b[0] : done;
                                return Encoding.Unicode.GetString(b, 2, (len - 2) & ~1);
                            }
                            finally { Marshal.FreeHGlobal(req); Marshal.FreeHGlobal(data); }
                        }
                        finally { IOObjectRelease(svc); }
                    }
                    }
                    finally { IOObjectRelease(it); }
                }
            }
            catch (Exception) { }
            return null;
        }
        static int RegInt(uint svc, string key)
        {
            IntPtr name = CFStr(key), v;
            try { v = IORegistryEntryCreateCFProperty(svc, name, IntPtr.Zero, 0); }
            finally { CFRelease(name); }
            if (v == IntPtr.Zero) return -1;
            int x; bool ok = CFNumberGetValue(v, 9, out x); CFRelease(v);
            return ok ? x : -1;
        }

        // one input report from any platform's reader: pen reports (ID 7, >= 10 bytes) are kept with their time
        static bool HidActive(int generation) { return hidRun && generation == hidGeneration; }
        static void HidReadFailed(int generation, string message)
        {
            lock (hidRec)
            {
                if (HidActive(generation)) hidFailure = message;
            }
        }
        static void HidStore(byte[] b, int n, int generation)
        {
            lock (hidRec)
            {
                if (!HidActive(generation) || n <= 0) return;
                // Transport diagnostics include ordinary ID 2 and other reports, even though
                // only ID 7 can carry the measurement firmware's telemetry.
                if (hidReportCounts[b[0]] < int.MaxValue) hidReportCounts[b[0]]++;
                if (n < 10 || b[0] != 7) { hidOther++; hidOtherId = b[0]; return; }
                if (hidN * 11 + 11 > hidRec.Length) return;
                for (int k = 0; k < 10; k++) hidRec[hidN * 11 + k] = b[k];
                hidRec[hidN * 11 + 10] = (int)((NowSec() - hidT0) * 1e6);
                hidN++;
            }
        }
        static void HidLoop(FileStream st, int len, int generation)
        {
            byte[] b = new byte[len];
            try
            {
                while (HidActive(generation))
                {
                    int n = st.Read(b, 0, b.Length);
                    if (n <= 0) { HidReadFailed(generation, "the tablet input stream closed; reconnect the tablet and reopen calibration"); break; }
                    HidStore(b, n, generation);
                }
            }
            catch (Exception e) { HidReadFailed(generation, "a tablet input stream stopped: " + e.Message + "; reconnect the tablet and reopen calibration"); }
            finally { try { st.Close(); } catch (Exception e) { HidReadFailed(generation, "tablet input close failed: " + e.Message); } }
        }
        static void HidLoopLinux(int fd, int generation)
        {
            byte[] bytes = new byte[64];
            try
            {
                while (HidActive(generation))
                {
                    long count = lx_read(fd, bytes, new UIntPtr((uint)bytes.Length)).ToInt64();
                    if (count > 0) { HidStore(bytes, (int)count, generation); continue; }
                    if (count == 0) { HidReadFailed(generation, "the tablet input stream closed; reconnect the tablet and reopen calibration"); break; }
                    int error = Marshal.GetLastWin32Error();
                    if (error != 4 && error != 11) { HidReadFailed(generation, "tablet read failed (error " + error.ToString() + "); reconnect the tablet and reopen calibration"); break; }
                    Thread.Sleep(5); // EINTR/EAGAIN: no report is ready; remain interruptible
                }
            }
            catch (Exception e) { HidReadFailed(generation, "tablet read failed: " + e.Message); }
            finally { lx_close(fd); }
        }
        // raw pen reports collected over SECONDS: 10 report bytes + time in us, per report
        public static int[] HidCollect(double seconds)
        {
            lock (hidRec) { hidN = 0; }
            Thread.Sleep((int)(seconds * 1000));
            lock (hidRec) { int[] o = new int[hidN * 11]; Array.Copy(hidRec, o, hidN * 11); return o; }
        }
        public static int[] HidReportCounts()
        {
            lock (hidRec) { int[] counts = new int[256]; Array.Copy(hidReportCounts, counts, 256); return counts; }
        }
        public static void HidClose()
        {
            Stopwatch stopping = Stopwatch.StartNew();
            lock (hidRec) { hidRun = false; hidGeneration++; }
            hidClosingFailed = false;
            MacSession session = macSession;
            if (session != null) session.running = false;
            WinRawSession raw = winRaw;
            if (raw != null) raw.running = false;      // its thread unregisters Raw Input and destroys its window
            for (int i = 0; i < hidStreams.Length; i++)
            {
                FileStream stream = hidStreams[i];
                if (stream == null) continue;
                try { if (IsWindows()) CancelIoEx(stream.SafeFileHandle, IntPtr.Zero); } catch (Exception) { }
                // The reader owns Close(). A driver that ignores cancellation must
                // not block the UI in FileStream.Close while Read is still pending.
            }
            for (int i = 0; i < hidThreads.Length; i++)
            {
                Thread thread = hidThreads[i];
                if (!HidJoin(thread, stopping))
                    hidClosingFailed = true;
                else { hidThreads[i] = null; hidStreams[i] = null; }
            }
            if (session != null && !HidJoin(session.thread, stopping))
                hidClosingFailed = true;
            else macSession = null;
            if (raw != null && !HidJoin(raw.thread, stopping))
                hidClosingFailed = true;
            else winRaw = null;
            if (hidClosingFailed) lock (hidRec) { hidFailure = "a tablet reader did not stop within one second; reconnect the tablet or restart this tool"; }
            hidOpen = 0;
        }
        static bool HidJoin(Thread thread, Stopwatch stopping)
        {
            if (thread == null || thread == Thread.CurrentThread || !thread.IsAlive) return true;
            int left = (int)Math.Max(0, 1000 - stopping.ElapsedMilliseconds);
            return thread.Join(left);
        }
        // why nothing arrives, for the LOST readout: "" if pen reports arrive, else what was seen instead
        public static string HidWhy()
        {
            lock (hidRec)
            {
                if (hidN > 0) return "";
                if (hidFailure.Length > 0) return hidFailure;
                if (hidOther > 0) return "reports arrive with ID " + hidOtherId.ToString() + "; calibration requires report ID 7";
                return hidSummary.Length > 0 ? "no reports at all (" + hidSummary + ")" : "no reports at all from " + hidOpen.ToString() + " open collection(s)";
            }
        }
        public static byte[] ReadAllBytes(string path) { return File.ReadAllBytes(path); }
        public static double NowSec() { return (double)Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
        public static void Sleep(double sec) { int ms = (int)(sec * 1000.0); if (ms > 0) Thread.Sleep(ms); }
        public static double Exp(double x) { return Math.Exp(x); }
        public static double Log(double x) { return Math.Log(x); }
        public static double Sqrt(double x) { return Math.Sqrt(x); }
        public static double Cos(double x) { return Math.Cos(x); }
        public static double Sin(double x) { return Math.Sin(x); }
        public static double Rint(double x) { return Math.Round(x, MidpointRounding.ToEven); }
        public static bool IsNaN(double x) { return double.IsNaN(x); }
        public static int FloatBits(double d) { return BitConverter.ToInt32(BitConverter.GetBytes((float)d), 0); }
        public static double BitsFloat(int b) { return (double)BitConverter.ToSingle(BitConverter.GetBytes(b), 0); }
        public static double ParseDouble(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        public static string Fixed3(double x) { return x.ToString("F3", CultureInfo.InvariantCulture); }
        public static int Len(string s) { return s.Length; }
        public static string Lower(string s) { return s.ToLowerInvariant(); }
        public static int CharAt(string s, int i) { return s[i]; }
        public static string Sub(string s, int a, int n) { return s.Substring(a, n); }
        public static string Trim(string s) { return s.Trim(); }
        public static bool StartsWith(string s, string p) { return s.StartsWith(p, StringComparison.Ordinal); }
        public static int IndexOf(string s, string p) { return s.IndexOf(p, StringComparison.Ordinal); }
        public static string[] Split(string s, char c) { return s.Split(c); }
        public static string[] Lines(string s) { return s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }
        public static string Ascii(byte[] b) { return Encoding.ASCII.GetString(b); }
        public static string Utf8(byte[] b) { return Encoding.UTF8.GetString(b); }
        public static byte[] Utf8Bytes(string text) { return Encoding.UTF8.GetBytes(text); }
        public static bool EndsWithHex(string p) { return p.ToLowerInvariant().EndsWith(".hex", StringComparison.Ordinal); }
        public static string Msg(Exception e) { return e.Message; }
        public static bool Eq(string a, string b) { return string.Equals(a, b); }
        public static void Copy(Array a, Array b, int n) { Array.Copy(a, b, n); }
        public static void Copy(Array a, int ai, Array b, int bi, int n) { Array.Copy(a, ai, b, bi, n); }

        static P() { ClearInputBom(); }

        static void ClearInputBom()
        {
            try
            {
                if (!IsWindows()) return;
                Encoding enc = Console.InputEncoding;
                if (enc.CodePage == 65001 && enc.GetPreamble().Length > 0)
                {
                    // Setting Console.InputEncoding resets _in = null in .NET Framework;
                    // preserve Console.In so redirected/buffered readers are not dropped.
                    TextReader current = Console.In;
                    Console.InputEncoding = new UTF8Encoding(false);
                    Console.SetIn(current);
                }
            }
            catch (Exception) { } // e.g., no attached console handle
        }
    }

    public class Fault : Exception { public string msg; public Fault(string m) : base(m) { msg = m; } }
    public class Stop : Exception { public string msg; public Stop(string m) : base(m) { msg = m; } }
//@@PLATFORM-END

    // ------------------------------------------------------------------------------------ helpers
    public static class F
    {
        public const long M32 = 0xFFFFFFFFL;
        public static long U(int v) { return v & 0xFFFFFFFFL; }
        public static int Lsr(int x, int n) { return (int)((x & 0xFFFFFFFFL) >> n); }
        public static int Sx(int v, int bits) { return (v & (1 << (bits - 1))) != 0 ? v - (1 << bits) : v; }

        public static string Str(long v)
        {
            if (v == 0) return "0";
            bool neg = v < 0;
            StringBuilder sb = new StringBuilder();
            long a = neg ? -v : v;
            while (a > 0) { sb.Insert(0, (char)('0' + (int)(a % 10))); a = a / 10; }
            if (neg) sb.Insert(0, '-');
            return sb.ToString();
        }

        public static string HexDigits(long v, int n, bool upper)
        {
            string digs = upper ? "0123456789ABCDEF" : "0123456789abcdef";
            StringBuilder sb = new StringBuilder();
            long a = v;
            for (int i = 0; i < n || a != 0; i++) { sb.Insert(0, (char)P.CharAt(digs, (int)(a & 15))); a = (long)((a >> 4) & 0x0FFFFFFFFFFFFFFFL); if (i > 20) break; }
            return sb.ToString();
        }

        // 0x-prefixed lower-case hex of the low 4*N bits (same text as hx() in deco640_sim.py)
        public static string Hx(long v, int n)
        {
            long mask = n >= 16 ? -1L : ((1L << (4 * n)) - 1);
            return "0x" + HexDigits(v & mask, n, false);
        }

        // fixed-point text with K decimals, round-half-even on X * 10^K (same as fx() in deco640_sim.py)
        public static string Fx(double x, int k)
        {
            double scale = 1.0;
            for (int i = 0; i < k; i++) scale = scale * 10.0;
            long q = (long)P.Rint(x * scale);
            string sign = q < 0 ? "-" : "";
            if (q < 0) q = -q;
            string s = Str(q);
            if (k == 0) return sign + s;
            while (P.Len(s) < k + 1) s = "0" + s;
            return sign + P.Sub(s, 0, P.Len(s) - k) + "." + P.Sub(s, P.Len(s) - k, k);
        }

        // %g-style text (up to 6 significant digits, no trailing zeros)
        public static string Gfmt(double x)
        {
            if (x == (double)(long)x && x < 1e15 && x > -1e15) return Str((long)x);
            string s = Fx(x, 6);
            while (P.EndsWith(s, "0")) s = P.Sub(s, 0, P.Len(s) - 1);
            if (P.EndsWith(s, ".")) s = P.Sub(s, 0, P.Len(s) - 1);
            return s;
        }

        public static long ParseInt(string s)
        {
            string t = P.Lower(P.Trim(s));
            long v = 0;
            if (P.StartsWith(t, "0x"))
            {
                for (int i = 2; i < P.Len(t); i++)
                {
                    int ch = P.CharAt(t, i);
                    int d = ch >= 'a' ? ch - 'a' + 10 : ch - '0';
                    if (d < 0 || d > 15) throw new Stop("bad number " + s);
                    v = v * 16 + d;
                }
                return v;
            }
            return (long)P.ParseDouble(t);
        }
    }

    // Python's random.Random (MT19937, seeding by init_by_array, random(), gauss()) - bit-exact.
    public class Rng
    {
        public long[] mt = new long[624];
        public int mti = 625;
        public bool hasNext = false;
        public double gaussNext = 0.0;

        public Rng(long seed)
        {
            long key = seed < 0 ? -seed : seed;
            InitGenrand(19650218L);
            int i = 1;
            for (int k = 0; k < 624; k++)
            {
                long prev = mt[i - 1];
                mt[i] = ((mt[i] ^ (((prev ^ (prev >> 30)) * 1664525L) & F.M32)) + key + 0) & F.M32;
                i++;
                if (i >= 624) { mt[0] = mt[623]; i = 1; }
            }
            for (int k2 = 0; k2 < 623; k2++)
            {
                long prev2 = mt[i - 1];
                mt[i] = ((mt[i] ^ (((prev2 ^ (prev2 >> 30)) * 1566083941L) & F.M32)) - i) & F.M32;
                i++;
                if (i >= 624) { mt[0] = mt[623]; i = 1; }
            }
            mt[0] = 0x80000000L;
            mti = 624;
        }

        public void InitGenrand(long s)
        {
            mt[0] = s & F.M32;
            for (int j = 1; j < 624; j++)
            {
                long p = mt[j - 1];
                mt[j] = ((1812433253L * (p ^ (p >> 30))) + j) & F.M32;
            }
            mti = 624;
        }

        public long NextU32()
        {
            if (mti >= 624)
            {
                for (int kk = 0; kk < 624; kk++)
                {
                    long y = (mt[kk] & 0x80000000L) | (mt[(kk + 1) % 624] & 0x7FFFFFFFL);
                    mt[kk] = mt[(kk + 397) % 624] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908B0DFL : 0L);
                }
                mti = 0;
            }
            long z = mt[mti];
            mti++;
            z ^= (z >> 11);
            z ^= (z << 7) & 0x9D2C5680L;
            z ^= (z << 15) & 0xEFC60000L;
            z ^= (z >> 18);
            return z & F.M32;
        }

        public double Random()
        {
            long a = NextU32() >> 5;
            long b = NextU32() >> 6;
            return ((double)a * 67108864.0 + (double)b) * (1.0 / 9007199254740992.0);
        }

        public double Gauss(double mu, double sigma)
        {
            double z;
            if (hasNext)
            {
                z = gaussNext;
                hasNext = false;
            }
            else
            {
                double x2pi = Random() * (2.0 * Math.PI);
                double g2rad = P.Sqrt(-2.0 * P.Log(1.0 - Random()));
                z = P.Cos(x2pi) * g2rad;
                gaussNext = P.Sin(x2pi) * g2rad;
                hasNext = true;
            }
            return mu + z * sigma;
        }
    }

    // int -> int hash map with "absent" (peripheral registers)
    public class IntMap
    {
        public int[] keys = new int[4096];
        public int[] vals = new int[4096];
        public bool[] used = new bool[4096];

        public int Slot(int k)
        {
            int h = (int)(((k & 0xFFFFFFFFL) * 2654435761L) >> 20) & 4095;
            while (used[h] && keys[h] != k) h = (h + 1) & 4095;
            return h;
        }
        public int Get(int k, int dflt) { int h = Slot(k); return used[h] ? vals[h] : dflt; }
        public void Set(int k, int v) { int h = Slot(k); keys[h] = k; vals[h] = v; used[h] = true; }
    }

    public class Bytes
    {
        public byte[] a = new byte[64];
        public int n = 0;
        public void Add(int b) { if (n == a.Length) { byte[] g = new byte[n * 2]; P.Copy(a, g, n); a = g; } a[n] = (byte)b; n++; }
        public void Clear() { n = 0; }
        public byte[] Take(int count) { int m = count < n ? count : n; byte[] o = new byte[m]; P.Copy(a, o, m); return o; }
    }

    public class Report
    {
        public long cycle; public int ep; public byte[] data;
        public Report(long c, int e, byte[] d) { cycle = c; ep = e; data = d; }
    }

    public class ReportList
    {
        public Report[] a = new Report[256];
        public int n = 0;
        public void Add(Report r) { if (n == a.Length) { Report[] g = new Report[n * 2]; P.Copy(a, g, n); a = g; } a[n] = r; n++; }
    }

    public class LongList
    {
        public long[] a = new long[1024];
        public int n = 0;
        public void Add(long v) { if (n == a.Length) { long[] g = new long[n * 2]; P.Copy(a, g, n); a = g; } a[n] = v; n++; }
    }

    // ======================================================================================== CPU
    public class Cpu
    {
        public Machine mc;
        public int[] r = new int[16];
        public int N, Z, C, V, it, primask, msp, excDepth;
        public long cycles, icount;
        public int[] S = new int[32];
        public int cur, pcVal, nextPc;
        public int acC, acV, shC;

        public Cpu(Machine m) { mc = m; }

        public int Rd(int a, int n) { return mc.Read(a, n); }
        public void Wr(int a, int n, int v) { mc.Write(a, n, v); }

        public bool Cond(int c)
        {
            if (c == 0) return Z == 1;
            if (c == 1) return Z == 0;
            if (c == 2) return C == 1;
            if (c == 3) return C == 0;
            if (c == 4) return N == 1;
            if (c == 5) return N == 0;
            if (c == 6) return V == 1;
            if (c == 7) return V == 0;
            if (c == 8) return C == 1 && Z == 0;
            if (c == 9) return C == 0 || Z == 1;
            if (c == 10) return N == V;
            if (c == 11) return N != V;
            if (c == 12) return Z == 0 && N == V;
            if (c == 13) return Z == 1 || N != V;
            return true;
        }

        public void SetNZ(int v) { N = (v >> 31) & 1; Z = v == 0 ? 1 : 0; }
        public int Reg(int n) { return n == 15 ? pcVal : r[n]; }

        public int AddC(int x, int y, int c)
        {
            long us = (x & 0xFFFFFFFFL) + (y & 0xFFFFFFFFL) + c;
            long ss = (long)x + (long)y + c;
            int res = (int)us;
            acC = (us >> 32) != 0 ? 1 : 0;
            acV = ((long)res != ss) ? 1 : 0;
            return res;
        }

        // shift types: 0 LSL, 1 LSR, 2 ASR, 3 ROR, 4 RRX; carry out in shC
        public int ShiftC(int v, int t, int n, int c)
        {
            if (t == 0)
            {
                if (n == 0) { shC = c; return v; }
                if (n > 32) { shC = 0; return 0; }
                shC = (int)(((v & 0xFFFFFFFFL) >> (32 - n)) & 1);
                return n == 32 ? 0 : v << n;
            }
            if (t == 1)
            {
                if (n == 0) { shC = c; return v; }
                if (n > 32) { shC = 0; return 0; }
                shC = (int)(((v & 0xFFFFFFFFL) >> (n - 1)) & 1);
                return n == 32 ? 0 : F.Lsr(v, n);
            }
            if (t == 2)
            {
                if (n == 0) { shC = c; return v; }
                if (n >= 32) { int rr = v < 0 ? -1 : 0; shC = rr & 1; return rr; }
                shC = (int)(((v & 0xFFFFFFFFL) >> (n - 1)) & 1);
                return v >> n;
            }
            if (t == 3)
            {
                if (n == 0) { shC = c; return v; }
                int m = n & 31;
                if (m == 0) { shC = (v >> 31) & 1; return v; }
                int rot = F.Lsr(v, m) | (v << (32 - m));
                shC = (rot >> 31) & 1;
                return rot;
            }
            shC = v & 1;
            return (c << 31) | F.Lsr(v, 1);
        }

        public int dsT, dsN;
        public void DecodeImmShift(int t, int imm5)
        {
            if (t == 0) { dsT = 0; dsN = imm5; return; }
            if (t == 1) { dsT = 1; dsN = imm5 == 0 ? 32 : imm5; return; }
            if (t == 2) { dsT = 2; dsN = imm5 == 0 ? 32 : imm5; return; }
            if (imm5 == 0) { dsT = 4; dsN = 1; return; }
            dsT = 3; dsN = imm5;
        }

        public int ThumbExpandC(int imm12, int c)
        {
            if ((imm12 >> 10) == 0)
            {
                int imm8 = imm12 & 0xFF;
                int t = (imm12 >> 8) & 3;
                shC = c;
                if (t == 0) return imm8;
                if (t == 1) return (imm8 << 16) | imm8;
                if (t == 2) return (imm8 << 24) | (imm8 << 8);
                return imm8 * 0x01010101;
            }
            int unrot = 0x80 | (imm12 & 0x7F);
            int rot = (imm12 >> 7) & 0x1F;
            int v = F.Lsr(unrot, rot) | (unrot << (32 - rot));
            shC = (v >> 31) & 1;
            return v;
        }

        public void Bx(int v)
        {
            if ((v & unchecked((int)0xFFFFFFF0)) == unchecked((int)0xFFFFFFF0)) { ExcReturn(v); return; }
            if ((v & 1) == 0) throw new Fault("BX to ARM state " + F.Hx(v, 8) + " at " + F.Hx(cur, 8));
            nextPc = v & ~1;
        }

        public void TakeException(int num)
        {
            int xpsr = (N << 31) | (Z << 30) | (C << 29) | (V << 28) | (1 << 24);
            int sp = r[13] - 32;
            if ((sp & 4) != 0) { sp -= 4; xpsr |= 1 << 9; }
            int[] frame = new int[] { r[0], r[1], r[2], r[3], r[12], r[14], r[15], xpsr };
            for (int k = 0; k < 8; k++) Wr(sp + 4 * k, 4, frame[k]);
            r[13] = sp;
            r[14] = unchecked((int)0xFFFFFFF9);
            excDepth++;
            r[15] = Rd(mc.vtor + 4 * num, 4) & ~1;
            it = 0;
        }

        public void ExcReturn(int v)
        {
            int sp = r[13];
            int[] fr = new int[8];
            for (int k = 0; k < 8; k++) fr[k] = Rd(sp + 4 * k, 4);
            r[0] = fr[0]; r[1] = fr[1]; r[2] = fr[2]; r[3] = fr[3]; r[12] = fr[4]; r[14] = fr[5];
            int xpsr = fr[7];
            N = (xpsr >> 31) & 1; Z = (xpsr >> 30) & 1; C = (xpsr >> 29) & 1; V = (xpsr >> 28) & 1;
            r[13] = sp + 32 + ((xpsr & (1 << 9)) != 0 ? 4 : 0);
            nextPc = fr[6];
            excDepth--;
        }

        public void Step()
        {
            if (mc.resetPending) mc.SystemReset();
            int pc = r[15];
            cur = pc;
            int o = pc - 0x08000000;
            if (o >= 0 && o < 0x10000)
            {
                int hk = mc.hookAt[o >> 1];
                if (hk != 0 && (o & 1) == 0 && mc.Hook(hk - 1)) return;
            }
            if (!(o >= 0 && o < 0x10000 - 3)) throw new Fault("instruction fetch outside flash @ " + F.Hx(pc, 8));
            byte[] fl = mc.flash;
            int h1 = (fl[o] & 0xFF) | ((fl[o + 1] & 0xFF) << 8);
            int top = h1 >> 11;
            bool is32 = top == 0x1D || top == 0x1E || top == 0x1F;
            int h2 = is32 ? ((fl[o + 2] & 0xFF) | ((fl[o + 3] & 0xFF) << 8)) : 0;
            int size = is32 ? 4 : 2;
            pcVal = pc + 4;
            nextPc = pc + size;
            bool itActive = (it & 0xF) != 0;
            bool execute = itActive ? Cond((it >> 4) & 0xF) : true;
            if (execute)
            {
                if (is32) Exec32(h1, h2); else Exec16(h1);
            }
            if (itActive && !(!is32 && (h1 & 0xFF00) == 0xBF00 && (h1 & 0xF) != 0))
            {
                if ((it & 7) == 0) it = 0; else it = (it & 0xE0) | ((it << 1) & 0x1F);
            }
            r[15] = nextPc;
            cycles += nextPc == pc + size ? 1 : 3;
            icount++;
        }

        // run without interrupts until PC == until (or any of untilSet) or maxSteps instructions
        public void Run(long maxSteps, int[] untilSet)
        {
            for (long s = 0; s < maxSteps; s++)
            {
                int pc = r[15];
                for (int k = 0; k < untilSet.Length; k++) if (untilSet[k] == pc) return;
                Step();
            }
            throw new Stop("step limit at pc=" + F.Hx(r[15], 8));
        }

        public static int PopCount(int v)
        {
            int c = 0;
            for (int k = 0; k < 32; k++) if (((v >> k) & 1) != 0) c++;
            return c;
        }

        // ------------------------------------------------------------------ 16-bit
        public void Exec16(int h)
        {
            bool S = (it & 0xF) == 0;
            int v, a, b, t, rd, rn, rt, rr, imm, o, addr, k, sp, rg, cnt;
            if ((h >> 14) == 0)
            {
                int opc = (h >> 9) & 0x1F;
                rd = h & 7;
                rn = (h >> 3) & 7;
                if (opc < 12)
                {
                    DecodeImmShift(opc >> 2, (h >> 6) & 0x1F);
                    v = ShiftC(r[rn], dsT, dsN, C);
                    r[rd] = v;
                    if (S) { SetNZ(v); C = shC; }
                    return;
                }
                if (opc <= 15)
                {
                    int op2 = opc < 14 ? r[(h >> 6) & 7] : (h >> 6) & 7;
                    if (opc == 12 || opc == 14) v = AddC(r[rn], op2, 0); else v = AddC(r[rn], ~op2, 1);
                    r[rd] = v;
                    if (S) { SetNZ(v); C = acC; V = acV; }
                    return;
                }
                rr = (h >> 8) & 7;
                imm = h & 0xFF;
                o = (opc >> 2) & 3;
                if (o == 0) { r[rr] = imm; if (S) SetNZ(imm); }
                else if (o == 1) { v = AddC(r[rr], ~imm, 1); SetNZ(v); C = acC; V = acV; }
                else if (o == 2) { v = AddC(r[rr], imm, 0); r[rr] = v; if (S) { SetNZ(v); C = acC; V = acV; } }
                else { v = AddC(r[rr], ~imm, 1); r[rr] = v; if (S) { SetNZ(v); C = acC; V = acV; } }
                return;
            }
            if ((h >> 10) == 0x10)
            {
                o = (h >> 6) & 0xF;
                int rdn = h & 7;
                a = r[rdn];
                b = r[(h >> 3) & 7];
                if (o == 0 || o == 1 || o == 12 || o == 13 || o == 14 || o == 15)
                {
                    if (o == 0) v = a & b;
                    else if (o == 1) v = a ^ b;
                    else if (o == 12) v = a | b;
                    else if (o == 13) v = a * b;
                    else if (o == 14) v = a & ~b;
                    else v = ~b;
                    r[rdn] = v;
                    if (S) SetNZ(v);
                }
                else if (o == 2 || o == 3 || o == 4 || o == 7)
                {
                    t = o == 2 ? 0 : o == 3 ? 1 : o == 4 ? 2 : 3;
                    v = ShiftC(a, t, b & 0xFF, C);
                    r[rdn] = v;
                    if (S) { SetNZ(v); C = shC; }
                }
                else if (o == 5 || o == 6 || o == 9)
                {
                    if (o == 5) v = AddC(a, b, C);
                    else if (o == 6) v = AddC(a, ~b, C);
                    else v = AddC(~b, 0, 1);
                    r[rdn] = v;
                    if (S) { SetNZ(v); C = acC; V = acV; }
                }
                else if (o == 8) SetNZ(a & b);
                else if (o == 10) { v = AddC(a, ~b, 1); SetNZ(v); C = acC; V = acV; }
                else { v = AddC(a, b, 0); SetNZ(v); C = acC; V = acV; }
                return;
            }
            if ((h >> 10) == 0x11)
            {
                o = (h >> 8) & 3;
                int rm = (h >> 3) & 0xF;
                int rdn2 = ((h >> 4) & 8) | (h & 7);
                if (o == 0)
                {
                    v = Reg(rdn2) + Reg(rm);
                    if (rdn2 == 15) Bx(v | 1); else r[rdn2] = v;
                }
                else if (o == 1) { v = AddC(Reg(rdn2), ~Reg(rm), 1); SetNZ(v); C = acC; V = acV; }
                else if (o == 2)
                {
                    v = Reg(rm);
                    if (rdn2 == 15) nextPc = v & ~1; else r[rdn2] = v;
                }
                else
                {
                    v = Reg(rm);
                    if ((h & 0x80) != 0) r[14] = (cur + 2) | 1;
                    Bx(v);
                }
                return;
            }
            if ((h >> 11) == 0x09) { r[(h >> 8) & 7] = Rd(((cur + 4) & ~3) + (h & 0xFF) * 4, 4); return; }
            if ((h >> 12) == 0x5)
            {
                o = (h >> 9) & 7;
                rt = h & 7;
                addr = r[(h >> 3) & 7] + r[(h >> 6) & 7];
                if (o == 0) Wr(addr, 4, r[rt]);
                else if (o == 1) Wr(addr, 2, r[rt]);
                else if (o == 2) Wr(addr, 1, r[rt]);
                else if (o == 3) r[rt] = F.Sx(Rd(addr, 1), 8);
                else if (o == 4) r[rt] = Rd(addr, 4);
                else if (o == 5) r[rt] = Rd(addr, 2);
                else if (o == 6) r[rt] = Rd(addr, 1);
                else r[rt] = F.Sx(Rd(addr, 2), 16);
                return;
            }
            if ((h >> 13) == 0x3)
            {
                int nb = ((h >> 12) & 1) != 0 ? 1 : 4;
                rt = h & 7;
                addr = r[(h >> 3) & 7] + ((h >> 6) & 0x1F) * nb;
                if (((h >> 11) & 1) != 0) r[rt] = Rd(addr, nb); else Wr(addr, nb, r[rt]);
                return;
            }
            if ((h >> 12) == 0x8)
            {
                rt = h & 7;
                addr = r[(h >> 3) & 7] + ((h >> 6) & 0x1F) * 2;
                if (((h >> 11) & 1) != 0) r[rt] = Rd(addr, 2); else Wr(addr, 2, r[rt]);
                return;
            }
            if ((h >> 12) == 0x9)
            {
                rt = (h >> 8) & 7;
                addr = r[13] + (h & 0xFF) * 4;
                if (((h >> 11) & 1) != 0) r[rt] = Rd(addr, 4); else Wr(addr, 4, r[rt]);
                return;
            }
            if ((h >> 11) == 0x14) { r[(h >> 8) & 7] = ((cur + 4) & ~3) + (h & 0xFF) * 4; return; }
            if ((h >> 11) == 0x15) { r[(h >> 8) & 7] = r[13] + (h & 0xFF) * 4; return; }
            if ((h >> 12) == 0xB)
            {
                if ((h >> 8) == 0xB0)
                {
                    imm = (h & 0x7F) * 4;
                    r[13] = (h & 0x80) != 0 ? r[13] - imm : r[13] + imm;
                    return;
                }
                if ((h & 0x0500) == 0x0100)
                {
                    int off = ((((h >> 9) & 1) << 5) | ((h >> 3) & 0x1F)) << 1;
                    if ((r[h & 7] != 0) == ((h & 0x800) != 0)) nextPc = cur + 4 + off;
                    return;
                }
                if ((h >> 8) == 0xB2)
                {
                    o = (h >> 6) & 3;
                    v = r[(h >> 3) & 7];
                    if (o == 0) r[h & 7] = F.Sx(v & 0xFFFF, 16);
                    else if (o == 1) r[h & 7] = F.Sx(v & 0xFF, 8);
                    else if (o == 2) r[h & 7] = v & 0xFFFF;
                    else r[h & 7] = v & 0xFF;
                    return;
                }
                if ((h >> 9) == 0x5A)
                {
                    cnt = PopCount(h & 0x1FF);
                    sp = r[13] - 4 * cnt;
                    k = 0;
                    for (rg = 0; rg < 8; rg++)
                    {
                        if ((h & (1 << rg)) != 0) { Wr(sp + 4 * k, 4, r[rg]); k++; }
                    }
                    if ((h & 0x100) != 0) Wr(sp + 4 * k, 4, r[14]);
                    r[13] = sp;
                    return;
                }
                if ((h >> 9) == 0x5E)
                {
                    sp = r[13];
                    for (rg = 0; rg < 8; rg++)
                    {
                        if ((h & (1 << rg)) != 0) { r[rg] = Rd(sp, 4); sp += 4; }
                    }
                    if ((h & 0x100) != 0)
                    {
                        v = Rd(sp, 4);
                        sp += 4;
                        r[13] = sp;
                        Bx(v);
                    }
                    else r[13] = sp;
                    return;
                }
                if ((h & 0xFFE8) == 0xB660)
                {
                    if ((h & 2) != 0) primask = (h & 0x10) != 0 ? 1 : 0;
                    return;
                }
                if ((h >> 8) == 0xBA)
                {
                    o = (h >> 6) & 3;
                    v = r[(h >> 3) & 7];
                    if (o == 0) r[h & 7] = ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | (F.Lsr(v, 8) & 0xFF00) | F.Lsr(v, 24);
                    else if (o == 1) r[h & 7] = (F.Lsr(v & unchecked((int)0xFF00FF00), 8)) | ((v & 0x00FF00FF) << 8);
                    else if (o == 3) r[h & 7] = F.Sx(((v & 0xFF) << 8) | ((v >> 8) & 0xFF), 16);
                    return;
                }
                if ((h >> 8) == 0xBE) throw new Stop("BKPT at " + F.Hx(cur, 8));
                if ((h >> 8) == 0xBF)
                {
                    if ((h & 0xF) != 0) it = h & 0xFF;
                    return;
                }
            }
            if ((h >> 12) == 0xC)
            {
                rn = (h >> 8) & 7;
                a = r[rn];
                k = 0;
                bool load = ((h >> 11) & 1) != 0;
                for (rg = 0; rg < 8; rg++)
                {
                    if ((h & (1 << rg)) != 0)
                    {
                        if (load) r[rg] = Rd(a + 4 * k, 4); else Wr(a + 4 * k, 4, r[rg]);
                        k++;
                    }
                }
                if (!(load && (h & (1 << rn)) != 0)) r[rn] = a + 4 * k;
                return;
            }
            if ((h >> 12) == 0xD)
            {
                int cc = (h >> 8) & 0xF;
                if (cc == 0xE) throw new Fault("UDF at " + F.Hx(cur, 8));
                if (cc == 0xF) throw new Stop("SVC at " + F.Hx(cur, 8));
                if (Cond(cc)) nextPc = cur + 4 + F.Sx(h & 0xFF, 8) * 2;
                return;
            }
            if ((h >> 11) == 0x1C) { nextPc = cur + 4 + F.Sx(h & 0x7FF, 11) * 2; return; }
            throw new Fault("undefined 16-bit " + F.Hx(h, 4) + " at " + F.Hx(cur, 8));
        }

        // ------------------------------------------------------------------ 32-bit
        public void Exec32(int h1, int h2)
        {
            int op1 = (h1 >> 11) & 3;
            int op2 = (h1 >> 4) & 0x7F;
            int rn = h1 & 0xF;
            int v, rd, rt, rm, o, a, k, rg, cnt, b;
            if (op1 == 1)
            {
                if ((op2 & 0x64) == 0)
                {
                    o = (h1 >> 7) & 3;
                    int W = (h1 >> 5) & 1, L = (h1 >> 4) & 1;
                    cnt = PopCount(h2 & 0xFFFF);
                    int bse = Reg(rn);
                    a = o == 1 ? bse : bse - 4 * cnt;
                    bool havePc = false; int newpc = 0;
                    k = 0;
                    for (rg = 0; rg < 16; rg++)
                    {
                        if ((h2 & (1 << rg)) != 0)
                        {
                            if (L != 0)
                            {
                                v = Rd(a + 4 * k, 4);
                                if (rg == 15) { havePc = true; newpc = v; } else r[rg] = v;
                            }
                            else Wr(a + 4 * k, 4, Reg(rg));
                            k++;
                        }
                    }
                    if (W != 0 && !(L != 0 && ((h2 >> rn) & 1) != 0)) r[rn] = o == 1 ? bse + 4 * cnt : bse - 4 * cnt;
                    if (havePc) Bx(newpc);
                    return;
                }
                if ((op2 & 0x64) == 0x04)
                {
                    int o1 = (h1 >> 7) & 3, o2 = (h1 >> 4) & 3, o3 = (h2 >> 4) & 0xF;
                    if (o1 == 1 && o2 == 1 && (o3 == 0 || o3 == 1))
                    {
                        int bs2 = Reg(rn);
                        int off2 = o3 == 0 ? Rd(bs2 + Reg(h2 & 0xF), 1) : Rd(bs2 + 2 * Reg(h2 & 0xF), 2);
                        nextPc = cur + 4 + 2 * off2;
                        return;
                    }
                    int P = (h1 >> 8) & 1, U = (h1 >> 7) & 1, W2 = (h1 >> 5) & 1, L2 = (h1 >> 4) & 1;
                    rt = h2 >> 12;
                    int rt2 = (h2 >> 8) & 0xF;
                    int imm = (h2 & 0xFF) * 4;
                    if ((o1 & 2) == 0 && (o2 == 0 || o2 == 1) && P == 0 && W2 == 0) throw new Fault("exclusive access at " + F.Hx(cur, 8));
                    int bs3 = rn == 15 ? ((cur + 4) & ~3) : r[rn];
                    int off3 = U != 0 ? imm : -imm;
                    a = P != 0 ? bs3 + off3 : bs3;
                    if (L2 != 0) { r[rt] = Rd(a, 4); r[rt2] = Rd(a + 4, 4); }
                    else { Wr(a, 4, r[rt]); Wr(a + 4, 4, r[rt2]); }
                    if (W2 != 0) r[rn] = bs3 + off3;
                    return;
                }
                if ((op2 & 0x60) == 0x20)
                {
                    int imm5 = (((h2 >> 12) & 7) << 2) | ((h2 >> 6) & 3);
                    DecodeImmShift((h2 >> 4) & 3, imm5);
                    int sh = ShiftC(Reg(h2 & 0xF), dsT, dsN, C);
                    Dp((h1 >> 5) & 0xF, (h1 >> 4) & 1, rn, (h2 >> 8) & 0xF, sh, shC);
                    return;
                }
                Cp(h1, h2);
                return;
            }
            if (op1 == 2)
            {
                if ((h2 & 0x8000) == 0)
                {
                    int i = (h1 >> 10) & 1, imm3 = (h2 >> 12) & 7, imm8 = h2 & 0xFF;
                    rd = (h2 >> 8) & 0xF;
                    if ((op2 & 0x20) == 0)
                    {
                        int imm = ThumbExpandC((i << 11) | (imm3 << 8) | imm8, C);
                        Dp((h1 >> 5) & 0xF, (h1 >> 4) & 1, rn, rd, imm, shC);
                        return;
                    }
                    o = (h1 >> 4) & 0x1F;
                    int imm12 = (i << 11) | (imm3 << 8) | imm8;
                    if (o == 0) { r[rd] = (rn == 15 ? ((cur + 4) & ~3) : r[rn]) + imm12; return; }
                    if (o == 0x0A) { r[rd] = (rn == 15 ? ((cur + 4) & ~3) : r[rn]) - imm12; return; }
                    if (o == 0x04) { r[rd] = (rn << 12) | imm12; return; }
                    if (o == 0x0C) { r[rd] = (r[rd] & 0xFFFF) | (((rn << 12) | imm12) << 16); return; }
                    int lsb = (imm3 << 2) | ((h2 >> 6) & 3);
                    int w = h2 & 0x1F;
                    if (o == 0x1C) { r[rd] = (int)(((r[rn] & 0xFFFFFFFFL) >> lsb) & ((1L << (w + 1)) - 1)); return; }
                    if (o == 0x14)
                    {
                        long fld = ((r[rn] & 0xFFFFFFFFL) >> lsb) & ((1L << (w + 1)) - 1);
                        if ((fld & (1L << w)) != 0) fld -= 1L << (w + 1);
                        r[rd] = (int)fld;
                        return;
                    }
                    if (o == 0x16)
                    {
                        int width = w - lsb + 1;
                        long mask = (((1L << width) - 1) << lsb) & 0xFFFFFFFFL;
                        long src = rn == 15 ? 0 : (((r[rn] & 0xFFFFFFFFL) << lsb) & 0xFFFFFFFFL);
                        r[rd] = (int)(((r[rd] & 0xFFFFFFFFL) & ~mask & 0xFFFFFFFFL) | (src & mask));
                        return;
                    }
                    if (o == 0x18 || o == 0x1A)
                    {
                        long sv = Reg(rn);
                        long hi = (1L << w) - 1;
                        r[rd] = (int)(sv < 0 ? 0 : (sv > hi ? hi : sv));
                        return;
                    }
                    if (o == 0x10 || o == 0x12)
                    {
                        long sv2 = Reg(rn);
                        long hi2 = (1L << w) - 1;
                        r[rd] = (int)(sv2 < -hi2 - 1 ? -hi2 - 1 : (sv2 > hi2 ? hi2 : sv2));
                        return;
                    }
                    throw new Fault("unhandled plain-immediate " + F.Hx(h1, 4) + " " + F.Hx(h2, 4) + " at " + F.Hx(cur, 8));
                }
                int b1 = (h2 >> 12) & 7, Sb = (h1 >> 10) & 1, J1 = (h2 >> 13) & 1, J2 = (h2 >> 11) & 1;
                if ((b1 & 5) == 0)
                {
                    if ((op2 & 0x38) != 0x38)
                    {
                        int offc = F.Sx((Sb << 20) | (J2 << 19) | (J1 << 18) | ((h1 & 0x3F) << 12) | ((h2 & 0x7FF) << 1), 21);
                        if (Cond((h1 >> 6) & 0xF)) nextPc = cur + 4 + offc;
                        return;
                    }
                    if ((op2 & 0x7E) == 0x38)
                    {
                        int sysm = h2 & 0xFF;
                        if (sysm == 16) primask = r[rn] & 1;
                        else if (sysm == 8 || sysm == 9) msp = r[rn];
                        return;
                    }
                    if ((op2 & 0x7E) == 0x3E)
                    {
                        int sysm2 = h2 & 0xFF;
                        rd = (h2 >> 8) & 0xF;
                        if (sysm2 == 16) r[rd] = primask;
                        else if (sysm2 == 8 || sysm2 == 9) r[rd] = r[13];
                        else r[rd] = (N << 31) | (Z << 30) | (C << 29) | (V << 28);
                        return;
                    }
                    return;
                }
                int I1 = 1 - (J1 ^ Sb), I2 = 1 - (J2 ^ Sb);
                int offb = F.Sx((Sb << 24) | (I1 << 23) | (I2 << 22) | ((h1 & 0x3FF) << 12) | ((h2 & 0x7FF) << 1), 25);
                if ((b1 & 5) == 5) r[14] = (cur + 4) | 1;
                else if ((b1 & 5) != 1) throw new Fault("BLX immediate at " + F.Hx(cur, 8));
                nextPc = cur + 4 + offb;
                return;
            }
            // op1 == 3
            if ((op2 & 0x71) == 0 || (op2 & 0x67) == 1 || (op2 & 0x67) == 3 || (op2 & 0x67) == 5)
            {
                int Sg = (h1 >> 8) & 1, sz = (h1 >> 5) & 3, Lb = (h1 >> 4) & 1;
                rt = h2 >> 12;
                if (sz == 3) throw new Fault("bad load/store size at " + F.Hx(cur, 8));
                int n = 1 << sz;
                bool wb = false;
                int newbase = 0;
                if (rn == 15)
                {
                    int imm = h2 & 0xFFF;
                    a = ((cur + 4) & ~3) + (((h1 >> 7) & 1) != 0 ? imm : -imm);
                }
                else if (((h1 >> 7) & 1) != 0) a = r[rn] + (h2 & 0xFFF);
                else if (((h2 >> 11) & 1) != 0)
                {
                    int imm = h2 & 0xFF;
                    int off4 = ((h2 >> 9) & 1) != 0 ? imm : -imm;
                    a = ((h2 >> 10) & 1) != 0 ? r[rn] + off4 : r[rn];
                    if (((h2 >> 8) & 1) != 0) { wb = true; newbase = r[rn] + off4; }
                }
                else a = r[rn] + (Reg(h2 & 0xF) << ((h2 >> 4) & 3));
                if (Lb != 0)
                {
                    if (rt == 15 && n == 4)
                    {
                        v = Rd(a, 4);
                        if (wb) r[rn] = newbase;
                        Bx(v);
                        return;
                    }
                    if (rt == 15) return;
                    v = Rd(a, n);
                    if (Sg != 0) v = F.Sx(v, 8 * n);
                    r[rt] = v;
                }
                else Wr(a, n, Reg(rt));
                if (wb) r[rn] = newbase;
                return;
            }
            if ((op2 & 0x70) == 0x20)
            {
                int o1 = (h1 >> 4) & 0xF, o2 = (h2 >> 4) & 0xF;
                rd = (h2 >> 8) & 0xF;
                rm = h2 & 0xF;
                if (o2 == 0 && o1 < 8)
                {
                    v = ShiftC(r[rn], o1 >> 1, r[rm] & 0xFF, C);
                    r[rd] = v;
                    if ((o1 & 1) != 0) { SetNZ(v); C = shC; }
                    return;
                }
                if ((o2 & 8) != 0 && o1 < 6)
                {
                    int rot = ((h2 >> 4) & 3) * 8;
                    v = rot != 0 ? ShiftC(r[rm], 3, rot, 0) : r[rm];
                    int e;
                    if (o1 == 0) e = F.Sx(v & 0xFFFF, 16);
                    else if (o1 == 1) e = v & 0xFFFF;
                    else if (o1 == 4) e = F.Sx(v & 0xFF, 8);
                    else if (o1 == 5) e = v & 0xFF;
                    else throw new Fault("xtb16 at " + F.Hx(cur, 8));
                    r[rd] = rn == 15 ? e : r[rn] + e;
                    return;
                }
                if ((o1 & 0xC) == 8 && (o2 & 0xC) == 8)
                {
                    o = ((o1 & 3) << 2) | (o2 & 3);
                    v = r[rm];
                    if (o == 0xC) { int z = 0; while (z < 32 && ((v >> (31 - z)) & 1) == 0) z++; r[rd] = z; return; }
                    if (o == 0x4) { r[rd] = ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | (F.Lsr(v, 8) & 0xFF00) | F.Lsr(v, 24); return; }
                    if (o == 0x5) { r[rd] = F.Lsr(v & unchecked((int)0xFF00FF00), 8) | ((v & 0x00FF00FF) << 8); return; }
                    if (o == 0x6) { int outv = 0; for (k = 0; k < 32; k++) outv |= ((v >> k) & 1) << (31 - k); r[rd] = outv; return; }
                    if (o == 0x7) { r[rd] = F.Sx(((v & 0xFF) << 8) | ((v >> 8) & 0xFF), 16); return; }
                }
                throw new Fault("unhandled dp-reg " + F.Hx(h1, 4) + " " + F.Hx(h2, 4) + " at " + F.Hx(cur, 8));
            }
            if ((op2 & 0x78) == 0x30)
            {
                int o1 = (h1 >> 4) & 7, o2 = (h2 >> 4) & 3, ra = h2 >> 12;
                rd = (h2 >> 8) & 0xF;
                rm = h2 & 0xF;
                if (o1 == 0 && o2 == 0) { r[rd] = r[rn] * r[rm] + (ra == 15 ? 0 : r[ra]); return; }
                if (o1 == 0 && o2 == 1) { r[rd] = r[ra] - r[rn] * r[rm]; return; }
                if (o1 == 1)
                {
                    a = F.Sx((((h2 & 0x20) != 0 ? F.Lsr(r[rn], 16) : r[rn])) & 0xFFFF, 16);
                    b = F.Sx((((h2 & 0x10) != 0 ? F.Lsr(r[rm], 16) : r[rm])) & 0xFFFF, 16);
                    r[rd] = a * b + (ra == 15 ? 0 : r[ra]);
                    return;
                }
                throw new Fault("unhandled multiply " + F.Hx(h1, 4) + " " + F.Hx(h2, 4) + " at " + F.Hx(cur, 8));
            }
            if ((op2 & 0x78) == 0x38)
            {
                int o1 = (h1 >> 4) & 7, o2 = (h2 >> 4) & 0xF, lo = h2 >> 12, hi = (h2 >> 8) & 0xF;
                rm = h2 & 0xF;
                if (o1 == 3 && o2 == 0xF)
                {
                    long d = r[rm] & 0xFFFFFFFFL;
                    r[hi] = d == 0 ? 0 : (int)((r[rn] & 0xFFFFFFFFL) / d);
                    return;
                }
                if (o1 == 1 && o2 == 0xF)
                {
                    long d2 = r[rm], nn = r[rn];
                    if (d2 == 0) r[hi] = 0;
                    else
                    {
                        long q = (nn < 0 ? -nn : nn) / (d2 < 0 ? -d2 : d2);
                        r[hi] = (int)((nn < 0) == (d2 < 0) ? q : -q);
                    }
                    return;
                }
                if (o2 == 0 && (o1 == 0 || o1 == 2 || o1 == 4 || o1 == 6))
                {
                    long lv;
                    if (o1 == 2) lv = (r[rn] & 0xFFFFFFFFL) * (r[rm] & 0xFFFFFFFFL);
                    else if (o1 == 0) lv = (long)r[rn] * (long)r[rm];
                    else if (o1 == 6) lv = (((r[hi] & 0xFFFFFFFFL) << 32) | (r[lo] & 0xFFFFFFFFL)) + (r[rn] & 0xFFFFFFFFL) * (r[rm] & 0xFFFFFFFFL);
                    else lv = (((long)r[hi]) << 32 | (r[lo] & 0xFFFFFFFFL)) + (long)r[rn] * (long)r[rm];
                    r[lo] = (int)lv;
                    r[hi] = (int)(lv >> 32);
                    return;
                }
                throw new Fault("unhandled long multiply " + F.Hx(h1, 4) + " " + F.Hx(h2, 4) + " at " + F.Hx(cur, 8));
            }
            Cp(h1, h2);
        }

        public void Cp(int h1, int h2)
        {
            if ((h2 & 0x0F00) == 0x0A00)
            {
                int sd = (((h2 >> 12) & 0xF) << 1) | ((h1 >> 6) & 1);
                int sn = ((h1 & 0xF) << 1) | ((h2 >> 7) & 1);
                int sm = ((h2 & 0xF) << 1) | ((h2 >> 5) & 1);
                if ((h1 & 0xFFE0) == 0xEE00 && (h2 & 0x0F7F) == 0x0A10)
                {
                    if ((h1 & 0x10) != 0) r[h2 >> 12] = S[sn]; else S[sn] = r[h2 >> 12];
                    return;
                }
                if ((h1 & 0xFFB8) == 0xEEB8 && (h2 & 0x0E50) == 0x0A40)
                {
                    int opc2 = h1 & 7, op = (h2 >> 7) & 1;
                    if (opc2 == 0)
                    {
                        double fv = op != 0 ? (double)S[sm] : (double)(S[sm] & 0xFFFFFFFFL);
                        S[sd] = P.FloatBits(fv);
                        return;
                    }
                    if (opc2 == 4 || opc2 == 5)
                    {
                        double f = P.BitsFloat(S[sm]);
                        long iv;
                        if (P.IsNaN(f)) iv = 0;
                        else
                        {
                            if (f > 1099511627776.0) f = 1099511627776.0;
                            if (f < -1099511627776.0) f = -1099511627776.0;
                            iv = op != 0 ? (long)f : (long)P.Rint(f);
                        }
                        if (opc2 == 5) { if (iv < -2147483648L) iv = -2147483648L; if (iv > 2147483647L) iv = 2147483647L; }
                        else { if (iv < 0) iv = 0; if (iv > 0xFFFFFFFFL) iv = 0xFFFFFFFFL; }
                        S[sd] = (int)iv;
                        return;
                    }
                }
                if ((h1 & 0xFFB0) == 0xEE80 && (h2 & 0x0E50) == 0x0A00)
                {
                    double x = P.BitsFloat(S[sn]), y = P.BitsFloat(S[sm]);
                    if (y == 0.0) S[sd] = x > 0 ? 0x7F800000 : (x < 0 ? unchecked((int)0xFF800000) : 0x7FC00000);
                    else S[sd] = P.FloatBits(x / y);
                    return;
                }
                if ((h1 & 0xFFB0) == 0xEE30 && (h2 & 0x0E10) == 0x0A00)
                {
                    double x2 = P.BitsFloat(S[sn]), y2 = P.BitsFloat(S[sm]);
                    S[sd] = P.FloatBits((h2 & 0x40) != 0 ? x2 - y2 : x2 + y2);
                    return;
                }
                if ((h1 & 0xFFB0) == 0xEE20 && (h2 & 0x0E10) == 0x0A00)
                {
                    S[sd] = P.FloatBits(P.BitsFloat(S[sn]) * P.BitsFloat(S[sm]));
                    return;
                }
            }
            throw new Fault("coprocessor/FPU instruction " + F.Hx(h1, 4) + " " + F.Hx(h2, 4) + " at " + F.Hx(cur, 8));
        }

        public void Dp(int o, int Sf, int rn, int rd, int op2, int c)
        {
            int a = rn != 15 ? Reg(rn) : 0;
            int v;
            if (o == 0) v = a & op2;
            else if (o == 1) v = a & ~op2;
            else if (o == 2) v = rn != 15 ? (a | op2) : op2;
            else if (o == 3) v = rn != 15 ? (a | ~op2) : ~op2;
            else if (o == 4) v = a ^ op2;
            else if (o == 8) v = AddC(a, op2, 0);
            else if (o == 10) v = AddC(a, op2, C);
            else if (o == 11) v = AddC(a, ~op2, C);
            else if (o == 13) v = AddC(a, ~op2, 1);
            else if (o == 14) v = AddC(op2, ~a, 1);
            else throw new Fault("data-processing op " + F.Str(o) + " at " + F.Hx(cur, 8));
            if (Sf != 0)
            {
                SetNZ(v);
                if (o <= 4) C = c; else { C = acC; V = acV; }
            }
            if (rd == 15)
            {
                if (Sf == 0) Bx(v | 1);
                return;
            }
            r[rd] = v;
        }
    }

    // ======================================================================================== machine
    public class Machine
    {
        public const long CLK = 72000000L;
        public const long NEVER = 1L << 62;
        public const int U = 0x50000000;
        public const int GINTF = U + 0x14, GINTEN = U + 0x18, GRSTATR = U + 0x1C, GRSTATP = U + 0x20;
        public const int DSTAT = U + 0x808, DAEPINT = U + 0x818, DIEPFEINTEN = U + 0x834;
        public static readonly int[] ALL_TIMERS = new int[] { 0x40012C00, 0x40000000, 0x40000400, 0x40002000, 0x40014000, 0x40014400, 0x40014800, 0x40001000 };
        public static readonly int[] IRQ_TIMERS = new int[] { 0x40012C00, 0x40000400, 0x40002000, 0x40014000, 0x40014400, 0x40014800 };
        public static readonly int[] TIMER_IRQN = new int[] { 13, 16, 19, 20, 21, 22 };
        public static readonly int[] IRQ_ORDER = new int[] { 13, 16, 19, 20, 21, 22, 67 };


        public static int DIEPCTL(int e) { return U + 0x900 + 0x20 * e; }
        public static int DIEPLEN(int e) { return U + 0x910 + 0x20 * e; }
        public static int DOEPCTL(int e) { return U + 0xB00 + 0x20 * e; }

        public byte[] flash;
        public byte[] sram = new byte[0x2000];
        public IntMap periph = new IntMap();
        public bool scan, dirty = true, realtime;
        public double wall = 0.0;
        public Cpu cpu;
        public int[] hookAt = new int[0x8000];
        public long[] bootCntBase = new long[8];
        public int adcSeq = 0;
        public UsbDevice usb = null;
        public Board board;                       // what the chip is wired to (null: nothing)
        public int vtor = 0x08000000;             // vector table: 0x08000000 for a whole-flash image
        public bool resetPending = false;
        public int resets = 0;

        // system reset (SYSRESETREQ): core and peripherals back to reset state, SRAM kept, start from flash 0x08000000
        public void SystemReset()
        {
            resetPending = false; resets++;
            periph = new IntMap(); periph.Set(0x40021000, 0x00000083);
            periph.Set(0x40021024, 1 << 28);       // RCU_RSTSCR: SWRSTF - this reset came from software (AIRCR)
            fmcCtl = 0x80; fmcStat = 0; fmcKey = 0;
            for (int i = 0; i < 6; i++) { tRun[i] = false; tNext[i] = -1; }
            nActive = 0; vtor = 0x08000000;
            Cpu c = cpu;
            for (int i = 0; i < 16; i++) c.r[i] = 0;
            c.r[13] = LE(flash, 0, 4); c.r[15] = LE(flash, 4, 4) & ~1; c.r[14] = -1;
            c.primask = 0; c.excDepth = 0; c.it = 0;
            Sched();
        }
        public Device dev;
        public ReportList reports = new ReportList();
        public int adcLatched = 0;
        public long adcEocAt = NEVER, tRel = -1, lastAdc = -1;
        public int fmcCtl = 0x80, fmcStat = 0, fmcAddr = 0, fmcKey = 0;   // flash controller (locked at reset)
        public static bool bootWp = false;                                  // test: bootloader area write-protected
        public int flashWrites = 0;
        public LongList procCyc = new LongList();      // per frame: frame restart - last ADC conversion (processing)
        public bool adcEoc = false;
        public bool[] tRun = new bool[6];
        public long[] tCnt0 = new long[6], tStart = new long[6], tNext = new long[6];
        public long nextEvent = NEVER;
        public int[] active = new int[64];
        public int nActive = 0;
        public long sofNext = -1;
        public int frame = 0, hostInterval = 0, bint82 = -1;
        public int[] hostIntervals = new int[4];

        public Machine(byte[] img, bool scanModel, Board b, bool rt)
        {
            dev = Devices.Default();
            string refuse = StmGuard.Check(img);
            if (refuse != null) throw new Stop(refuse);
            // Each machine owns its flash. Independent checks may run the same input concurrently and the
            // firmware can program it; sharing the caller's buffer leaks writes between those checks.
            flash = new byte[img.Length];
            P.Copy(img, flash, img.Length);
            scan = scanModel;
            board = b;
            realtime = rt;
            cpu = new Cpu(this);
            periph.Set(0x40021000, 0x00000083);
            for (int i = 0; i < 6; i++) tNext[i] = -1;
            if (board != null) board.Attach(this);
        }

        public void SetHook(int pc, int id) { hookAt[(pc - 0x08000000) >> 1] = id + 1; }

        public bool Hook(int hk) { return board != null && board.Hook(this, hk); }

        // ------------------------------------------------------------------ memory
        public int Read(int a, int n)
        {
            cpu.cycles++;
            long ua = a & 0xFFFFFFFFL;
            if ((ua >= 0x08000000L && ua < 0x08010000L) || ua < 0x10000L)
            {
                long o = ua >= 0x08000000L ? ua - 0x08000000L : ua;
                if (o + n > 0x10000) throw new Fault("read past region end at " + F.Hx(a, 8));
                return LE(flash, (int)o, n);
            }
            if (ua >= 0x20000000L && ua < 0x20002000L)
            {
                long o2 = ua - 0x20000000L;
                if (o2 + n > 0x2000) throw new Fault("read past region end at " + F.Hx(a, 8));
                return LE(sram, (int)o2, n);
            }
            if (ua >= 0x1FFFF000L && ua < 0x20000000L)
            {
                if (ua == 0x1FFFF7E0L && (n == 2 || n == 4)) return 64;
                return n == 4 ? -1 : (1 << (8 * n)) - 1;
            }
            if ((ua >= 0x40000000L && ua < 0x60000000L) || ua >= 0xE0000000L)
            {
                if (!dev.HasPeripheral(ua)) throw new Fault(F.Hx(a, 8) + " is not a " + dev.name + " peripheral (this emulator models GigaDevice GD32 parts only)");
                if (ua % n != 0) throw new Fault("unaligned " + F.Str(n) + "-byte peripheral read @ " + F.Hx(a, 8));
                if (usb != null && ua >= U + 0x1000 && ua < U + 0x5000) return usb.FifoRead(n);
                int v = PeriphRead(a & ~3);
                int sh = (a & 3) * 8;
                return n == 4 ? v : (int)(((v & 0xFFFFFFFFL) >> sh) & ((1L << (8 * n)) - 1));
            }
            throw new Fault("bus fault: read " + F.Str(n) + " @ " + F.Hx(a, 8));
        }

        public static int LE(byte[] b, int o, int n)
        {
            if (n == 1) return b[o] & 0xFF;
            if (n == 2) return (b[o] & 0xFF) | ((b[o + 1] & 0xFF) << 8);
            return (b[o] & 0xFF) | ((b[o + 1] & 0xFF) << 8) | ((b[o + 2] & 0xFF) << 16) | ((b[o + 3] & 0xFF) << 24);
        }

        public void Write(int a, int n, int v)
        {
            if (n < 4) v &= (1 << (8 * n)) - 1;
            cpu.cycles++;
            long ua = a & 0xFFFFFFFFL;
            if (ua >= 0x40000000L) dirty = true;
            if ((ua >= 0x08000000L && ua < 0x08010000L) || ua < 0x10000L) { FlashProgram(a, n, v); return; }
            if (ua >= 0x20000000L && ua < 0x20002000L)
            {
                long o = ua - 0x20000000L;
                if (o + n > 0x2000) throw new Fault("write past region end at " + F.Hx(a, 8));
                for (int k = 0; k < n; k++) sram[(int)o + k] = (byte)((v >> (8 * k)) & 0xFF);
                return;
            }
            if ((ua >= 0x40000000L && ua < 0x60000000L) || ua >= 0xE0000000L)
            {
                if (!dev.HasPeripheral(ua)) throw new Fault(F.Hx(a, 8) + " is not a " + dev.name + " peripheral (this emulator models GigaDevice GD32 parts only)");
                if (ua % n != 0) throw new Fault("unaligned " + F.Str(n) + "-byte peripheral write @ " + F.Hx(a, 8));
                if (usb != null && ua >= U + 0x1000 && ua < U + 0x5000) { usb.FifoWrite(a, n, v); return; }
                int wa = a & ~3;
                int nv;
                if (n == 4) nv = v;
                else
                {
                    int sh = (a & 3) * 8;
                    int mask = ((1 << (8 * n)) - 1) << sh;
                    nv = (periph.Get(wa, 0) & ~mask) | (v << sh);
                }
                PeriphWrite(wa, nv);
                return;
            }
            throw new Fault("bus fault: write " + F.Str(n) + " @ " + F.Hx(a, 8) + " = " + F.Hx(v, 8));
        }

        // programming through the flash controller: PG set and unlocked, target erased (else PGERR), not protected
        public void FlashProgram(int a, int n, int v)
        {
            if ((fmcCtl & 0x80) != 0 || (fmcCtl & 1) == 0) throw new Fault("write to flash @ " + F.Hx(a, 8));
            int o = (int)((a & 0xFFFFFFFFL) >= 0x08000000L ? (a & 0xFFFFFFFFL) - 0x08000000L : a);
            if (bootWp && o < 0x3800) { fmcStat |= 0x10; return; }
            for (int k = 0; k < n; k++) if ((flash[o + k] & 0xFF) != 0xFF) { fmcStat |= 0x04; return; }
            for (int k = 0; k < n; k++) flash[o + k] = (byte)((v >> (8 * k)) & 0xFF);
            fmcStat |= 0x20;
            flashWrites++;
        }

        public void FmcWrite(int wa, int nv)
        {
            if (wa == 0x40022004)
            {
                if (nv == 0x45670123) fmcKey = 1;
                else if (fmcKey == 1 && nv == unchecked((int)0xCDEF89AB)) { fmcCtl &= ~0x80; fmcKey = 0; }
                else fmcKey = 0;
                return;
            }
            if (wa == 0x4002200C) { fmcStat &= ~nv; return; }
            if (wa == 0x40022014) { fmcAddr = nv; return; }
            if (wa == 0x40022010)
            {
                if ((fmcCtl & 0x80) != 0) { fmcCtl |= nv & 0x80; return; }
                fmcCtl = nv & ~0x40;
                if ((nv & 0x40) != 0 && (nv & 2) != 0)
                {
                    int o = (int)(((fmcAddr & 0xFFFFFFFFL) - 0x08000000L) & ~0x3FFL);
                    if (o < 0 || o >= 0x10000) { fmcStat |= 0x04; return; }
                    if (bootWp && o < 0x3800) { fmcStat |= 0x10; return; }
                    for (int k = 0; k < 0x400; k++) flash[o + k] = (byte)0xFF;
                    fmcStat |= 0x20;
                    flashWrites++;
                }
            }
        }

        public int Peek(int a, int n)
        {
            if ((a & 0xFFFFFFFFL) >= 0x40000000L) { int v = PeriphRead(a & ~3) >> (8 * (a & 3)); return n >= 4 ? v : v & ((1 << (8 * n)) - 1); }
            return LE(sram, a - 0x20000000, n);
        }
        public void Poke(int a, int n, int v) { for (int k = 0; k < n; k++) sram[a - 0x20000000 + k] = (byte)((v >> (8 * k)) & 0xFF); }

        public static int TimerIndex(int tb) { for (int i = 0; i < 6; i++) if (IRQ_TIMERS[i] == tb) return i; return -1; }
        public static int AllIndex(int tb) { for (int i = 0; i < 8; i++) if (ALL_TIMERS[i] == tb) return i; return -1; }

        // ------------------------------------------------------------------ peripheral registers
        public int PeriphRead(int wa)
        {
            IntMap p = periph;
            int v;
            if (wa == 0x40021000)
            {
                v = p.Get(wa, 0);
                if ((v & 1) != 0) v |= 2;
                if ((v & (1 << 16)) != 0) v |= 1 << 17;
                if ((v & (1 << 24)) != 0) v |= 1 << 25;
                return v;
            }
            if (wa == 0x40021004) { v = p.Get(wa, 0); return (v & ~0xC) | ((v & 3) << 2); }
            if (wa == 0x40022010) return fmcCtl;
            if (wa == 0x4002200C) return fmcStat;
            if (wa == 0x40022014) return fmcAddr;
            if (wa == 0x40021034) { v = p.Get(wa, 0); return v | ((v & 1) != 0 ? 2 : 0); }
            if (wa == 0x400210C0) { v = p.Get(wa, 0); return v | ((v & (1 << 16)) != 0 ? (1 << 17) : 0); }
            if (wa == 0x40012400)
            {
                if (scan)
                {
                    if (!adcEoc && cpu.cycles >= adcEocAt) adcEoc = true;
                    return (p.Get(wa, 0) & ~2) | (adcEoc ? 2 : 0);
                }
                return p.Get(wa, 0) | 2;
            }
            if (wa == 0x4001244C)
            {
                if (scan) { adcEoc = false; return adcLatched; }
                adcSeq++;
                return 300;
            }
            if ((wa & 0xFF) == 0x24 && AllIndex(wa - 0x24) >= 0)
            {
                int tb = wa - 0x24;
                int ti = TimerIndex(tb);
                if (scan && ti >= 0)
                {
                    long car = Car(tb);
                    return (int)(CntNow(ti) % (car != 0 ? car + 1 : 0x10000));
                }
                int ctl = p.Get(tb, 0);
                int cnt0 = p.Get(wa, 0);
                if ((ctl & 1) == 0) return cnt0;
                long psc = p.Get(tb + 0x28, 0) & 0xFFFF;
                long car2 = p.Get(tb + 0x2C, 0xFFFF) & 0xFFFF;
                long elapsed = (cpu.cycles - bootCntBase[AllIndex(tb)]) / (psc + 1);
                return (int)(((cnt0 & 0xFFFFFFFFL) + elapsed) % (car2 != 0 ? car2 + 1 : 0x10000));
            }
            if (wa == U + 0x10) return (p.Get(wa, 0) & ~0x31) | unchecked((int)0x80000000);
            if (usb != null && (wa & 0xFFFFFFFFL) >= U && (wa & 0xFFFFFFFFL) < U + 0x1000)
            {
                long rv = usb.RegRead(wa);
                if (rv != -1L) return (int)rv;
            }
            if (wa == unchecked((int)0xE000E010)) return p.Get(wa, 0) | (1 << 16);
            if (wa == unchecked((int)0xE0042000)) return dev.debugId;
            if (wa == 0x48000010 || wa == 0x48000410 || wa == 0x48000810 || wa == 0x48001410) return 0xFFFF;
            return p.Get(wa, 0);
        }

        public void PeriphWrite(int wa, int nv)
        {
            IntMap p = periph;
            if (wa == 0x40021024 && (nv & (1 << 24)) != 0) { p.Set(wa, (p.Get(wa, 0) & 0x00FFFFFF & ~(1 << 24)) | (nv & 0x00FFFFFF & ~(1 << 24))); return; }   // RSTFC: clears the reset flags
            if (wa >= 0x40022004 && wa <= 0x40022014 && wa != 0x40022008) { FmcWrite(wa, nv); return; }
            // GPIO ports A-F (0x48000000 + 0x400 each): BOP (set low half / clear high half) and BC (clear) change the
            // output register OCTL (+0x14), so a pin's state can be read back (e.g. an LED)
            if ((wa & 0xFFFFE000) == 0x48000000 && ((wa & 0x3FF) == 0x18 || (wa & 0x3FF) == 0x28))
            {
                int ob = (wa & ~0x3FF) + 0x14, o = p.Get(ob, 0);
                if ((wa & 0x3FF) == 0x18) o = (o | (nv & 0xFFFF)) & ~((nv >> 16) & 0xFFFF);
                else o &= ~(nv & 0xFFFF);
                p.Set(ob, o);
            }
            if (wa == 0x40012408)
            {
                if (scan && (nv & (1 << 22)) != 0)
                {
                    long now = cpu.cycles;
                    adcLatched = board != null ? board.Adc(this) : 0;
                    adcEocAt = now + 100;
                    adcEoc = false;
                }
                p.Set(wa, nv & ~((1 << 2) | (1 << 3) | (1 << 22) | (1 << 30)));
                return;
            }
            if (scan && wa == 0x40012400)
            {
                p.Set(wa, nv & ~2);
                if ((nv & 2) == 0)
                {
                    adcEoc = false;
                    adcEocAt = cpu.cycles >= adcEocAt ? NEVER : adcEocAt;
                }
                return;
            }
            int tb = wa & ~0x3F;
            if (AllIndex(tb) >= 0)
            {
                int off = wa - tb;
                int ti = TimerIndex(tb);
                if (scan && ti >= 0)
                {
                    if (off == 0x00)
                    {
                        p.Set(wa, nv);
                        if ((nv & 1) != 0 && !tRun[ti]) { Arm(ti); TxEdge(tb, true); }
                        else if ((nv & 1) == 0 && tRun[ti])
                        {
                            tCnt0[ti] = CntNow(ti);
                            tRun[ti] = false;
                            tNext[ti] = -1;
                            Sched();
                            TxEdge(tb, false);
                        }
                        return;
                    }
                    if (off == 0x10) { p.Set(wa, p.Get(wa, 0) & nv); return; }
                    if (off == 0x14)
                    {
                        if ((nv & 1) != 0)
                        {
                            tCnt0[ti] = 0;
                            tStart[ti] = cpu.cycles;
                            p.Set(tb + 0x10, p.Get(tb + 0x10, 0) | 1);
                            if (tRun[ti]) Arm(ti);
                        }
                        return;
                    }
                    if (off == 0x24)
                    {
                        tCnt0[ti] = nv & 0xFFFF;
                        tStart[ti] = cpu.cycles;
                        if (tRun[ti]) Arm(ti);
                        return;
                    }
                    if (off == 0x2C)
                    {
                        p.Set(wa, nv);
                        if (tRun[ti]) Arm(ti);
                        return;
                    }
                }
                else if (off == 0x24)
                {
                    p.Set(wa, nv & 0xFFFF);
                    bootCntBase[AllIndex(tb)] = cpu.cycles;
                    return;
                }
            }
            if (wa == GINTF)
            {
                if (usb != null) usb.latch &= ~nv; else p.Set(wa, p.Get(wa, 0) & ~nv);
                return;
            }
            if (usb != null && (wa & 0xFFFFFFFFL) >= U && (wa & 0xFFFFFFFFL) < U + 0x1000 && usb.RegWrite(wa, nv)) return;
            long uw = wa & 0xFFFFFFFFL;
            if (wa == unchecked((int)0xE000ED08)) { vtor = nv & ~0x7F; if (vtor == 0) vtor = 0x08000000; p.Set(wa, nv); return; }
            if (wa == unchecked((int)0xE000ED0C) && (F.Lsr(nv, 16) == 0x05FA) && (nv & 4) != 0) { resetPending = true; return; }
            if (scan && uw >= 0xE000E100L && uw <= 0xE000E108L) { p.Set(wa, p.Get(wa, 0) | nv); return; }
            if (scan && uw >= 0xE000E180L && uw <= 0xE000E188L) { p.Set(wa - 0x80, p.Get(wa - 0x80, 0) & ~nv); return; }
            p.Set(wa, nv);
        }

        // ------------------------------------------------------------------ scan model: timers
        public long Psc(int tb) { return (periph.Get(tb + 0x28, 0) & 0xFFFF) + 1; }
        public long Car(int tb) { return periph.Get(tb + 0x2C, 0xFFFF) & 0xFFFF; }
        public long CntNow(int ti)
        {
            if (!tRun[ti]) return tCnt0[ti];
            return tCnt0[ti] + (cpu.cycles - tStart[ti]) / Psc(IRQ_TIMERS[ti]);
        }

        public void Sched()
        {
            long ne = NEVER;
            for (int i = 0; i < 6; i++) if (tNext[i] >= 0 && tNext[i] < ne) ne = tNext[i];
            if (sofNext >= 0 && sofNext < ne) ne = sofNext;
            nextEvent = ne;
        }

        public void Arm(int ti)
        {
            int tb = IRQ_TIMERS[ti];
            long car = Car(tb);
            long cnt = tRun[ti] ? CntNow(ti) : tCnt0[ti];
            tCnt0[ti] = cnt;
            tStart[ti] = cpu.cycles;
            long ticks = cnt <= car ? (car - cnt + 1) : (0x10000 - cnt + car + 1);
            tNext[ti] = cpu.cycles + ticks * Psc(tb);
            tRun[ti] = true;
            Sched();
        }

        public void TxEdge(int tb, bool on) { if (board != null) board.TimerEdge(this, tb, on); }

        public void TimerEvent(int ti)
        {
            int tb = IRQ_TIMERS[ti];
            IntMap p = periph;
            p.Set(tb + 0x10, p.Get(tb + 0x10, 0) | 1);
            int ctl = p.Get(tb, 0);
            tCnt0[ti] = 0;
            if ((ctl & 8) != 0)
            {
                p.Set(tb, ctl & ~1);
                tRun[ti] = false;
                tNext[ti] = -1;
                TxEdge(tb, false);
            }
            else
            {
                tStart[ti] = tNext[ti];
                tNext[ti] = tNext[ti] + (Car(tb) + 1) * Psc(tb);
            }
        }

        // ------------------------------------------------------------------ scan model: NVIC
        public bool IrqLine(int irq)
        {
            if (irq == 67) return usb != null && usb.Pending();
            int tb = 0;
            for (int i = 0; i < 6; i++) if (TIMER_IRQN[i] == irq) tb = IRQ_TIMERS[i];
            return (periph.Get(tb + 0x10, 0) & periph.Get(tb + 0x0C, 0) & 1) != 0;
        }

        public void CheckIrq()
        {
            Cpu c = cpu;
            if (c.primask != 0) return;
            if ((c.it & 0xF) != 0) { dirty = true; return; }
            int curp = nActive > 0 ? active[nActive - 1] : 99;
            int bestPr = -1, bestIrq = -1;
            for (int k = 0; k < IRQ_ORDER.Length; k++)
            {
                int irq = IRQ_ORDER[k];
                if (((periph.Get(unchecked((int)0xE000E100) + 4 * (irq / 32), 0) >> (irq % 32)) & 1) != 0 && IrqLine(irq))
                {
                    int ipr = (periph.Get(unchecked((int)0xE000E400) + (irq & ~3), 0) >> (8 * (irq & 3))) & 0xFF;
                    int pr = (ipr >> 4) >> 2;
                    if (pr < curp && (bestIrq < 0 || pr < bestPr)) { bestPr = pr; bestIrq = irq; }
                }
            }
            if (bestIrq >= 0)
            {
                active[nActive++] = bestPr;
                c.TakeException(16 + bestIrq);
                c.cycles += 12;
            }
        }

        // ------------------------------------------------------------------ USB attach / scan run loop
        public VirtualHost AttachUsb(bool enumerateNow)
        {
            usb = new UsbDevice(this);
            ConfigureUsb(null);
            VirtualHost host = new VirtualHost(usb);
            if (enumerateNow) host.Enumerate();
            if (scan)
            {
                sofNext = cpu.cycles + 72000;
                Sched();
            }
            return host;
        }

        public void ConfigureUsb(UsbConfiguration configuration)
        {
            hostInterval = 0; bint82 = -1;
            for (int e = 1; e <= 3; e++)
            {
                int address = 0x80 | e;
                hostIntervals[e] = configuration != null && configuration.endpointInterface[address] >= 0
                    && (configuration.attributes[address] & 3) == 3
                    ? UsbConfiguration.ModelPollInterval(configuration.interval[address]) : 0;
            }
            if (hostIntervals[2] > 0) { bint82 = configuration.interval[0x82]; hostInterval = hostIntervals[2]; }
        }

        public void UsbFrame()
        {
            frame++;
            usb.frame = frame;
            usb.latch |= 1 << 3;
            for (int e = 1; e <= 3; e++) if (hostIntervals[e] > 0 && frame % hostIntervals[e] == 0) HostPoll(e);
            sofNext += 72000;
        }

        public void HostPoll(int e)
        {
            if (e < 1 || e > 3) throw new Stop("USB interrupt IN endpoint must be 1, 2 or 3");
            UsbDevice d = usb;
            IntMap p = periph;
            if ((p.Get(DIEPCTL(e), 0) & unchecked((int)0x80000000)) == 0) return;
            int tlen = p.Get(DIEPLEN(e), 0) & 0x7FFFF;
            if (d.txbuf[e].n < tlen) return;
            reports.Add(new Report(cpu.cycles, e, d.txbuf[e].Take(tlen)));
            d.txbuf[e].Clear();
            p.Set(DIEPCTL(e), p.Get(DIEPCTL(e), 0) & ~unchecked((int)0x80000000));
            p.Set(DIEPLEN(e), 0);
            d.iepf[e] |= 1;
        }

        public void UsbTxfe()
        {
            UsbDevice d = usb;
            IntMap p = periph;
            for (int e = 1; e <= 3; e++)
            {
                if ((p.Get(DIEPCTL(e), 0) & unchecked((int)0x80000000)) != 0 && ((p.Get(DIEPFEINTEN, 0) >> e) & 1) != 0
                    && d.txbuf[e].n == 0 && (p.Get(DIEPLEN(e), 0) & 0x7FFFF) != 0)
                    d.iepf[e] |= 1 << 7;
                else
                    d.iepf[e] &= ~(1 << 7);
            }
        }

        public void RunMs(double ms)
        {
            Cpu c = cpu;
            long end = c.cycles + (long)(ms * 72000000.0 / 1000.0);
            long c0 = c.cycles;
            double w0 = P.NowSec();
            long n = 0;
            while (c.cycles < end)
            {
                n++;
                if ((n & 31) == 0)
                {
                    dirty = true;
                    if (realtime && (n & 8191) == 0) Pace(c0, w0);
                }
                if (c.cycles >= nextEvent)
                {
                    for (int i = 0; i < 6; i++) if (tNext[i] >= 0 && c.cycles >= tNext[i]) TimerEvent(i);
                    if (sofNext >= 0 && c.cycles >= sofNext) UsbFrame();
                    Sched();
                    dirty = true;
                }
                if (dirty)
                {
                    dirty = false;
                    if (usb != null) UsbTxfe();
                    CheckIrq();
                }
                int depth = c.excDepth;
                c.Step();
                if (c.excDepth < depth)
                {
                    nActive--;
                    dirty = true;
                    c.cycles += 10;
                }
            }
            if (realtime) Pace(c0, w0);
            wall += P.NowSec() - w0;
        }

        public void Pace(long c0, double w0)
        {
            double ahead = (double)(cpu.cycles - c0) / (double)CLK - (P.NowSec() - w0);
            if (ahead > 0.002) P.Sleep(ahead);
        }

        public string Boot(long maxSteps)
        {
            int vo = vtor - 0x08000000;
            int sp = LE(flash, vo, 4), pc = LE(flash, vo + 4, 4);
            if ((sp & 0x2FFE0000) != 0x20000000) return "no valid vector table at " + F.Hx(vtor, 8);
            Cpu c = cpu;
            for (int i = 0; i < 16; i++) c.r[i] = 0;
            c.r[13] = sp;
            c.r[15] = pc & ~1;
            c.r[14] = -1;
            int[] stops = board != null ? board.BootStops(this) : new int[0];
            try
            {
                c.Run(maxSteps, stops);
                return "ok";
            }
            catch (Fault e) { return e.msg + " (pc=" + F.Hx(c.r[15], 8) + ")"; }
            catch (Stop e) { return e.msg + " (pc=" + F.Hx(c.r[15], 8) + ")"; }
        }

        public int Call(int fn, int arg0, long maxSteps)
        {
            Cpu c = cpu;
            int[] saved = (int[])c.r.Clone();
            c.r[0] = arg0;
            c.r[14] = 0x0800FFF1;
            c.r[15] = fn & ~1;
            c.Run(maxSteps, new int[] { 0x0800FFF0 });
            int ret = c.r[0];
            c.r = saved;
            return ret;
        }
    }

    // ======================================================================================== USB
    public class UsbDevice
    {
        public Machine mc;
        public int[] rxqSt = new int[64];
        public byte[][] rxqData = new byte[64][];
        public int rxqN = 0;
        public byte[] rxdata = new byte[0];
        public int rxpos = 0;
        public Bytes[] txbuf = new Bytes[] { new Bytes(), new Bytes(), new Bytes(), new Bytes() };
        public int latch = 0;
        public int[] iepf = new int[4], oepf = new int[4];
        public int frame = 0;

        public UsbDevice(Machine m) { mc = m; }

        public void Push(int st, byte[] payload) { rxqSt[rxqN] = st; rxqData[rxqN] = payload; rxqN++; }

        public int Gintf()
        {
            int v = latch;
            if (rxqN > 0) v |= 1 << 4;
            if (iepf[0] != 0 || iepf[1] != 0 || iepf[2] != 0 || iepf[3] != 0) v |= 1 << 18;
            if (oepf[0] != 0 || oepf[1] != 0 || oepf[2] != 0 || oepf[3] != 0) v |= 1 << 19;
            return v;
        }

        // register value, or -1 when the register is not modelled here
        public long RegRead(int wa)
        {
            if (wa == Machine.GINTF) return Gintf() & 0xFFFFFFFFL;
            if (wa == Machine.GRSTATR) return rxqN > 0 ? (rxqSt[0] & 0xFFFFFFFFL) : 0;
            if (wa == Machine.GRSTATP)
            {
                if (rxqN == 0) return 0;
                int v = rxqSt[0];
                rxdata = rxqData[0];
                rxpos = 0;
                for (int i = 1; i < rxqN; i++) { rxqSt[i - 1] = rxqSt[i]; rxqData[i - 1] = rxqData[i]; }
                rxqN--;
                return v & 0xFFFFFFFFL;
            }
            if (wa == Machine.DAEPINT)
            {
                int v2 = 0;
                for (int e = 0; e < 4; e++) { if (iepf[e] != 0) v2 |= 1 << e; if (oepf[e] != 0) v2 |= 1 << (16 + e); }
                return v2 & 0xFFFFFFFFL;
            }
            if (wa == Machine.DSTAT) return (3 << 1) | ((frame & 0x7FF) << 8);
            for (int e2 = 0; e2 < 4; e2++)
            {
                if (wa == Machine.U + 0x908 + 0x20 * e2) return iepf[e2] & 0xFFFFFFFFL;
                if (wa == Machine.U + 0xB08 + 0x20 * e2) return oepf[e2] & 0xFFFFFFFFL;
                if (wa == Machine.U + 0x918 + 0x20 * e2) return 0x40;
            }
            return -1L;
        }

        public bool RegWrite(int wa, int nv)
        {
            for (int e = 0; e < 4; e++)
            {
                if (wa == Machine.U + 0x908 + 0x20 * e) { iepf[e] &= ~nv; return true; }
                if (wa == Machine.U + 0xB08 + 0x20 * e) { oepf[e] &= ~nv; return true; }
            }
            return false;
        }

        public int FifoRead(int n)
        {
            int v = 0;
            for (int k = 0; k < 4; k++) { int b = rxpos + k < rxdata.Length ? rxdata[rxpos + k] & 0xFF : 0; v |= b << (8 * k); }
            rxpos += 4;
            if (rxpos > rxdata.Length) rxpos = rxdata.Length;
            return n == 4 ? v : (int)((v & 0xFFFFFFFFL) & ((1L << (8 * n)) - 1));
        }

        public void FifoWrite(int a, int n, int v)
        {
            int ep = (int)(((a & 0xFFFFFFFFL) - Machine.U - 0x1000) / 0x1000);
            for (int k = 0; k < n; k++) txbuf[ep].Add((v >> (8 * k)) & 0xFF);
        }

        public bool Pending() { return (Gintf() & mc.periph.Get(Machine.GINTEN, 0)) != 0; }

        public void Irq()
        {
            Cpu c = mc.cpu;
            if (c.primask != 0) throw new Stop("interrupts masked");
            // exception entry does not save an IT block's state (TakeException clears it), so finish the block first,
            // as CheckIrq does by waiting: taken inside one, its conditional instructions ran unconditionally after the
            // return (found with the APIP training build, R45: a conditional pop of pc read a stale stack word)
            for (int g = 0; g < 8 && (c.it & 0xF) != 0; g++) c.Step();
            c.TakeException(16 + 67);
            long steps = 0;
            while (c.excDepth > 0)
            {
                c.Step();
                steps++;
                if (steps > 400000) throw new Stop("USB ISR did not return (pc=" + F.Hx(c.r[15], 8) + ")");
            }
        }

        public void Service()
        {
            for (int i = 0; i < 50; i++)
            {
                if (!Pending()) return;
                Irq();
            }
            throw new Stop("USB interrupt storm");
        }
    }

    // Active alternate setting zero only. Endpoint addresses retain their direction bit.
    public class UsbConfiguration
    {
        public int interfaces, value;
        public bool[] active = new bool[256];
        public int[] classes = new int[256], subclasses = new int[256], protocols = new int[256];
        public int[] endpointInterface = new int[256], attributes = new int[256], maxPacket = new int[256], interval = new int[256];

        public UsbConfiguration() { for (int i = 0; i < 256; i++) endpointInterface[i] = -1; }

        public static UsbConfiguration Parse(byte[] cfg)
        {
            if (cfg == null || cfg.Length < 9 || (cfg[0] & 255) != 9 || (cfg[1] & 255) != 2)
                throw new Stop("USB configuration descriptor must start with a complete 9-byte header");
            int total = (cfg[2] & 255) | ((cfg[3] & 255) << 8);
            if (total != cfg.Length) throw new Stop("USB configuration length does not match wTotalLength");
            UsbConfiguration c = new UsbConfiguration(); c.interfaces = cfg[4] & 255; c.value = cfg[5] & 255;
            if (c.interfaces == 0 || c.value == 0) throw new Stop("USB configuration needs interfaces and a nonzero configuration value");
            bool[] alternates = new bool[65536], numbers = new bool[256], localEndpoints = new bool[256];
            int count = 0, number = -1, alternate = -1, expected = 0, found = 0;
            for (int i = 9; i < total; )
            {
                if (i + 2 > total) throw new Stop("Truncated USB descriptor header at offset " + F.Str(i));
                int length = cfg[i] & 255, type = cfg[i + 1] & 255;
                if (length < 2 || length > total - i) throw new Stop("Invalid USB descriptor length at offset " + F.Str(i));
                if (type == 2) throw new Stop("Unexpected second USB configuration header");
                if (type == 4)
                {
                    if (length != 9) throw new Stop("USB interface descriptor must be 9 bytes");
                    if (number >= 0 && found != expected) throw new Stop("USB interface endpoint count does not match bNumEndpoints");
                    number = cfg[i + 2] & 255; alternate = cfg[i + 3] & 255;
                    int key = number * 256 + alternate;
                    if (alternates[key]) throw new Stop("Duplicate USB interface alternate setting");
                    alternates[key] = true;
                    if (!numbers[number]) { numbers[number] = true; count++; }
                    expected = cfg[i + 4] & 255; found = 0;
                    for (int k = 0; k < 256; k++) localEndpoints[k] = false;
                    if (alternate == 0)
                    {
                        c.active[number] = true;
                        c.classes[number] = cfg[i + 5] & 255; c.subclasses[number] = cfg[i + 6] & 255; c.protocols[number] = cfg[i + 7] & 255;
                    }
                }
                else if (type == 5)
                {
                    if (length < 7 || number < 0) throw new Stop("USB endpoint descriptor needs an interface and at least 7 bytes");
                    int address = cfg[i + 2] & 255, endpoint = address & 15, attr = cfg[i + 3] & 255;
                    int packet = (cfg[i + 4] & 255) | ((cfg[i + 5] & 255) << 8), binterval = cfg[i + 6] & 255;
                    if ((address & 0x70) != 0 || endpoint == 0 || endpoint > 3)
                        throw new Stop("Unsupported USB endpoint address 0x" + F.HexDigits(address, 2, true) + " (model supports endpoints 1 to 3)");
                    int transfer = attr & 3;
                    if (transfer == 0 || (attr & (transfer == 1 ? 0xC0 : 0xFC)) != 0)
                        throw new Stop("Invalid USB endpoint attributes at 0x" + F.HexDigits(address, 2, true));
                    if (packet == 0 || packet > (transfer == 1 ? 1023 : 64))
                        throw new Stop("Invalid USB max packet size at 0x" + F.HexDigits(address, 2, true));
                    if ((transfer == 1 || transfer == 3) && binterval == 0)
                        throw new Stop("USB periodic endpoint has zero bInterval at 0x" + F.HexDigits(address, 2, true));
                    if (localEndpoints[address]) throw new Stop("Duplicate USB endpoint in an interface alternate setting");
                    localEndpoints[address] = true; found++;
                    if (alternate == 0)
                    {
                        if (c.endpointInterface[address] >= 0) throw new Stop("Duplicate active USB endpoint address");
                        c.endpointInterface[address] = number; c.attributes[address] = attr; c.maxPacket[address] = packet; c.interval[address] = binterval;
                    }
                }
                i += length;
            }
            if (number < 0 || found != expected) throw new Stop("USB interface endpoint count does not match bNumEndpoints");
            if (count != c.interfaces) throw new Stop("USB interface count does not match bNumInterfaces");
            for (int i = 0; i < 256; i++) if (numbers[i] && !c.active[i]) throw new Stop("USB interface is missing alternate setting zero");
            return c;
        }

        // Retain the model's established frame scheduling (1 -> 1 ms, 3 -> 2 ms).
        // This is a simulation policy, not a measurement of a physical host controller.
        public static int ModelPollInterval(int binterval)
        {
            int frames = 1;
            while (frames * 2 <= binterval) frames *= 2;
            return frames;
        }
    }

    public class VirtualHost
    {
        public UsbDevice d;
        public byte[] dev, cfg;
        public UsbConfiguration configuration;

        public VirtualHost(UsbDevice dv) { d = dv; }

        public void BusReset()
        {
            d.mc.ConfigureUsb(null); configuration = null;
            d.latch |= 1 << 12; d.Service();
            d.latch |= 1 << 13; d.Service();
        }

        public byte[] InTransfer(int e)
        {
            if (e < 0 || e > 3) throw new Stop("USB IN transfer endpoint must be between 0 and 3");
            IntMap p = d.mc.periph;
            Bytes outb = new Bytes();
            for (int r = 0; r < 20; r++)
            {
                if ((p.Get(Machine.DIEPCTL(e), 0) & unchecked((int)0x80000000)) == 0) break;
                int tlen = p.Get(Machine.DIEPLEN(e), 0) & 0x7FFFF;
                d.txbuf[e].Clear();
                if (tlen != 0 && ((p.Get(Machine.DIEPFEINTEN, 0) >> e) & 1) != 0)
                {
                    d.iepf[e] |= 1 << 7;
                    d.Service();
                }
                byte[] got = d.txbuf[e].Take(tlen);
                for (int k = 0; k < got.Length; k++) outb.Add(got[k]);
                p.Set(Machine.DIEPCTL(e), p.Get(Machine.DIEPCTL(e), 0) & ~unchecked((int)0x80000000));
                p.Set(Machine.DIEPLEN(e), 0);
                d.iepf[e] |= 1;
                d.Service();
                if (e != 0) break;
            }
            return outb.Take(outb.n);
        }

        public void OutZlp(int e)
        {
            if (e < 0 || e > 3) throw new Stop("USB OUT transfer endpoint must be between 0 and 3");
            IntMap p = d.mc.periph;
            d.Push((2 << 17) | e, new byte[0]);
            d.Push((3 << 17) | e, new byte[0]);
            d.Service();
            p.Set(Machine.DOEPCTL(e), p.Get(Machine.DOEPCTL(e), 0) & ~unchecked((int)0x80000000));
            d.oepf[e] |= 1;
            d.Service();
        }

        public byte[] Control(int rtype, int req, int value, int index, int length)
        {
            byte[] setup = new byte[] { (byte)rtype, (byte)req, (byte)(value & 0xFF), (byte)(value >> 8), (byte)(index & 0xFF), (byte)(index >> 8), (byte)(length & 0xFF), (byte)(length >> 8) };
            d.Push((6 << 17) | (8 << 4), setup);
            d.Push(4 << 17, new byte[0]);
            d.Service();
            d.oepf[0] |= 1 << 3;
            d.Service();
            if ((rtype & 0x80) != 0)
            {
                byte[] data = InTransfer(0);
                OutZlp(0);
                if (data.Length > length) { byte[] cut = new byte[length]; P.Copy(data, cut, length); return cut; }
                return data;
            }
            return InTransfer(0);
        }

        public byte[] GetDescriptor(int dtype, int index, int length, int windex, int rtype)
        {
            return Control(rtype, 6, (dtype << 8) | index, windex, length);
        }

        public void Enumerate()
        {
            BusReset();
            GetDescriptor(1, 0, 64, 0, 0x80);
            Control(0x00, 5, 7, 0, 0);
            dev = GetDescriptor(1, 0, 18, 0, 0x80);
            if (dev.Length != 18 || (dev[0] & 255) != 18 || (dev[1] & 255) != 1)
                throw new Stop("USB device descriptor must be 18 bytes");
            if ((dev[17] & 255) == 0) throw new Stop("USB device descriptor advertises no configurations");
            int packet = dev[7] & 255;
            if (packet != 8 && packet != 16 && packet != 32 && packet != 64) throw new Stop("Invalid USB endpoint zero max packet size");
            byte[] cfg9 = GetDescriptor(2, 0, 9, 0, 0x80);
            if (cfg9.Length != 9 || (cfg9[0] & 255) != 9 || (cfg9[1] & 255) != 2) throw new Stop("USB configuration header is missing or truncated");
            int total = (cfg9[2] & 255) | ((cfg9[3] & 255) << 8);
            if (total < 9) throw new Stop("USB configuration wTotalLength is shorter than its header");
            cfg = GetDescriptor(2, 0, total, 0, 0x80);
            configuration = UsbConfiguration.Parse(cfg);
            Control(0x00, 9, configuration.value, 0, 0);
            d.mc.ConfigureUsb(configuration);
        }
    }


    // ======================================================================================== board
    // What the chip is wired to. The emulator calls these; a board registers PC hooks with Machine.SetHook.
    public abstract class Board
    {
        public virtual void Attach(Machine mc) { }
        public virtual bool Hook(Machine mc, int id) { return false; }
        public virtual int Adc(Machine mc) { return 0; }
        public virtual void TimerEdge(Machine mc, int timerBase, bool on) { }
        public virtual int[] BootStops(Machine mc) { return new int[0]; }   // where a fast boot stops (e.g. a main loop)
    }

    public abstract class Job { public abstract void Run(); }

    public static class Mem
    {
        public static int[] FindAll(byte[] img, byte[] pat, int lo, int hi)
        {
            int[] tmp = new int[64];
            int n = 0;
            for (int i = lo; i + pat.Length <= img.Length && i < hi; i++)
            {
                bool ok = true;
                for (int k = 0; k < pat.Length; k++) if (img[i + k] != pat[k]) { ok = false; break; }
                if (ok && n < 64) tmp[n++] = 0x08000000 + i;
            }
            int[] outv = new int[n];
            P.Copy(tmp, outv, n);
            return outv;
        }
    }

    // ======================================================================================== devices
    // GigaDevice GD32F3x0 parts only. There is deliberately no STM32 part in this list, and no way to add one from the
    // command line: the memory map, ID registers and flash controller modelled here are the GD32F3x0 ones.
    public class Device
    {
        public string name;
        public int flashKb, sramKb, debugId;
        public Device(string n, int f, int s, int id) { name = n; flashKb = f; sramKb = s; debugId = id; }
        // GD32F3x0 memory map (user manual): 1 KB blocks unless noted
        public static readonly int[] PERIPH = new int[] {
            0x40000000, 0x40000400, 0x40001000, 0x40002000, 0x40002800, 0x40002C00, 0x40003000, 0x40003800, 0x40004400,
            0x40005400, 0x40005800, 0x40007000, 0x40007400, 0x40007800, 0x4000C800, 0x40010000, 0x40010400, 0x40012400,
            0x40012C00, 0x40013000, 0x40013800, 0x40014000, 0x40014400, 0x40014800, 0x40020000, 0x40021000, 0x40022000,
            0x40023000, 0x40024000, 0x48000000, 0x48000400, 0x48000800, 0x48000C00, 0x48001400 };
        public bool HasPeripheral(long ua)
        {
            if (ua >= 0xE0000000L && ua < 0xE0100000L) return true;            // Cortex-M4 private peripheral bus
            if (ua >= 0x50000000L && ua < 0x50040000L) return true;            // USBFS
            for (int i = 0; i < PERIPH.Length; i++) if (ua >= (PERIPH[i] & 0xFFFFFFFFL) && ua < (PERIPH[i] & 0xFFFFFFFFL) + 0x400) return true;
            return false;
        }
    }

    public static class Devices
    {
        public static readonly Device[] ALL = new Device[] {
            // Machine currently models 64 KB flash and 8 KB SRAM. CB variants require larger memory maps.
            new Device("GD32F350C8", 64, 8, 0x13030041),
            new Device("GD32F350G8", 64, 8, 0x13030041), new Device("GD32F350R8", 64, 8, 0x13030041),
            new Device("GD32F330C8", 64, 8, 0x13030041) };
        public static Device Default() { return ALL[0]; }
        public static Device Find(string name)
        {
            for (int i = 0; i < ALL.Length; i++) if (P.Eq(P.Lower(ALL[i].name), P.Lower(name))) return ALL[i];
            return null;
        }
    }

    // Refuses STM32 firmware. GD32 parts are register-compatible with many STM32 parts, so this cannot catch every
    // possible binary; it refuses anything carrying an ST fingerprint, and the strict GD32F3x0 memory map stops a
    // program that touches peripherals only STM32 parts have (STM32F1 GPIO at 0x40010800, STM32F0/F1 USB at 0x40005C00).
    public static class StmGuard
    {
        static bool Has(byte[] img, string s, bool wide)
        {
            int n = P.Len(s), step = wide ? 2 : 1;
            for (int i = 0; i + n * step <= img.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < n && ok; k++)
                {
                    int c = img[i + k * step] & 0xFF, e = P.CharAt(s, k);
                    if (c >= 'a' && c <= 'z') c -= 32;
                    if (c != e || (wide && img[i + k * step + 1] != 0)) ok = false;
                }
                if (ok) return true;
            }
            return false;
        }

        public static string Check(byte[] img)
        {
            string[] marks = new string[] { "STM32", "STMICROELECTRONICS", "STMICRO" };
            for (int i = 0; i < marks.Length; i++)
                if (Has(img, marks[i], false) || Has(img, marks[i], true)) return "refused: this image carries an STMicroelectronics fingerprint (\"" + marks[i] + "\"). This emulator runs GigaDevice GD32 firmware only.";
            for (int i = 0; i + 18 <= img.Length; i++)
                if ((img[i] & 0xFF) == 0x12 && (img[i + 1] & 0xFF) == 0x01 && (img[i + 8] & 0xFF) == 0x83 && (img[i + 9] & 0xFF) == 0x04)
                    return "refused: this image has a USB device descriptor with STMicroelectronics' vendor ID 0483. This emulator runs GigaDevice GD32 firmware only.";
            int[] stOnly = new int[] { 0x1FFFF7CC, 0x1FFF7A22, 0x1FFF75E0, 0x1FFF7590, 0x40005C00 };
            for (int i = 0; i + 4 <= img.Length; i += 4)
                for (int k = 0; k < stOnly.Length; k++)
                    if (Machine.LE(img, i, 4) == stOnly[k]) return "refused: this image reads " + F.Hx(stOnly[k], 8) + ", an address that only exists on STM32 parts. This emulator runs GigaDevice GD32 firmware only.";
            return null;
        }
    }
}
