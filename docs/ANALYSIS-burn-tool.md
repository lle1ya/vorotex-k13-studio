# VOROTEX K13: analysis of the chip vendor's factory burning tool (IST83025B V2.01)

Date: 2026-11-16 (UTC+5). Purely offline. **No device access, no writes, nothing was run on the user's machines.**
Working files (extracted installer, Ghidra project, decompilation, verifier `synth.py`) are kept outside this repository; no vendor executables or binaries are included here.

Confidence tags: **[CODE]** read directly in the decompiled code; **[DUMP]** byte-exact match with our real dumps; **[INF]** inference, needs a hardware test.

---

## 0. TL;DR

* The makers' `config.ini` **is the K13's factory image**. Re-encoding it with the burn tool's own serializer reproduces **0x0000–0x00D7 byte-for-byte**, all 216 bytes, on our unit (`exp07b-full`, `k13-backup-20261112`) and on the makers' unit (`diag1517/k13-read-20261116-150723`). **[CODE+DUMP]**
* The memory is an **external I²C EEPROM (24Cxx)**. The tool probes its size with write/read-back tests. It's written with **the same `07 18 03 / 09 / 01` commands our app and the vendor app use**. The only difference is that byte 7 also carries an addressing-mode bit (0x20 = 8-bit word address, 0x40 = 16-bit address, 0x60 = page-erase mode). There's no separate "burn" protocol and no firmware update: the tool only writes configuration. **[CODE]**
* The factory flags we wanted are in the 0x0000 block:
  * **`FN_M_MODE_SV_EN` ("FN+M模式保存", remember mode) = bit 1 of 0x000F.** Both units hold 0x91, so the bit is 0.
  * `MODE_CHG_UNLOCK_EN` = 0x000F bit 3.
  * `MODE_LED_EN` = 0x000F bit 4. This is the blue LED as the mode indicator, and it's 1 on the K13 ✓.
  * `SAVE_CONFIG_EN` = 0x0008 bit 7, which is 1.
  * `FN_M_MODE_SW_EN` = 0x0008 bit 6, `FN_R_MODE_RST_EN` = 0x0008 bit 5, `PHANTOM_EN` = 0x0008 bit 4.

  **[CODE+DUMP]**
* **`KEYS_MODE` isn't in the factory block. It's bit 7 of BASE+0 (0x00E0)**, the same bit the vendor app calls KEYSMODE. The factory burn writes BASE+0 = **0x40**, which is KEYS_MODE=0, i.e. Normal. That's exactly the makers' status byte 3 at power-on (`07 00 41 40 …`). **[CODE]** EEPROM now holds 0xC0, because the vendor app and Studio write bit 7 = 1, yet the keypad still boots in Normal (status 0x40). So **the firmware clears the mode bit at boot**. Most likely that's because "remember mode" (0x000F bit 1) is off. **[INF, ≈65 %]**
* **Proposed fix:** change 0x000F from **0x91 to 0x93**, a single-byte write, and keep BASE+0 bit 7 = 1 as now. The K13 should then boot in Game, or at least in whatever mode was last used. It's reversible by writing 0x91 back. Physical writability is ≈85 %, the mapping is ≈95 %, and the effect is ≈60–65 %. A hardware test is needed (§6).
* **Fallback, behaviour-proof:** write the user's keys into the **Normal table at 0x0020 + col·8** too. The burn tool fills that table with the very same write primitive. Then both modes behave the same and the boot mode stops mattering. ≈80 % it works, but it has caveats (§5.3).
* **PID:**
  * `PID1=0x2330` is **not** what gets burned. It's only one more PID the tool *accepts* when it looks for a device.
  * The tool writes **PID2** to EEPROM 0x0003–0x0004. The "N+1" checkbox (`N_1_STATUS`) forces that value to **0x2350**, or to **0x2351** if ticked. **[CODE]**
  * With the makers' file (`N_1_STATUS=0`) the PID stays 0x2350. Ticking "N+1", or loading the installer's stock `config.ini` (`PID2=0x2351`), would change the USB PID to 0x2351, and then neither our app nor the vendor app would find the keypad.
  * The tool is still risky to run (§7).

---

## 1. What the makers sent

| File | What it is |
|---|---|
| `tool/英斯特IST83025B烧录程序V2.01.[20221205].exe` | **Inno Setup 5.5.7 installer**, not the tool itself. Extracted with innoextract 1.9: `app/KB_Config.exe` (the real tool: MFC, VS2017, x86, 4.1 MB, imports `HidD_Get/SetFeature`), `config/{config.ini, config_org.ini, KBconfig.ini, kbd_org.ini, macro*.dat, MP.ini}`, skins, and `backup/Backup_IST83025B[…].zip`. |
| `tool/1-4.png` | Screenshots of the tool with the X808 (14-key) config loaded: Game and Normal tabs, "出厂功能" checkboxes. |
| `sw/ExpOrImpFiles/*` | **"Export config" output of the tool for the K13**: `ReadMe.txt` (UTF-16, dated 2024-06-27 15:33:24, tool `IST83025B_V2.00`), `config.ini` (the K13 image, see §2), `KBconfig.ini` (= KD table + DFN), `config_org.ini` / `kbd_org.ini` / `macro_org.dat` (stock vendor templates for a 26-key demo board). |
| installer `config/MP.ini` | `MP=0` (edit mode), **`AUTO_WRITE=1` ("burn automatically as soon as USB is recognized")**. Auto-burn fires on `DBT_DEVICEARRIVAL` only in production mode (`DAT_0065131c==1 && AUTO_WRITE==1`, `FUN_0042b260`). |

Device detection (`FUN_0040d500` + `FUN_00429fe0`): VID 0x30FA with PID 0x2350/2351/2352/2353/2354/2360 (hard-coded), plus `PID1` and `PID2` from the ini. A **shipping K13 (2350) is recognized out of the box.** **[CODE]**

## 2. EEPROM map: what the burn tool writes (`FUN_00413170`, "Start OnApply...")

All addresses are absolute. Bit formulas are taken from the decompiled code. "Value" is what's in both K13s now. Every listed byte is **[CODE+DUMP]**, verified by `synth.py` (Appendix A).

### 2.1 Header block 0x0000–0x0007 (never touch)
| Addr | Value | Meaning |
|---|---|---|
| 0x00 | B4 | EEPROM-type magic: 0xB4 for ≤ 16 Kbit (EEP_CAP ≤ 4), 0x4B for larger. Also the size-probe value. |
| 0x01 | 01 | constant |
| 0x02 | B4 | `0xB4 & -(DAT_0064b038≠0)` (static 1) |
| 0x03–0x04 | 50 23 | **USB PID (LE) = PID2 → 0x2350** (0x2351 if "N+1") |
| 0x05–0x06 | FA 30 | **USB VID 0x30FA** (VID2) |
| 0x07 | 4B | constant |

### 2.2 Factory function flags 0x0008–0x000F
| Addr | Value | Bits (MSB→LSB) |
|---|---|---|
| **0x08** | **80** | b7 **SAVE_CONFIG_EN** (实时配置保存, "save config in real time") =1 · b6 **FN_M_MODE_SW_EN** (FN_M切换模式) =0 · b5 **FN_R_MODE_RST_EN** (FN+R复位到普通模式) =0 · b4 **PHANTOM_EN** (开启冲突检测) =0 · b3-2 FN_COLOR_EN =0 · b1 BRIGHT_EN =0 · b0 =0 |
| 0x09 | 9B | 0x90 + (HR_LED_NUM−1)&15 (12) |
| 0x0A | 85 | ((HB_LED_NUM+15)·16) XOR (HG_LED_NUM−1) (9 / 6) |
| 0x0B | 10 | (Z_GUN_EN·2 + SINGLE_LED_BAK_ON)·64 + 0x10 (0x10/7 because IC_TYPE=1) |
| 0x0C | 07 | (LED_POR_EN·2 + LIGHT_PAD_MODE_EN)·64 + 7 |
| 0x0D | 07 | 7 |
| 0x0E | 10 | (OFF_ALL_LED·2 + DAT_00660584)·64 + 0x10 |
| **0x0F** | **91** | b7 FN_LED_EN =1 · b6 CHG_FN_LED_TYPE =0 · b5-4 **MODE_LED_EN** (WinLock灯修改为模式指示灯, "WinLock LED becomes the mode indicator") =1 · b3 **MODE_CHG_UNLOCK_EN** (模式切换后键盘特定功能恢复, "restore Win-lock / all-lock / WASD swap after a mode switch") =0 · b2 unused (always 0) · **b1 FN_M_MODE_SV_EN (FN+M模式保存, "remember mode") =0** · b0 (ISRGBMODE≠1) =1 |

### 2.3 0x0010–0x001F: hotkeys / misc
| Addr | Value | Meaning |
|---|---|---|
| 0x10 | 01 | DSCROLL0<<4 \| DSCROLL1 (factory wheel) |
| 0x11–0x16 | 52 51 4F 50 49 15 | BRIGHT_P/N, SPEED_P/N, COR_CHG, **MODE_RST_KEY (0x15 = R)** |
| 0x17 | 10 | **MODE_CHG_KEY = 0x10 (M)**, the "M" in Fn+M |
| 0x18–0x1A | 47 46 0F | LED_CHG_KEY, LED_ONOFF_KEY, LED_ZONE_KEY |
| 0x1B | 01 | (USB_SN_EN·2+USB_STR_EN)<<6 \| len(product string)+1 |
| 0x1C–0x1D | E0 00 | **BASE = 0x00E0** (hard-coded, or 0x0118 when custom USB strings are enabled) |
| 0x1E | 01 | USB_MANU_EN<<4 \| len(manufacturer)+1 |
| 0x1F | 00 | FN_DEL_RST_LED_EN/MODE/COR |

### 2.4 Tables and header
| Addr | Content |
|---|---|
| **0x0020–0x00BF** | **Normal table = `KD_r_c`**, column-major `0x20 + col·8 + row`. K13 row 0: `59 5A 5B 5C 5D 5E 5F 60 28 2B 61 62 50 4F 51 52 F4 E9` (Num1…Num0 … arrows, **F4 = KBD Mode**, E9 = LedMode). |
| 0x00C0–0x00CB | Normal-mode Fn+F1..F12 (`DFN_F1..12`) |
| 0x00D0–0x00D7 | `E1 1E <BASE> <custom-strings flag> F0 0F 00 00`. The burn tool writes the same 0x1EE1 constant the vendor app does. |
| 0x00E0… (BASE) | User block, identical to the vendor-app layout (PROTOCOL §3): flags, LED, **Game table `KU_r_c` at BASE+0x48**, Fn table, macros. |

### 2.5 BASE+0 (0x00E0) flags byte as the burn tool writes it **[CODE]**
`b7 = KEYS_MODE` · `b6 = (ALT_F4_ENB == 0)` · `b5 = WIN_LOCK_SAVE_EN` · `b4 = FN_W_ENB` (关闭Fn+W) · `b3-2 = WIN_LOCK_EN` · `b1-0 = 0`.
With the K13 file that gives **0x40**. The vendor app later writes bit 7 = KEYSMODE = 1 (→ 0xC0) and preserves bits 5–4, which is consistent with those two bits being factory-only.

In the burn tool `KEYS_MODE` also chooses which table the editor shows: `KEYS_MODE=0` copies KD into the working table, otherwise KU (`FUN_0042bd70`). The window title reads [普通模式] (Normal mode) or [游戏模式] (Game mode). So **KEYS_MODE = the mode the keypad is in after a factory burn**, and the firmware's live mode bit is BASE+0 bit 7. That fits PROTOCOL §30: status byte 3 is a live copy of BASE+0.

## 3. Burn/write protocol (`FUN_0040d1d0` write, `FUN_0040cff0` read, `FUN_004287e0` frame builder)

Same command 0x18. Byte 2 = `b5<<5|b4<<4|b2<<3|b3<<2|b1<<1|b0`, and byte 7 = `n−1 + 0x20` (8-bit-address EEPROM), `+0x40` (16-bit address) or `+0x60` (page mode with erase).
```
write n≤8:  07 18 03 i AL AH d[i] (n-1)|mode  ×n ; Sleep 2 ; 07 18 09 00 AL AH 00 (n-1)|mode ; Sleep 10 ; 07 18 01 00 AL AH 00 00
read  n≤8:  07 18 03 i AL AH 00 (n-1)|mode ×n ; Sleep 1 ; 07 18 00 00 AL AH 00 00 ; Sleep 1 ;
            (07 18 05 i AL AH 00 (n-1)|mode ; Sleep 1 ; GetFeature) ×n ; Sleep 1 ; 07 18 01 00 00 00 00 00
finish:     07 18 11 00 00 00 00 00|0x60 ; Sleep 10 ; 07 18 00 00 00 00 00 00
```
Our app and the vendor app send byte 7 = n−1 (mode bits 0), and the firmware then uses its configured addressing. That already works for the factory area: v5.7.3 rewrote 0x00D0 (DA 1E → E1 1E) on the makers' unit and the read-back matched.

**Burn sequence (`FUN_00413170`):**
1. GetFeature status, checking that byte 20 is compatible with IC_TYPE.
2. **EEPROM size probe (`FUN_004333d0`)**: write/read single bytes 0x4B/0xB4 at **0x0000**, 0x3C at 0x0010 and 0x4B at 0x0020, then 0x3C/0x2D at 0x7FF/0x3FF/0x1FF (or 0x3FFF/0x1FFF/0xFFF) to detect address aliasing. *Destructive test writes into the factory and Normal areas.* (The ini's `EEPROM=2` stands for 2 KB / 0x7FF.)
3. Write a variant marker at memsize−0x41.
4. Write 0x0000, 0x0008 and 0x0010, read 0x0008 back and compare. Write 0x0018.
5. Write the KD table (20×8 at 0x20), DFN at 0xC0 and the header at 0xD0. Optionally write USB strings at 0xE0+.
6. Write the BASE block: flags, LED, KU at BASE+0x48, Fn table, macro table and macro bodies.
7. Write a 64-byte trailer at memsize−0x3F, read it back and verify.
8. `07 18 11`, `07 18 00`.

Our K13 has FF at 0x7BE/0x7C0–0x7FF, so it was not burned with exactly this build and size setting. The 0x00–0xD7 content is still identical.

## 4. Answers to the goals

| Question | Answer | Confidence |
|---|---|---|
| Where is the Normal table? | 0x0020 + col·8 + row (KD) | **very high** (CODE+DUMP) |
| Where is KEYS_MODE? | BASE+0 (0x00E0) bit 7; factory value 0, vendor app/Studio write 1 | high (CODE); its runtime meaning (live mode) ≈80 % |
| Where is FN_M_MODE_SV_EN (remember mode)? | **0x000F bit 1 (mask 0x02)**, currently 0 | mapping **≈95 %** (CODE+DUMP of the neighbouring bits) |
| Mode-restore option (MODE_CHG_UNLOCK_EN)? | 0x000F bit 3; only restores Win-lock/all-lock/WASD after a switch, **unrelated to boot mode** | high |
| Fn+M toggle / Fn+R reset to Normal | 0x0008 bit 6 / bit 5 (both 0; the K13 has no Fn key anyway; MODE_CHG_KEY=M at 0x17, MODE_RST_KEY=R at 0x16) | high |
| Can an ordinary write reach these bytes? | Yes. Same opcode; our app already wrote 0x00D0 in the same factory area on the makers' unit and verified it. | ≈85 % |
| Does the burn tool change the PID? | It always rewrites 0x0003–4, with **PID2** forced to 0x2350 / 0x2351 by the "N+1" box, **not PID1=0x2330**. | high (CODE) |
| Why does the K13 boot in Normal although EEPROM BASE+0 = C0? | The firmware resets the mode bit at power-on unless "remember mode" is on. `07 18 11` presumably reloads the flags from EEPROM (bit 7 = 1), so Game. | ≈65 % |

**Side note (§26.3 in PROTOCOL.md):** the makers' later dumps show 0x0000 = **B4**, not BA, and their whole factory block equals ours. So the "BA" was a stale-buffer read artefact, not a different unit. Both units have identical factory configuration.

## 5. What our app could do

### 5.1 Option A: enable "remember mode" (preferred, smallest change)
* Write **1 byte at 0x000F: 0x91 → 0x93**. Do **not** use a hard-coded 0x93: read 0x0008–0x000F, OR in 0x02, write only 0x000F (n=1), then read it back.
* Keep BASE+0 bit 7 = 1, which v5.8 already does when it writes flags.
* Expected: after replug the keypad comes up in the **last used mode**. If the stick toggled to Normal, it would stay Normal after replug. The firmware probably persists the toggle because SAVE_CONFIG_EN=1, which also explains the 0x40 we once saw in EEPROM at 13:32–13:37 on 13.11. Whether the "FN+M" save also covers the 0xF4 KBD-Mode key isn't proven; one mode variable is likely (≈60–65 %).
* Revert: write 0x91.

### 5.2 What NOT to do
* Never write 0x0000–0x0007 (EEPROM-type magic + VID/PID).
* Avoid rewriting the whole 0x0008 block. If an 8-byte write is unavoidable, use the exact read-back bytes with only bit 1 of 0x0F changed.
* Don't use the burn tool (§7).

### 5.3 Option B: mirror the user's keys into the Normal table (fallback)
* For each physical key column c (0..17), write the Game code to `0x20 + c·8` (row 0). Keep col 16 = 0xF4 if the user wants the stick toggle, keep col 17 = 0xE9, and keep or write arrows on 12–15.
* Factory values to restore: `59 5A 5B 5C 5D 5E 5F 60 28 2B 61 62 50 4F 51 52 F4 E9` (from KD / `KBconfig.ini`).
* Caveats:
  * Macros (A5…) and consumer codes may or may not be honoured from the Normal table (unknown).
  * The firmware probably caches the table at boot, so changes need a replug.
  * The vendor app never rewrites 0x0020, so a user switching back to the vendor app would keep our Normal table until we restore it.
* Confidence that the keys then work in Normal: ≈80 %.

## 6. Minimal reversible hardware test plan (needs the user's explicit approval; one unit)

0. **Preconditions.** Studio with the auto-activation setting **off** («Автоматически включать Game при подключении» unticked) so that it doesn't mask the result. Full read backup 0x0000–0x07FF (2 passes, 0 mismatches). Record status byte 3. Expect 0x000F = 0x91 and BASE+0 = 0xC0. If either differs, stop.
1. **Write (12 frames + finish):** `07 18 03 00 0F 00 93 00` → Sleep 2 → `07 18 09 00 0F 00 00 00` → Sleep 10 → `07 18 01 00 0F 00 00 00` → `07 18 11 …` → `07 18 00 …`. Read back block 0x0008: only 0x0F may differ (0x93).
2. **Boot test:** close Studio, put the keypad in Game (blue LED on), unplug and replug. **Pass A:** blue LED on, key 4 sends the Game assignment (F6), and status byte 3 = 0xC0 when Studio is reopened.
3. **Remember test:** press the stick → Normal (LED off), replug. If it stays Normal, "remember last mode" is confirmed. Then press the stick → Game, replug → Game.
4. Read BASE+0 after steps 2–3 to see whether the firmware writes bit 7 on toggle. Read-only.
5. **Revert** (always, or keep if the user likes it): write 0x91 at 0x000F the same way, replug, confirm Normal at boot.
6. **If step 2 fails** (still Normal), revert to 0x91. Option B (§5.3) is then the next test, on a single key: write 0x0038 (col 3 = key 4) from 0x5C to the Game code, replug, check that key 4 sends it in Normal, restore 0x5C.

Risk assessment for steps 1–5:
* The VID/PID/magic bytes aren't touched, so the HID config interface and enumeration are unaffected. Recovery is the same 1-byte write.
* The worst realistic outcome is an unexpected behaviour change, such as the mode bit being saved or LED-related behaviour changing. Bricking risk is low (≈2–3 %, limited to unknown firmware reactions to bit 1).

## 7. Risks of the factory burn tool (tell the makers / user not to run it on a K13)

* It **auto-detects shipping K13s** (30FA:2350).
* It **rewrites the entire EEPROM**: VID/PID, factory flags, the Normal table, the user block (every Studio/vendor-app setting and macro is overwritten with the ini's KU/macro data), plus destructive size-probe writes.
* `MP.ini AUTO_WRITE=1` makes it burn **immediately on plug-in** in production mode.
* The "N+1" checkbox or a stock ini (`PID2=0x2351`) changes the PID to 0x2351, and the keypad disappears from Studio and the vendor app. Only re-burning would bring it back.
* An interruption after the size probe leaves test bytes in 0x0000/0x0010/0x0020.
* With the makers' exact `config.ini` (`N_1_STATUS=0`) it would rebuild the factory K13 image with PID 0x2350, so it's usable as a *last-resort factory restore*, but only with that file and "N+1" unticked.

## 8. Open items

* Runtime effect of 0x000F bit 1 on the 0xF4 KBD-Mode key (test §6).
* Whether `07 18 11` is "reload config from EEPROM" (consistent with all observations; §6 step 4 helps).
* Whether the firmware writes BASE+0 bit 7 itself on mode toggle when SAVE_CONFIG_EN=1.

---

## Appendix A: verifier (`synth.py`, not in the repo)

Rebuilds 0x0000–0x00D7 from `config.ini` + `KBconfig.ini` using the burn tool's formulas and diffs them against dumps. Result:
```
exp07b-full-0000-1000.bin       diffs(0x00-0xD7): []
k13-read-20261116-150723.bin    diffs(0x00-0xD7): []   (makers' unit)
k13-backup-20261112-171754.bin  diffs(0x00-0xD7): []
synth 0x00-0x1F: b4 01 b4 50 23 fa 30 4b 80 9b 85 10 07 07 10 91 01 52 51 4f 50 49 15 10 47 46 0f 01 e0 00 01 00
```

## Appendix B: key code locations in KB_Config.exe (Ghidra 12.2)
* `FUN_00413170`: burn ("Start OnApply...").
  * 0x0000 block @ ~0x4139b6 (literal 0xB4/0x4B).
  * 0x0008 flags @ ~0x413b53–0x413b81.
  * 0x0010 / 0x0018 blocks, KD loop at 0x20, DFN 0xC0, header 0xD0 (`0x1EE1`).
  * BASE flags @ ~0x414c5d.
  * KU at BASE+0x48.
  * Trailer at memsize−0x3F; `07 18 11` at the end.
* `FUN_0042bd70`: ini loader. INI key → variable mapping:
  * `SAVE_CONFIG_EN`→`6517f0`, `FN_M_MODE_SW_EN`→`6517f4`, `FN_R_MODE_RST_EN`→`6517f8`, `PHANTOM_EN`→`6517fc`
  * `FN_M_MODE_SV_EN`→`651810`, `MODE_LED_EN`→`651814`, `MODE_CHG_UNLOCK_EN`→`651818`
  * `KEYS_MODE`→`651cd8`, `ALT_F4_ENB`→`651cdc`, `WIN_LOCK_SAVE_EN`→`651ce0`, `FN_W_ENB`→`651ce4`, `WIN_LOCK_EN`→`651cec`
  * `PID2`→`65fad4`, `VID2`→`65fad0`
* `FUN_00419e80`: dialog init. `PID1`→`65fac4`, which is used only for detection. `N_1_STATUS` forces `65fad4` to 0x2350/0x2351. `FUN_004280a0` is the "N+1" checkbox handler.
* `FUN_004333d0`: EEPROM size probe. `FUN_0040d1d0` / `FUN_0040cff0` / `FUN_004287e0`: write / read / frame builder.
* `FUN_0042b260`: WM_DEVICECHANGE auto-burn (MP mode + AUTO_WRITE).
