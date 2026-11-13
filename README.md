# VOROTEX K13 Studio (WPF / .NET 8)

- `src/K13.Core` — протокол, кодек памяти, импорт config.ini, профили, резервные копии (кроссплатформенно, покрыто тестами).
- `src/K13.Studio` — нативное окно Windows (WPF), трей, перехват клавиш (F13–F24 → действия на ПК), автопереключение профилей.
- `tests/K13.Core.Tests` — 53 теста: побайтовый round-trip всех 11 дампов, воспроизведение каждого Apply родного приложения, импорт ini, безопасность (запись блокируется).

Сборка: `dotnet test tests/K13.Core.Tests -c Release`, `dotnet publish src/K13.Studio -c Release -o out/publish` (single-file, self-contained win-x64).

Запись в клавиатуру отключена константой `K13WriteGate.WritesCompiledIn = false` (K13.Core/K13Device.cs). Любой кадр, кроме 07 18 00/05/01, бросает WriteBlockedException.

Проверка только чтением: `VorotexK13Studio.exe --read-backup <папка>` → headless-read.txt + .bin/.hex.txt.
Данные: %APPDATA%\VorotexK13Studio\profiles.json, %LOCALAPPDATA%\VorotexK13Studio\backups, studio.log.
