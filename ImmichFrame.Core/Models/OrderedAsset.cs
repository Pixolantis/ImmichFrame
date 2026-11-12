using ImmichFrame.Core.Api;

namespace ImmichFrame.Core.Models;

// PositionAfter is where the next request continues once this asset has been shown.
public record OrderedAsset(AssetResponseDto Asset, string? PositionAfter, bool IsMemory = false)
{
    public const string EndPosition = "end";
}
