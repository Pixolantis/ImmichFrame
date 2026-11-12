using System.Text;
using System.Text.Json;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Logic.AccountSelection;

public class TotalAccountImagesSelectionStrategy(
    ILogger<TotalAccountImagesSelectionStrategy> _logger,
    IAssetAccountTracker _tracker,
    IList<IAccountImmichFrameLogic> _accounts) : IAccountSelectionStrategy
{
    public async Task<(IAccountImmichFrameLogic, AssetResponseDto)?> GetNextAsset()
    {
        if (_accounts.Count == 0)
        {
            _logger.LogDebug("No accounts configured, returning no asset");
            return null;
        }

        var chosen = await _accounts.ChooseOne(logic => logic.GetTotalAssets());

        var asset = await chosen.GetNextAsset();
        if (asset != null)
        {
            await _tracker.RecordAssetLocation(chosen, asset.Id);
            return (chosen, asset);
        }

        _logger.LogDebug("No next asset found");
        return null;
    }

    private async Task<(IList<long>, long)> GetWeights(IList<IAccountImmichFrameLogic> accounts)
    {
        var weights = await Task.WhenAll(accounts.Select(GetTotalForAccount));
        return (weights, weights.Sum());
    }

    private async Task<IList<double>> GetProportions(IList<IAccountImmichFrameLogic> accounts)
    {
        var (totals, sum) = await GetWeights(accounts);
        if (sum == 0)
        {
            // No account reported any assets — avoid 0/0 NaN proportions; uniform weights
            // keep the (empty) batches intact.
            return accounts.Select(_ => 1d).ToList();
        }

        return totals.Select(t => (double)t / sum).ToList();
    }

    private Task<long> GetTotalForAccount(IImmichFrameLogic account)
    {
        return account.GetTotalAssets();
    }

    public async Task<IEnumerable<(IAccountImmichFrameLogic, AssetResponseDto)>> GetAssets()
    {
        if (_accounts.Count == 0)
        {
            _logger.LogDebug("No accounts configured, returning no assets");
            return Enumerable.Empty<(IAccountImmichFrameLogic, AssetResponseDto)>();
        }

        var proportions = await GetProportions(_accounts);
        var maxAccount = proportions.Max();
        var adjustedProportions = proportions.Select(x => x / maxAccount).ToList();
        var assetLists = _accounts.Select(account => account.GetAssets()).ToList();

        var taskList = assetLists
            .Zip(_accounts, adjustedProportions)
            .Select(async tuple =>
            {
                var (task, account, proportion) = tuple;
                var assets = (await task).ToList();
                _logger.LogDebug("Retrieved {total} asset(s) for account [{account}], will take {proportion}%", assets.Count(), account, proportion * 100);
                return (account, assets.Shuffle().TakeProportional(proportion));
            });

        var accountAssetTupleList = await Task.WhenAll(taskList);

        _logger.LogDebug("Processing {} list(s) of asset(s) of length {}", accountAssetTupleList.Length, string.Join(",", accountAssetTupleList.Select(a => a.Item2.Count())));

        foreach (var accountAssetTuple in accountAssetTupleList)
        {
            foreach (var asset in accountAssetTuple.Item2)
            {
                await _tracker.RecordAssetLocation(accountAssetTuple.account, asset.Id);
            }
        }

        var assets = accountAssetTupleList.SelectMany(tuple => tuple.Item2.Select(asset => (tuple.account, asset))).ToList();

        _logger.LogDebug("Returning {count} asset(s)", assets.Count);

        return assets;
    }

    public async Task<IReadOnlyList<(IAccountImmichFrameLogic, OrderedAsset)>> GetOrderedAssets(ImageOrder order, string? position)
    {
        var positions = DecodePosition(order, position);
        var batches = await Task.WhenAll(_accounts.Select((account, i) => account.GetOrderedAssets(order, positions[i])));
        var next = new int[batches.Length];
        var assets = new List<(IAccountImmichFrameLogic, OrderedAsset)>();

        while (assets.Count < 25)
        {
            var chosen = -1;
            for (var i = 0; i < batches.Length; i++)
            {
                if (next[i] < batches[i].Count)
                {
                    // Memories are not part of the timeline and are shown where the account placed them.
                    if (batches[i][next[i]].IsMemory)
                    {
                        chosen = i;
                        break;
                    }

                    if (chosen < 0 || ComesFirst(batches[i][next[i]], batches[chosen][next[chosen]], order))
                    {
                        chosen = i;
                    }
                }
                else if (batches[i].Count > 0 && batches[i][^1].PositionAfter != OrderedAsset.EndPosition)
                {
                    // Its next batch may sort before the other accounts' remaining assets.
                    chosen = -1;
                    break;
                }
            }

            if (chosen < 0)
            {
                break;
            }

            var asset = batches[chosen][next[chosen]++];
            positions[chosen] = asset.PositionAfter;
            await _tracker.RecordAssetLocation(_accounts[chosen], asset.Asset.Id);
            assets.Add((_accounts[chosen], asset with { PositionAfter = EncodePosition(order, positions) }));
        }

        return assets;
    }

    private static bool ComesFirst(OrderedAsset candidate, OrderedAsset current, ImageOrder order)
        => order == ImageOrder.CaptureDateDescending
            ? candidate.Asset.FileCreatedAt > current.Asset.FileCreatedAt
            : candidate.Asset.FileCreatedAt < current.Asset.FileCreatedAt;

    private record PositionState(ImageOrder Order, string?[] Accounts);

    private static string EncodePosition(ImageOrder order, string?[] positions)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new PositionState(order, positions))));

    // An unreadable position, or one from another order or account setup, starts from the beginning.
    private string?[] DecodePosition(ImageOrder order, string? position)
    {
        try
        {
            var state = position == null ? null
                : JsonSerializer.Deserialize<PositionState>(Encoding.UTF8.GetString(Convert.FromBase64String(position)));
            if (state?.Accounts != null && state.Order == order && state.Accounts.Length == _accounts.Count)
            {
                return state.Accounts;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            _logger.LogDebug("Ignoring unreadable position");
        }

        return new string?[_accounts.Count];
    }

    public T ForAsset<T>(Guid assetId, Func<IAccountImmichFrameLogic, T> f)
        => _tracker.ForAsset(assetId, f);
}