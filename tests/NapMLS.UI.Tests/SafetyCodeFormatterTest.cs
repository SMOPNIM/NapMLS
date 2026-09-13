using NapMLS.UI.Services;
using Xunit;

namespace NapMLS.UI.Tests;

public class SafetyCodeFormatterTest
{
    [Theory]
    [InlineData(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x78, 0x90 }, "NAPMLS-A1B2-C3D4-E5F6-7890")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, "NAPMLS-0000-0000-0000-0000")]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "NAPMLS-FFFF-FFFF-FFFF-FFFF")]
    public void Format_Bytes_ReturnsCorrectCode(byte[] input, string expected)
    {
        Assert.Equal(expected, SafetyCodeFormatter.Format(input));
    }

    [Theory]
    [InlineData("A1B2C3D4E5F67890", "NAPMLS-A1B2-C3D4-E5F6-7890")]
    [InlineData("0000000000000000", "NAPMLS-0000-0000-0000-0000")]
    public void FormatFromHex_ReturnsCorrectCode(string hex, string expected)
    {
        Assert.Equal(expected, SafetyCodeFormatter.FormatFromHex(hex));
    }

    [Theory]
    [InlineData("NAPMLS-A1B2-C3D4-E5F6-7890", true)]
    [InlineData("NAPMLS-0000-0000-0000-0000", true)]
    [InlineData("NAPMLS-FFFF-FFFF-FFFF-FFFF", true)]
    [InlineData("", false)]
    [InlineData("NAPMLS", false)]
    [InlineData("NAPMLS-A1B2-C3D4-E5F6", false)]
    [InlineData("NAPMLS-A1B2-C3D4-E5F6-789G", false)] // G not hex
    [InlineData("NAPMLS-A1B2-C3D4-E5F6-78901", false)] // too long segment
    [InlineData("napmls-a1b2-c3d4-e5f6-7890", false)] // lowercase
    public void IsValid_ReturnsCorrectly(string code, bool expected)
    {
        Assert.Equal(expected, SafetyCodeFormatter.IsValid(code));
    }

    [Fact]
    public void Format_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => SafetyCodeFormatter.Format(new byte[4]));
    }

    [Fact]
    public void Roundtrip_FormatThenIsValid()
    {
        byte[] fp = { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE };
        var code = SafetyCodeFormatter.Format(fp);
        Assert.Equal("NAPMLS-DEAD-BEEF-CAFE-BABE", code);
        Assert.True(SafetyCodeFormatter.IsValid(code));
    }
}
