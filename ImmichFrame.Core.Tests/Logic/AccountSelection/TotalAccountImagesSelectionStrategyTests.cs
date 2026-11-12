using Moq;
using NUnit.Framework;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.AccountSelection;
using ImmichFrame.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichFrame.Core.Tests.Logic.AccountSelection
{
    [TestFixture]
    public class TotalAccountImagesSelectionStrategyTests
    {
        private Mock<IAssetAccountTracker> _mockTracker;

        [SetUp]
        public void Setup()
        {
            _mockTracker = new Mock<IAssetAccountTracker>();
            _mockTracker
                .Setup(t => t.RecordAssetLocation(It.IsAny<IAccountImmichFrameLogic>(), It.IsAny<Guid>()))
                .ReturnsAsync(true);
        }

        private TotalAccountImagesSelectionStrategy CreateStrategy(params IAccountImmichFrameLogic[] accounts)
            => new(NullLogger<TotalAccountImagesSelectionStrategy>.Instance, _mockTracker.Object, accounts.ToList());

        private static Mock<IAccountImmichFrameLogic> CreateAccount(long totalAssets, IEnumerable<AssetResponseDto> assets)
        {
            var mock = new Mock<IAccountImmichFrameLogic>();
            mock.Setup(a => a.GetTotalAssets()).ReturnsAsync(totalAssets);
            mock.Setup(a => a.GetAssets()).ReturnsAsync(assets);
            return mock;
        }

        private static AssetResponseDto CreateAsset() => new()
        {
            Id = Guid.NewGuid(),
            OriginalPath = "/path/asset.jpg",
            Type = AssetTypeEnum.IMAGE,
        };

        [Test]
        public async Task GetAssets_NoAccounts_ReturnsEmpty()
        {
            var strategy = CreateStrategy();

            var assets = await strategy.GetAssets();

            Assert.That(assets, Is.Empty);
        }

        [Test]
        public async Task GetAssets_AllAccountsEmpty_ReturnsEmptyWithoutNaN()
        {
            var account1 = CreateAccount(0, Enumerable.Empty<AssetResponseDto>());
            var account2 = CreateAccount(0, Enumerable.Empty<AssetResponseDto>());
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var assets = await strategy.GetAssets();

            Assert.That(assets, Is.Empty);
        }

        [Test]
        public async Task GetAssets_UnevenAccounts_LargestAccountKeepsAllAssets()
        {
            var largeAssets = Enumerable.Range(0, 10).Select(_ => CreateAsset()).ToList();
            var smallAssets = Enumerable.Range(0, 10).Select(_ => CreateAsset()).ToList();
            var largeAccount = CreateAccount(1000, largeAssets);
            var smallAccount = CreateAccount(100, smallAssets);
            var strategy = CreateStrategy(largeAccount.Object, smallAccount.Object);

            var result = (await strategy.GetAssets()).ToList();

            var fromLarge = result.Count(t => ReferenceEquals(t.Item1, largeAccount.Object));
            var fromSmall = result.Count(t => ReferenceEquals(t.Item1, smallAccount.Object));
            Assert.Multiple(() =>
            {
                // The account with the most assets keeps its full batch, smaller ones are scaled.
                Assert.That(fromLarge, Is.EqualTo(10));
                Assert.That(fromSmall, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task GetAssets_RecordsEveryReturnedAssetWithTracker()
        {
            var assets = Enumerable.Range(0, 5).Select(_ => CreateAsset()).ToList();
            var account = CreateAccount(5, assets);
            var strategy = CreateStrategy(account.Object);

            var result = (await strategy.GetAssets()).ToList();

            Assert.That(result, Has.Count.EqualTo(5));
            _mockTracker.Verify(
                t => t.RecordAssetLocation(account.Object, It.IsAny<Guid>()),
                Times.Exactly(5));
        }

        [Test]
        public async Task GetNextAsset_ReturnsAssetAndRecordsLocation()
        {
            var asset = CreateAsset();
            var account = CreateAccount(1, new[] { asset });
            account.Setup(a => a.GetNextAsset()).ReturnsAsync(asset);
            var strategy = CreateStrategy(account.Object);

            var result = await strategy.GetNextAsset();

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.Item2.Id, Is.EqualTo(asset.Id));
            _mockTracker.Verify(t => t.RecordAssetLocation(account.Object, asset.Id), Times.Once);
        }

        private static OrderedAsset Ordered(int day, string? positionAfter, bool isMemory = false) => new(
            new AssetResponseDto { Id = Guid.NewGuid(), FileCreatedAt = new DateTimeOffset(2020, 1, day, 0, 0, 0, TimeSpan.Zero) },
            positionAfter,
            isMemory);

        [Test]
        public async Task GetOrderedAssets_Descending_MergesNewestFirst()
        {
            var account1 = new Mock<IAccountImmichFrameLogic>();
            account1.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateDescending, null))
                .ReturnsAsync(new[] { Ordered(4, "a1"), Ordered(1, OrderedAsset.EndPosition) });
            var account2 = new Mock<IAccountImmichFrameLogic>();
            account2.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateDescending, null))
                .ReturnsAsync(new[] { Ordered(3, "b1"), Ordered(2, OrderedAsset.EndPosition) });
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var result = await strategy.GetOrderedAssets(ImageOrder.CaptureDateDescending, null);

            Assert.That(result.Select(r => r.Item2.Asset.FileCreatedAt.Day), Is.EqualTo(new[] { 4, 3, 2, 1 }));
        }

        [Test]
        public async Task GetOrderedAssets_MemoryKeepsItsPlaceWithoutBlockingTheTimeline()
        {
            var account1 = new Mock<IAccountImmichFrameLogic>();
            account1.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(28, null, isMemory: true), Ordered(2, OrderedAsset.EndPosition) });
            var account2 = new Mock<IAccountImmichFrameLogic>();
            account2.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(1, "b1"), Ordered(3, OrderedAsset.EndPosition) });
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var result = await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

            Assert.That(result.Select(r => r.Item2.Asset.FileCreatedAt.Day), Is.EqualTo(new[] { 28, 1, 2, 3 }));
        }

        [Test]
        public async Task GetOrderedAssets_AccountWithOnlyMemories_DoesNotBlockOtherAccounts()
        {
            var account1 = new Mock<IAccountImmichFrameLogic>();
            account1.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(9, OrderedAsset.EndPosition, isMemory: true) });
            var account2 = new Mock<IAccountImmichFrameLogic>();
            account2.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(1, "b1"), Ordered(2, OrderedAsset.EndPosition) });
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var result = await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

            Assert.That(result.Select(r => r.Item2.Asset.FileCreatedAt.Day), Is.EqualTo(new[] { 9, 1, 2 }));
        }

        [Test]
        public async Task GetOrderedAssets_PositionFromOtherOrder_StartsFromBeginning()
        {
            var account = new Mock<IAccountImmichFrameLogic>();
            account.Setup(a => a.GetOrderedAssets(It.IsAny<ImageOrder>(), It.IsAny<string?>()))
                .ReturnsAsync(new[] { Ordered(1, "a1") });
            var strategy = CreateStrategy(account.Object);
            var ascending = await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

            await strategy.GetOrderedAssets(ImageOrder.CaptureDateDescending, ascending[^1].Item2.PositionAfter);

            account.Verify(a => a.GetOrderedAssets(ImageOrder.CaptureDateDescending, null), Times.Once);
        }

        [Test]
        public async Task GetOrderedAssets_MergesAccountsByCaptureDate()
        {
            var account1 = new Mock<IAccountImmichFrameLogic>();
            account1.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(1, "a1"), Ordered(4, OrderedAsset.EndPosition) });
            var account2 = new Mock<IAccountImmichFrameLogic>();
            account2.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(2, "b1"), Ordered(3, OrderedAsset.EndPosition) });
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var result = await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

            Assert.That(result.Select(r => r.Item2.Asset.FileCreatedAt.Day), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            _mockTracker.Verify(t => t.RecordAssetLocation(account2.Object, It.IsAny<Guid>()), Times.Exactly(2));
        }

        [Test]
        public async Task GetOrderedAssets_StopsWhenAnAccountNeedsItsNextBatch()
        {
            var account1 = new Mock<IAccountImmichFrameLogic>();
            account1.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(1, "a1"), Ordered(2, "a2") });
            var account2 = new Mock<IAccountImmichFrameLogic>();
            account2.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(5, OrderedAsset.EndPosition) });
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var result = await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);

            Assert.That(result.Select(r => r.Item2.Asset.FileCreatedAt.Day), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public async Task GetOrderedAssets_PositionContinuesEachAccount()
        {
            var account1 = new Mock<IAccountImmichFrameLogic>();
            account1.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, It.IsAny<string?>()))
                .ReturnsAsync(Array.Empty<OrderedAsset>());
            account1.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(1, "a1"), Ordered(3, "a2") });
            var account2 = new Mock<IAccountImmichFrameLogic>();
            account2.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, It.IsAny<string?>()))
                .ReturnsAsync(Array.Empty<OrderedAsset>());
            account2.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null))
                .ReturnsAsync(new[] { Ordered(2, "b1"), Ordered(4, "b2") });
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var first = await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, null);
            await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, first[^1].Item2.PositionAfter);

            account1.Verify(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, "a2"), Times.Once);
            account2.Verify(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, "b1"), Times.Once);
        }

        [TestCase("not base64!")]
        [TestCase("e30=")]
        public async Task GetOrderedAssets_UnreadablePosition_StartsFromBeginning(string position)
        {
            var account = new Mock<IAccountImmichFrameLogic>();
            account.Setup(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, It.IsAny<string?>()))
                .ReturnsAsync(Array.Empty<OrderedAsset>());
            var strategy = CreateStrategy(account.Object);

            await strategy.GetOrderedAssets(ImageOrder.CaptureDateAscending, position);

            account.Verify(a => a.GetOrderedAssets(ImageOrder.CaptureDateAscending, null), Times.Once);
        }
    }
}
