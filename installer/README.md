# Установщик NexusPDF

Статус: **готов и проверен** (Windows 11, per-user цикл прогнан автоматически:
установка → файлы/реестр/ярлык → запуск приложения → удаление без следов).

## Поставка

| Артефакт | Что это |
| --- | --- |
| `artifacts/NexusPdfSetup.exe` | фирменный установщик с WPF-интерфейсом (hero-панель, выбор режима и пути, ярлык, лицензия, прогресс, запуск после установки) |
| `artifacts/NexusPdf.msi` | MSI x64 (WiX 5) — основа: обновления, Repair, удаление через «Установленные приложения»; при прямом запуске показывает запасной мастер WixUI на русском |
| `artifacts/NexusPdf-<v>-portable-win-x64.zip` | portable-версия без установки |
| `artifacts/checksums.sha256.txt` | контрольные суммы |

Сборка всего: `./build.ps1 -All`.

## Тихая установка / удаление

```powershell
.\NexusPdfSetup.exe /S                     # для текущего пользователя
.\NexusPdfSetup.exe /S /allusers           # для всех (UAC)
.\NexusPdfSetup.exe /S /dir="D:\Apps\NexusPDF" /nodesktop
msiexec /x NexusPdf.msi /qn                # тихое удаление
```

Код возврата — стандартный код msiexec (0 — успех, 3010 — нужна перезагрузка).

## Что регистрирует установка

- файлы приложения (+ qpdf, THIRD_PARTY_NOTICES, лицензия);
- ярлык в меню «Пуск» (+ на рабочем столе, если выбран);
- ProgID `NexusPdf.Document.1` (значок, команда открытия с путём в кавычках);
- `.pdf\OpenWithProgids` — «Открыть с помощью»;
- `Capabilities` + `RegisteredApplications` — страница «Приложения по умолчанию»;
- запись в «Установленные приложения» с иконкой и версией.

Принципиально **не делает**: не подменяет `UserChoice`, не назначает себя
обработчиком по умолчанию, при удалении не трогает чужие ассоциации и
документы пользователя.

## Инструменты

WiX **5.0.2** (`dotnet tool install --global wix --version 5.0.2` +
`wix extension add --global WixToolset.UI.wixext/5.0.2`). WiX 7 осознанно не
используется: требует принятия платного OSMF EULA.

## Остатки (см. KNOWN_LIMITATIONS)

- артефакты не подписаны сертификатом кода (SmartScreen при скачивании);
- per-machine режим реализован, но авто-цикл прогнан только per-user;
- Setup.exe ~117 МБ (два self-contained рантайма) — план: framework-dependent
  Setup или нативный стуб.
