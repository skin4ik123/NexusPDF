# Установщик NexusPDF

Статус: **MSI собирается** (WiX 5.0.2, проверено локально: ~52 МБ с
self-contained приложением). Цикл установки/обновления/удаления ещё не
прогонялся — см. docs/KNOWN_LIMITATIONS.md. План поставки: подписанный
Setup.exe-bootstrapper + MSI + portable ZIP.

Важно: WiX ставится **версии 5.x** (`dotnet tool install --global wix
--version 5.0.2`). WiX 7 требует принятия платного OSMF EULA — осознанно не
используется.

## Сборка MSI

```powershell
./build.ps1 -Msi
```

или вручную:

```powershell
./build.ps1                                   # наполняет artifacts/publish/win-x64
dotnet tool install --global wix --version 5.0.2
wix build installer/Msi/NexusPdf.wxs -bindpath publish=<абс. путь>/artifacts/publish/win-x64 -o artifacts/NexusPdf.msi
```

(bindpath передавайте абсолютным путём — относительный WiX разрешает от
каталога .wxs.)

## Что регистрирует MSI

- файлы приложения в `Program Files\NexusPDF` (или профиль пользователя при
  установке per-user);
- ярлык в меню «Пуск»;
- ProgID `NexusPdf.Document.1` (значок, команда открытия с путём в кавычках);
- `.pdf\OpenWithProgids` — появление в «Открыть с помощью»;
- `Capabilities` + `RegisteredApplications` — появление в системной странице
  «Приложения по умолчанию».

Чего установщик **не делает** намеренно:

- не подменяет `UserChoice` и не назначает себя обработчиком по умолчанию —
  это выбор пользователя в настройках Windows;
- не трогает чужие ассоциации при удалении.

## Осталось до полноценного установщика (этап 8–10)

- bootstrapper Setup.exe (лицензия, выбор пути, per-user/per-machine, проверка
  места и архитектуры, тихий режим, лог);
- диалог удаления с выбором: сохранить/удалить настройки, кэш, журналы;
- portable ZIP;
- подпись артефактов;
- installer-тесты из TEST_MATRIX (установка/обновление/repair/удаление).
