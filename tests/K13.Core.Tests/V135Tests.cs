using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>1.3.5: every read session ends with the full session end 07 18 11 / 07 18 00 / 07 20 — also on failure.
/// 1.3.4 ended the model check with 07 18 01 only and both K13 and K12 stopped typing until replug (makers, 19.11.2026).</summary>
public class V135Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static readonly byte[][] End = K13Protocol.SessionEndFrames.Select(f => f.Data).ToArray();

    static void EndsWithSessionEnd(IReadOnlyList<byte[]> frames)
    {
        Assert.True(frames.Count >= 3);
        var tail = frames.Skip(frames.Count - 3).ToArray();
        for (int i = 0; i < 3; i++) Assert.Equal(Convert.ToHexString(End[i]), Convert.ToHexString(tail[i]));
    }

    [Fact]
    public void Session_end_is_11_00_20()
    {
        Assert.Equal(new[] { "0718110000000000", "0718000000000000", "0720000000000000" }, End.Select(Convert.ToHexString));
    }

    public static IEnumerable<object[]> ReadPaths() => new[]
    {
        new object[] { "проверка модели (ReadSignature)" },
        new object[] { "ReadBlocks + Dispose (индикатор, «Запоминать режим», быстрая проверка)" },
        new object[] { "ReadMemory + Dispose" },
        new object[] { "ReadImageVerified + Dispose («Прочитать», резервная копия)" },
        new object[] { "ReadBlocksAfterWrite + Dispose (проверка после записи)" },
    };

    static void Run(string path, K13Device dev)
    {
        switch (path[..path.IndexOf(' ')])
        {
            case "проверка": K13Model.ReadSignature(dev); break;
            case "ReadBlocks": dev.ReadBlocks(K13Model.SignatureBlocks); break;
            case "ReadMemory": dev.ReadMemory(0, 0x40); break;
            case "ReadImageVerified": dev.ReadImageVerified(); break;
            case "ReadBlocksAfterWrite": dev.SettleAfterWriteMs = 0; dev.ReadBlocksAfterWrite(new[] { (0x0008, 8) }); break;
            default: throw new ArgumentException(path);
        }
    }

    [Theory, MemberData(nameof(ReadPaths))]
    public void Every_read_path_ends_with_the_full_session_end(string path)
    {
        var fake = new FakeK13(Img("k13-backup-20261112-171754.bin"));
        using (var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 }) Run(path, dev);
        EndsWithSessionEnd(fake.Frames);
        Assert.False(K13Device.SessionLeftOpen, path);
    }

    [Theory, MemberData(nameof(ReadPaths))]
    public void Every_read_path_ends_the_session_when_the_read_fails(string path)
    {
        // the keypad stops answering read requests half-way (07 18 05 fails), session-end frames still go through
        var fake = new FlakyK13(new FakeK13(Img("k13-backup-20261112-171754.bin"))) { FailWhen = r => r[1] == 0x18 && r[2] == 0x05 };
        using (var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 })
            Assert.ThrowsAny<Exception>(() => Run(path, dev));
        EndsWithSessionEnd(fake.Inner.Frames);
        Assert.False(K13Device.SessionLeftOpen, path);
    }

    [Fact]
    public void A_session_left_open_is_flagged_until_a_session_end()
    {
        K13Device.ForgetOpenSession();
        var fake = new FakeK13(Img("k13-backup-20261112-171754.bin"));
        using (var dev = new K13Device(fake) { DelayMs = 0, EndSessionOnDispose = false }) dev.ReadBlocks(K13Model.SignatureBlocks);
        Assert.True(K13Device.SessionLeftOpen);
        using (var dev = new K13Device(fake)) dev.EndSession();   // what Studio sends on the next poll / on exit
        Assert.False(K13Device.SessionLeftOpen);
        EndsWithSessionEnd(fake.Frames);
    }

    [Fact]
    public void Unplugged_during_the_end_keeps_the_flag()
    {
        K13Device.ForgetOpenSession();
        var fake = new FlakyK13(new FakeK13(Img("k13-backup-20261112-171754.bin"))) { FailWhen = r => r[1] == 0x18 && r[2] == 0x11 || r[1] == 0x20 };
        using (var dev = new K13Device(fake) { DelayMs = 0 }) K13Model.ReadSignature(dev);
        Assert.True(K13Device.SessionLeftOpen);
        K13Device.ForgetOpenSession();
    }

    [Fact]
    public void Status_only_is_not_a_session()
    {
        K13Device.ForgetOpenSession();
        var fake = new FakeK13(Img("k13-backup-20261112-171754.bin"));
        using (var dev = new K13Device(fake)) dev.GetStatus();
        Assert.Empty(fake.Frames);
        Assert.False(K13Device.SessionLeftOpen);
    }

    /// <summary>Only the bisect test may switch the session end off (it ends the session itself in finally).</summary>
    [Fact]
    public void Studio_switches_the_session_end_off_only_in_the_bisect_test()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "K13.Studio"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var hits = Directory.GetFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllLines(f).Any(l => !l.TrimStart().StartsWith("//") && l.Contains("EndSessionOnDispose = false")))
            .Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "MainWindow.Activate.cs" }, hits);
    }
}
