# NexusPDF

**PDF work that never leaves your computer.**

A desktop PDF editor for Windows: read and rearrange pages, edit existing text
and images, annotate and draw, fill forms, sign, recognise scanned text,
clean up scans, export to Word and Excel, and print. No account, no upload,
no subscription — every engine runs on your own processor.

[Download](https://nexus.internetdeco.com) · [Changelog](https://nexus.internetdeco.com/changelog)

![NexusPDF](docs/images/main.png)

## What it does

- **Pages in any order** — reorder, rotate, duplicate, extract. Drag pages from
  one open document straight into another and drop them where you want.
- **Scans that come out clean** — straighten the sheet, remove scanner speckles
  and turn uneven grey paper into even white, with a before-and-after preview.
- **Text and images edited in place** — change existing text keeping its font,
  size and position; replace a picture, or send a page to an image editor and
  bring it back.
- **Text recognition, offline** — scanned pages get a searchable text layer.
  Sixteen language packs ship with the program.
- **Export to Word and Excel** — paragraphs, tables, links and comments. Tables
  are taken from drawn borders, and where there are none, from column gaps.
- **Print centre** — booklets, posters, several pages per sheet, manual duplex,
  crop marks and bleed, with a preview of the actual sheet.
- **Comments and drawing** — highlight, note, pencil, line and arrow with stroke
  stabilisation. Every mark is a standard PDF annotation.
- **Passwords and redaction** — AES-256 encryption, and redaction that removes
  the content underneath rather than covering it with a black box.
- **Signatures and comparison** — sign with a certificate, verify existing
  signatures, compare two files page by page.

There is also a command-line tool (`NexusPdfCli`) for batch work: export,
merge, compress, protect, recognise and compare.

## System requirements

- Windows 10 version 21H2 or Windows 11, 64-bit
- About 1 GB of free disk space
- 4 GB of memory; 8 GB is more comfortable for large scans
- No .NET installation needed — the runtime is included
- No internet connection required, during install or afterwards

## Privacy

Documents are processed locally and never uploaded. There is no account, no
licence server and no telemetry. Logs stay on the device and never contain
document content or passwords.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Windows.

```powershell
./build.ps1 -All
```

This restores the pinned native dependencies (qpdf, Tesseract data, OCR
models — versions and SHA-256 sums are in `tools/*.lock.json`), builds, runs
the tests, and produces the installer, the MSI, and a portable archive in
`artifacts/`.

For a plain build and test run:

```powershell
dotnet build NexusPdf.slnx -c Release
dotnet test tests/NexusPdf.UnitTests -c Release
```

More detail in [docs/BUILD.md](docs/BUILD.md) and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Most documentation in `docs/` is
written in Russian.

## Licence

NexusPDF is licensed under the **GNU Affero General Public License v3.0** —
see [LICENSE](LICENSE).

The reason is MuPDF: document compression uses MuPDF by Artifex Software, which
is distributed under the AGPL-3.0. Bundling it means the combined work carries
the same licence. In practice: you may use, study, modify and redistribute
NexusPDF, and anyone who distributes a modified version must publish their
source under the same terms.

## Third-party components

| Component | Purpose | Licence |
| --- | --- | --- |
| PDFium | page rendering, text and content editing | Apache-2.0 / BSD-3-Clause |
| qpdf | file structure, AES-256 encryption, optimization | Apache-2.0 |
| MuPDF, MuPDF.NET | image and font compression | AGPL-3.0 |
| PaddleOCR (via RapidOcrNet) + ONNX Runtime | text recognition | Apache-2.0 / MIT |
| Tesseract + Leptonica | fallback recognition | Apache-2.0 / BSD-2-Clause |
| DocumentFormat.OpenXml | export to Word and Excel | MIT |
| SkiaSharp, Clipper2 | raster and geometry | MIT / BSL-1.0 |
| .NET, WPF, CommunityToolkit.Mvvm | platform | MIT |
| Serilog | logging | Apache-2.0 |
| gong-wpf-dragdrop | drag and drop | BSD-3-Clause |

Full notices: [docs/THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md).
Licence compatibility is tracked in
[docs/DEPENDENCY_AND_LICENSE_MATRIX.md](docs/DEPENDENCY_AND_LICENSE_MATRIX.md).

## Known limitations

Kept deliberately honest, not hidden:
[docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md),
[docs/PRINT_KNOWN_LIMITATIONS.md](docs/PRINT_KNOWN_LIMITATIONS.md).

The builds are not code-signed, so SmartScreen warns on download. Checksums are
published with every release.

## Support the work

NexusPDF is free and stays free. If it saved you time:
[nexus.internetdeco.com/#support](https://nexus.internetdeco.com/#support)

---

Crafted by Artur Yurchuk.
