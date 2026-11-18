# Установщик (Inno Setup 6)

Собирается на Linux под Wine (сборка Wine 11 wow64, префикс без wine-mono):

```
export WINEPREFIX=$HOME/.isfx2 WINEDLLOVERRIDES="mscoree,mshtml="
xvfb-run -a wineboot -i                                  # один раз
xvfb-run -a wine innosetup-6.7.4.exe /VERYSILENT /CURRENTUSER /DIR=C:\InnoSetup6   # один раз
dotnet publish src/K13.Studio -c Release -o out/publish
xvfb-run -a wine 'C:\InnoSetup6\ISCC.exe' /DAppVersion=0.5.10.0 /DAppTag=v5.10.0 \
  /DSourceExe=Z:\\полный\\путь\\out\\publish\\VorotexK13Studio.exe /OZ:\\полный\\путь\\out installer\\VorotexK13Studio.iss
```

Установка для текущего пользователя (без прав администратора) в `%LOCALAPPDATA%\Programs\VorotexK13Studio`, ярлык в «Пуске»,
по желанию — на рабочем столе. Профили и резервные копии не трогаются ни при установке, ни при удалении.
Переносной exe (без установки) по-прежнему выкладывается в каждый выпуск.

## Выпуск (с 1.1.0)

В каждый выпуск кладётся `SHA256SUMS.txt` (формат `sha256sum`: `<hex>  <имя файла>`) для обоих exe. Без него кнопка «Скачать и обновить» отказывается устанавливать. `VersionInfoCopyright` в .iss содержит `AppId <guid> v<версия>` — программа проверяет эту метку в скачанном установщике; AppId не менять.
Проверка готовой сборки: `K13_VERIFY_VERSION=1.1.0 K13_VERIFY_SETUP=… K13_VERIFY_PORTABLE=… dotnet test tests/K13.Core.Tests --filter V110BuildCheck`.
