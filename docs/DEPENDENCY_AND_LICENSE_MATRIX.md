# Зависимости и лицензии

> **Внимание: с добавлением MuPDF условия распространения изменились.**
> MuPDF (Artifex) — AGPL-3.0. Пока он входит в поставку, программу нельзя
> распространять как закрытый продукт: любому получателю копии нужно давать
> и полный исходный код всего приложения на условиях AGPL-3.0. Альтернатива —
> купить коммерческую лицензию Artifex либо убрать проект
> `NexusPdf.Pdf.MuPdf` из поставки (сжатие тогда работает на запасном
> движке PDFium + qpdf, слабее на 3–5% и медленнее втрое).
> Решение принято сознательно: MuPDF даёт лучшее сжатие и умеет то, чего
> публичный API PDFium не умеет (1-битные сканы, урезание шрифтов,
> дедупликация объектов).

Все ОСТАЛЬНЫЕ компоненты совместимы с закрытым коммерческим продуктом.
Обязательство по ним одно: включить тексты лицензий/уведомления в поставку —
файл [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) устанавливается вместе с
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

| qpdf | 12.4.0 | структура, оптимизация, AES-шифрование | Apache-2.0 | да | да | официальные бинарники GitHub qpdf/qpdf | SHA-256 пин в `tools/qpdf.lock.json` |
| MuPDF.NET | 3.28.1.6 | сжатие: изображения, шрифты, структура | **AGPL-3.0** (или коммерческая Artifex) | **нет** — см. предупреждение выше | да | nuget.org (Artifex) | SHA-512 NuGet |
| MuPDF.NativeAssets.Windows.X64 | 1.28.2 | нативная библиотека MuPDF (~48 МБ) | **AGPL-3.0** (или коммерческая Artifex) | **нет** | да | nuget.org (Artifex) | SHA-512 NuGet |

## Запланированные (ещё не поставляются)

| Зависимость | Назначение | Лицензия | Примечание |
| --- | --- | --- | --- |
| RapidOcrNet + ONNX Runtime | локальный OCR по умолчанию (PaddleOCR PP-OCRv6) | Apache-2.0 / MIT | 16 языковых пакетов, версии и SHA-256 закреплены в `tools/ocrmodels.lock.json` |
| Модели и словари PaddleOCR PP-OCRv5/v6 | наборы символов и распознаватели | Apache-2.0 | загружаются `tools/fetch-ocrmodels.ps1`, поставляются в `tools/ocrmodels` |
| SkiaSharp, Clipper2 | растр и геометрия внутри RapidOcrNet | MIT / BSL-1.0 | транзитивные зависимости |
| Tesseract + tessdata (rus, eng, ukr, kat) | запасной локальный OCR | Apache-2.0 | этап 7; языковые пакеты из tesseract-ocr/tessdata_fast |

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
