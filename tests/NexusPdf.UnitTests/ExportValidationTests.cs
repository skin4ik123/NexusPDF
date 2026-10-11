using NexusPdf.Application;

namespace NexusPdf.UnitTests;

public sealed class ExportValidationTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(1201)]
    public async Task Non_Finite_Or_Invalid_Dpi_Is_Rejected_Before_Rendering(double dpi)
    {
        var convert = new ConvertService(null!);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => convert.ExportImagesAsync(
            null!, null, dpi, (_, _, _, _) => Task.CompletedTask, null, CancellationToken.None));
    }
}
