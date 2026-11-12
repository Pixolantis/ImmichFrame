using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;

namespace ImmichFrame.Core.Logic.Pool;

public class AccountSearchPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : IAssetPool
{
    private const int PageSize = 50;

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return 0;
        }

        var filter = SearchFilters.ForAccount(accountSettings, tagIds);
        return await apiCache.GetOrAddAsync("stats", async () =>
        {
            var stats = await immichApi.SearchAssetStatisticsAsync(new StatisticsSearchDto
            {
                Filter = filter
            }, ct);
            return stats.Total;
        });
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return [];
        }

        return await immichApi.SearchRandomAsync(new RandomSearchDto
        {
            Size = requested,
            WithExif = true,
            WithPeople = true,
            Filter = SearchFilters.ForAccount(accountSettings, tagIds)
        }, ct);
    }

    // A position is "<index in page>:<Immich cursor of that page>", or OrderedAsset.EndPosition once every asset was returned.
    public async Task<IReadOnlyList<OrderedAsset>> GetSortedAssets(int requested, AssetOrder direction, string? position, CancellationToken ct = default)
    {
        if (position == OrderedAsset.EndPosition)
        {
            return [];
        }

        var tagIds = await ResolveTagIds(ct);
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return [];
        }

        var (index, cursor) = ParsePosition(position);
        var filter = SearchFilters.ForAccount(accountSettings, tagIds);
        var assets = new List<OrderedAsset>(requested);
        while (assets.Count < requested)
        {
            var page = (await immichApi.SearchAssetsAsync(null, null, new MetadataSearchDto
            {
                Filter = filter,
                OrderBy = new SearchOrder { Field = SearchOrderField.FileCreatedAt, Direction = direction },
                Cursor = cursor,
                Size = PageSize,
                WithExif = true,
                WithPeople = true
            }, ct)).Assets;
            var items = page.Items.ToList();

            for (; index < items.Count && assets.Count < requested; index++)
            {
                var positionAfter = index + 1 < items.Count ? $"{index + 1}:{cursor}"
                    : page.NextCursor != null ? $"0:{page.NextCursor}"
                    : OrderedAsset.EndPosition;
                assets.Add(new OrderedAsset(items[index], positionAfter));
            }

            if (assets.Count >= requested || page.NextCursor == null)
            {
                break;
            }

            (index, cursor) = (0, page.NextCursor);
        }

        return assets;
    }

    private static (int Index, string? Cursor) ParsePosition(string? position)
    {
        var separator = position?.IndexOf(':') ?? -1;
        if (separator < 0 || !int.TryParse(position![..separator], out var index) || index < 0)
        {
            return (0, null);
        }

        var cursor = position[(separator + 1)..];
        return (index, cursor.Length > 0 ? cursor : null);
    }

    private async Task<IReadOnlyList<Guid>> ResolveTagIds(CancellationToken ct)
    {
        if (accountSettings.Tags is not { Count: > 0 } values)
        {
            return [];
        }

        var allTags = await apiCache.GetOrAddAsync($"allTags_{accountSettings.ImmichServerUrl}",
            () => immichApi.GetAllTagsAsync(ct));
        var tagValueToTag = allTags.ToDictionary(t => t.Value);
        return values
            .Where(tagValueToTag.ContainsKey)
            .Select(value => tagValueToTag[value].Id)
            .ToList();
    }
}
