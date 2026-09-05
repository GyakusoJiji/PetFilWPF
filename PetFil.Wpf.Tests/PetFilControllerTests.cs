// Ported from the original Python test suite:
// tests/test_petfil_controller.py (TestPureHelpers, TestTemperatureParsing)
// in the "Pet Bottle Recycler" repository. Only the toolkit-independent
// (non-serial) logic is covered here, mirroring what those Python tests
// exercised against printrun.petfil.controller.

using Xunit;

namespace PetFil.Wpf.Tests;

public class PetFilControllerPureHelperTests
{
    [Fact]
    public void ChunkLengthCoversOnePeriodWithOverlap()
    {
        // 300 mm/min is 5 mm/s, and a 1 s chunk overlaps by 20%.
        Assert.Equal(6.0, PetFilController.WinderChunkMm(300), 3);
        Assert.Equal(12.0, PetFilController.WinderChunkMm(600), 3);
    }

    [Fact]
    public void ChunkLengthIsNeverNegative()
    {
        Assert.Equal(0.0, PetFilController.WinderChunkMm(-100));
    }

    [Theory]
    [InlineData("ok T:205.3 /210.0 B:0.0 /0.0", true)]
    [InlineData(" T:205.3 /210.0 @:127", true)]
    [InlineData("ok", false)]
    [InlineData("X:0.00 Y:0.00 Z:0.00", false)]
    [InlineData("echo:busy T:1 -- not just a report", false)]
    public void TemperatureReportDetection(string line, bool expected)
    {
        Assert.Equal(expected, PetFilController.IsTemperatureReport(line));
    }
}

public class PetFilControllerTemperatureParsingTests
{
    [Fact]
    public void PlainReportUpdatesBothValues()
    {
        var controller = new PetFilController();

        Assert.True(controller.TryUpdateTemperatures("ok T:205.3 /210.0 B:0.0 /0.0"));

        Assert.Equal(205.3, controller.CurrentTemp!.Value, 3);
        Assert.Equal(210.0, controller.TargetTemp!.Value, 3);
    }

    [Fact]
    public void ToolIndexedReportIsUnderstood()
    {
        var controller = new PetFilController();

        Assert.True(controller.TryUpdateTemperatures("ok T0:198.7 /200.0 T:198.7 /200.0"));

        Assert.Equal(198.7, controller.CurrentTemp!.Value, 3);
        Assert.Equal(200.0, controller.TargetTemp!.Value, 3);
    }

    [Fact]
    public void MissingTargetKeepsPreviousTarget()
    {
        var controller = new PetFilController();

        controller.TryUpdateTemperatures("ok T:205.3 /210.0");
        controller.TryUpdateTemperatures("ok T:206.1");

        Assert.Equal(206.1, controller.CurrentTemp!.Value, 3);
        Assert.Equal(210.0, controller.TargetTemp!.Value, 3);
    }
}
