# One Text Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One "Text" mode on the level of Acrobat Pro: existing text is edited as paragraphs, blank form cells and lines are filled in place, blocks move and resize while typing — released as NexusPDF 1.0.0.

**Architecture:** Pure geometry and grouping live in `Pdf.Abstractions/TextBlocks` (form targets, paragraph builder, model additions) and are fed by one new engine call that describes a page's text objects, thin lines and rulings in displayed points. A new overlay `TextParagraphReplacement` removes a paragraph's objects (verified by fingerprint) and writes a text block in their place, so an edited paragraph becomes an ordinary, re-editable block. The WPF layer only routes hover/click/keys to these pieces and reuses the existing block editor.

**Tech Stack:** C# / .NET 10, WPF, PDFiumCore 153 (own DllImports where bindings are wrong), xUnit; tests on Linux with `-p:EnableWindowsTargeting=true`.

**Spec:** `docs/superpowers/specs/2026-10-24-text-editing-design.md` (builds on `docs/superpowers/specs/2026-10-23-text-block-design.md`).

## Global Constraints

- Version at the end: **1.0.0** (`Directory.Build.props` 4 fields, `app.manifest` `1.0.0.0`, `tools/verify-release.ps1` default, README Download link, `CHANGELOG.md`, `docs/RELEASE-1.0.0.md`).
- Block mark stays `V=1`; new codec keys are optional and written only when not default: `VA` (vertical align), `FI` (first-line indent), `LI` (left indent). Marks written by 0.28.0 decode unchanged.
- Engines see only `Pdf.Abstractions`; UI strings in ru/en/uk (`Resources/i18n/*.json`); public repo — no internal hosts, paths or ship data.
- Never change text the person did not edit; never write missing-glyph boxes; a refused paragraph leaves the existing line editing (`InlineTextEditor`) as the path.
- Build/test on fort with `-m:2`, `nice`; `agent-lock` held; `dotnet build-server shutdown` after.
- `UT` = `nice dotnet test tests/NexusPdf.UnitTests/NexusPdf.UnitTests.csproj -c Release -m:2 -p:EnableWindowsTargeting=true`; `ET` = same for `tests/NexusPdf.PdfEngineTests/NexusPdf.PdfEngineTests.csproj`; `APP` = `nice dotnet build src/NexusPdf.App.Desktop/NexusPdf.App.Desktop.csproj -c Release -m:2 -p:EnableWindowsTargeting=true -warnaserror`.

## Review Focus

1. Word-exported PDFs where every word (or glyph run) is its own text object with no space characters — paragraph text must get spaces from horizontal gaps (> ~0.15 × size), not run words together (test in Task 6).
2. A paragraph in an embedded subset font that is not installed — it is written with the substitute family, keeps every character (fallback faces), and the toolbar says which font is used (tests in Tasks 6 and 8).
3. A page with `/Rotate 90` and a rotated paragraph — detection and the rewritten block land where the text was, in displayed points (test in Task 7).
4. A CAD-like page with tens of thousands of line segments — hover target search stays fast (cell search < 200 ms on 50 000 rulings, cached per page) (test in Task 4).
5. A paragraph whose objects changed after detection (another edit, undo) — saving fails with a clear message instead of removing the wrong objects (test in Task 7).

---

### Task 1: Vertical alignment and paragraph indents

**Files:**
- Modify: `src/NexusPdf.Pdf.Abstractions/TextBlocks/TextBlockModel.cs` (`TextBlockOverlay` gains `TextVerticalAlign VerticalAlign = Top` as the LAST optional parameter after `BlockId`; `TextParagraph` gains `double FirstLineIndentPt = 0, double LeftIndentPt = 0`), `TextBlockLayout.cs`, `TextBlockCodec.cs`, `TextBlockEditorModel.cs` (`Para` carries indents; `SetIndents(double firstLine, double left)` for the paragraphs of the selection; `SetVerticalAlign`).
- Test: `tests/NexusPdf.UnitTests/TextBlocks/TextBlockLayoutTests.cs`, `TextBlockCodecTests.cs`, `TextBlockEditorModelTests.cs`.

**Interfaces:**
- Produces: `public enum TextVerticalAlign { Top, Center, Bottom }`; layout shifts all lines by `(frameHeight − contentHeight) × {0, ½, 1}` when the height is fixed; the first line of a paragraph starts at `LeftIndentPt + FirstLineIndentPt`, the others at `LeftIndentPt`, and the wrap width shrinks accordingly (negative first-line indent = hanging).

- [ ] **Step 1: Write the failing tests**: `Center_And_Bottom_Shift_Lines_In_A_Fixed_Frame` (fixed height 2·Pad+40, one 10 pt line → top offset 15 / 30 pt from Top); `Auto_Height_Ignores_Vertical_Align`; `First_Line_And_Hanging_Indents` (Mono500, width 2·Pad+30, "aaa bbb ccc", FI 10 → first line "aa…" wraps one letter earlier; FI −10 with LI 10 → first line starts at Pad, others at Pad+10); codec: round trip keeps `VA`, `FI`, `LI`; `Old_Mark_Without_New_Keys_Decodes_As_Before`; editor: `SetIndents_Applies_To_Selected_Paragraphs`, caret X on the first line includes the indent.
- [ ] **Step 2: Run** `UT --filter "TextBlockLayoutTests|TextBlockCodecTests|TextBlockEditorModelTests"` — FAIL (compile).
- [ ] **Step 3: Implement.** Codec: `VA` string enum, `FI`/`LI` numbers in −1000…1000; omit defaults.
- [ ] **Step 4: Run** — PASS; also `UT --filter TextBlocks` all green.
- [ ] **Step 5: Commit** `feat: vertical alignment and paragraph indents in text blocks`

### Task 2: Move, resize and rotate while typing

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/BlockFrameDrag.cs` (pure: which zone of the editor frame is under a point, and the frame after a drag; lives in Abstractions because `NexusPdf.Ux` references no other project and cannot see `TextBlockOverlay`).
- Modify: `src/NexusPdf.App.Desktop/Views/Text/TextBlockCanvas.cs` (draw 8 handles + rotate handle, hit zones, cursors, drag → `TextBlockEditorModel.SetFrame`; Ctrl not needed), `TextBlockHost.cs` (sync during drags), `DocumentView.xaml.cs` (Text tool: mouse down on a selected/edited block's border moves instead of editing).
- Test: `tests/NexusPdf.UnitTests/TextBlocks/BlockFrameDragTests.cs`.

**Interfaces:**
- Produces:
  ```csharp
  public enum FrameZone { Inside, Border, Handle, Rotate, Outside }
  public enum FrameHandle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }
  public static class BlockFrameDrag
  {
      public static (FrameZone Zone, FrameHandle Handle) HitTest(OverlayBox frameLocal, double x, double y,
          double borderPt, double handlePt, double rotateOffsetPt);
      public static TextBlockOverlay Apply(TextBlockOverlay start, FrameZone zone, FrameHandle handle,
          double dxDisplayed, double dyDisplayed, double pointerXDisplayed, double pointerYDisplayed);
  }
  ```
  Points are in block-local coordinates for `HitTest` (sizes already converted from screen points by the caller), displayed points for `Apply`, which delegates to `OverlayGeometry.Moved`/`Resized`/`Rotated` (resize fixes the width and clears AutoHeight only when the height changed; rotation snaps to right angles within 3°).

- [ ] **Step 1: Write the failing tests**: border vs inside vs handle vs rotate hit-test at scale 1 and 2; `Border_Drag_Moves_The_Frame`; `Handle_Drag_On_A_Rotated_Block_Keeps_The_Opposite_Edge` (reuse the 90° numbers from `TextBlockGeometryTests`); `Rotate_Drag_Snaps_To_Right_Angles_Within_3_Degrees`.
- [ ] **Step 2: Run** `UT --filter BlockFrameDragTests` — FAIL.
- [ ] **Step 3: Implement** the helper, then the canvas: border band 6 DIP, handles `ObjectHandles.HandleSizeDip`, rotate handle `RotateHandleOffsetDip` above top-centre; mouse capture during drag; one undo step for the whole edit (the frame lives in the editor model until commit).
- [ ] **Step 4: Run** the tests and `APP` — PASS, 0 warnings.
- [ ] **Step 5: Commit** `feat: move, resize and rotate a text block while typing`

### Task 3: Page text layout from the engine

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/PageTextLayout.cs`, `src/NexusPdf.Pdf.Pdfium/PdfiumDocumentHandle.PageTextLayout.cs`.
- Modify: `src/NexusPdf.Pdf.Abstractions/IPdfRenderEngine.cs` (`IPdfDocumentHandle`).
- Test: `tests/NexusPdf.PdfEngineTests/PageTextLayoutTests.cs`.

**Interfaces:**
- Produces (all coordinates in displayed points for `extraQuarterTurns`, y down):
  ```csharp
  public sealed record PageTextObject(IReadOnlyList<int> Path, string Text, OverlayBox BoundsPt,
      double OriginXPt, double OriginYPt, double AngleDeg, double FontSizePt, string FontName,
      bool Bold, bool Italic, uint ColorArgb, bool Invisible, bool Type3, bool InForm,
      IReadOnlyList<double> CharAdvancesPt, string Fingerprint);
  public sealed record PageLineObject(IReadOnlyList<int> Path, OverlayBox BoundsPt, uint ColorArgb); // thin filled rect or stroked segment
  public sealed record PageRuling(bool Horizontal, double PositionPt, double StartPt, double EndPt);
  public sealed record PageTextLayout(IReadOnlyList<PageTextObject> Objects, IReadOnlyList<PageLineObject> Lines,
      IReadOnlyList<PageRuling> Rulings);
  // IPdfDocumentHandle:
  Task<PageTextLayout> GetPageTextLayoutAsync(int pageIndex, int extraQuarterTurns, CancellationToken ct);
  ```
  `Text` is read char by char without generated characters (as `TextByObject` in `PdfiumDocumentHandle.TextBlocks.cs`); `FontSizePt` = font size × matrix scale; `Fingerprint` = SHA-256 hex of text + rounded origin (0.01) + size — the same function the writer uses to verify (Task 7). `Lines` = top-level path objects whose box is thinner than 1.5 pt in one direction; `Rulings` = the existing ruling extraction mapped to displayed points.

- [ ] **Step 1: Write the failing tests** on `PdfFixture` pages (`RawContent` with Helvetica): two paragraphs → objects with right text, origin and size; a `0.5 0 0 0.5` matrix halves `FontSizePt`; `/Rotate 90` page → displayed coordinates; an underline `re f` → one `PageLineObject`; a 3×2 grid → rulings; text inside a Form XObject → `InForm`; render mode 3 → `Invisible`.
- [ ] **Step 2: Run** `ET --filter PageTextLayoutTests` — FAIL.
- [ ] **Step 3: Implement** with `PdfObjectTree` (paths, nested forms), `FPDFTextGetTextObject`/`IsGenerated` per char, `FPDFPageObjGetBounds`, matrix angle, `FPDFFontGetFlags`/weight for bold/italic, `FPDFFontGetIsEmbedded`; Type3 via `FPDFFont` type flag.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: describe a page's text objects, thin lines and rulings in displayed points`

### Task 4: Form targets

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/FormTargets.cs`.
- Test: `tests/NexusPdf.UnitTests/TextBlocks/FormTargetsTests.cs`.

**Interfaces:**
- Produces:
  ```csharp
  public enum FormTargetKind { Cell, CaptionCell, Line, TextBlank }
  public sealed record FormTarget(FormTargetKind Kind, OverlayBox AreaPt, OverlayBox CellPt, int TableId, int Row, int Column);
  public sealed class FormTargetIndex
  {
      public static FormTargetIndex Build(PageTextLayout layout, double defaultFontSizePt);
      public FormTarget? At(double xPt, double yPt);
      public FormTarget? Next(FormTarget from, bool backwards);   // same TableId, row-major, skips filled cells
  }
  ```
  Cells: smallest closed rectangle of merged horizontal/vertical rulings around the point (snap 1.5 pt, gaps ≤ 2 pt, side coverage ≥ 90 %); empty = no visible object text inside; with text → `CaptionCell`, `AreaPt` = largest empty rectangle right of / below the caption on the side of the point. Line: horizontal ruling ≥ 20 pt with no text in the band of 1.5 × default size above it; `AreaPt` from the point (or line start) to the line end, bottom 1 pt above the line. TextBlank: an object word of ≥ 3 `_` or `.` → its bounds.

- [ ] **Step 1: Write the failing tests**: empty cell in a grid; merged cell (missing inner border) is one cell; open rectangle → no cell; caption cell → area right of "Name of ship"; underline line; text blank "ETA ____" and ".........."; `Next` row-major and backwards, skipping filled cells; `Fifty_Thousand_Rulings_Stay_Fast` (build + 100 lookups < 200 ms).
- [ ] **Step 2: Run** `UT --filter FormTargetsTests` — FAIL.
- [ ] **Step 3: Implement** (sort + bucket by position for speed; reuse merge tolerances from `RulingTableDetector`).
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: find blank form cells, lines and text blanks under the pointer`

### Task 5: Filling forms in the Text tool

**Files:**
- Modify: `src/NexusPdf.App.Desktop/ViewModels/DocumentViewModel.TextBlocks.cs` (per-page cache of `PageTextLayout` + `FormTargetIndex` next to stored blocks; `TextTargetAt(page, x, y)` in the spec's order; `BeginTextBlockInTargetAsync`; `MoveToNextCellAsync(bool backwards)`; `ArmMark(MarkKind)` and placement), `PageViewModel.cs` (`HoverTargetRect`, `HoverTargetKind`), `DocumentView.xaml(.cs)` (hover outline/fill, click routing, Tab from the editor), `Views/Text/TextBlockCanvas.cs` (`TabRequested` event), `Views/Text/TextBlockToolbar.xaml(.cs)` (✓ ✗ ● insert at caret), `MainWindow.xaml` (✓ ✗ ● next to the Text tool, visible when it is on), i18n packs.
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/FormFill.cs` (pure: block for a target and a default style — frame, size cap `(h − 2·Pad)/1.2`, Center/Bottom, ShrinkToFit; mark block centred at 70 % height).
- Test: `tests/NexusPdf.UnitTests/TextBlocks/FormFillTests.cs`.

**Interfaces:**
- Consumes: Tasks 1, 3, 4.
- Produces: `public static TextBlockOverlay FormFill.BlockFor(FormTarget target, TextStyle style, TextAlign align, double spacing)`; `public static TextBlockOverlay FormFill.MarkFor(FormTarget? target, double xPt, double yPt, char mark, TextStyle style)`.

- [ ] **Step 1: Write the failing tests** for `FormFill`: cell → frame inset 1 pt, Fixed, AutoHeight off, ShrinkToFit on, `Center`, size capped for a 12 pt-high cell; line → `Bottom`, frame bottom 1 pt above the line, right edge at the line end; mark in a cell → centred, size 70 % of height; mark without target → at the point with the default size.
- [ ] **Step 2: Run** `UT --filter FormFillTests` — FAIL.
- [ ] **Step 3: Implement** `FormFill`, then the view model and view wiring; Tab in a cell block commits and opens `Next`; no next → normal Tab. Marks use a face from `FontCatalog.FallbackFor` for ✓ (U+2713), ✗ (U+2717), ● (U+25CF); a mark nobody can draw disables its button with a reason.
- [ ] **Step 4: Run** the tests, `UT --filter Localization`, `APP` — PASS.
- [ ] **Step 5: Commit** `feat: fill form cells, lines and text blanks with the Text tool; Tab between cells; marks`

### Task 6: Paragraph builder

**Files:**
- Create: `src/NexusPdf.Pdf.Abstractions/TextBlocks/ParagraphBuilder.cs`.
- Test: `tests/NexusPdf.UnitTests/TextBlocks/ParagraphBuilderTests.cs`.

**Interfaces:**
- Consumes: `PageTextLayout` (Task 3), `FormTargetIndex` (Task 4, for "inside a ruled cell"), `FontSubstitution`, `IFontProvider`.
- Produces:
  ```csharp
  public sealed record DetectedParagraph(IReadOnlyList<PageTextObject> Objects, IReadOnlyList<PageLineObject> Decorations,
      OverlayBox BoundsPt, TextBlockOverlay? Block, string? RefusedKey, IReadOnlyList<(string Document, string Written)> SubstitutedFonts);
  public static class ParagraphBuilder
  { public static IReadOnlyList<DetectedParagraph> Build(PageTextLayout layout, FormTargetIndex cells, IFontProvider fonts); }
  ```
  Rules from the spec: lines by baseline (±0.3 size) and gap (> 2 sizes = columns); paragraphs by equal pitch (±20 %), one rotation, shared edge or centre; spaces from gaps between objects (> 0.15 size) when the text has none; joins (space / hyphen / deliberate line break); indents; alignment; line spacing; runs with substituted families; Bold/Italic from name (`Bold`, `Black`, `Heavy`, `Italic`, `Oblique`) or flags; underline/strike lines; width rule (multi-line Fixed, single-line AutoWidth, inside a cell → cell width + ShrinkToFit + the vertical position it had). Refusals: `InForm`, `Type3`, `Invisible`, complex scripts (`TextBlockText.IsSupported`), mixed angle, overlapping objects that cannot merge.

- [ ] **Step 1: Write the failing tests** (synthetic `PageTextObject`s): two columns → two paragraphs; centred lines → `Center`; justified → `Justify`; hyphen join; deliberate break before a word that fitted; "Address:" line keeps a break; first-line indent and hanging indent; per-word objects without spaces get spaces; bold/italic by name; substituted family and the reported pair; underline line → `Underline` on the run above it and listed in `Decorations`; table border not taken; single line → AutoWidth; text in a cell → cell width + ShrinkToFit; refusals by reason key.
- [ ] **Step 2: Run** `UT --filter ParagraphBuilderTests` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: group a page's text objects into editable paragraphs`

### Task 7: Replacing paragraphs in the file

**Files:**
- Modify: `src/NexusPdf.Pdf.Abstractions/TextBlocks/TextBlockModel.cs` (`public sealed record TextParagraphReplacement(IReadOnlyList<ParagraphObjectRef> Objects, TextBlockOverlay? NewBlock) : PageOverlay;` with `public sealed record ParagraphObjectRef(IReadOnlyList<int> Path, string Fingerprint);`), `PdfiumOverlayWriter.TextBlock.cs` (apply before other overlays: resolve each path, recompute fingerprint, mismatch → `PdfEngineException` with a clear message; remove; write `NewBlock` with a new Id), `OverlayLayers.cs`, `OverlayGeometry.cs`, `OverlayDisplayMapper.cs` (like `TextBlockReplacement`), `src/NexusPdf.Application/RedactionBaker.cs` (removal before the raster, new block carried), `TextBlockSessionService.cs` (`CommitParagraph(session, pageIndex, DetectedParagraph source, TextBlockOverlay? edited)`: a second edit of the same object set replaces the pending replacement; deleting writes `NewBlock = null`).
- Test: `tests/NexusPdf.PdfEngineTests/ParagraphReplacementTests.cs`, `tests/NexusPdf.UnitTests/TextBlocks/TextBlockSessionTests.cs`.

**Interfaces:**
- Consumes: Tasks 3, 6.
- Produces: the overlay and `TextBlockSessionService.CommitParagraph` as above; the fingerprint function is shared with Task 3 (`PageTextFingerprint.Of(text, originX, originY, size)` in Abstractions).

- [ ] **Step 1: Write the failing tests**: detect paragraphs on a fixture with three paragraphs, replace the middle one with new text → saved page text has the new text, not the old, the other two unchanged, and `GetTextBlocksAsync` finds one intact block; underline path removed with its paragraph; `/Rotate 90` page with a rotated paragraph lands in place (Review Focus 3); signed fixture is appended and the signature stays valid; erase elsewhere on the page drops the old paragraph from the raster; an object changed after detection → `PdfEngineException` (Review Focus 5); session: second edit replaces, delete writes null.
- [ ] **Step 2: Run** `ET --filter ParagraphReplacementTests` and `UT --filter TextBlockSessionTests` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS, plus `ET --filter "TextBlock|Overlay|Redaction|RegionErase"`.
- [ ] **Step 5: Commit** `feat: replace a paragraph of existing text with an editable block`

### Task 8: Editing existing text in the app

**Files:**
- Modify: `DocumentViewModel.TextBlocks.cs` (paragraph targets from the cached layout via `ParagraphBuilder`; `EditTextBlockAsync` tries blocks, then paragraphs; `TextBlockEdit` gains `DetectedParagraph? Paragraph` and `string? FontNote`; commit goes through `CommitParagraph`; refusal → `RequestInlineEdit` with the reason in the status bar), `PageViewModel.cs` (hover paragraph outline), `DocumentView.xaml.cs` (double click without the tool: blocks/paragraphs first, line editing otherwise), `TextBlockToolbar.xaml(.cs)` (font note line), i18n packs (`TextFontSubstituted` with `{0}` document font and `{1}` written font; refusal keys).
- Test: build + probe (Task 9); pure parts are covered in Tasks 6–7.

- [ ] **Step 1:** Implement; `APP` — 0 warnings; `UT --filter Localization` — PASS.
- [ ] **Step 2: Commit** `feat: click a paragraph of existing text to edit it as a block`

### Task 9: Probe, docs, 1.0.0

**Files:**
- Modify: `tests/NexusPdf.DesktopAuditProbe/Program.cs` (cherry-pick `7e1d9ad` "probe waits for the tab to switch to the saved file"; new scenario: a generated form with a ruled table and an underline line — Text tool, click a cell, type, Tab to the next cell, type, mark ✓; click a paragraph of existing text, edit, drag the frame border to move it; save, reopen, the edited paragraph opens again as a block), `CHANGELOG.md`, `docs/RELEASE-1.0.0.md`, `docs/USER_GUIDE.md`, `docs/KNOWN_LIMITATIONS.md`, `docs/INTERACTIVE_PDF_SUPPORT_MATRIX.md`, `docs/ARCHITECTURE.md`, version files (Global Constraints).
- [ ] **Step 1:** Probe scenario; build the probe (`-warnaserror`).
- [ ] **Step 2:** Full `UT` and `ET` — only the known environmental failures (GDI+, tessdata, bundled qpdf.exe).
- [ ] **Step 3:** Docs and version 1.0.0.
- [ ] **Step 4: Commit** `docs: one Text mode, release notes and 1.0.0`
- [ ] **Step 5:** Push and PR only after Artur's «да».
