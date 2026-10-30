using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class TransactionFingerprintTests
{
    private static readonly LocalDate Day = new(2026, 10, 12);

    [Fact]
    public void Same_input_gives_the_same_lower_hex_sha256()
    {
        var a = TransactionFingerprint.Compute(Day, -13.49m, "AMAZON", 1);
        Assert.Equal(a, TransactionFingerprint.Compute(Day, -13.49m, "AMAZON", 1));
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.NotEqual(a, TransactionFingerprint.Compute(Day, -13.49m, "AMAZON", 2));
    }

    [Fact]
    public void Trailing_zeros_do_not_change_it()
        => Assert.Equal(TransactionFingerprint.Compute(Day, -13.49m, "X", 1), TransactionFingerprint.Compute(Day, -13.490m, "X", 1));

    [Fact]
    public void Two_equal_lines_the_same_day_keep_their_rank()
    {
        var fingerprints = TransactionFingerprint.ForBatch([(Day, -3m, "CAFE"), (Day, -3.00m, "CAFE"), (Day, -3m, "CAFE")]);
        Assert.Equal(TransactionFingerprint.Compute(Day, -3m, "CAFE", 1), fingerprints[0]);
        Assert.Equal(TransactionFingerprint.Compute(Day, -3m, "CAFE", 2), fingerprints[1]);
        Assert.Equal(TransactionFingerprint.Compute(Day, -3m, "CAFE", 3), fingerprints[2]);
        Assert.Equal(3, fingerprints.Distinct().Count());
    }

    [Fact]
    public void Ranks_of_other_lines_start_again()
    {
        var f = TransactionFingerprint.ForBatch([(Day, -3m, "CAFE"), (Day, -3m, "CAFE"), (Day, -4m, "CAFE")]);
        Assert.Equal(TransactionFingerprint.Compute(Day, -4m, "CAFE", 1), f[2]);
    }
}
