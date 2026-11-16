using System.IO.Compression;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>
/// Model of the maker's K13 (header DA 1E), v5.2 report 16.11.2026: «Проходы чтения расходятся по адресу 0x0000 (BA ≠ FF)».
/// The first byte read after a load that follows 07 18 01 (end of a pass) still comes from the previous buffer.
/// </summary>
sealed class StaleFirstReadK13(byte[] mem) : IHidFeatureTransport
{
    byte[] _buf = Enumerable.Repeat((byte)0xFF, 8).ToArray();
    byte[]? _pending;
    bool _afterEnd = true;
    byte _resp;
    public List<byte[]> Frames { get; } = new();
    public byte[] Memory => mem;

    public bool SetFeature(byte[] r)
    {
        Frames.Add((byte[])r.Clone());
        if (r[1] != 0x18) return true;
        switch (r[2])
        {
            case 0x00:
                int a = r[4] | r[5] << 8;
                var data = Enumerable.Range(0, 8).Select(i => a + i < mem.Length ? mem[a + i] : (byte)0xFF).ToArray();
                if (_afterEnd) { _pending = data; _afterEnd = false; } else { _buf = data; _pending = null; }
                break;
            case 0x05:
                _resp = _buf[r[3] & 7];
                if (_pending != null) { _buf = _pending; _pending = null; } // slow firmware: load lands after the first answer
                break;
            case 0x01: _afterEnd = true; break;
        }
        return true;
    }

    public bool GetFeature(byte[] b) { b[1] = _resp; for (int i = 2; i < b.Length; i++) b[i] = 0; if (b.Length > 15) { b[14] = 0xE0; b[15] = 0; } return true; }
    public void Dispose() { }
}

/// <summary>Answers randomly for one address — a pass mismatch that extra passes cannot settle.</summary>
sealed class NoisyK13(FakeK13 inner, int addr) : IHidFeatureTransport
{
    int _hits, _block; bool _on;
    public bool SetFeature(byte[] r)
    {
        if (r[1] == 0x18 && r[2] == 0x00) _block = r[4] | r[5] << 8;
        if (r[1] == 0x18 && r[2] == 0x05) { _on = _block + (r[3] & 7) == addr; if (_on) _hits++; }
        return inner.SetFeature(r);
    }
    public bool GetFeature(byte[] b) { var ok = inner.GetFeature(b); if (_on) b[1] = (byte)(0x11 * _hits); return ok; }
    public void Dispose() { }
}

public class V564Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static byte[] MakersUnit() { var m = Img("exp14-macro.bin"); m[0] = 0xBA; return m; }
    static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "k13v564-" + Guid.NewGuid().ToString("N")); return d; }

    [Fact]
    public void Old_read_without_warm_up_reproduces_BA_vs_FF_at_0x0000()
    {
        var fake = new StaleFirstReadK13(MakersUnit());
        using var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 1, WarmUpRead = false };
        var a = dev.ReadMemory(0, 0x40);
        var b = dev.ReadMemory(0, 0x40);
        Assert.Equal(0xFF, a[0]);                 // first pass: buffer still FF from power-up
        Assert.NotEqual(0xBA, b[0]);              // second pass: still the last block of pass 1
        Assert.Equal(MakersUnit().Skip(1).Take(0x3F), a.Skip(1));
    }

    [Fact]
    public void Warm_up_read_gives_identical_passes_on_the_makers_unit()
    {
        var mem = MakersUnit();
        var fake = new StaleFirstReadK13(mem);
        using var dev = new K13Device(fake) { DelayMs = 0 };
        var img = dev.ReadImageVerified();
        Assert.Equal(mem.Take(img.Length), img);
        Assert.Equal(0xBA, img[0]);
        Assert.Equal("", dev.LastReadReport);
        Assert.All(fake.Frames, f => Assert.True(K13Protocol.IsReadOnlyFrame(f) || K13Protocol.IsSessionEndFrame(f), BitConverter.ToString(f)));
    }

    [Fact]
    public void Defaults_follow_vendor_timing()
    {
        using var dev = new K13Device(new FakeK13(new byte[0x300]));
        Assert.True(dev.DelayMs >= 1);
        Assert.True(dev.LoadDelayMs >= 2);
        Assert.True(dev.WarmUpRead);
    }

    [Fact]
    public void Mismatch_is_settled_by_majority_vote()
    {
        var mem = MakersUnit();
        var p1 = mem.Take(0x40).ToArray(); var p2 = (byte[])p1.Clone(); p2[0] = 0xFF;   // exactly the reported pair
        var calls = 0;
        var (img, report) = K13Device.Reconcile(p1, p2, blocks => { calls++; return blocks.Select(b => mem.Skip(b.Addr).Take(b.Len).ToArray()).ToList(); });
        Assert.Equal(0xBA, img[0]);
        Assert.Equal(1, calls);
        Assert.Contains("0x0000: BA/FF/BA", report);
        // stale value in pass 1 instead → still the real one
        var (img2, _) = K13Device.Reconcile(p2, p1, blocks => blocks.Select(b => mem.Skip(b.Addr).Take(b.Len).ToArray()).ToList());
        Assert.Equal(0xBA, img2[0]);
    }

    [Fact]
    public void Unsettled_mismatch_throws_with_both_passes_and_saves_diagnostics()
    {
        var p1 = new byte[16]; var p2 = new byte[16]; p1[3] = 0xBA; p2[3] = 0xFF;
        byte n = 0;
        var ex = Assert.Throws<K13ReadMismatchException>(() => K13Device.Reconcile(p1, p2, blocks => blocks.Select(b => { var x = new byte[b.Len]; x[3] = ++n; return x; }).ToList()));
        Assert.Equal(new[] { 3 }, ex.Addresses);
        Assert.Contains("0x0003", ex.Message);
        Assert.Contains("Сохранить диагностику", ex.Message);
        var dir = TempDir();
        try
        {
            var files = BackupStore.SaveReadPasses(ex, dir);
            Assert.Contains(files, f => Path.GetFileName(f).StartsWith("k13-readpass-A-") && f.EndsWith(".bin"));
            Assert.Contains(files, f => Path.GetFileName(f).StartsWith("k13-readpass-B-") && f.EndsWith(".bin"));
            Assert.Equal(p1, File.ReadAllBytes(files[0]));
            Assert.Equal(p2, File.ReadAllBytes(files[1]));
            var log = Path.Combine(dir, "studio.log"); File.WriteAllText(log, "12:00 [k13] test\n");
            BackupStore.Save(new byte[8], dir, "k13-prewrite");
            var zip = DiagnosticsBundle.Create(Path.Combine(dir, "out", "diag.zip"), new[] { log, Path.Combine(dir, "missing.log") }, dir, "VOROTEX K13 Studio v5.6.4");
            using var z = ZipFile.OpenRead(zip);
            var names = z.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("logs/studio.log", names);
            Assert.Contains("info.txt", names);
            Assert.Contains(names, x => x.StartsWith("dumps/k13-readpass-A-"));
            Assert.Contains(names, x => x.StartsWith("dumps/k13-readpass-B-"));
            Assert.Contains(names, x => x.StartsWith("dumps/k13-readpass-") && x.EndsWith(".txt") && !x.EndsWith(".hex.txt"));
            Assert.Contains(names, x => x.StartsWith("dumps/k13-prewrite-"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Writer_on_unreadable_unit_writes_nothing_and_keeps_both_passes()
    {
        var mem = Img("exp14-macro.bin"); var orig = (byte[])mem.Clone();
        var inner = new FakeK13(mem) { Writable = true };
        var noisy = new NoisyK13(inner, 0x0000);
        var dev = new K13Device(noisy) { DelayMs = 0, LoadDelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(orig); cfg.SetKey(3, 0x3B);
        var dir = TempDir();
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var lines = new List<string>();
            var ex = Assert.Throws<K13ReadMismatchException>(() => K13Writer.WriteProfile(dev, cfg, lines.Add, null, false, dir));
            Assert.Equal(orig, mem);
            Assert.DoesNotContain(inner.Frames, f => f[1] == 0x18 && (f[2] == 0x03 || f[2] == 0x09));
            Assert.True(ex.SavedFiles.Count >= 2);
            Assert.All(ex.SavedFiles, f => Assert.True(File.Exists(f)));
            Assert.Contains(lines, l => l.Contains("k13-readpass-A"));
        }
        finally { K13WriteGate.Lock(); dev.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Backup_folder_is_created_up_front()
    {
        var dir = TempDir();
        try { Assert.Equal(dir, BackupStore.EnsureFolder(dir)); Assert.True(Directory.Exists(dir)); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void F14_to_F24_are_marked_as_not_sent_by_the_firmware()
    {
        Assert.True(KeyCatalog.FirmwareSends(0x68));                      // F13 — stick, proven 13.11.2026
        for (byte c = 0x69; c <= 0x73; c++) Assert.False(KeyCatalog.FirmwareSends(c), KeyCatalog.Name(c));
        Assert.True(KeyCatalog.FirmwareSends(0x3A));
        Assert.True(KeyCatalog.FirmwareSends(0x47));
        Assert.True(KeyCatalog.FirmwareSends(KeyCatalog.FirstMacro));
    }
}
