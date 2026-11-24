namespace Nid.Tax.Infrastructure.Persistence;

/// <summary>Tax estimate spec §7: the household's profile of a revenue year, as a camelCase JSON document.</summary>
public sealed class TaxProfileRow
{
    public int Year { get; set; }
    public string Data { get; set; } = "{}";
}

/// <summary>Tax assessments spec §3: a notice as a camelCase JSON document; <c>Year</c> repeats the document's for the declarants' unique index.</summary>
public sealed class TaxAssessmentRow
{
    public Guid Id { get; set; }
    public int Year { get; set; }
    public string Data { get; set; } = "{}";
    public uint Version { get; set; }
}

/// <summary>Tax assessments spec §3: who declared a notice, one row per declarant; unique per (year, person).</summary>
public sealed class TaxAssessmentDeclarantRow
{
    public Guid AssessmentId { get; set; }
    public Guid PersonId { get; set; }
    public int Year { get; set; }
    public int Rank { get; set; }
}
