using System.Net;
using System.Text.Json;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Models;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic;

[TestFixture]
public class PooledImmichFrameLogicTests
{
    private Mock<HttpMessageHandler> _handler;
    private Mock<IAccountSettings> _account;
    private List<string> _requests;

    [SetUp]
    public void Setup()
    {
        _handler = new Mock<HttpMessageHandler>();
        _requests = [];
        _account = new Mock<IAccountSettings>();
        _account.SetupGet(a => a.ImmichServerUrl).Returns("http://immich.local");
        _account.SetupGet(a => a.ApiKey).Returns("key");
        _account.SetupGet(a => a.Albums).Returns([Guid.NewGuid()]);
        _account.SetupGet(a => a.People).Returns([]);
        _account.SetupGet(a => a.Tags).Returns([]);
        _account.SetupGet(a => a.ExcludedAlbums).Returns([]);
    }

    private PooledImmichFrameLogic CreateLogic()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler.Object));
        var settings = new Mock<IGeneralSettings>();
        settings.SetupGet(s => s.RefreshAlbumPeopleInterval).Returns(12);
        return new PooledImmichFrameLogic(_account.Object, settings.Object, factory.Object);
    }

    private void Respond(string path, object body)
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath.EndsWith(path)),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => _requests.Add(req.RequestUri!.AbsolutePath))
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(body))
            });
    }

    private static AssetResponseDto Asset(int day)
    {
        var timestamp = new DateTimeOffset(2020, 1, day, 0, 0, 0, TimeSpan.Zero);
        return new AssetResponseDto
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            Type = AssetTypeEnum.IMAGE,
            OriginalPath = "/path/image.jpg",
            OriginalFileName = "image.jpg",
            Checksum = "checksum",
            Thumbhash = "I0cMCQS94XmImZeXmYd3d3g=",
            Visibility = AssetVisibility.Timeline,
            ExifInfo = new ExifResponseDto(),
            FileCreatedAt = timestamp,
            FileModifiedAt = timestamp,
            LocalDateTime = timestamp,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    private static SearchResponseDto Page(params AssetResponseDto[] assets) => new()
    {
        Assets = new SearchAssetResponseDto { Items = assets.ToList(), NextCursor = null }
    };

    private static MemoryResponseDto[] Memories(params AssetResponseDto[] assets) =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            Type = MemoryType.On_this_day,
            Data = new OnThisDayDto { Year = 2015 },
            Assets = assets.ToList(),
            MemoryAt = DateTimeOffset.Now,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        }
    ];

    [Test]
    public async Task GetOrderedAssets_WithoutMemories_ReturnsSortedAssets()
    {
        Respond("/search/metadata", Page(Asset(1), Asset(2)));
        using var logic = CreateLogic();

        var result = await logic.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Select(r => r.Asset.FileCreatedAt.Day), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(result[^1].PositionAfter, Is.EqualTo(OrderedAsset.EndPosition));
            Assert.That(result.Any(r => r.IsMemory), Is.False);
        });
    }

    [Test]
    public async Task GetOrderedAssets_EmptyLibrary_ShowsMemoriesAndEndsTheAccount()
    {
        _account.SetupGet(a => a.ShowMemories).Returns(true);
        Respond("/search/metadata", Page());
        Respond("/search/statistics", new SearchStatisticsResponseDto { Total = 0 });
        Respond("/memories", Memories(Asset(5)));
        using var logic = CreateLogic();

        var result = await logic.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].IsMemory, Is.True);
            Assert.That(result[0].PositionAfter, Is.EqualTo(OrderedAsset.EndPosition));
        });
    }

    [Test]
    public async Task GetOrderedAssets_MemoriesOnlyAccount_ReturnsNothing()
    {
        _account.SetupGet(a => a.Albums).Returns([]);
        _account.SetupGet(a => a.ShowMemories).Returns(true);
        using var logic = CreateLogic();

        var result = await logic.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

        Assert.That(result, Is.Empty);
        Assert.That(_requests, Is.Empty);
    }

    [Test]
    public async Task GetOrderedAssets_AtEnd_ReturnsNothingWithoutRequests()
    {
        _account.SetupGet(a => a.ShowMemories).Returns(true);
        using var logic = CreateLogic();

        var result = await logic.GetOrderedAssets(ImageOrder.CaptureDateAscending, OrderedAsset.EndPosition);

        Assert.That(result, Is.Empty);
        Assert.That(_requests, Is.Empty);
    }
}
