# Выпуск

## Версионирование

SemVer. Единая точка правды — `Directory.Build.props`
(`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`).
Версия видна: в окне «О программе», в свойствах exe, в журнале запуска.

## Порядок выпуска

1. Обновить `Directory.Build.props` и `CHANGELOG.md`.
2. `./build.ps1` — сборка, тесты, publish, SHA-256.
3. Собрать MSI (когда каркас `installer/` будет доведён): WiX v5,
   `dotnet tool install --global wix`, `wix build installer/Msi/NexusPdf.wxs`.
4. Подписать артефакты сертификатом кода (когда появится сертификат) —
   без подписи SmartScreen будет предупреждать, это честно указывается
   в замечаниях к выпуску.
5. Тег `v<версия>` в git.

## Каналы

Пока один канал (alpha, вручную). Updater с подписанным манифестом,
проверкой хеша и откатом — этап 9 плана.

## Правила ассоциации PDF (для установщика)

- версионируемый ProgID `NexusPdf.Document.1`, FriendlyTypeName, DefaultIcon,
  команда Open с путём в кавычках;
- регистрация в `RegisteredApplications`/`Capabilities` и OpenWith;
- **никогда** не подменять UserChoice; выбор по умолчанию делает пользователь
  через системную страницу «Приложения по умолчанию» (программа умеет её
  открывать);
- при удалении: удалить только свои ProgID/записи, ассоциации других программ
  не трогать, выбор нового обработчика оставить Windows.
