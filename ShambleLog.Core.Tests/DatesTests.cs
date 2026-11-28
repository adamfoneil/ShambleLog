using ShambleLog.Core.Helpers;

namespace ShambleLog.Core.Tests;

[TestClass]
public sealed class DatesTests
{
    [TestMethod]
    [DataRow(0, "0 seconds ago")]
    [DataRow(500, "0 seconds ago")]
    [DataRow(-500, "0 seconds from now")]
    [DataRow(59999, "59 seconds ago")]
    [DataRow(-59999, "59 seconds from now")]
    [DataRow(60000, "1 minutes ago")]
    [DataRow(-60000, "1 minutes from now")]
    [DataRow(3599999, "59 minutes ago")]
    [DataRow(-3599999, "59 minutes from now")]
    [DataRow(3600000, "1 hours ago")]
    [DataRow(-3600000, "1 hours from now")]
    [DataRow(86399999, "23 hours ago")]
    [DataRow(-86399999, "23 hours from now")]
    [DataRow(86400000, "1 days ago")]
    [DataRow(-86400000, "1 days from now")]
    [DataRow(172800000, "2 days ago")]
    [DataRow(-172800000, "2 days from now")]
    public void AgeExpression_UsesAbsoluteDurationAndDirection(int elapsedMilliseconds, string expected)
    {
        var reference = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var timestamp = reference.AddMilliseconds(-elapsedMilliseconds);

        Assert.AreEqual(expected, timestamp.AgeExpression(reference));
    }
}
