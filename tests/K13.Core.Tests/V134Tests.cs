using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>1.3.4: read-only 24-byte model check on connect; only K13 images are reused.</summary>
public class V134Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static List<byte[]> Sig(byte[] img) => K13Model.SignatureBlocks.Select(b => img.Skip(b.Addr).Take(b.Len).ToArray()).ToList();

    [Fact]
    public void Signature_is_24_bytes_in_three_blocks()
    {
        Assert.Equal(new[] { 0x48, 0x80, 0xA0 }, K13Model.SignatureBlocks.Select(b => (int)b.Addr));
        Assert.Equal(24, K13Model.SignatureBlocks.Sum(b => b.Len));
    }

    [Fact]
    public void K13_dump_is_K13()
    {
        var s = Sig(Img("k13-backup-20261112-171754.bin"));
        Assert.Equal(new byte[] { 0x5E, 0x50, 0xF4 }, s.Select(b => b[0]));
        Assert.Equal(KeypadModel.K13, K13Model.DetectSignature(s));
        Assert.Equal(KeypadModel.K13, K13Model.DetectSignature(Sig(Img("exp15-postwrite-20261113-133715.bin"))));
    }

    [Fact]
    public void K12_dumps_are_K12()
    {
        foreach (var f in new[] { "k12-reset-20261119-094139.bin", "k12-apply-20261119-095032.bin" })
        {
            var s = Sig(Img(f));
            Assert.Equal(new byte[] { 0x58, 0xF4, 0xFF }, s.Select(b => b[0]));
            Assert.Equal(KeypadModel.K12Likely, K13Model.DetectSignature(s));
            Assert.False(K13Model.WriteAllowed(K13Model.DetectSignature(s)));
        }
    }

    [Fact]
    public void Anything_else_is_unknown()
    {
        Assert.Equal(KeypadModel.Unknown, K13Model.DetectSignature(null));
        Assert.Equal(KeypadModel.Unknown, K13Model.DetectSignature(new List<byte[]>()));
        var s = Sig(Img("k13-backup-20261112-171754.bin"));
        Assert.Equal(KeypadModel.Unknown, K13Model.DetectSignature(s.Take(2).ToList()));
        var t = s.Select(b => (byte[])b.Clone()).ToList(); t[1][3] = 0x00;
        Assert.Equal(KeypadModel.Unknown, K13Model.DetectSignature(t));
        var u = s.Select(b => (byte[])b.Clone()).ToList(); u[2][0] = 0x12;
        Assert.Equal(KeypadModel.Unknown, K13Model.DetectSignature(u));
        Assert.Equal(KeypadModel.Unknown, K13Model.DetectSignature(new List<byte[]> { new byte[8], new byte[8], new byte[8] }));
        Assert.Equal(KeypadModel.Unknown, K13Model.DetectSignature(s.Select(b => b.Take(7).ToArray()).ToList()));
    }

    [Fact]
    public void Last_known_image_is_a_K13_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "v134-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(K13Model.LastKnownK13(dir));
            Assert.Null(K13Model.LastKnownK13(Path.Combine(dir, "missing")));
            var k13 = Path.Combine(dir, "k13.bin"); File.WriteAllBytes(k13, Img("k13-backup-20261112-171754.bin"));
            File.SetLastWriteTimeUtc(k13, DateTime.UtcNow.AddHours(-2));
            var k12 = Path.Combine(dir, "k12.bin"); File.WriteAllBytes(k12, Img("k12-reset-20261119-094139.bin"));
            File.SetLastWriteTimeUtc(k12, DateTime.UtcNow);   // newest, but a K12
            var pre = Path.Combine(dir, "x-prewrite.bin"); File.WriteAllBytes(pre, Img("k13-backup-20261112-171754.bin"));
            var r = K13Model.LastKnownK13(dir);
            Assert.NotNull(r);
            Assert.Equal(k13, r!.Value.File);
            File.Delete(k13);
            Assert.Null(K13Model.LastKnownK13(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void K12_tester_names_without_an_image()
    {
        // model known from the connect check, memory not read: the factory K12 row is used
        Assert.NotEmpty(K12Layout.ControlsFor(0x61, HwMode.Normal, null));
    }

    [Fact]
    public void Connect_check_sends_read_frames_only_and_no_session_end()
    {
        foreach (var (f, want) in new[] { ("k13-backup-20261112-171754.bin", KeypadModel.K13), ("k12-reset-20261119-094139.bin", KeypadModel.K12Likely) })
        {
            var mem = Img(f); var before = (byte[])mem.Clone();
            var fake = new FakeK13(mem);
            using (var dev = new K13Device(fake) { DelayMs = 0 })
            {
                var r = K13Model.ReadSignature(dev);
                Assert.Equal(want, r.Model);
                Assert.Equal(24, r.Blocks.Sum(b => b.Length));
            }   // Dispose: nothing more may be sent
            Assert.All(fake.Frames, fr => Assert.True(fr[0] == 7 && fr[1] == 0x18 && fr[2] is 0x00 or 0x05 or 0x01, Convert.ToHexString(fr)));
            Assert.DoesNotContain(fake.Frames, fr => fr[2] == 0x11 || fr[1] == 0x20);
            Assert.Equal(0x01, fake.Frames[^1][2]);   // ends with 07 18 01 (end read)
            Assert.True(fake.Frames.Count < 40, "кадров: " + fake.Frames.Count);
            Assert.Equal(before, mem);
        }
    }

    [Fact]
    public void Connect_check_failure_is_an_exception_not_a_model()
    {
        var fake = new FlakyAlways();
        using var dev = new K13Device(fake) { DelayMs = 0 };
        Assert.ThrowsAny<IOException>(() => K13Model.ReadSignature(dev));
    }

    sealed class FlakyAlways : IHidFeatureTransport
    {
        public bool SetFeature(byte[] r) => false;
        public bool GetFeature(byte[] b) => false;
        public void Dispose() { }
    }
}
