using NUnit.Framework;

public sealed class BasisBeeRangePolicyTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("bytes 1-8/100")]
    [TestCase("bytes 0-8/100")]
    [TestCase("bytes 0-7/7")]
    [TestCase("bytes 0-7/*")]
    [TestCase("bytes */100")]
    [TestCase("bytes +0-7/100")]
    [TestCase("bytes 0-7/9223372036854775808")]
    [TestCase("bytes 0-7/100,200")]
    public void RejectsMissingMalformedOrDifferentRanges(string header)
    {
        Assert.That(BasisBeeRangePolicy.Validate(header, 0, 7, null, null, -1, out _, out _), Is.False);
    }

    [Test]
    public void AcceptsExactRangeAndBindsRepresentation()
    {
        Assert.That(BasisBeeRangePolicy.Validate("bytes 8-31/4096", 8, 31, "\"v1\"", "\"v1\"", 4096, out long total, out string error), Is.True);
        Assert.That(total, Is.EqualTo(4096));
        Assert.That(error, Is.Null);
        Assert.That(BasisBeeRangePolicy.Validate("bytes 8-31/4096", 8, 31, "\"v2\"", "\"v1\"", 4096, out _, out _), Is.False);
        Assert.That(BasisBeeRangePolicy.Validate("bytes 8-31/4096", 8, 31, null, "\"v1\"", 4096, out _, out _), Is.False);
        Assert.That(BasisBeeRangePolicy.Validate("bytes 8-31/4097", 8, 31, null, null, 4096, out _, out _), Is.False);
    }

    [Test]
    public void ConditionalRequestUsesOnlyStrongValidators()
    {
        Assert.That(BasisBeeRangePolicy.IsStrongETag("\"sha256\""), Is.True);
        Assert.That(BasisBeeRangePolicy.IsStrongETag("W/\"weak\""), Is.False);
        Assert.That(BasisBeeRangePolicy.IsStrongETag("\"bad\r\nheader\""), Is.False);
        Assert.That(BasisBeeRangePolicy.IsStrongETag(null), Is.False);
    }
}
