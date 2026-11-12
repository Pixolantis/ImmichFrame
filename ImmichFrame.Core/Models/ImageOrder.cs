namespace ImmichFrame.Core.Models;

public enum ImageOrder
{
    Random,
    CaptureDateAscending,
    CaptureDateDescending
}

public static class ImageOrders
{
    // Enum.TryParse would also accept numbers such as "1", which are not valid setting values.
    public static bool TryParse(string? value, out ImageOrder order)
    {
        var name = Enum.GetNames<ImageOrder>().FirstOrDefault(n => string.Equals(n, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        order = name != null ? Enum.Parse<ImageOrder>(name) : ImageOrder.Random;
        return name != null;
    }
}
