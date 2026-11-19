# VOROTEX K12 — analysis of the makers' data (19.11.2026)

Sources:
- `01_after_reset/k13-read-20261119-094139.bin`: 0x880 bytes, taken after a reset in the vendor K12 app.
- `02_after_apply/k13-read-20261119-095032.bin`: taken after one «Применить» with the Normal profile selected.
- `memory_diff.txt`.
- Studio 1.3.1 diagnostics: `01_after_reset/VorotexK13-диагностика-20261119-0942.zip`.
- The vendor `config.ini`: GBK/ANSI, converted to `config.utf8.ini`.
- The layout photo `04_layout/K12_top.png`.
- Studio key-tester and Key-Test screenshots in `03_key_tests`.

All times are as written by the makers' PC (UTC+3).

## 1. Summary

- **Same USB IDs, header, BASE and structure as the K13.**
  - 0x0000–0x0007 = `B4 01 B4 50 23 FA 30 4B`, identical to the K13.
  - Header 0x00D0 = `E1 1E E0 00 F0 0F 00 00`; BASE = 0x00E0.
  - Keymap pointer = 0x0128 and macro-table pointer = 0x01D8, as on the K13.
- **Built-in Normal table at 0x0020:** 14 columns, 8 bytes per column, row 0. The K13 has 18.
- **Stored Game table at 0x0128 (BASE+0x48):** also 14 columns. Rows 1–7 are all FF, and columns 14–19 are FF.
- **Column → physical key mapping is certain.** It is confirmed three ways: the memory, the vendor `config.ini`, and the makers' key tests (Studio tester and Key-Test).
- **The K12 macro table is NOT the same as the K13's.** It has 22 entries, not 20, and its first 4 entries hold non-`07` bytes after a vendor reset. Writing K13-format data to a K12 would corrupt it. **K12 must stay write-blocked** (it is, in `K13Model.WriteAllowed`).
- **The vendor «Применить» does not touch 0x000F,** so there is no remember-mode or indicator flag change.
  - The keypad went to Game because «Применить» writes **BASE+0 bit 7 (KEYSMODE) = 1**: `0x00E0` 40 → C0. The vendor app always stores `KEYSMODE=1`, even in the profile named «Normal» (`config.ini`: `[Config1]` GAME and `[Config2]` Normal both have `KEYSMODE=1` and identical keys).
  - The app then sends the usual settings re-apply (07 18 11), and the firmware takes the new KEYSMODE at once.
  - After the vendor reset, BASE+0 was 0x40 (KEYSMODE = 0), and the keypad was in Normal.

## 2. Key table (columns)

| col | physical key (photo) | Normal (0x0020, firmware) | Game (0x0128, after vendor reset / Apply) | `config.ini` KU index |
|---|---|---|---|---|
| 0–4 | 1 … 5 (top row) | Num 1 … Num 5 (59–5D) | 1 … 5 top row (1E–22) | KU_17…KU_21 |
| 5 | ENTER (tall, right) | Num Enter (58) | Enter (28) | KU_50 |
| 6 | 9 | Num 9 (61) | 9 (26) | KU_25 |
| 7 | 8 | Num 8 (60) | 8 (25) | KU_24 |
| 8 | 7 | Num 7 (5F) | 7 (24) | KU_23 |
| 9 | 6 | Num 6 (5E) | 6 (23) | KU_22 |
| 10 | VOROTEX (long) | Num 0 (62) | 0 (27) | KU_26 |
| 11 | «•» (dot) | Num . (63) | **` ~ (35)** | KU_16 |
| 12 | knob press | F4 = Normal/Game toggle | F4 | — |
| 13 | none (not a key) | E9 | E9 | — (the K13 has E9 in hidden column 17 too) |

The second row is wired in reverse: column 6 is the key «9» and column 9 is the key «6».

**The dot key in Game sends «` ~»** (HID 0x35), not a dot. This shows in the makers' Studio tester log («нажата … — «` ~»») and in the vendor profile, where KU_16 is the `~` position of a full keyboard.

What the makers called the «tilde» row is the ENTER key: Num Enter in Normal, Enter in Game. Their Key-Test screenshot lights the numpad Enter in Normal, which matches column 5 = 0x58.

**Knob turn:**
- In Normal it is volume (firmware; not in the table).
- In Game it follows the wheel byte BASE+0x20 = **0x32**: high nibble 3 = scroll down on the left turn, low nibble 2 = scroll up on the right turn.
- This matches `USCROLL0=3`, `USCROLL1=2` in `config.ini` and the K13 meaning of the nibbles, where low = right/«Roll+».

**Knob press:** F4 in both tables. It switches Normal ↔ Game, as the stick press does on the K13.

**Fn layer:** 0x01C8 = 12 × FF, so no Fn (`UFN_F1…F12=255`).

## 3. The 10 bytes changed by «Применить»

| addr | meaning | reset → apply | comment |
|---|---|---|---|
| 0x00E0 | BASE+0 flags | 40 → C0 | bit 7 KEYSMODE = 1 (Game layout active), bits 6–0 unchanged. **This is why it went to Game.** |
| 0x00E2 | colour 0, 2nd byte (X<<4 \| G) | 5F → 1F | X nibble only (G = F kept) |
| 0x00E4 | colour 1, 2nd byte | 1F → 0F | X nibble only |
| 0x00F0, 0x00F1 | BASE+0x10/0x11 (raw LED extra, preserved by Studio) | 01 01 → 00 00 | the vendor writes 00 00; the K13 also has 00 00 |
| 0x01D8–0x01DB, 0x01E4 | first macro-table entries | E0 FC 5C 05 / 3C → A4 F8 81 01 / 7C | not valid entries (no 0x07 marker); looks like an uninitialised vendor buffer, like the K13 «trailer» bytes |

**Unchanged:**
- 0x0000–0x000F, including 0x000F = **0x1B**.
- Both key tables and the wheel byte.
- LED mode/brightness 0x00F8–0x00F9 = 43 05.

So «Применить» did not set a remember-mode flag. The keys did not change because the vendor's «Normal» profile is the same as GAME.

## 4. Factory block 0x0008–0x000F

| | 0x0008 | 0x0009 | 0x000A | 0x000B | 0x000C | 0x000D | 0x000E | 0x000F |
|---|---|---|---|---|---|---|---|---|
| K13 | 80 | 9B | 85 | 10 | 07 | 07 | 10 | 91 (93 = remember, 81 = indicator off) |
| K12 | 82 | 9B | 85 | 10 | 87 | 07 | 10 | **1B** |

- On the K13, 0x000F bit 1 = remember the mode and bit 4 = mode indicator (both confirmed by the makers).
- The K12's 0x1B has bits 0, 1, 3 and 4 set and bit 7 clear. By K13 rules that would mean «remember mode ON, indicator ON».
- Bit 4 fits: the indicator is lit in Game.
- Bit 1 does **not** obviously fit: the K12 was in Normal after the reset, which could simply be the remembered mode.
- **These K12 bits are unverified.** Studio's «Запоминать режим» and «Синий индикатор» refuse 0x1B as an unknown pattern (`IsKnownPattern` accepts only 81/83/91/93), which is correct.

## 5. Status report (GetFeature)

The K12 sends the same 21-byte status. Byte 3 is the mode (40 = Normal, C0 = Game), as on the K13, and Studio's mode polling works on the K12. The tester log shows Normal ↔ Game in sync.

Byte 1 varies: 00 and FF on the K12; 91, 93 and 01 on the K13. Do not use it. Byte 4 is 43 on the K12 and 41 on the K13; byte 7 is 05 on the K12 and 01 or 08 on the K13. Their meaning is unknown.

## 6. Studio diagnostics (1.3.1, 09:40–09:42)

- Studio started with the K12 plugged in and, as designed («память не читается автоматически»), took the **last K13 image** for comparison.
- It planned a Normal-lighting write at 09:40:33. The write was skipped only because the factory state blocked automatic writes.
- Even without that, the quick-write path re-reads the model signature blocks (0x0048, 0x0080, 0x00A0) before writing, which would have caught the K12 and refused. **Nothing was written to the K12 on 19.11.**
- «Прочитать» at 09:41:39 identified the K12 correctly («похоже, VOROTEX K12 … Запись заблокирована»).
- **Recommendation:** after a connect, treat the model as «не проверена» until the 3 signature blocks are read. This is a read-only check of 24 bytes. It would stop the main window from showing a K13 layout or factory state for a K12. Not done in 1.3.3, because it changes the «no automatic read on connect» rule; that needs the owner's decision.
- **History:** on 17.11 at 11:34, Studio 1.1.x wrote a K13 layout into this K12, before model detection existed. The 19.11 reset dump shows the vendor reset restored the Game table. The Normal table at 0x0020 is built-in and was never changed.

## 7. Vendor `config.ini`

- `KB_NAME=IST83025B-K210`, `WindowName=MKESPN-K808`: the same OEM family as the K13 app. 5 profile slots; `CurConfig=2`, `ConfgCnt=2`; profiles «GAME» and «Normal».
- **Profiles:** both have `KEYSMODE=1`, `USCROLL0=3`, `USCROLL1=2`, `BMODELED=0`, the same 12 key codes (KU_16…26, KU_50) and no Fn.
- **Lighting:** `[ConfigN_LED1/3/7]` and `[..._LED_COLOR]` hold the LED colour sets; their meaning is as on the K13.
- **`[Config0]` and `[Config]`** are full-keyboard templates (KU_0…KU_103), not K12 data.
- **`BMODELED`** is 0 here while the indicator works. It is probably a per-profile option the app doesn't send, or it refers to a different LED.

## 8. What Studio needs to support the K12

**Read-only (done in 1.3.3):**
- K12 key names and a K12 schematic in «Проверка кнопок» (`K12Layout`): Normal from 0x0020, Game from the memory's table, knob from the wheel byte.
- The K12 Normal/Game layout in «Сохранить диагностику».

**Still to do for full support:**
- A K12 main-window schematic and editor: 12 keys plus the knob, no stick, columns 0–11 editable, 12 = knob press (keep F4), 13 = leave E9.
- A **K12 codec variant**:
  - the macro table has 22 entries (0x01D8–0x0245), then `FA 00`, with macro data from 0x0248;
  - the first entries may be invalid after a vendor reset, so they must be rewritten deliberately or kept;
  - the key table and wheel can use the K13 code with 14 columns;
  - rows 1–7 and columns 14–19 must stay FF.
- **Writer:** the model check must allow the K12 only with the K12 codec; never write 0x0000–0x0007 or 0x0020 (built-in table).
- **Flags:** don't offer «Запоминать режим» or «Синий индикатор» on the K12 until 0x000F is verified.

## 9. Hardware tests needed before enabling K12 writes

1. **Replug test (mode and 0x000F).** Switch to Game with the knob press, unplug and replug, and note the mode (indicator; key 1 sends 1 or Num 1). Repeat starting from Normal. This tells whether the K12 remembers the mode (0x000F bit 1) or starts from BASE+0 bit 7.
2. **Vendor assignment test (layout, wheel, macros).**
   - In the vendor app, give the 12 keys distinct codes (letters A–L in a known order), set the knob to brightness ±, and record one short macro on the VOROTEX key.
   - Press «Применить», then in Studio do «Прочитать» and «Сохранить диагностику», and test every key in «Проверка кнопок» in Game.
   - This confirms the columns for writing, the wheel nibbles and the 22-entry macro table and data format.
3. **Second vendor reset dump.** Reset again and dump, to see whether the first macro entries are random each time; this is needed to decide how a K12 writer treats them.

Until tests 1–3 are done and a K12 codec passes round-trip tests on those dumps, the K12 stays write-blocked.
