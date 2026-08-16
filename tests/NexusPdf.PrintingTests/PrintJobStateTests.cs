using NexusPdf.Printing;

namespace NexusPdf.PrintingTests;

/// <summary>
/// Разбор состояния задания. Windows выставляет флаги пачкой, и порядок их
/// разбора — это и есть смысл: человеку важнее узнать «кончилась бумага», чем
/// «печатается», хотя стоят оба.
/// </summary>
public sealed class PrintJobStateTests
{
    [Theory]
    [InlineData(WindowsJobStatus.None, PrintJobState.Queued)]
    [InlineData(WindowsJobStatus.Spooling, PrintJobState.Spooling)]
    [InlineData(WindowsJobStatus.Printing, PrintJobState.Printing)]
    [InlineData(WindowsJobStatus.Paused, PrintJobState.Paused)]
    [InlineData(WindowsJobStatus.Printed, PrintJobState.Completed)]
    [InlineData(WindowsJobStatus.Complete, PrintJobState.Completed)]
    [InlineData(WindowsJobStatus.Deleted, PrintJobState.Cancelled)]
    [InlineData(WindowsJobStatus.Error, PrintJobState.Error)]
    public void Single_Flags_Read_Plainly(WindowsJobStatus status, PrintJobState expected) =>
        Assert.Equal(expected, PrintJobStateMapper.Map(status));

    [Fact]
    public void Paper_Out_Wins_Over_Printing()
    {
        // Именно этот случай пользователь и должен увидеть: принтер «печатает»,
        // но бумага кончилась, и без него ничего не сдвинется.
        var status = WindowsJobStatus.Printing | WindowsJobStatus.PaperOut;
        Assert.Equal(PrintJobState.NeedsAttention, PrintJobStateMapper.Map(status));
    }

    [Fact]
    public void Offline_And_Intervention_Also_Call_For_A_Human()
    {
        Assert.Equal(PrintJobState.NeedsAttention,
            PrintJobStateMapper.Map(WindowsJobStatus.Printing | WindowsJobStatus.Offline));
        Assert.Equal(PrintJobState.NeedsAttention,
            PrintJobStateMapper.Map(WindowsJobStatus.Spooling | WindowsJobStatus.UserIntervention));
    }

    [Fact]
    public void Deleting_Beats_Everything()
    {
        // Отменённое задание не «печатается», сколько бы флагов ни осталось.
        var status = WindowsJobStatus.Printing | WindowsJobStatus.Deleting | WindowsJobStatus.PaperOut;
        Assert.Equal(PrintJobState.Cancelled, PrintJobStateMapper.Map(status));
    }

    [Fact]
    public void Paused_Beats_Queued_But_Not_Trouble()
    {
        Assert.Equal(PrintJobState.Paused,
            PrintJobStateMapper.Map(WindowsJobStatus.Paused | WindowsJobStatus.Spooling));
        Assert.Equal(PrintJobState.NeedsAttention,
            PrintJobStateMapper.Map(WindowsJobStatus.Paused | WindowsJobStatus.PaperOut));
    }

    [Fact]
    public void An_Active_Job_Can_Be_Stopped_And_A_Finished_One_Cannot()
    {
        var printing = new PrintJobSnapshot(1, "HP", "счёт.pdf", PrintJobState.Printing, 2, 10, true);
        var done = printing with { State = PrintJobState.Completed };
        var cancelled = printing with { State = PrintJobState.Cancelled };

        Assert.True(printing.IsActive);
        Assert.False(done.IsActive);
        Assert.False(cancelled.IsActive);
    }

    [Fact]
    public void Every_State_Has_Its_Own_Caption_Key()
    {
        var keys = Enum.GetValues<PrintJobState>().Select(PrintJobStateMapper.TitleKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("PrintJobState_", k));
    }
}
