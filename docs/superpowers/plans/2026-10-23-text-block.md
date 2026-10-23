# Text Block (stage 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Acrobat-level text block in NexusPDF: type directly on the page with per-fragment formatting and any installed font, move/resize/rotate the block, and re-edit it after the file is saved and reopened.

**Architecture:** Pure core in `NexusPdf.Pdf.Abstractions/TextBlocks` (TrueType reader, font catalog, block model, layout engine, editor model) shared by the PDFium writer and the WPF editor, so screen and file come from one layout. PDFium writes the block as ordinary text objects tagged with marked content `NexusTextBlock`; a reader regroups them for re-editing. The WPF editor draws the layout with `GlyphRun` and owns caret, selection, IME and clipboard.

**Tech Stack:** C# / .NET 10, WPF, PDFiumCore 153, xUnit; no new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-23-text-block-design.md` (Russian; read it first). Mockup: `docs/superpowers/specs/2026-10-23-text-block-mockup.html`.

## Global Constraints

- Engines see only `Pdf.Abstractions`; Domain/Application know nothing of WPF or SDKs (`docs/ARCHITECTURE.md`).
- Warnings in `src` are build errors (`src/Directory.Build.props`). No new NuGet packages (NuGet audit fails CI on vulnerabilities).
- Public AGPL repository: tests, fixtures and docs contain only invented data.
- Old `TextOverlay` (stamps, headers/footers, watermarks, comment summary) stays unchanged.
- UI strings go into all three packs `src/NexusPdf.App.Desktop/Resources/i18n/{ru,en,uk}.json` (LocalizationCoverageTests enforce parity).
- Fonts written into PDF must be embeddable (`OS/2.fsType` not Restricted License 0x0002 alone, not bitmap-only 0x0200) and must never produce missing-glyph boxes: missing characters use a fallback face.
- Complex scripts (Arabic, Hebrew, Indic, Thai and other shaping/RTL scripts) are refused at input with an explanation.
- No text is lost: overflow is never clipped; click-away and Esc commit; empty block on commit is removed.
- The hidden block description never contains the block text (redaction safety).
- A block changed by another program is not restored from NexusPDF data (integrity check fails → plain text).
- Arrow keys keep today's step for all objects: 5 pt, Ctrl — 1 pt.
- Tests run on Linux with `-p:EnableWindowsTargeting=true`; test fonts are Liberation (SIL OFL 1.1) shipped in `tests/TestFonts` with the licence.
- Final version 0.28.0 (`Directory.Build.props` 4 fields, `src/NexusPdf.App.Desktop/app.manifest`, `tools/verify-release.ps1` default), CHANGELOG and `docs/RELEASE-0.28.0.md`.

Test command used below (run from the worktree root):
`nice dotnet test <project> -c Release -m:2 -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~<Class>"` — `UT` = `tests/NexusPdf.UnitTests/NexusPdf.UnitTests.csproj`, `ET` = `tests/NexusPdf.PdfEngineTests/NexusPdf.PdfEngineTests.csproj`.

## Review Focus

1. A broken or truncated font file in the Fonts folder — the catalog skips it, the picker still opens (test in Task 3).
2. Pasting text from Excel/Word with tabs, CR/LF and very long content (100 000 characters) — tabs become one space, CR/LF become paragraphs, nothing hangs (test in Task 6).
3. Block on a page with `/Rotate 90` plus a rotated block — file positions match the layout (test in Task 7).
4. A file reopened on a PC without the block's font — the block opens with the substituted font marked, text intact (test in Task 8).
5. A hostile PDF with a forged `NexusTextBlock` mark (malformed JSON, giant numbers, offsets out of range) — reader returns `Intact=false`, never throws (test in Task 8).

---

### Task 1: Test fonts and synthetic font builder

**Files:**
- Create: `tests/TestFonts/LiberationSans-{Regular,Bold,Italic,BoldItalic}.ttf`, `tests/TestFonts/LiberationMono-Regular.ttf` (copy from `/usr/share/fonts/truetype/liberation/`), `tests/TestFonts/LICENSE-Liberation.txt` (SIL OFL 1.1 text with the Liberation copyright lines).
- Create: `tests/Shared/TinyFontBuilder.cs` (compiled into both test projects via `<Compile Include="..\Shared\TinyFontBuilder.cs" Link="TestFonts\TinyFontBuilder.cs" />`).
- Modify: `tests/NexusPdf.UnitTests/NexusPdf.UnitTests.csproj`, `tests/NexusPdf.PdfEngineTests/NexusPdf.PdfEngineTests.csproj` — `<None Include="..\TestFonts\**" LinkBase="TestFonts" CopyToOutputDirectory="PreserveNewest" />` and the shared compile item.
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TinyFontBuilderTests.cs`

**Interfaces:**
- Produces: `TestFontPaths.Directory` (absolute path of the copied `TestFonts` folder), `TestFontPaths.Sans(bool bold = false, bool italic = false)`.
- Produces: `TinyFontSpec(string Family, string Subfamily = "Regular", int UnitsPerEm = 1000, short Ascender = 800, short Descender = -200, short LineGap = 0, ushort FsType = 0, ushort WeightClass = 400, bool Italic = false, IReadOnlyDictionary<int, ushort>? Advances = null)`; `TinyFontBuilder.Build(TinyFontSpec) -> byte[]` (sfnt with head, hhea, maxp, OS/2 v4, hmtx, cmap format 4 + 12 (platform 3/1 and 3/10), name ids 1,2,4,6,16,17, post v3, loca/glyf with empty glyphs; `.notdef` advance 500; default advances: 500 for every codepoint in `Advances` or for `0x20–0x7E` when null); `TinyFontBuilder.BuildCollection(params byte[][] faces) -> byte[]` (TTC 1.0).

- [ ] **Step 1: Write the failing test** — `Built_Font_Has_Valid_Table_Directory`: `Build(new TinyFontSpec("Tiny"))` starts with `0x00010000`, table count 11, every table offset+length inside the array; `BuildCollection(a, b)` starts with `"ttcf"` and has 2 offsets.
- [ ] **Step 2: Run** `UT --filter TinyFontBuilderTests` — FAIL (type missing).
- [ ] **Step 3: Implement** `TinyFontBuilder` and `TestFontPaths` (directory = `Path.Combine(AppContext.BaseDirectory, "TestFonts")`).
- [ ] **Step 4: Run** — PASS. Also check `ls bin/Release/*/TestFonts` contains the five Liberation files.
- [ ] **Step 5: Commit** `test: Liberation test fonts and synthetic TrueType builder`

### Task 2: TrueType reader

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/TrueTypeFont.cs`
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TrueTypeFontTests.cs`

**Interfaces:**
- Produces (namespace `NexusPdf.Pdf.Abstractions.TextBlocks`):
  ```csharp
  public sealed class TrueTypeFont
  {
      public static int FaceCount(ReadOnlySpan<byte> data);              // 0 = not a font
      public static TrueTypeFont Parse(byte[] data, int faceIndex = 0); // FormatException on any malformed input
      public string FamilyName { get; }      // name 16, else 1 (Windows Unicode, prefer en-US 0x409)
      public string SubfamilyName { get; }   // name 17, else 2
      public string PostScriptName { get; }  // name 6
      public int UnitsPerEm { get; }
      public bool IsBold { get; }            // OS/2.fsSelection bit 5, else head.macStyle bit 0
      public bool IsItalic { get; }          // OS/2.fsSelection bit 0, else head.macStyle bit 1
      public int WeightClass { get; }
      public short Ascender { get; }  public short Descender { get; }  public short LineGap { get; } // hhea
      public short UnderlinePosition { get; }  public short UnderlineThickness { get; }           // post
      public short StrikeoutPosition { get; }  public short StrikeoutSize { get; }                // OS/2
      public ushort FsType { get; }
      public bool IsEmbeddable { get; }      // (FsType & 0x000F) != 0x0002 && (FsType & 0x0200) == 0
      public bool HasTrueTypeOutlines { get; } // 'glyf' present
      public bool HasGlyph(int codePoint);
      public int AdvanceWidth(int codePoint);  // font units; missing glyph → .notdef advance
      public byte[] ExtractFace();             // standalone sfnt of this face (copy for single-face files)
  }
  ```

- [ ] **Step 1: Write the failing tests**
  - `Reads_Liberation_Sans_Metrics`: family `"Liberation Sans"`, subfamily `"Regular"`, `UnitsPerEm == 2048`, `AdvanceWidth('A') == 1366`, `AdvanceWidth(' ') == 569`, `AdvanceWidth('Ж') == 1891`, `Ascender == 1854`, `Descender == -434`, `LineGap == 67`, `UnderlinePosition == -67`, `UnderlineThickness == 150`, `StrikeoutPosition == 530`, `StrikeoutSize == 102`, `IsEmbeddable`, `HasTrueTypeOutlines`.
  - `Detects_Missing_Cyrillic`: tiny font with advances only for `0x20–0x7E` → `HasGlyph(0x416) == false`, `AdvanceWidth(0x416) == 500`.
  - `Restricted_And_Bitmap_Fonts_Are_Not_Embeddable`: `FsType = 0x0002` → false; `0x0200` → false; `0x0004` → true; `0x0008` → true.
  - `Reads_Faces_Of_A_Collection`: `BuildCollection(tinyA, tinyB)` → `FaceCount == 2`; `Parse(ttc, 1).FamilyName == "B"`; `Parse(Parse(ttc, 1).ExtractFace()).FamilyName == "B"`.
  - `Malformed_Data_Throws_FormatException` (Theory): empty array, 12 random bytes, Liberation truncated to 5000 bytes, table offset pointing past the end → `FormatException` only.
- [ ] **Step 2: Run** `UT --filter TrueTypeFontTests` — FAIL.
- [ ] **Step 3: Implement** with bounds-checked big-endian reads (`BinaryPrimitives` over `ReadOnlySpan<byte>`); cmap: prefer (3,10) format 12, else (3,1)/(0,x) format 4; `ExtractFace` rebuilds the table directory with 4-byte aligned tables and recomputed offsets.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: TrueType reader for font metrics, coverage and embedding rights`

### Task 3: Font catalog and PDF font substitution

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/FontCatalog.cs`, `src/NexusPdf.Pdf.Abstractions/TextBlocks/FontSubstitution.cs`
- Modify: `src/NexusPdf.Pdf.Abstractions/PdfFontCatalog.cs` — `AvailableFamilies()` and `ResolvePath()` delegate to `FontCatalog.System` (old API kept; `ResolvePath` returns null for faces inside `.ttc`).
- Test: `tests/NexusPdf.UnitTests/TextBlocks/FontCatalogTests.cs`, `FontSubstitutionTests.cs`

**Interfaces:**
- Consumes: `TrueTypeFont` (Task 2).
- Produces:
  ```csharp
  public sealed record FontFaceRef(string Path, int FaceIndex);
  public sealed record FontFaceInfo(FontFaceRef Ref, string Family, string Subfamily, bool Bold, bool Italic,
      int Weight, bool HasCyrillic, bool Embeddable, bool Supported, long FileSize);
  public sealed record FontFamilyInfo(string Name, IReadOnlyList<FontFaceInfo> Faces)
  { public bool HasCyrillic { get; } public bool Embeddable { get; } public bool Supported { get; } } // any face
  public interface IFontProvider
  {
      FontFaceInfo? Resolve(string family, bool bold, bool italic);
      TrueTypeFont Load(FontFaceRef face);              // cached
      IReadOnlyList<string> FallbackFamilies { get; }   // order: DefaultFamily, "Arial", "Segoe UI", then all
  }
  public sealed class FontCatalog : IFontProvider
  {
      public const string DefaultFamily = "Segoe UI";
      public static FontCatalog System { get; }                         // lazy; scans SystemFontDirectories()
      public static IReadOnlyList<string> SystemFontDirectories();      // SpecialFolder.Fonts + %LOCALAPPDATA%\Microsoft\Windows\Fonts
      public static FontCatalog Scan(IEnumerable<string> directories);  // *.ttf, *.otf, *.ttc; skips unreadable/malformed
      public IReadOnlyList<FontFamilyInfo> Families { get; }            // sorted, StringComparer.OrdinalIgnoreCase
      public long LargeFontWarningBytes { get; } = 10 * 1024 * 1024;
  }
  public static class FontSubstitution
  { public static string? FamilyFor(string pdfFontName, IFontProvider fonts); }
  ```
  `HasCyrillic` = glyphs for every codepoint U+0410–U+044F plus U+0401, U+0451. `Supported` = `HasTrueTypeOutlines`. `Resolve` order: exact (bold, italic) → (bold, false) → regular → first face; null for unknown family.

- [ ] **Step 1: Write the failing tests**
  - `Scans_Families_And_Faces`: `Scan([TestFontPaths.Directory])` contains `"Liberation Sans"` with 4 faces and `"Liberation Mono"`; `Resolve("Liberation Sans", true, true).Ref.Path` ends with `BoldItalic.ttf`; `Resolve("liberation sans", false, false)` works case-insensitively.
  - `Marks_Fonts_Without_Cyrillic_Or_Embedding_Rights`: temp dir with tiny Latin-only font and tiny `FsType=2` font → `HasCyrillic == false`, `Embeddable == false` respectively; both still listed.
  - `Skips_Broken_Font_Files` (Review Focus 1): temp dir with a 10-byte `broken.ttf`, a truncated Liberation copy and one valid tiny font → exactly one family, no exception.
  - `Collection_Faces_Are_Listed`: temp `pair.ttc` from `BuildCollection` → two families with `FaceIndex` 0 and 1.
  - `FontSubstitutionTests` (Theory, catalog = test fonts plus tiny fonts named `Arial`, `Times New Roman`, `Courier New`): `"Helvetica"`→`"Arial"`, `"ABCDEF+Helvetica-Bold"`→`"Arial"`, `"TimesNewRomanPSMT"`→`"Times New Roman"`, `"Times-Italic"`→`"Times New Roman"`, `"Courier"`→`"Courier New"`, `"ABCDEF+LiberationSans,Bold"`→`"Liberation Sans"`, `"UnknownFont"`→`null`.
- [ ] **Step 2: Run** `UT --filter "FontCatalogTests|FontSubstitutionTests"` — FAIL.
- [ ] **Step 3: Implement** `FontCatalog` (scan in parallel with at most 2 threads; per-file `try/catch (FormatException or IOException or UnauthorizedAccessException)`), `FontSubstitution` (strip `XXXXXX+`, `,Style`, `-Style`, `PS`, `MT`, `PSMT`; standard-14 map; then name match ignoring spaces).
- [ ] **Step 4: Run** the two classes plus `UT --filter "Font|PdfFont"` (existing font tests) — PASS.
- [ ] **Step 5: Commit** `feat: catalog of all installed fonts with coverage, embedding and substitution`

### Task 4: Block model and structure codec

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/TextBlockModel.cs`, `src/NexusPdf.Pdf.Abstractions/TextBlocks/TextBlockCodec.cs`, `src/NexusPdf.Pdf.Abstractions/TextBlocks/TextBlockText.cs`
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TextBlockCodecTests.cs`, `TextBlockTextTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum TextAlign { Left, Center, Right, Justify }
  public enum BaselineShift { None, Superscript, Subscript }
  public enum TextBlockWidthMode { AutoWidth, Fixed }
  public sealed record TextStyle(string FontFamily, double FontSizePt, bool Bold = false, bool Italic = false,
      bool Underline = false, bool Strike = false, uint ColorArgb = 0xFF000000,
      BaselineShift Baseline = BaselineShift.None, double LetterSpacingPercent = 0);
  public sealed record TextRun(string Text, TextStyle Style);
  public sealed record TextParagraph(IReadOnlyList<TextRun> Runs, TextAlign Align = TextAlign.Left, double LineSpacing = 1.15)
  { public string Text { get; } }                                         // concatenated run texts
  public sealed record TextBlockOverlay(double XPt, double YPt, double WidthPt, double HeightPt, double RotationDegrees,
      TextBlockWidthMode WidthMode, bool AutoHeight, bool ShrinkToFit, IReadOnlyList<TextParagraph> Paragraphs,
      Guid BlockId = default) : PageOverlay;                             // displayed points, top-left; rotation about frame centre
  public sealed record TextBlockReplacement(Guid BlockId, TextBlockOverlay? NewBlock) : PageOverlay;
  public sealed record TextPiece(int Paragraph, int Start, string Text);
  public static class TextBlockCodec
  {
      public const int FormatVersion = 1;
      public static string EncodeStructure(TextBlockOverlay block);  // JSON: V, frame, modes, paragraphs (align, spacing, runs: length + style) — no text
      public static TextBlockOverlay? Decode(string json, IReadOnlyList<TextPiece> pieces, Guid id); // null on any mismatch
  }
  public static class TextBlockText
  {
      public const char LineBreak = ' ';
      public static bool IsSupported(int codePoint);
      public static string Normalize(string pasted); // \t → ' ', \r\n|\r|\n → ' ' paragraph separators, other C0 controls removed
  }
  ```
  Unsupported ranges: U+0590–08FF, U+0900–0DFF, U+0E00–0FFF, U+1000–109F, U+1780–17FF, U+FB1D–FDFF, U+FE70–FEFF.

- [ ] **Step 1: Write the failing tests**
  - `Structure_Has_No_Text`: block with run `"SECRET 42"` → `EncodeStructure(...)` does not contain `"SECRET"`; contains `"\"V\":1"`.
  - `Decode_Restores_Block_From_Pieces`: two paragraphs, three runs, pieces covering every character (split differently from runs) → decoded block `.Paragraphs[i].Text` and run styles equal the original; `BlockId == id`.
  - `Decode_Rejects_Gaps_Overlaps_And_Length_Mismatch` (Theory) → `null`.
  - `Decode_Rejects_Malformed_Json` (Theory: `""`, `"{"`, `"{\"V\":99}"`, run length `-1`, length `2147483647`, 100 000 paragraphs) → `null`, no exception.
  - `TextBlockTextTests`: `IsSupported('Ж')`, `IsSupported('é')`, `IsSupported(0x1F600)` true; `IsSupported(0x05D0)`, `IsSupported(0x0627)`, `IsSupported(0x0915)` false; `Normalize("a\tb\r\nc") == "a b c"`.
- [ ] **Step 2: Run** `UT --filter "TextBlockCodecTests|TextBlockTextTests"` — FAIL.
- [ ] **Step 3: Implement** (System.Text.Json with explicit limits: max 10 000 paragraphs, 100 000 runs, total length 1 000 000).
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: text block model and text-free structure codec`

### Task 5: Layout engine

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/TextBlockLayout.cs`
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TextBlockLayoutTests.cs`

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces:
  ```csharp
  public sealed record LaidOutPiece(int Paragraph, int Start, string Text, FontFaceRef Face, string Family, TextStyle Style,
      double FontSizePt, double XPt, double BaselinePt, double WidthPt, IReadOnlyList<double> AdvancesPt, bool IsFallback);
  public sealed record LaidOutDecoration(double XPt, double YPt, double WidthPt, double HeightPt, uint ColorArgb);
  public sealed record LaidOutLine(int Paragraph, int Start, int Length, double TopPt, double BaselinePt, double HeightPt,
      IReadOnlyList<LaidOutPiece> Pieces);
  public sealed record LaidOutBlock(double WidthPt, double HeightPt, IReadOnlyList<LaidOutLine> Lines,
      IReadOnlyList<LaidOutDecoration> Decorations, bool Overflows, double Scale);
  public static class TextBlockLayout
  {
      public const double PaddingPt = 2;
      public static LaidOutBlock Compute(TextBlockOverlay block, IFontProvider fonts);
  }
  ```
  Coordinates are block-local, origin at the frame's top-left, before rotation. `WidthPt/HeightPt` of the result are the frame size after AutoWidth/AutoHeight. Rules (spec «Вёрстка»): advances = `AdvanceWidth × size / UnitsPerEm`, no kerning; wrap after spaces and hyphens, long words split by characters, `TextBlockText.LineBreak` forces a line; line height = `(Ascender − Descender + LineGap) / UnitsPerEm × size × LineSpacing`, max over the line's pieces; baseline = top + `Ascender/UnitsPerEm × size` of the tallest piece; Justify widens spaces (pieces split at spaces), not on the paragraph's last line; super/subscript size `× 0.65`, baseline `∓ 0.33 × size`; letter spacing `LetterSpacingPercent / 100 × size` added after each character; underline rect at `−UnderlinePosition`, thickness `UnderlineThickness`, strike at `StrikeoutPosition`/`StrikeoutSize`; missing glyph → first face from `FallbackFamilies` that has it, same bold/italic, `IsFallback = true`; ShrinkToFit lowers every size by the same factor in 0.5 pt steps of the largest size until it fits, smallest size never below 4 pt; `Overflows` = fixed height and content taller than `HeightPt − 2 × PaddingPt` after shrinking. Trailing spaces at a wrap point stay in the piece text but take no width for alignment.

- [ ] **Step 1: Write the failing tests** (fonts: `FontCatalog.Scan([TestFontPaths.Directory])` plus a tiny font `"Mono500"` where every glyph is 500/1000 em)
  - `Auto_Width_Keeps_One_Line`: `"AAA"` Liberation Sans 10 pt, AutoWidth → one line, piece width `3 × 1366 / 2048 × 10` (6.669 pt, ±0.001), result width = that + 4.
  - `Fixed_Width_Wraps_At_Spaces`: Mono500 10 pt (5 pt per char), width `4 + 30` → `"aaa bbb ccc"` gives lines `"aaa "`, `"bbb "`, `"ccc"`.
  - `Long_Word_Is_Split_By_Characters`: width `4 + 20`, `"abcdefghij"` → `"abcd"`, `"efgh"`, `"ij"`.
  - `Line_Height_Uses_Font_Metrics_And_Spacing`: Liberation 10 pt, spacing 1.15 → line height `(1854 + 434 + 67) / 2048 × 10 × 1.15`.
  - `Alignment_Positions` (Theory Left/Center/Right): Mono500 10 pt `"ab"` in width `4 + 50` → x = 2, 22, 42.
  - `Justify_Spreads_Spaces_Except_Last_Line`.
  - `Superscript_Is_Smaller_And_Raised`.
  - `Letter_Spacing_Adds_After_Each_Character`: 10 % at 10 pt → each advance + 1 pt.
  - `Missing_Glyph_Uses_Fallback_Face`: Latin-only tiny font run `"aЖb"` → three pieces, middle `IsFallback`, family from `FallbackFamilies`.
  - `Shrink_To_Fit_Reduces_Size_To_Fit` and `Shrink_Never_Below_4pt_And_Reports_Overflow`.
  - `Fixed_Height_Overflow_Is_Not_Clipped`: all text still present in pieces, `Overflows == true`.
  - `Empty_Paragraph_Still_Has_A_Line`.
- [ ] **Step 2: Run** `UT --filter TextBlockLayoutTests` — FAIL.
- [ ] **Step 3: Implement** `Compute`.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: deterministic text block layout shared by editor and writer`

### Task 6: Editor model

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/TextBlockEditorModel.cs`
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TextBlockEditorModelTests.cs`

**Interfaces:**
- Consumes: Tasks 3–5.
- Produces:
  ```csharp
  public readonly record struct TextPosition(int Paragraph, int Offset);
  public enum CaretMove { Left, Right, WordLeft, WordRight, LineStart, LineEnd, Up, Down, BlockStart, BlockEnd }
  public sealed record InputResult(bool Accepted, string? RejectedReasonKey); // "TextBlockUnsupportedScript"
  public sealed class TextBlockEditorModel
  {
      public TextBlockEditorModel(TextBlockOverlay block, IFontProvider fonts, TextStyle defaultStyle);
      public TextBlockOverlay Block { get; }   public LaidOutBlock Layout { get; }
      public TextPosition Caret { get; }       public TextPosition Anchor { get; }   public bool HasSelection { get; }
      public TextStyle StyleAtCaret { get; }   // pending style when set, else style of the char before the caret
      public string Composition { get; }       // IME preedit, drawn underlined at the caret, not part of Block
      public event EventHandler? Changed;
      public InputResult Insert(string text);  // via TextBlockText.Normalize; replaces selection; refuses whole insert if any unsupported char
      public void NewParagraph();  public void LineBreak();
      public void Backspace(bool word);  public void Delete(bool word);
      public void MoveCaret(CaretMove move, bool extend);
      public void PlaceCaret(double xPt, double yPt, bool extend);   // block-local points
      public void SelectWordAt(double xPt, double yPt);  public void SelectParagraphAt(double xPt, double yPt);  public void SelectAll();
      public void ApplyStyle(Func<TextStyle, TextStyle> change);      // selection, or pending style when no selection
      public void SetAlign(TextAlign align);  public void SetLineSpacing(double spacing); // paragraphs touched by selection/caret
      public void SetFrame(double xPt, double yPt, double widthPt, double heightPt, TextBlockWidthMode mode, bool autoHeight, bool shrinkToFit, double rotationDegrees);
      public void SetComposition(string preedit);  public void CommitComposition(string text);
      public bool Undo();  public bool Redo();                        // typing of consecutive characters merges into one step
      public string CopyPlainText();  public string CopyRich();  public InputResult Paste(string? rich, string plain);
      public (double X, double Top, double Height) CaretRect();  public IReadOnlyList<OverlayBox> SelectionRects();
      public bool IsEmpty { get; }
  }
  ```
  Adjacent runs with equal styles are merged after every edit; empty runs are dropped (an empty paragraph keeps one empty run with the paragraph's style).

- [ ] **Step 1: Write the failing tests** (Mono500 tiny font, 10 pt)
  - `Typing_Inserts_With_Pending_Style`: `ApplyStyle(s => s with { Bold = true })` with no selection, then `Insert("ab")` → one run `"ab"` bold.
  - `Style_Applies_Only_To_Selection_And_Merges_Runs`.
  - `Backspace_And_Delete_Join_Paragraphs`; `Word_Delete_Removes_Whole_Word`.
  - `Caret_Moves_By_Words_Lines_And_Visual_Rows`: wrapped paragraph — `Down` keeps the x column.
  - `PlaceCaret_Hits_Nearest_Character_Boundary`.
  - `Unsupported_Script_Is_Refused_Whole`: `Insert("ok שלום")` → `Accepted == false`, `RejectedReasonKey == "TextBlockUnsupportedScript"`, block unchanged.
  - `Undo_Merges_Typing_And_Redo_Restores`.
  - `Rich_Copy_Paste_Keeps_Styles_Plain_Paste_Takes_Caret_Style`.
  - `Paste_From_Spreadsheet` (Review Focus 2): `Paste(null, "a\tb\r\nc\r\n")` → paragraphs `"a b"`, `"c"`, `""`; `Paste(null, new string('x', 100_000))` returns within 2 s.
  - `Composition_Is_Not_Part_Of_The_Block_Until_Committed`.
- [ ] **Step 2: Run** `UT --filter TextBlockEditorModelTests` — FAIL.
- [ ] **Step 3: Implement** (relayout after each edit; caret geometry from `LaidOutPiece.AdvancesPt`).
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: text block editing model (caret, selection, styles, undo, clipboard, IME)`

### Task 7: Writing blocks into the PDF

**Files:**
- Create: `src/NexusPdf.Pdf.Pdfium/PdfiumOverlayWriter.TextBlock.cs` (make `PdfiumOverlayWriter` `partial`)
- Modify: `src/NexusPdf.Pdf.Pdfium/PdfiumOverlayWriter.cs` (switch: `case TextBlockOverlay`), `src/NexusPdf.Pdf.Pdfium/OverlayFontCache.cs` (`ForFace(FontFaceRef face)`; loads `TrueTypeFont.ExtractFace()` bytes, cached by `(Path, FaceIndex)`), `src/NexusPdf.Pdf.Pdfium/PdfiumRenderEngine.cs` (constructor `PdfiumRenderEngine(IXfaPacketWriter? xfaWriter, IFontProvider? fonts)`; existing constructors pass `FontCatalog.System`), `src/NexusPdf.Pdf.Abstractions/OverlayLayers.cs` (`TextBlockOverlay` and `TextBlockReplacement` change text — baked page path, search, selection), `src/NexusPdf.Application/OcrService.cs:255` (pages with a `TextBlockOverlay` are treated like pages with `TextOverlay`).
- Test: `tests/NexusPdf.PdfEngineTests/TextBlockWriteTests.cs`

**Interfaces:**
- Consumes: Tasks 3–5.
- Produces: marked content on every object of a block — name `NexusTextBlock`, string params `Id` (`Guid.ToString("N")`), `V` (`"1"`), `R` (`"{paragraph}:{start}"` for text, `"d:{index}"` for decoration rectangles); the first text object also `Data` (`TextBlockCodec.EncodeStructure`) and `Hash` (lower-case hex SHA-256 of `TextBlockHash.Canonical(pieces)`).
  ```csharp
  public static class TextBlockHash  // in Abstractions/TextBlocks
  { public static string Compute(IEnumerable<(string Text, double FontSizePt, uint ColorArgb, double XPt, double YPt)> pieces); } // values rounded to 0.1, invariant culture
  ```
  One text object per laid-out piece; per character when `Style.LetterSpacingPercent != 0`; decorations are filled rectangles in the run colour. Block-local → displayed: translate by frame, rotate by `RotationDegrees` about the frame centre; displayed → content via the existing `DisplayedToContent` helper and page `/Rotate`.

- [ ] **Step 1: Write the failing tests** (engine `new PdfiumRenderEngine(null, FontCatalog.Scan([TestFontPaths.Directory]))`)
  - `Block_Text_Lands_In_The_File`: two-paragraph Liberation Sans block via `SaveService.SaveAsAsync` → reopened page text contains both paragraphs.
  - `File_Positions_Match_The_Layout`: for each piece, `GetTextObjectAtAsync` at the piece centre returns text starting with the piece text and `XPt` within ±0.5 pt of `frame.X + piece.XPt`.
  - `Rotated_Page_And_Rotated_Block` (Review Focus 3; Theory `/Rotate` 0, 90, 180, 270 × block rotation 0, 30): same assertion in displayed coordinates.
  - `Missing_Glyph_Is_Written_With_Fallback_Font`: Latin-only tiny font + `"aЖb"` → page text `"aЖb"`, `Ж` object font differs from `a`.
  - `Underline_And_Strike_Are_Drawn`: dark pixels under the baseline in the rendered page.
  - `Font_Is_Embedded_Once_Per_Face`: ten blocks with one face → one font object of that face in the saved file.
  - `Signed_Document_Keeps_Signature_With_A_Block`: sign a fixture as `SigningTests` do, add a block, `SaveService.SaveAsAsync` → signature still valid (`PdfSignatureInspector`), block text present.
- [ ] **Step 2: Run** `ET --filter TextBlockWriteTests` — FAIL.
- [ ] **Step 3: Implement** with `fpdf_edit.FPDFPageObjAddMark` / `FPDFPageObjMarkSetStringParam`.
- [ ] **Step 4: Run** the class plus `ET --filter "OverlayTests|TextOverlayFontTests|SaveStructureTests"` — PASS (font-dependent tests skipped on Linux as today).
- [ ] **Step 5: Commit** `feat: write text blocks as marked page text`

### Task 8: Reading stored blocks, replacing them, document fonts

**Files:**
- Create: `src/NexusPdf.Pdf.Pdfium/PdfiumDocumentHandle.TextBlocks.cs`
- Modify: `src/NexusPdf.Pdf.Abstractions/IPdfRenderEngine.cs` (`IPdfDocumentHandle`), `src/NexusPdf.Pdf.Pdfium/PdfiumOverlayWriter.TextBlock.cs` (`TextBlockReplacement` applied before other overlays: remove top-level objects whose mark `Id` matches, then write `NewBlock` with the same Id).
- Test: `tests/NexusPdf.PdfEngineTests/TextBlockReadTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record StoredTextBlock(Guid Id, TextBlockOverlay? Block, OverlayBox BoundsPt, bool Intact); // displayed points for the given rotation
  // IPdfDocumentHandle:
  Task<IReadOnlyList<StoredTextBlock>> GetTextBlocksAsync(int pageIndex, int extraQuarterTurns, CancellationToken ct);
  Task<IReadOnlyList<string>> GetDocumentFontNamesAsync(int maxPages, CancellationToken ct); // base font names of text objects, distinct
  ```
  `Intact` = Data decodes, pieces cover the text exactly, recomputed `TextBlockHash` equals `Hash`. Not intact → `Block = null` (plain text, normal line editing).

- [ ] **Step 1: Write the failing tests**
  - `Saved_Block_Is_Found_Again`: write, save, reopen → one `StoredTextBlock`, `Intact`, block equals the original (except `BlockId`).
  - `Marks_Survive_Repeated_Full_And_Incremental_Saves`.
  - `Replacement_Removes_Old_Objects_And_Keeps_Id`; `Replacement_With_Null_Deletes_The_Block`.
  - `Block_Changed_By_Another_Program_Is_Not_Restored`: after saving, change one marked object's text with raw PDFium (`FPDFTextSetText`) and save → `Intact == false`, page text keeps the foreign change.
  - `Region_Erase_Over_A_Block_Leaves_No_Block`: block + `RegionEraseDraft` over it, save → `GetTextBlocksAsync` empty.
  - `Reopened_Without_The_Font_Uses_Substitute` (Review Focus 4): write with a tiny font `"Gone"`, reopen with a catalog lacking it → `Intact`, text equal; layout pieces of that run have `IsFallback == true`.
  - `Forged_Marks_Do_Not_Break_The_Reader` (Review Focus 5; Theory): mark with `Data` = `"{"`, `R` = `"9999999:-5"`, `Hash` missing, 50 000 marked objects → no exception, `Intact == false`.
  - `Document_Font_Names_Are_Listed`.
- [ ] **Step 2: Run** `ET --filter TextBlockReadTests` — FAIL.
- [ ] **Step 3: Implement** (text of an object via `FPDFTextObjGetText` with a page text handle; colour `FPDFPageObjGetFillColor`; size `FPDFTextObjGetFontSize`; origin from `FPDFPageObjGetMatrix`). If `Marks_Survive_Repeated_Full_And_Incremental_Saves` fails because PDFium drops marks on save, stop and report: the spec's `/PieceInfo` fallback cannot be written through PDFium and needs a decision.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: reopen NexusPDF text blocks for editing with integrity check`

### Task 9: Geometry, selection and rotation of blocks

**Files:**
- Modify: `src/NexusPdf.Pdf.Abstractions/OverlayGeometry.cs` (`OverlayAbilities` gains `bool CanRotate = false`; `TextBlockOverlay` → move, resize, rotate; `TextBlockReplacement` → same through `NewBlock`; `BoundsOf` = frame; `Resized` switches `WidthMode` to `Fixed`; new `Rotated(PageOverlay, double degrees)`), `src/NexusPdf.Pdf.Abstractions/OverlayDisplayMapper.cs` (extra page turns rotate the frame and add to `RotationDegrees`), `src/NexusPdf.Ux/ObjectHandles.cs` (`ResizeHandle.Rotate` above the top-centre handle).
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TextBlockGeometryTests.cs`

**Interfaces:**
- Produces: `OverlayGeometry.Rotated(PageOverlay overlay, double degrees) -> PageOverlay?`; `ObjectHandles.HitTest` returns `ResizeHandle.Rotate`.

- [ ] **Step 1: Write the failing tests**: abilities of a block are `(true, true, true, CanRotate: true)`; `Moved` shifts the frame; `Resized` on an AutoWidth block gives `Fixed`; `Rotated` normalizes to `[0, 360)`; display mapper round-trips a block through 1 and 3 extra turns; `ObjectHandles` finds `Rotate` 20 DIP above the top-centre handle; existing objects keep `CanRotate == false`.
- [ ] **Step 2: Run** `UT --filter "TextBlockGeometryTests|OverlayGeometry|ObjectHandles"` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS, including existing geometry and handle tests.
- [ ] **Step 5: Commit** `feat: move, resize and rotate text blocks`

### Task 10: Font picker and settings

**Files:**
- Create: `src/NexusPdf.Ux/FontPickerModel.cs`, `src/NexusPdf.App.Desktop/Views/Text/FontPicker.xaml(.cs)`
- Modify: `src/NexusPdf.Infrastructure/AppSettings.cs` (`List<string> RecentFonts` (max 8), `TextStyleSettings DefaultTextStyle` with Family `"Segoe UI"`, SizePt 11, Bold, Italic, ColorArgb `0xFF000000`, Align `"Left"`, LineSpacing 1.15), `src/NexusPdf.App.Desktop/Views/InlineTextEditor.cs` (ComboBox → `FontPicker`, first entry stays «Keep: X»), i18n packs.
- Test: `tests/NexusPdf.UnitTests/TextBlocks/FontPickerModelTests.cs`, existing settings round-trip tests.

**Interfaces:**
- Produces:
  ```csharp
  public sealed record FontChoice(string Family, bool HasCyrillic, bool Embeddable, bool Supported); // NexusPdf.Ux
  public enum FontGroup { Recent, InDocument, All }
  public sealed record FontPickerItem(FontGroup Group, string Family, string? SubstituteFamily, bool Enabled, string? NoteKey);
  public static class FontPickerModel
  { public static IReadOnlyList<FontPickerItem> Build(IReadOnlyList<FontChoice> all, IReadOnlyList<string> recent,
        IReadOnlyList<(string PdfName, string? Family)> inDocument, string query); }
  ```
  `NoteKey`: `"FontNoCyrillic"` (enabled), `"FontNotEmbeddable"` (disabled), `"FontNotSupported"` (disabled), `"FontLarge"` (enabled, file over `LargeFontWarningBytes`). Search: case-insensitive substring; empty groups omitted; recent families missing from `all` dropped.

- [ ] **Step 1: Write the failing tests** for grouping, search, disabled reasons, recent ordering and the «written with Arial» substitute.
- [ ] **Step 2: Run** `UT --filter "FontPickerModelTests|Settings"` — FAIL.
- [ ] **Step 3: Implement** the model, the WPF control (popup: search box; list virtualized; names drawn in their own font via `FontFamily(name)` for installed fonts; disabled rows with the note) and the inline editor change; add keys `FontRecent`, `FontInDocument`, `FontAll`, `FontSearch`, `FontNoCyrillic`, `FontNotEmbeddable`, `FontNotSupported`, `FontLarge`, `FontWrittenWith` to ru/en/uk.
- [ ] **Step 4: Run** the tests plus `UT --filter LocalizationCoverageTests`; build App.Desktop with `-p:EnableWindowsTargeting=true` — PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat: font picker with all installed fonts, search, recent and document fonts`

### Task 11: On-page editor control

**Files:**
- Create: `src/NexusPdf.App.Desktop/Views/Text/TextBlockCanvas.cs`, `src/NexusPdf.App.Desktop/Views/Text/GlyphCache.cs`
- Test: DesktopAuditProbe scenario in Task 14 (WPF rendering is not unit-testable on Linux); model logic is covered by Task 6.

**Interfaces:**
- Consumes: `TextBlockEditorModel` (Task 6).
- Produces:
  ```csharp
  internal sealed class TextBlockCanvas : FrameworkElement
  {
      public TextBlockCanvas(TextBlockEditorModel model, double zoom);
      public double Zoom { get; set; }
      public event EventHandler? CommitRequested;   // Esc, focus lost to the page
      public event EventHandler<string>? InputRejected; // localization key for the status bar
  }
  ```
  Drawing: `GlyphRun` per `LaidOutPiece` with `GlyphTypeface` from the face file (`.ttc` face via URI fragment `#<faceIndex>`; verify with `cambria.ttc`), glyph indices from `CharacterToGlyphMap`, advances = `AdvancesPt × zoom` — never WPF's own advances; decorations as rectangles; fallback characters with a dotted underline; selection rectangles; caret at `CaretRect()` blinking at `GetCaretBlinkTime()` (user32); composition text underlined. Input: `TextInput` and `TextCompositionManager` start/update handlers (IME window at the caret through `InputMethod`), keys per the mockup table (Ctrl+B/I/U, Ctrl+Z/Y, Ctrl+A/C/X/V, word moves), mouse: click, drag, double/triple click, Shift+click; clipboard formats `"NexusPdf.TextBlock"` (rich) + `UnicodeText`. UI Automation: `TextBlockCanvasAutomationPeer` with `ValuePattern` and `TextPattern` (document range, selection, caret range) so Narrator reads it and audits find it by `AutomationId = "TextBlockEditor"`.

- [ ] **Step 1:** Build App.Desktop — compiles with the new control, 0 warnings.
- [ ] **Step 2:** Manual check on Windows is impossible here; the Task 14 probe drives the control. Write the probe hooks now: `AutomationId`, `ValuePattern.SetValue` routed through `Insert`.
- [ ] **Step 3: Commit** `feat: on-page text block editor drawn by the shared layout`

### Task 12: Text tool and block lifecycle

**Files:**
- Modify: `src/NexusPdf.App.Desktop/ViewModels/MainViewModel.cs` (`AddTextOverlay` → `ToggleTextTool`), `src/NexusPdf.App.Desktop/Views/MainWindow.xaml:234` (toggle button bound to `ActiveDocument.IsTextTool`), `src/NexusPdf.App.Desktop/ViewModels/DocumentViewModel.cs` (new partial `DocumentViewModel.TextBlocks.cs`), `src/NexusPdf.App.Desktop/Views/DocumentView.xaml(.cs)` (mouse routing, hosting `TextBlockCanvas` in the page adorner layer), `src/NexusPdf.Ux/CommandIds.cs` (`AddText` id kept; label becomes «Text»), i18n packs.
- Delete: `src/NexusPdf.App.Desktop/Views/AddTextDialog.xaml`, `AddTextDialog.xaml.cs` and their now-unused strings.
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TextBlockSessionTests.cs` (operations on `DocumentSession` without WPF)

**Interfaces:**
- Consumes: Tasks 6–11.
- Produces (DocumentViewModel): `bool IsTextTool`, `void ToggleTextTool()`, `Task BeginTextBlockAtAsync(PageViewModel page, double xPt, double yPt)` (AutoWidth, default style from settings), `Task BeginTextBlockInRectAsync(PageViewModel page, OverlayBox rect)` (Fixed, AutoHeight), `Task EditTextBlockAsync(PageViewModel page, double xPt, double yPt)` (draft or stored block under the point), `void CommitTextBlock()`, `TextBlockEditorModel? ActiveTextBlock`. Session effects (one undo step each): new block → `AddOverlayOperation(page, block)`; edited draft → `ReplaceOverlayOperation`; edited stored block → `AddOverlayOperation(page, new TextBlockReplacement(id, block))` or `ReplaceOverlayOperation` of the pending replacement; empty on commit → nothing for a new block, `TextBlockReplacement(id, null)` / `RemoveOverlayAtOperation` otherwise. Single click on a stored block selects it; moving/resizing/rotating/Delete on it creates or updates its `TextBlockReplacement`.

- [ ] **Step 1: Write the failing tests** (`TextBlockSessionTests`): committing a new block adds one overlay and one undo step; undo removes it; editing a stored block adds a replacement whose `BlockId` matches; deleting a stored block adds `TextBlockReplacement(id, null)`; a second edit of the same stored block replaces the pending replacement instead of stacking another.
- [ ] **Step 2: Run** `UT --filter TextBlockSessionTests` — FAIL.
- [ ] **Step 3: Implement** — put the session rules into `src/NexusPdf.Application/TextBlockSessionService.cs` (`Commit(DocumentSession session, int pageIndex, TextBlockOverlay? original, Guid? storedId, TextBlockOverlay? edited)`), call it from the view model; wire the tool, clicks, drag-to-create (minimum 8 pt drag, else click), double click, Esc/click-away commit, tab switch and save commit the active block first.
- [ ] **Step 4: Run** the tests, `UT --filter LocalizationCoverageTests`, build App.Desktop — PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat: Text tool replaces the add-text dialog; blocks are created, edited and re-edited on the page`

### Task 13: Floating toolbar and properties panel

**Files:**
- Create: `src/NexusPdf.App.Desktop/Views/Text/TextBlockToolbar.xaml(.cs)`, `src/NexusPdf.App.Desktop/Views/Text/ColorPickerPopup.xaml(.cs)`
- Modify: `src/NexusPdf.App.Desktop/Views/DocumentView.xaml` (properties panel section «Text»: Font, Paragraph, Character, Box, Use as default style — mockup section 1, item 4), `DocumentViewModel.TextBlocks.cs` (bindable style state from `StyleAtCaret`, commands), i18n packs.
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TextBlockStyleCommandsTests.cs`

**Interfaces:**
- Produces: `src/NexusPdf.Ux/TextBlockStyleCommands.cs` — pure helpers used by both toolbar and panel: `static double NextSize(double current, int direction)` (steps 6, 7, 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72; above 72 by 12), `static bool TryParseSize(string text, out double pt)` (1–999, culture-aware), `static bool TryParseColor(string text, out uint argb)` (`#RGB`, `#RRGGBB`, `#AARRGGBB`).

- [ ] **Step 1: Write the failing tests** for the three helpers (11 + 1 → 12, 72 + 1 → 84, 10.5 − 1 → 10; `"11,5"` in ru-RU → 11.5; `"#1B1C20"` → `0xFF1B1C20`).
- [ ] **Step 2: Run** `UT --filter TextBlockStyleCommandsTests` — FAIL.
- [ ] **Step 3: Implement** helpers, toolbar (popup above the block, follows scroll/zoom, «⋯» holds line spacing, letter spacing, super/subscript, Auto height, Shrink to fit when the window is narrow), colour popup (palette of six as today, recent colours, hex box), properties section (X/Y/W/H in mm as in the existing object panel, rotation, Auto height, Shrink to fit, «Use as default style» writes `AppSettings.DefaultTextStyle`), recent fonts updated on every font change.
- [ ] **Step 4: Run** tests, localization tests, App.Desktop build — PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat: text block toolbar and properties panel`

### Task 14: UI audit scenario, docs, version

**Files:**
- Modify: `tests/NexusPdf.DesktopAuditProbe/` (add a scenario: Text tool click in an empty cell of a generated form, type «24 Oct 2026», Ctrl+B, change font through the picker, drag to resize, click away, save, reopen, double click, edit, save — assert text in the file and the block found again; drag-to-create a wrapped block; IME-less typing of Cyrillic), `CHANGELOG.md`, `docs/RELEASE-0.28.0.md`, `docs/USER_GUIDE.md` (text section), `docs/KNOWN_LIMITATIONS.md` (remove the «moving added overlay» row for text; add complex scripts, FreeText not used), `docs/INTERACTIVE_PDF_SUPPORT_MATRIX.md`, `docs/ARCHITECTURE.md` (TextBlocks in Abstractions), `Directory.Build.props` / `app.manifest` / `tools/verify-release.ps1` → 0.28.0.
- [ ] **Step 1:** Add the probe scenario; build the probe with `-p:EnableWindowsTargeting=true`.
- [ ] **Step 2:** Run `UT` and `ET` in full on Linux — only the known Windows-only failures (GDI+, Segoe UI/Arial, bundled qpdf) remain.
- [ ] **Step 3:** Update docs and version.
- [ ] **Step 4: Commit** `docs: text block release notes, guide and 0.28.0`
- [ ] **Step 5:** Push the branch and open a PR only after Artur's «да»; CI (`ci.yml`) must be green; the probe runs in `release-audit.yml` at release time.
