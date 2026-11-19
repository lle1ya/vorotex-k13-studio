namespace Vorotex.K13;

/// <summary>One bisect step: frames our read already sends, sent alone (PROTOCOL.md §28).</summary>
public sealed record ActivationStep(string Name, string Frames, Action<K13Device> Run);

/// <summary>v5.7.4: the makers' K13 keeps the written layout in memory but starts with the factory one after power-up;
/// a memory read (no write) switches it to the saved layout (VOROTEX, 16.11.2026 15:07). This sends only frames that the
/// read and its session end already send — nothing that writes memory.</summary>
public static class K13Activation
{
    /// <summary>Short activation: read the 8-byte header block (07 18 00 / 07 18 05 ×8 / 07 18 01), then the session end
    /// (07 18 11, 07 18 00, 07 20). Returns the header bytes. ≈ 0.1 s.</summary>
    public static byte[] Activate(K13Device dev)
    {
        var head = dev.ReadBlocks(new[] { (K13Layout.HeaderAddress, 8) })[0];
        dev.EndSession();
        return head;
    }

    /// <summary>Bisect order: from the least to the most likely trigger. The device must be opened with the session end on Dispose switched off.</summary>
    public static IReadOnlyList<ActivationStep> BisectSteps { get; } = new ActivationStep[]
    {
        new("Запрос статуса", "GetFeature (статус, ничего не отправляется)", d => d.GetStatus()),
        new("Загрузка и чтение блока заголовка", "07 18 00 00 D0 00 00 00, 07 18 05 i … ×8 + GetFeature", d =>
        {
            d.SendSafe(K13Protocol.LoadBlock(K13Layout.HeaderAddress));
            Thread.Sleep(3);
            for (int i = 0; i < 8; i++) { d.SendSafe(K13Protocol.ReadByte(K13Layout.HeaderAddress, i, 8)); Thread.Sleep(2); d.ReadAnswer(); }
        }),
        new("Конец чтения", "07 18 01 00 00 00 00 00", d => d.SendSafe(K13Protocol.EndRead())),
        new("Завершение сеанса", "07 18 11 00 00 00 00 00", d => d.SendSafe(K13Protocol.SessionEndFrames[0].Data)),
        new("Сброс", "07 18 00 00 00 00 00 00", d => d.SendSafe(K13Protocol.SessionEndFrames[1].Data)),
        new("Выход из режима настройки", "07 20 00 00 00 00 00 00", d => d.SendSafe(K13Protocol.SessionEndFrames[2].Data)),
    };

    public enum Seen { Nothing, Saved, Factory, Mixed }

    /// <summary>What the keys pressed after a step say: the saved layout (memory explains them), the factory one, or nothing pressed.</summary>
    public static Seen Classify(K13DeviceConfig? memory, IReadOnlyCollection<ushort> vks)
    {
        var keys = vks.Where(v => !KeypadOutputCheck.IsModifier(v)).ToList();
        if (keys.Count == 0 || memory == null) return Seen.Nothing;
        int saved = keys.Count(v => KeypadOutputCheck.Explains(memory, v));
        return saved == keys.Count ? Seen.Saved : saved == 0 ? Seen.Factory : Seen.Mixed;
    }
}
