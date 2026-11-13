using System.Text;

namespace Vorotex.K13;

/// <summary>Minimal INI reader (sections are case-insensitive, later keys win).</summary>
public sealed class IniFile
{
    public Dictionary<string, Dictionary<string, string>> Sections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Load(string path)
    {
        Encoding enc;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); enc = Encoding.GetEncoding(1251); }
        catch { enc = Encoding.Latin1; }
        return Parse(File.ReadAllText(path, enc));
    }

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Dictionary<string, string>? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!ini.Sections.TryGetValue(name, out cur)) ini.Sections[name] = cur = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || cur == null) continue;
            cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return ini;
    }

    public string? Get(string section, string key) =>
        Sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public int? GetInt(string section, string key) =>
        int.TryParse(Get(section, key), out var v) ? v : null;
}

public sealed record VendorProfile(int Number, string Name, bool IsCurrent, K13DeviceConfig Config, List<string> Notes);

/// <summary>Imports profiles from the vendor app's config\config.ini (PROTOCOL.md §11.2).</summary>
public static class VendorIniImporter
{
    /// <summary>Lists all ConfigN sections. Each profile is merged onto a clone of <paramref name="template"/>
    /// (normally the last config read from the device), because the INI has no flags/X nibbles and stores macro
    /// bodies elsewhere (macro\*.dat): those are kept from the template.</summary>
    public static List<VendorProfile> Import(string path, K13DeviceConfig template) => Import(IniFile.Load(path), template);

    public static List<VendorProfile> Import(IniFile ini, K13DeviceConfig template)
    {
        var list = new List<VendorProfile>();
        int cur = ini.GetInt("Init", "CurConfig") ?? 1;
        for (int n = 1; n <= 5; n++)
        {
            if (!ini.Sections.ContainsKey("Config" + n)) continue;
            var name = (ini.Get("Init", "ConfigName" + n) ?? "Профиль " + n).Trim();
            var notes = new List<string>();
            var cfg = ApplyTo(ini, n, template.Clone(), notes);
            list.Add(new VendorProfile(n, name, n == cur, cfg, notes));
        }
        return list;
    }

    public static K13DeviceConfig ApplyTo(IniFile ini, int n, K13DeviceConfig cfg, List<string>? notes = null)
    {
        string sec = "Config" + n, led = $"Config{n}_LED_COLOR";
        foreach (var c in K13Layout.Controls)
        {
            if (c.KuIndex is not int ku) continue;
            var v = ini.GetInt(sec, "KU_" + ku);
            if (v is null) continue;
            cfg.SetKey(c.Column, v == 0 ? c.FactoryCode : (byte)v.Value);
        }
        for (int i = 0; i < 12; i++)
            if (ini.GetInt(sec, "UFN_F" + (i + 1)) is int f) cfg.Fn[i] = f == 0 ? (byte)0xFF : (byte)f;
        if (ini.GetInt(sec, "USCROLL0") is int w0) cfg.WheelFirst = w0 & 15;
        if (ini.GetInt(sec, "USCROLL1") is int w1) cfg.WheelSecond = w1 & 15;

        if (ini.Sections.ContainsKey(led))
        {
            var l = cfg.Led;
            for (int i = 0; i < 7; i++)
            {
                if (ini.GetInt(led, "LED_COL_R" + (i + 1)) is int r) l.Colors[i].R = Math.Clamp(r, 0, 15);
                if (ini.GetInt(led, "LED_COL_G" + (i + 1)) is int g) l.Colors[i].G = Math.Clamp(g, 0, 15);
                if (ini.GetInt(led, "LED_COL_B" + (i + 1)) is int b) l.Colors[i].B = Math.Clamp(b, 0, 15);
                // the vendor ini stores the raw device values (same bytes as in memory, exp11/12) → logical colours
                var (lr, lg, lb) = K13MemoryCodec.ColorMap.ToLogical(l.Colors[i].R, l.Colors[i].G, l.Colors[i].B);
                l.Colors[i].R = lr; l.Colors[i].G = lg; l.Colors[i].B = lb;
            }
            if (ini.GetInt(led, "HR_DIRECTION") is int d) l.Direction = d & 3;
            if (ini.GetInt(led, "HR_BRIGHT") is int br) l.Brightness = br & 7;
            if (ini.GetInt(led, "HR_SPEED") is int sp) l.Speed = sp & 7;
            if (ini.GetInt(led, "HR_SYMMETRY") is int sy) l.Symmetry = sy != 0;
            if (ini.GetInt(led, "HR_COLOR_SEL") is int cs) l.ColorIndex = cs & 7;
            if (ini.GetInt(led, "HR_LED_MODE") is int m) l.Mode = m & 15;
            int mask = 0; bool any = false;
            for (int i = 0; i < 7; i++) if (ini.GetInt(led, "RGB_HR_COLOR_EN" + i) is int e) { any = true; if (e != 0) mask |= 1 << i; }
            if (any) l.CycleMask = (byte)(0x80 | mask);
            int me = 0; any = false;
            for (int i = 0; i < 8; i++) if (ini.GetInt(led, "HR_LED_MODE_EN" + i) is int e) { any = true; if (e != 0) me |= 1 << i; }
            if (any) l.ModeEnableMask = (byte)me;
            if (ini.GetInt(led, "HR_REACT_LED") is int react)
                notes?.Add($"HR_REACT_LED={react}: точное место в памяти не установлено (влияет на X-ниббл цвета 1), значение не переносится.");
        }
        notes?.Add("Тела макросов хранятся в macro\\*.dat и не импортируются: макросы взяты из текущего состояния клавиатуры.");
        return cfg;
    }
}
