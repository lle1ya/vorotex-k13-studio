using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vorotex.K13;

/// <summary>Abstraction over a HID feature-report endpoint (lets tests use a fake device).</summary>
public interface IHidFeatureTransport : IDisposable
{
    bool SetFeature(byte[] report);
    bool GetFeature(byte[] buffer);
}

public sealed class WriteBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// Global write gate. Writing is compiled IN since 0.4.3 (the user approved live writes after exp15/exp16), but every
/// write still needs an explicit runtime unlock (the app asks once for confirmation). Read paths never write.
/// </summary>
public static class K13WriteGate
{
    public const bool WritesCompiledIn = true;
    public const string ConfirmationPhrase = "Я ПОДТВЕРЖДАЮ ЗАПИСЬ";
    private static bool _unlocked;

    public static bool IsOpen => WritesCompiledIn && _unlocked;

    public static void Unlock(string phrase)
    {
#pragma warning disable CS0162
        if (!WritesCompiledIn) throw new WriteBlockedException("Запись на клавиатуру отключена в этой сборке (только чтение). Требуется подтверждение первого теста записи.");
        if (phrase != ConfirmationPhrase) throw new WriteBlockedException("Неверная фраза подтверждения.");
        _unlocked = true;
#pragma warning restore CS0162
    }

    public static void Lock() => _unlocked = false;
}

public sealed record K13Status(byte[] Raw)
{
    public int Base => Raw.Length > 15 ? Raw[14] | Raw[15] << 8 : -1;
    public string Hex => string.Join(" ", Raw.Select(b => b.ToString("X2")));
}

/// <summary>VOROTEX K13 vendor channel (COL07, UsagePage 0xFF01, Report ID 7). Read-only unless the write gate is open.</summary>
public sealed class K13Device : IDisposable
{
    private readonly IHidFeatureTransport _t;
    private readonly object _lock = new();
    public string Path { get; }
    /// <summary>Pause after each read-byte request, ms (precise, not Thread.Sleep: Sleep(2) on Windows lasts ~15.6 ms).</summary>
    public double DelayMs { get; set; } = 0;
    /// <summary>Pause after "load block" (07 18 00), ms.</summary>
    public double LoadDelayMs { get; set; } = 1;
    /// <summary>Scale for the vendor write pauses (last byte 2 ms, commit 10 ms, finish 10 ms). 1 = vendor values.</summary>
    public double WritePauseScale { get; set; } = 1;
    /// <summary>Old behaviour (Thread.Sleep with OS timer granularity) – only for benchmarks.</summary>
    public bool LegacySleep { get; set; }
    /// <summary>Number of HID calls that failed and were retried (diagnostics).</summary>
    public int Retries { get; private set; }
    /// <summary>Every frame sent to the device (for diagnostics / tests).</summary>
    public List<byte[]> SentLog { get; } = new();
    /// <summary>Send <see cref="K13Protocol.SessionEndFrames"/> on Dispose when anything was sent (default on). Without it the keypad stops typing until replug.</summary>
    public bool EndSessionOnDispose { get; set; } = true;
    private bool _touched, _ended;

    private readonly Mutex? _mutex;
    private readonly int _mutexThread;

    /// <summary>Name of the cross-process lock held for every HID session (app, tools).</summary>
    public const string MutexName = "Global\\VorotexK13Device";

    public K13Device(IHidFeatureTransport transport, string path = "", int lockTimeoutMs = 60000)
    {
        _t = transport; Path = path;
        if (transport is WindowsHidTransport)
        {   // one HID session at a time across all processes (app instances, k13write…)
            try { _mutex = new Mutex(false, MutexName); }
            catch (UnauthorizedAccessException) { _mutex = new Mutex(false, "Local\\VorotexK13Device"); }
            bool got;
            try { got = _mutex.WaitOne(lockTimeoutMs); } catch (AbandonedMutexException) { got = true; }
            if (!got) { _mutex.Dispose(); _t.Dispose(); throw new IOException("Клавиатура занята другой программой (идёт чтение/запись). Повторите через минуту."); }
            _mutexThread = Environment.CurrentManagedThreadId;
        }
    }

    public static K13Device? OpenFirst()
    {
        foreach (var p in WindowsHid.FindVendorInterfaces())
        {
            var t = WindowsHidTransport.TryOpen(p);
            if (t != null) return new K13Device(t, p);
        }
        return null;
    }

    private void Send(byte[] frame)
    {
        bool readOnly = K13Protocol.IsReadOnlyFrame(frame) || K13Protocol.IsSessionEndFrame(frame);
        if (!readOnly && !K13WriteGate.IsOpen)
            throw new WriteBlockedException($"Команда {string.Join(" ", frame.Select(b => b.ToString("X2")))} заблокирована: режим только чтения.");
        SentLog.Add((byte[])frame.Clone());
        _touched = true; _ended = false;
        for (int attempt = 0; ; attempt++)
        {
            if (_t.SetFeature(frame)) return;
            if (attempt >= 3) throw new IOException("HidD_SetFeature не удалось (устройство отключено?).");
            Retries++;
            Pause(5 + attempt * 10);
        }
    }

    private void Pause(double ms)
    {
        if (ms <= 0) return;
        if (LegacySleep) { Thread.Sleep((int)Math.Ceiling(ms)); return; }
        PreciseDelay(ms);
    }

    /// <summary>Precise short delay: Thread.Sleep for the bulk (when long), then spin.</summary>
    public static void PreciseDelay(double ms)
    {
        if (ms <= 0) return;
        long end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        if (ms > 25) Thread.Sleep((int)ms - 18);
        while (System.Diagnostics.Stopwatch.GetTimestamp() < end) Thread.SpinWait(20);
    }

    public K13Status GetStatus()
    {
        lock (_lock)
        {
            var buf = new byte[21]; buf[0] = K13Layout.ReportId;
            if (!_t.GetFeature(buf))
            {
                buf = new byte[16]; buf[0] = K13Layout.ReportId;
                if (!_t.GetFeature(buf)) throw new IOException("HidD_GetFeature не удалось.");
            }
            return new K13Status(buf);
        }
    }

    /// <summary>Reads memory using only load(07 18 00) / read(07 18 05)+GetFeature / end(07 18 01).</summary>
    public byte[] ReadMemory(int start, int length, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var o = new byte[length];
            try
            {
                for (int off = 0; off < length; off += 8)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(8, length - off);
                    ReadBlockInto(start + off, n, o, off);
                    progress?.Report((double)(off + n) / length);
                }
            }
            finally
            {
                try { Pause(1); Send(K13Protocol.EndRead()); } catch (IOException) { }
            }
            return o;
        }
    }

    /// <summary>Reads only the given blocks (address, length ≤ 8 each) – one pass. Returns them in the same order.</summary>
    public List<byte[]> ReadBlocks(IReadOnlyList<(int Addr, int Len)> blocks, IProgress<double>? progress = null)
    {
        lock (_lock)
        {
            var res = new List<byte[]>();
            try
            {
                for (int k = 0; k < blocks.Count; k++)
                {
                    var (a, n) = blocks[k];
                    var buf = new byte[n];
                    ReadBlockInto(a, n, buf, 0);
                    res.Add(buf);
                    progress?.Report((k + 1.0) / blocks.Count);
                }
            }
            finally
            {
                try { Pause(1); Send(K13Protocol.EndRead()); } catch (IOException) { }
            }
            return res;
        }
    }

    private void ReadBlockInto(int a, int n, byte[] o, int off)
    {
        if (n is < 1 or > 8) throw new ArgumentException("Блок чтения 1..8 байт.");
        Send(K13Protocol.LoadBlock(a));
        Pause(LegacySleep ? Math.Max(1, DelayMs) : LoadDelayMs);
        for (int i = 0; i < n; i++)
        {
            bool ok = false;
            for (int t = 0; t < 4 && !ok; t++)
            {
                if (t > 0) { Retries++; Pause(5 * t); if (t == 2) { Send(K13Protocol.LoadBlock(a)); Pause(Math.Max(2, LoadDelayMs)); } }
                Send(K13Protocol.ReadByte(a, i, n));
                Pause(DelayMs);
                var r = new byte[8]; r[0] = K13Layout.ReportId;
                if (!_t.GetFeature(r)) continue;
                o[off + i] = r[1]; ok = true;
            }
            if (!ok) throw new IOException($"Чтение не удалось по адресу 0x{a + i:X4}.");
        }
    }

    /// <summary>Reads the config image twice and compares. Size grows to cover macro bodies.</summary>
    public byte[] ReadImageVerified(IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken ct = default)
    {
        int len = K13Layout.MinImageLength;
        byte[] Pass(int n, string stage) => ReadMemory(0, n, progress == null ? null : new Progress<double>(f => progress.Report((stage, f))), ct);
        var p1 = Pass(len, "Чтение (проход 1)");
        int used = K13Layout.Align8(K13MemoryCodec.UsedEnd(p1));
        if (used > len)
        {
            var ext = ReadMemory(len, used - len, null, ct);
            p1 = p1.Concat(ext).ToArray(); len = used;
        }
        var p2 = Pass(len, "Чтение (проход 2, проверка)");
        for (int i = 0; i < len; i++)
            if (p1[i] != p2[i]) throw new IOException($"Проходы чтения расходятся по адресу 0x{i:X4} ({p1[i]:X2} ≠ {p2[i]:X2}). Повторите чтение.");
        return p1;
    }

    /// <summary>Vendor-equivalent Apply. Throws <see cref="WriteBlockedException"/> unless the write gate is open (it is compiled out in this build).</summary>
    public void Apply(IReadOnlyList<WriteBlock> plan, IProgress<double>? progress = null, bool programmingMode = false)
    {
        if (!K13WriteGate.IsOpen) throw new WriteBlockedException("Запись на клавиатуру не подтверждена.");
        var frames = K13Protocol.BuildApplyFrames(plan, programmingMode);
        lock (_lock)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                Send(frames[i].Data);
                if (frames[i].SleepAfterMs > 0) Pause(LegacySleep ? frames[i].SleepAfterMs : frames[i].SleepAfterMs * WritePauseScale);
                progress?.Report((i + 1.0) / frames.Count);
            }
        }
    }

    /// <summary>Leaves the vendor channel the way the vendor app does (see <see cref="K13Protocol.SessionEndFrames"/>). Safe to call repeatedly.</summary>
    public void EndSession()
    {
        lock (_lock)
        {
            foreach (var f in K13Protocol.SessionEndFrames)
            {
                Send(f.Data);
                Pause(f.SleepAfterMs);
            }
            _ended = true;
        }
    }

    public void Dispose()
    {
        if (EndSessionOnDispose && _touched && !_ended)
        {
            try { EndSession(); } catch (Exception) { /* device unplugged – nothing to end */ }
        }
        _t.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
    }
}

/// <summary>Windows HID helpers (hid.dll / setupapi.dll).</summary>
public static class WindowsHid
{
    public static IEnumerable<string> FindVendorInterfaces()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var p in Native.EnumerateHidPaths())
        {
            var lp = p.ToLowerInvariant();
            if (!lp.Contains("vid_30fa") || !lp.Contains("pid_2350")) continue;
            if (Native.TryGetCaps(p, out var up, out var u, out var feat) && up == K13Layout.VendorUsagePage && u == K13Layout.VendorUsage && feat >= 8)
                yield return p;
        }
    }

    public static bool IsPresent() => FindVendorInterfaces().Any();

    internal static class Native
    {
        private static readonly Guid HidGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");
        private const int DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, int f);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, ref HIDP_CAPS caps);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

        public static IEnumerable<string> EnumerateHidPaths()
        {
            var g = HidGuid;
            var set = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                    var buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, req, out _, IntPtr.Zero)) continue;
                        var path = Marshal.PtrToStringUni(buf + 4);
                        if (path != null) yield return path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        public static bool TryGetCaps(string path, out ushort usagePage, out ushort usage, out int featureLen)
        {
            usagePage = usage = 0; featureLen = 0;
            using var h = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return false;
            if (!HidD_GetPreparsedData(h, out var pp)) return false;
            try
            {
                var caps = new HIDP_CAPS();
                if (HidP_GetCaps(pp, ref caps) != 0x00110000) return false;
                usagePage = caps.UsagePage; usage = caps.Usage; featureLen = caps.FeatureReportByteLength;
                return true;
            }
            finally { HidD_FreePreparsedData(pp); }
        }
    }
}

public sealed class WindowsHidTransport : IHidFeatureTransport
{
    private readonly SafeFileHandle _h;
    private WindowsHidTransport(SafeFileHandle h) => _h = h;

    public static WindowsHidTransport? TryOpen(string path)
    {
        var h = WindowsHid.Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new WindowsHidTransport(h);
    }

    public bool SetFeature(byte[] report) => WindowsHid.Native.HidD_SetFeature(_h, report, report.Length);
    public bool GetFeature(byte[] buffer) => WindowsHid.Native.HidD_GetFeature(_h, buffer, buffer.Length);
    public void Dispose() => _h.Dispose();
}
