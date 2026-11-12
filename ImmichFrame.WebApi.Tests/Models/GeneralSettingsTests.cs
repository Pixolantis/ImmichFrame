using ImmichFrame.WebApi.Models;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Models;

[TestFixture]
public class GeneralSettingsTests
{
    [TestCase("Random")]
    [TestCase("CaptureDateAscending")]
    [TestCase("capturedatedescending")]
    [TestCase("")]
    public void Validate_ValidImageOrder_IsAccepted(string imageOrder)
    {
        Assert.DoesNotThrow(new GeneralSettings { ImageOrder = imageOrder }.Validate);
    }

    [TestCase("Oldest")]
    [TestCase("1")]
    public void Validate_InvalidImageOrder_Throws(string imageOrder)
    {
        Assert.Throws<InvalidOperationException>(new GeneralSettings { ImageOrder = imageOrder }.Validate);
    }
}
