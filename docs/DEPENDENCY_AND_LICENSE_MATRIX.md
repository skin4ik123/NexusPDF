# Зависимости и лицензии

Все компоненты совместимы с закрытым коммерческим продуктом. Публикация
исходного кода не требуется ни одной из зависимостей. Обязательство одно:
включить тексты лицензий/уведомления в поставку — файл
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) устанавливается вместе с
программой.

| Зависимость | Версия | Назначение | Лицензия | Закрытый комм. продукт | Уведомления | Источник | Проверка поставки |
| --- | --- | --- | --- | --- | --- | --- | --- |
| .NET 10 (runtime/SDK) | 10.0.400 | платформа | MIT | да | да | dotnet.microsoft.com | подпись Microsoft |
| PDFiumCore | 152.0.7961 | биндинги PDFium | MIT | да | да | nuget.org | SHA-512 NuGet |
| bblanchon.PDFium.Win32 | 152.0.7961 | нативный PDFium | Apache-2.0 + BSD-3-Clause | да | да | nuget.org (сборки pdfium-binaries) | SHA-512 NuGet |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM-генераторы | MIT | да | да | nuget.org | SHA-512 NuGet |
| Microsoft.Extensions.DependencyInjection | 10.0.11 | DI | MIT | да | да | nuget.org | SHA-512 NuGet |
| gong-wpf-dragdrop | 4.0.0 | drag-and-drop сетки страниц | BSD-3-Clause | да | да | nuget.org | SHA-512 NuGet |
| Serilog + Sinks.File | 4.4.0 / 7.0.0 | журналирование | Apache-2.0 | да | да | nuget.org | SHA-512 NuGet |
| xunit (только dev) | 2.x | тесты | Apache-2.0 | не поставляется | — | nuget.org | SHA-512 NuGet |
| WiX Toolset (только сборка) | 5.0.2 | MSI-установщик | MS-RL | инструмент сборки, в поставку не входит | — | nuget.org (dotnet tool) | SHA-512 NuGet |

Примечание: WiX **v7** намеренно не используется — требует принятия платного
OSMF EULA; закреплена версия 5.0.2.

## Запланированные (ещё не поставляются)

| Зависимость | Назначение | Лицензия | Примечание |
| --- | --- | --- | --- |
| qpdf (qpdf.exe) | проверка структуры, линеаризация, шифрование | Apache-2.0 | официальные бинарники GitHub qpdf/qpdf; версия и SHA-256 пинуются в `tools/fetch-qpdf.ps1` |
| Tesseract + tessdata (rus, eng, ukr, kat) | локальный OCR | Apache-2.0 | этап 7; языковые пакеты из tesseract-ocr/tessdata_fast |

## Осознанно отклонённые

- **AGPL-движки (MuPDF, iText без комм. лицензии, PDFsharp-форки с AGPL)** —
  несовместимы с закрытым продуктом без покупки лицензии. Возможны позже как
  опциональный Advanced-адаптер при покупке коммерческой лицензии.
- **FluentAssertions ≥ 8** — сменила лицензию на платную для коммерческого
  использования; используем голый xunit.

## Обновление зависимостей

NuGet-пакеты обновляются через `dotnet outdated`/PR; nативный PDFium — вместе
с пакетом PDFiumCore (тянет pdfium-binaries той же версии). Контроль
целостности: NuGet сверяет SHA-512 из lock-графа с nuget.org; qpdf — SHA-256
пин в скрипте загрузки.
