using Nid.RealEstate.Application.Import;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3F1 §2: a rule's API field, as the file's member an import issue names.</summary>
public class EstateParsingTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("name", ".name")]
    [InlineData("purchaseDate", ".purchase_date")]
    [InlineData("purchasePrice", ".purchase_price.amount")]
    [InlineData("surfaceM2", ".surface_m2")]
    [InlineData("every.unit", ".every.unit")]
    [InlineData("rentMonthly", ".rent_monthly.amount")]
    public void A_rule_field_is_named_as_the_file_member(string field, string member)
        => Assert.Equal(member, EstateParsing.Member(field));

    [Fact]
    public void A_loan_refusal_names_the_file_entity_that_brings_the_part_at_fault()
    {
        static Failure At(string field) => new("realestate.invalid_loan", "x", new Dictionary<string, object?> { ["field"] = field });
        string?[] phases = ["loan_phases[4]", null];
        string?[] events = [null, "loan_events[2]"];
        string?[] none = [];

        Assert.Equal("loan_phases[4].seq", EstateParsing.PathOf(At("phases[0].seq"), "loans[1]", phases, none, none, events));
        Assert.Equal("loans[1]", EstateParsing.PathOf(At("phases[1].annualRate"), "loans[1]", phases, none, none, events)); // a stored phase
        Assert.Equal("loan_events[2].amount.amount", EstateParsing.PathOf(At("events[1].amount"), "loans[1]", phases, none, none, events));
        Assert.Equal("loans[1].principal.amount", EstateParsing.PathOf(At("principal"), "loans[1]", phases, none, none, events));
        Assert.Equal("loans[1].total_payments", EstateParsing.PathOf(At("totalPayments"), "loans[1]", phases, none, none, events));
        Assert.Equal("loans[1]", EstateParsing.PathOf(At("phases"), "loans[1]", phases, none, none, events));
        string?[] rows = [null, "loan_bank_schedule[0]"];
        Assert.Equal("loan_bank_schedule[0].crd", EstateParsing.PathOf(At("rows[1].crd"), "loans[1]", phases, none, none, events, rows));
        Assert.Equal("loan_bank_schedule[0].principal", EstateParsing.PathOf(At("rows[1].principal"), "loans[1]", phases, none, none, events, rows)); // a plain decimal
        Assert.Equal("loan_bank_schedule[0].due_date", EstateParsing.PathOf(At("rows[1].dueDate"), "loans[1]", phases, none, none, events, rows));
        Assert.Equal("loans[1]", EstateParsing.PathOf(At("rows[0].payment"), "loans[1]", phases, none, none, events, rows)); // a stored row
        Assert.Equal("loans[1].schedule_source", EstateParsing.PathOf(At("scheduleSource"), "loans[1]", phases, none, none, events, rows));
    }
}
