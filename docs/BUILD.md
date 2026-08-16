# Сборка

## Требования

- Windows 10 22H2 / Windows 11, x64
- .NET SDK 10 (проверено на 10.0.400)

Если SDK ставился в профиль пользователя (`dotnet-install.ps1 -Channel 10.0`),
перед командами задайте окружение:

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"
```

## Команды

```powershell
dotnet build NexusPdf.slnx -c Release        # сборка всего решения
dotnet test tests/NexusPdf.UnitTests         # доменные/инфраструктурные тесты
dotnet test tests/NexusPdf.PdfEngineTests    # интеграционные тесты PDFium
./build.ps1                                  # всё сразу + publish в artifacts/
```

Запуск из сборки: `src\NexusPdf.App.Desktop\bin\Release\net10.0-windows\win-x64\NexusPdf.exe [файлы.pdf]`.
Подкаталог `win-x64` появляется потому, что в Release проект собирается вместе
со средой выполнения; в Debug его нет.

## Publish

`build.ps1` публикует self-contained x64-сборку в `artifacts/publish/win-x64`
и считает SHA-256. ARM64: `dotnet publish src/NexusPdf.App.Desktop -r win-arm64`
(нативный pdfium для win-arm64 приезжает из того же пакета PDFiumCore).

## qpdf (опционально, для будущих функций)

```powershell
./tools/fetch-qpdf.ps1 -Version <версия> -ExpectedSha256 <sha из релиза>
```

Кладёт `qpdf.exe` в `tools/qpdf` рядом с приложением; без него функции qpdf
просто недоступны (и не показываются в UI).
