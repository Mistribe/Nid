using Nid.Assets.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Assets.Application;

/// <summary>The checks the asset writes and reads share.</summary>
internal static class AssetCheck
{
    public const int NameMax = 100;
    public const int NoteMax = 2000;
    private const decimal AmountBound = 1_000_000_000_000m;

    public static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    /// <summary>The asset and its item; an item that is not an asset (a property, a goal) answers as a missing asset.</summary>
    public static async Task<Result<(AssetRecord Asset, ItemAccess Item)>> LoadAsync(IAssetRecordStore assets, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var asset = await assets.FindAsync(id, ct);
        var item = asset is null ? null : await items.FindAsync(id, ct);
        return asset is null || item is null
            ? Result.Failure<(AssetRecord, ItemAccess)>(new NotFoundFailure("asset", id.ToString()))
            : Result.Success((asset, item));
    }

    /// <summary>A write that counts the valuations locks the asset's row first, then reads it as it now is.</summary>
    public static async Task<Result<(AssetRecord Asset, ItemAccess Item)>> LockAsync(IAssetRecordStore assets, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        await assets.LockAsync(id, ct);
        return await LoadAsync(assets, items, id, ct);
    }

    public static Failure? Note(string? note)
        => note is { Length: > NoteMax } ? Shape("note", $"A note has at most {NoteMax} characters.") : null;

    public static Failure? Name(string? name, string? note)
        => name is null || name.Trim().Length is < 1 or > NameMax
            ? Shape("name", $"A name is 1 to {NameMax} characters, and not only whitespace.")
            : Note(note);

    /// <summary>A value is above zero, with at most two decimals, below 10^12.</summary>
    public static Failure? Positive(decimal amount, string field)
        => amount <= 0m || decimal.Round(amount, 2) != amount || amount >= AmountBound
            ? new Failure(AssetsCodes.InvalidValue, "A value is above zero, with at most two decimals, below 10^12.", new Dictionary<string, object?> { ["field"] = field })
            : null;

    public static Failure ValueRequired(string field)
        => new(AssetsCodes.ValueRequired, "An asset has a purchase price or at least one valuation.", new Dictionary<string, object?> { ["field"] = field });

    public static Money MoneyOf(decimal amount, string? currency)
        => Currency.TryCreate(currency ?? "", out var c) && Money.Create(amount, c).TryGetValue(out var money)
            ? money
            : throw new InvalidOperationException("A checked amount has two decimals and an asset has a currency.");

    public static Failure Shape(string path, string message) => FieldErrors.Shape([new FieldError(path, FieldErrors.ShapeCode, message)]);
}
