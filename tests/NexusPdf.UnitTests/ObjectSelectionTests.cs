using NexusPdf.Pdf.Abstractions;
using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Рамка выделенного объекта и его перетаскивание. Здесь ловятся ровно те
/// ошибки, которые пользователь описывает как «объект прыгает при захвате» и
/// «рамка вывернулась наизнанку».
/// </summary>
public sealed class ObjectSelectionTests
{
    private static readonly HandleBox Box = new(100, 200, 60, 40);

    // ----- Ручки -----

    [Fact]
    public void Every_Handle_Sits_On_Its_Own_Corner_Or_Side()
    {
        Assert.Equal((100.0, 200.0), ObjectHandles.CenterOf(Box, ResizeHandle.TopLeft));
        Assert.Equal((160.0, 240.0), ObjectHandles.CenterOf(Box, ResizeHandle.BottomRight));
        Assert.Equal((130.0, 200.0), ObjectHandles.CenterOf(Box, ResizeHandle.Top));
        Assert.Equal((100.0, 220.0), ObjectHandles.CenterOf(Box, ResizeHandle.Left));
    }

    [Fact]
    public void Clicking_A_Corner_Grabs_The_Handle_Not_The_Object()
    {
        // Промах по ручке — это перетаскивание вместо растягивания, то есть
        // испорченный объект.
        Assert.Equal(ResizeHandle.TopLeft, ObjectHandles.HitTest(Box, 101, 201, canResize: true));
        Assert.Equal(ResizeHandle.BottomRight, ObjectHandles.HitTest(Box, 159, 239, canResize: true));
    }

    [Fact]
    public void Clicking_Inside_Grabs_The_Object()
    {
        Assert.Equal(ResizeHandle.Move, ObjectHandles.HitTest(Box, 130, 220, canResize: true));
    }

    [Fact]
    public void Clicking_Far_Away_Hits_Nothing()
    {
        Assert.Equal(ResizeHandle.None, ObjectHandles.HitTest(Box, 500, 500, canResize: true));
    }

    [Fact]
    public void Objects_That_Do_Not_Resize_Give_Move_Even_On_Their_Corner()
    {
        // У надписи и заметки ручек нет: там угол — это тоже тело объекта.
        Assert.Equal(ResizeHandle.Move, ObjectHandles.HitTest(Box, 100, 200, canResize: false));
    }

    [Fact]
    public void Handle_Tolerance_Follows_The_Zoom()
    {
        // На мелком масштабе допуск в точках страницы должен расти, иначе
        // попасть в ручку пальцем или мышью невозможно.
        Assert.Equal(ResizeHandle.None, ObjectHandles.HitTest(Box, 88, 200, canResize: true, scale: 1));
        Assert.Equal(ResizeHandle.TopLeft, ObjectHandles.HitTest(Box, 88, 200, canResize: true, scale: 3));
    }

    // ----- Перетаскивание -----

    [Fact]
    public void Dragging_The_Body_Moves_Without_Changing_Size()
    {
        var moved = ObjectHandles.Drag(Box, ResizeHandle.Move, 15, -25);
        Assert.Equal(new HandleBox(115, 175, 60, 40), moved);
    }

    [Fact]
    public void Dragging_A_Corner_Moves_Only_That_Corner()
    {
        var resized = ObjectHandles.Drag(Box, ResizeHandle.BottomRight, 10, 20);
        Assert.Equal(new HandleBox(100, 200, 70, 60), resized);

        var fromTopLeft = ObjectHandles.Drag(Box, ResizeHandle.TopLeft, 10, 5);
        Assert.Equal(new HandleBox(110, 205, 50, 35), fromTopLeft);
    }

    [Fact]
    public void Side_Handles_Change_Only_One_Dimension()
    {
        Assert.Equal(new HandleBox(100, 200, 60, 55), ObjectHandles.Drag(Box, ResizeHandle.Bottom, 99, 15));
        Assert.Equal(new HandleBox(100, 200, 75, 40), ObjectHandles.Drag(Box, ResizeHandle.Right, 15, 99));
    }

    [Fact]
    public void Dragging_Through_The_Opposite_Side_Is_Straightened_Out()
    {
        // Рамку вывернули: ширина ушла в минус — модель обязана привести её
        // в порядок, а не сохранить вывернутый прямоугольник.
        var inverted = ObjectHandles.Drag(Box, ResizeHandle.Right, -100, 0);
        Assert.True(inverted.Width < 0);

        var normalized = OverlayGeometry.Normalize(
            new OverlayBox(inverted.X, inverted.Y, inverted.Width, inverted.Height));
        Assert.True(normalized.WidthPt >= OverlayGeometry.MinimumSizePt);
        Assert.True(normalized.XPt <= inverted.X);
    }

    // ----- Привязка -----

    [Fact]
    public void Grid_Snapping_Only_Works_Near_A_Node()
    {
        Assert.Equal(100, Snapping.ToGrid(102, 10));         // рядом — прилипло
        // Между узлами объект обязан остаться там, куда его поставили: иначе
        // сетка из помощи превращается в клетку.
        Assert.Equal(105.5, Snapping.ToGrid(105.5, 10), 3);
        Assert.Equal(104, Snapping.ToGrid(104, 10), 3);
    }

    [Fact]
    public void Guides_Win_Over_The_Grid()
    {
        var guides = new[] { 103.0 };
        var box = Snapping.Apply(new HandleBox(102, 50, 10, 10),
            useGrid: true, gridStepPt: 10, verticalGuides: guides, horizontalGuides: Array.Empty<double>());
        // Направляющую пользователь поставил осознанно — она важнее сетки.
        Assert.Equal(103, box.X, 3);
    }

    [Fact]
    public void Snapping_Never_Changes_The_Size()
    {
        var box = Snapping.Apply(new HandleBox(102, 207, 60, 40),
            useGrid: true, gridStepPt: 10,
            verticalGuides: Array.Empty<double>(), horizontalGuides: Array.Empty<double>());
        Assert.Equal(60, box.Width);
        Assert.Equal(40, box.Height);
    }

    // ----- Возможности объектов -----

    [Fact]
    public void Image_And_Shape_Can_Be_Moved_And_Resized()
    {
        var image = new ImageOverlay(new byte[4], 1, 1, 10, 20, 100, 50);
        Assert.True(OverlayGeometry.AbilitiesOf(image).CanResize);

        var shape = new ShapeAnnotationDraft(10, 20, 100, 50, 0, 0, 1, false, "", "");
        Assert.True(OverlayGeometry.AbilitiesOf(shape).CanResize);
    }

    [Fact]
    public void Text_And_Note_Move_But_Do_Not_Resize()
    {
        // Точную ширину надписи знает только шрифт: растягивать рамку, которая
        // врёт о результате, — хуже, чем не растягивать вовсе.
        var text = new TextOverlay("Привет", 10, 20, 14, 0xFF000000, 0);
        var abilities = OverlayGeometry.AbilitiesOf(text);
        Assert.True(abilities.CanMove);
        Assert.False(abilities.CanResize);

        var note = new NoteAnnotationDraft(10, 20, "текст", "автор");
        Assert.True(OverlayGeometry.AbilitiesOf(note).CanMove);
        Assert.False(OverlayGeometry.AbilitiesOf(note).CanResize);
    }

    [Fact]
    public void Text_Markup_Can_Only_Be_Selected_And_Deleted()
    {
        // Сдвинуть разметку отдельно от текста — значит перестать быть
        // разметкой этого текста.
        var markup = new TextMarkupDraft(TextMarkupKind.Highlight,
            new[] { new TextMarkupRect(10, 20, 100, 12) }, 0x66FDE047, "", "");
        var abilities = OverlayGeometry.AbilitiesOf(markup);
        Assert.True(abilities.CanSelect);
        Assert.False(abilities.CanMove);
    }

    [Fact]
    public void Whole_Page_Edits_Are_Not_Selectable_Objects()
    {
        Assert.False(OverlayGeometry.AbilitiesOf(new PageRasterReplacement(new byte[4], 1, 1)).CanSelect);
        Assert.False(OverlayGeometry.AbilitiesOf(new TextObjectReplacement(0, "текст")).CanSelect);
    }

    // ----- Рамки -----

    [Fact]
    public void Ink_Bounds_Cover_All_Strokes_With_Room_For_The_Line_Width()
    {
        var ink = new InkAnnotationDraft(
            new[] { new[] { new InkPoint(10, 10), new InkPoint(50, 30) } }, 0xFF000000, 4, "", "");
        var box = OverlayGeometry.BoundsOf(ink);
        Assert.NotNull(box);
        Assert.True(box!.Value.XPt < 10, "рамка обязана учесть толщину линии");
        Assert.True(box.Value.Right > 50);
    }

    [Fact]
    public void Moving_Ink_Moves_Every_Point()
    {
        var ink = new InkAnnotationDraft(
            new[] { new[] { new InkPoint(10, 10), new InkPoint(50, 30) } }, 0xFF000000, 2, "", "");
        var moved = (InkAnnotationDraft)OverlayGeometry.Moved(ink, 5, -5)!;
        Assert.Equal(15, moved.Strokes[0][0].XPt);
        Assert.Equal(5, moved.Strokes[0][0].YPt);
        Assert.Equal(55, moved.Strokes[0][1].XPt);
    }

    [Fact]
    public void Resizing_Ink_Scales_The_Drawing_Into_The_New_Frame()
    {
        var ink = new InkAnnotationDraft(
            new[] { new[] { new InkPoint(0, 0), new InkPoint(10, 10) } }, 0xFF000000, 2, "", "");
        var resized = (InkAnnotationDraft)OverlayGeometry.Resized(ink, new OverlayBox(30, 40, 20, 20))!;

        // Договор — рамка: рисунок целиком оказывается там, куда её растянули,
        // вместе с запасом на толщину линии.
        var box = OverlayGeometry.BoundsOf(resized);
        Assert.NotNull(box);
        Assert.Equal(30, box!.Value.XPt, 1);
        Assert.Equal(40, box.Value.YPt, 1);
        Assert.Equal(20, box.Value.WidthPt, 1);
        Assert.Equal(20, box.Value.HeightPt, 1);
    }

    [Fact]
    public void Resizing_Keeps_The_Object_Grabbable()
    {
        var shape = new ShapeAnnotationDraft(10, 20, 100, 50, 0, 0, 1, false, "", "");
        var tiny = (ShapeAnnotationDraft)OverlayGeometry.Resized(shape, new OverlayBox(10, 20, 0.1, 0.1))!;
        Assert.True(tiny.WidthPt >= OverlayGeometry.MinimumSizePt);
        Assert.True(tiny.HeightPt >= OverlayGeometry.MinimumSizePt);
    }
}
