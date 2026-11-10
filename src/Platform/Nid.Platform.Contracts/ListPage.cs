namespace Nid.Platform.Contracts;

/// <summary>Clamps a requested list page to 1..int.MaxValue/pageSize, and gives the rows to skip (never overflows).</summary>
public static class ListPage
{
    public static (int Page, int Skip) Of(int? page, int pageSize)
    {
        var clamped = Math.Clamp(page ?? 1, 1, int.MaxValue / pageSize);
        return (clamped, (clamped - 1) * pageSize);
    }
}
