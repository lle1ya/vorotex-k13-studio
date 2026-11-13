namespace Vorotex.K13;

public sealed class K13FormatException(string message) : Exception(message);

/// <summary>A block the vendor-equivalent Apply would write (≤ 8 bytes, never crossing its own range).</summary>
public sealed record WriteBlock(int Address, byte[] Data, string Purpose);

/// <summary>
/// Bidirectional codec between a raw config-memory image (from address 0) and <see cref="K13DeviceConfig"/>.
/// Encoding is done ON TOP of an existing image so everything the profile does not own (factory area,
/// BASE+0x21..0x47, the trailer, stale macro tails) is preserved byte-for-byte.
/// </summary>
public static class K13MemoryCodec
{
    public static readonly byte[] HeaderMagic = { 0xE1, 0x1E };

    public static int ReadBase(byte[] img)
    {
        if (img.Length < K13Layout.HeaderAddress + 8) throw new K13FormatException("Образ слишком короткий (нет заголовка 0xD0).");
        if (img[K13Layout.HeaderAddress] != 0xE1 || img[K13Layout.HeaderAddress + 1] != 0x1E)
            throw new K13FormatException($"Нет сигнатуры E1 1E по адресу 0xD0 (прочитано {img[0xD0]:X2} {img[0xD1]:X2}).");
        return img[0xD2] | img[0xD3] << 8;
    }

    /// <summary>Logical ↔ device colour mapping used by Encode/Decode (set by the app from the store / calibration).</summary>
    public static LedColorMap ColorMap { get; set; } = new();

    public static K13DeviceConfig Decode(byte[] img)
    {
        int b = ReadBase(img);
        if (img.Length < b + K13Layout.OffTrailer) throw new K13FormatException("Образ не покрывает таблицу макросов.");
        var cfg = new K13DeviceConfig { Base = b };
        var led = cfg.Led;
        led.Flags = img[b + K13Layout.OffFlags];
        for (int i = 0; i < 7; i++)
        {
            byte a = img[b + K13Layout.OffColors + 2 * i], c = img[b + K13Layout.OffColors + 2 * i + 1];
            var (lr, lg, lbb) = ColorMap.ToLogical(a >> 4, c & 15, a & 15);
            led.Colors[i] = new ColorSlot { R = lr, G = lg, B = lbb, X = c >> 4 };
        }
        led.Extra = img.AsSpan(b + K13Layout.OffLedExtra, K13Layout.LedExtraLength).ToArray();
        byte la = img[b + K13Layout.OffLedA], lb = img[b + K13Layout.OffLedB];
        led.Direction = la >> 6; led.Brightness = (la >> 3) & 7; led.Speed = la & 7;
        led.Symmetry = (lb & 0x80) != 0; led.ColorIndex = (lb >> 4) & 7; led.Mode = lb & 15;
        led.CycleMask = img[b + K13Layout.OffCycleMask];
        led.ModeEnableMask = img[b + K13Layout.OffModeEnable];

        int keymapPtr = U16(img, b + K13Layout.OffKeymapPtr), macroPtr = U16(img, b + K13Layout.OffMacroPtr);
        if (keymapPtr != b + K13Layout.OffKeymap || macroPtr != b + K13Layout.OffMacroTable)
            throw new K13FormatException($"Неожиданные указатели keymap=0x{keymapPtr:X4} macro=0x{macroPtr:X4} (ожидалось BASE+0x48/BASE+0xF8).");

        byte w = img[b + K13Layout.OffWheel];
        cfg.WheelFirst = w >> 4; cfg.WheelSecond = w & 15;
        cfg.Keymap = img.AsSpan(b + K13Layout.OffKeymap, K13Layout.KeymapLength).ToArray();
        cfg.Fn = img.AsSpan(b + K13Layout.OffFn, 12).ToArray();
        cfg.FnPad = img.AsSpan(b + K13Layout.OffFn + 12, 4).ToArray();

        for (int i = 0; i < K13Layout.MacroSlots; i++)
        {
            int e = b + K13Layout.OffMacroTable + i * K13Layout.MacroEntrySize;
            if (img[e] != 0x07) throw new K13FormatException($"Запись макроса {i} не начинается с 0x07 (0x{img[e]:X2}).");
            int len = (img[e + 1] & 0x7F) | img[e + 4] << 7;
            int addr = img[e + 2] | (img[e + 3] & 0x3F) << 8;
            var slot = new MacroSlot { Mode2Bit = (img[e + 1] & 0x80) != 0, Mode1Bit = (img[e + 3] & 0x80) != 0, FlagBit = (img[e + 3] & 0x40) != 0 };
            if (len > 0)
            {
                if (addr + 2 + 2 * len > img.Length) throw new K13FormatException($"Тело макроса {i} (0x{addr:X4}, {len} событий) выходит за пределы образа.");
                slot.Repeat = img[addr] | img[addr + 1] << 8;
                int p = addr + 2;
                for (int k = 0; k < len; k++)
                {
                    if (p + 2 > img.Length) throw new K13FormatException($"Тело макроса {i} выходит за пределы образа.");
                    byte d = img[p], code = img[p + 1];
                    p += 2;
                    var ev = new MacroEvent { Down = (d & 0x80) != 0, Delay = d & 0x7F, Code = code };
                    int extra = MouseCodes.ExtraLength(code);
                    if (extra < 0) { slot.Unsupported = true; slot.Events.Add(ev); break; }
                    if (extra > 0)
                    {
                        if (p + extra > img.Length) throw new K13FormatException($"Тело макроса {i} выходит за пределы образа.");
                        ev.Extra = img.AsSpan(p, extra).ToArray();
                        p += extra;
                    }
                    slot.Events.Add(ev);
                }
            }
            cfg.Macros[i] = slot;
        }
        return cfg;
    }

    /// <summary>Highest address (exclusive) referenced by the macro bodies of an image.</summary>
    public static int UsedEnd(byte[] img)
    {
        int b = ReadBase(img);
        int end = b + K13Layout.OffTrailer + 4;
        for (int i = 0; i < K13Layout.MacroSlots; i++)
        {
            int e = b + K13Layout.OffMacroTable + i * 5;
            int len = (img[e + 1] & 0x7F) | img[e + 4] << 7;
            int addr = img[e + 2] | (img[e + 3] & 0x3F) << 8;
            if (len <= 0) continue;
            int p = addr + 2;
            for (int k = 0; k < len && p + 2 <= img.Length; k++)
            {
                int extra = MouseCodes.ExtraLength(img[p + 1]);
                p += 2 + Math.Max(0, extra);
            }
            end = Math.Max(end, p);
        }
        return end;
    }

    /// <summary>Encodes <paramref name="cfg"/> on top of a copy of <paramref name="baseImage"/>.</summary>
    public static byte[] Encode(K13DeviceConfig cfg, byte[] baseImage) => Encode(cfg, baseImage, out _);

    public static byte[] Encode(K13DeviceConfig cfg, byte[] baseImage, out List<WriteBlock> plan)
    {
        Validate(cfg);
        int b = cfg.Base;
        var layout = LayoutMacros(cfg);
        int needed = Math.Max(layout.End, b + K13Layout.OffTrailer + 4);
        var img = new byte[Math.Max(baseImage.Length, K13Layout.Align8(needed))];
        Array.Fill(img, (byte)0xFF);
        Array.Copy(baseImage, img, baseImage.Length);
        var blocks = new List<WriteBlock>();
        plan = blocks;

        void Put(int addr, byte[] data, string purpose)
        {
            Array.Copy(data, 0, img, addr, data.Length);
            for (int o = 0; o < data.Length; o += 8)
                blocks.Add(new WriteBlock(addr + o, data.AsSpan(o, Math.Min(8, data.Length - o)).ToArray(), purpose));
        }

        // 1. header @0xD0
        Put(K13Layout.HeaderAddress, new byte[] { 0xE1, 0x1E, (byte)b, (byte)(b >> 8), 0xF0, 0x0F, 0x00, 0x00 }, "Заголовок");
        // 2. BASE+0x00..0x17 : flags, colours, extra
        var led = cfg.Led;
        var ledBytes = new byte[0x18];
        ledBytes[0] = led.Flags;
        for (int i = 0; i < 7; i++)
        {
            var c = led.Colors[i];
            var (rr, rg, rb) = ColorMap.ToRaw(c.R, c.G, c.B);
            ledBytes[1 + 2 * i] = (byte)(rr << 4 | rb);
            ledBytes[2 + 2 * i] = (byte)((c.X & 15) << 4 | rg);
        }
        Array.Copy(led.Extra, 0, ledBytes, K13Layout.OffLedExtra, K13Layout.LedExtraLength);
        Put(b, ledBytes, "Подсветка: флаги и цвета");
        // 3. BASE+0x18..0x1F : LED mode block + pointers
        int km = b + K13Layout.OffKeymap, mt = b + K13Layout.OffMacroTable;
        Put(b + 0x18, new byte[]
        {
            (byte)((led.Direction & 3) << 6 | (led.Brightness & 7) << 3 | (led.Speed & 7)),
            (byte)((led.Symmetry ? 0x80 : 0) | (led.ColorIndex & 7) << 4 | (led.Mode & 15)),
            led.CycleMask, led.ModeEnableMask,
            (byte)km, (byte)(km >> 8), (byte)mt, (byte)(mt >> 8)
        }, "Подсветка: режим, яркость, скорость");
        // 4. wheel (1 byte; BASE+0x21..0x47 is never written)
        Put(b + K13Layout.OffWheel, new[] { (byte)((cfg.WheelFirst & 15) << 4 | (cfg.WheelSecond & 15)) }, "Колесо");
        // 5. keymap, Fn
        Put(km, cfg.Keymap, "Раскладка");
        Put(b + K13Layout.OffFn, cfg.Fn.Concat(cfg.FnPad).ToArray(), "Fn-слой");
        // 6. macro table (exactly 100 bytes – unlike the vendor, no stack garbage) and bodies
        Put(mt, layout.Table, "Таблица макросов");
        foreach (var (addr, body, slot) in layout.Bodies) Put(addr, body, $"Макрос {slot}");
        return img;
    }

    private sealed record MacroLayout(byte[] Table, List<(int Addr, byte[] Body, int Slot)> Bodies, int End);

    private static MacroLayout LayoutMacros(K13DeviceConfig cfg)
    {
        int b = cfg.Base;
        var table = new byte[K13Layout.MacroTableLength];
        var bodies = new List<(int, byte[], int)>();
        int cursor = K13Layout.Align8(b + K13Layout.OffMacroTable + K13Layout.MacroTableLength);
        for (int i = 0; i < K13Layout.MacroSlots; i++)
        {
            var m = cfg.Macros[i];
            int len = m.Events.Count;
            int addr = cursor;
            var e = table.AsSpan(i * 5, 5);
            e[0] = 0x07;
            e[1] = (byte)((len & 0x7F) | (m.Mode2Bit ? 0x80 : 0));
            e[2] = (byte)addr;
            e[3] = (byte)((m.Mode1Bit ? 0x80 : 0) | (m.FlagBit ? 0x40 : 0) | ((addr >> 8) & 0x3F));
            e[4] = (byte)(len >> 7);
            if (len > 0)
            {
                if (m.Unsupported) throw new K13FormatException($"Макрос {i + 1} содержит блок текста/подсветки родной программы — такой макрос можно только удалить или перезаписать.");
                var body = new byte[m.BodyLength];
                int rep = m.Mode1Bit || m.Mode2Bit ? 1 : m.Repeat; // hold / toggle: header is always 01 00 (vendor)
                body[0] = (byte)rep; body[1] = (byte)(rep >> 8);
                int p = 2;
                for (int k = 0; k < len; k++)
                {
                    var ev = m.Events[k];
                    body[p++] = (byte)((ev.Down ? 0x80 : 0) | (ev.Delay & 0x7F));
                    body[p++] = ev.Code;
                    int extra = MouseCodes.ExtraLength(ev.Code);
                    if (extra > 0)
                    {
                        var x = ev.Extra ?? Array.Empty<byte>();
                        for (int j = 0; j < extra; j++) body[p++] = j < x.Length ? x[j] : (byte)0;
                    }
                }
                bodies.Add((addr, body, i));
                cursor = K13Layout.Align8(addr + body.Length);
            }
        }
        if (cursor > K13Layout.MacroAreaLimit) throw new K13FormatException($"Макросы не помещаются в память (конец 0x{cursor:X4} > 0x4000).");
        return new MacroLayout(table, bodies, cursor);
    }

    public static void Validate(K13DeviceConfig cfg)
    {
        if (cfg.Base < 0xD8 || cfg.Base > 0x1000) throw new K13FormatException($"Недопустимый BASE 0x{cfg.Base:X}.");
        if (cfg.Keymap.Length != K13Layout.KeymapLength) throw new K13FormatException("Раскладка должна быть 160 байт.");
        if (cfg.Fn.Length != 12 || cfg.FnPad.Length != 4) throw new K13FormatException("Fn-слой должен быть 12+4 байт.");
        if (cfg.Macros.Count != K13Layout.MacroSlots) throw new K13FormatException("Должно быть 20 слотов макросов.");
        if (cfg.Led.Colors.Count != 7 || cfg.Led.Extra.Length != K13Layout.LedExtraLength) throw new K13FormatException("Подсветка: 7 цветов и 9 байт extra.");
        foreach (var c in cfg.Led.Colors)
            if (c.R is < 0 or > 15 || c.G is < 0 or > 15 || c.B is < 0 or > 15 || c.X is < 0 or > 15) throw new K13FormatException("Каналы цвета 0..15.");
        var l = cfg.Led;
        if (l.Direction is < 0 or > 3 || l.Brightness is < 0 or > 7 || l.Speed is < 0 or > 7 || l.Mode is < 0 or > 15 || l.ColorIndex is < 0 or > 7)
            throw new K13FormatException("Параметры подсветки вне диапазона.");
        if (cfg.WheelFirst is < 0 or > 15 || cfg.WheelSecond is < 0 or > 15) throw new K13FormatException("Колесо: 0..15.");
        for (int i = 0; i < cfg.Macros.Count; i++)
        {
            var m = cfg.Macros[i];
            if (m.Events.Count > 0x3FFF) throw new K13FormatException($"Макрос {i}: слишком много событий.");
            if (m.Repeat is < 0 or > 0xFFFF) throw new K13FormatException($"Макрос {i}: повторы 0..65535.");
            foreach (var ev in m.Events)
            {
                if (ev.Delay is < 0 or > 127) throw new K13FormatException($"Макрос {i}: задержка 0..127 (×8 мс).");
                int extra = MouseCodes.ExtraLength(ev.Code);
                if (extra > 0 && (ev.Extra?.Length ?? 0) != extra) throw new K13FormatException($"Макрос {i}: событие 0x{ev.Code:X2} требует {extra} байт данных.");
            }
        }
    }

    private static int U16(byte[] a, int o) => a[o] | a[o + 1] << 8;
}
