using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic;

[TestFixture]
public class MultiImmichFrameLogicDelegateTests
{
    private Mock<IAccountSelectionStrategy> _strategy;
    private MultiImmichFrameLogicDelegate _logic;

    [SetUp]
    public void Setup()
    {
        _strategy = new Mock<IAccountSelectionStrategy>();
        var settings = new Mock<IServerSettings>();
        settings.SetupGet(s => s.Accounts).Returns(new List<IAccountSettings>());
        _logic = new MultiImmichFrameLogicDelegate(settings.Object, _ => new Mock<IAccountImmichFrameLogic>().Object,
            NullLogger<MultiImmichFrameLogicDelegate>.Instance, _ => _strategy.Object);
    }

    [TearDown]
    public void TearDown() => _logic.Dispose();

    private static (IAccountImmichFrameLogic, OrderedAsset) Ordered()
    {
        var account = new Mock<IAccountImmichFrameLogic>();
        account.SetupGet(a => a.AccountSettings).Returns(Mock.Of<IAccountSettings>(s => s.ImmichServerUrl == "https://photos.example"));
        return (account.Object, new OrderedAsset(new AssetResponseDto { Id = Guid.NewGuid() }, "next"));
    }

    [Test]
    public async Task GetOrderedAssets_EndReached_StartsOver()
    {
        var first = Ordered();
        _strategy.Setup(s => s.GetOrderedAssets(ImageOrder.CaptureDateAscending, "end-of-library")).ReturnsAsync([]);
        _strategy.Setup(s => s.GetOrderedAssets(ImageOrder.CaptureDateAscending, null)).ReturnsAsync([first]);

        var result = await _logic.GetOrderedAssets(ImageOrder.CaptureDateAscending, "end-of-library");

        Assert.That(result.Select(r => r.Asset.Id), Is.EqualTo(new[] { first.Item2.Asset.Id }));
    }

    [Test]
    public async Task GetOrderedAssets_ImmichRejectsPosition_StartsOver()
    {
        var first = Ordered();
        _strategy.Setup(s => s.GetOrderedAssets(ImageOrder.CaptureDateAscending, "stale"))
            .ThrowsAsync(new ApiException("Bad cursor", 400, null, new Dictionary<string, IEnumerable<string>>(), null));
        _strategy.Setup(s => s.GetOrderedAssets(ImageOrder.CaptureDateAscending, null)).ReturnsAsync([first]);

        var result = await _logic.GetOrderedAssets(ImageOrder.CaptureDateAscending, "stale");

        Assert.That(result.Select(r => r.Asset.Id), Is.EqualTo(new[] { first.Item2.Asset.Id }));
    }

    [Test]
    public void GetOrderedAssets_ImmichFailsWithoutPosition_Throws()
    {
        _strategy.Setup(s => s.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
            .ThrowsAsync(new ApiException("Server error", 500, null, new Dictionary<string, IEnumerable<string>>(), null));

        Assert.ThrowsAsync<ApiException>(() => _logic.GetOrderedAssets(ImageOrder.CaptureDateAscending, null));
    }
}
