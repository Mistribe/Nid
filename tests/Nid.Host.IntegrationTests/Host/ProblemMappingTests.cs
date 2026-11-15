using Microsoft.AspNetCore.Mvc;
using Nid.Goals.Contracts;
using Nid.Host.Api;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.AspNetCore.Mappers;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>#250: the status and the RFC 9457 body of each kind of failure, as <see cref="ProblemMapping"/> answers them.</summary>
public class ProblemMappingTests
{
    private static (int Status, ProblemDetails Body) Map(Failure failure)
    {
        var response = ProblemMapping.Mapper.GetFailureResponse(ProblemMapping.Located(failure))!;
        return (response.StatusCode, Assert.IsType<ProblemDetails>(response.Body));
    }

    [Fact]
    public void Statuses_follow_the_failure_type()
    {
        Assert.Equal(404, Map(new NotFoundFailure("subscription", "x")).Status);
        Assert.Equal(403, Map(new UnauthorizedFailure("No.", null)).Status);
        Assert.Equal(401, Map(new UnauthenticatedFailure("Sign in.", null)).Status);
    }

    [Fact]
    public void A_listed_code_keeps_its_status_and_a_plain_refusal_is_422()
    {
        Assert.Equal(409, Map(new Failure(InvestmentsCodes.EnvelopeClosed, "Closed.", null)).Status);
        Assert.Equal(403, Map(new Failure(AdminCodes.Required, "Admins only.", null)).Status);
        Assert.Equal(422, Map(new Failure("core.something_new", "Refused.", null)).Status);
    }

    [Fact]
    public void The_sentence_is_in_detail_and_the_title_names_the_status()
    {
        var (_, closed) = Map(new Failure(InvestmentsCodes.EnvelopeClosed, "The envelope is closed.", null));
        var (_, other) = Map(new Failure(GoalsCodes.Conflict, "Someone changed this goal.", null));

        Assert.Equal("The envelope is closed.", closed.Detail);
        Assert.Equal(closed.Title, other.Title);
        Assert.Equal(InvestmentsCodes.EnvelopeClosed, closed.Extensions["code"]);
    }

    [Fact]
    public void A_server_error_does_not_show_its_message()
    {
        var (status, body) = Map(new Failure("ERROR", "Npgsql said something internal.", null));

        Assert.Equal(500, status);
        Assert.Null(body.Detail);
    }

    [Fact]
    public void Fields_become_an_rfc_9457_list_with_pointers()
    {
        var (status, body) = Map(FieldErrors.Shape([
            new FieldError("payload.movements[3].amount", FieldErrors.ShapeCode, "A number is required."),
            new FieldError("name", "core.name_required", "A name is required."),
        ]));

        Assert.Equal(422, status);
        Assert.Equal(FieldErrors.ShapeCode, body.Extensions["code"]);
        Assert.Equal(
            [
                new ValidationProblemError("A number is required.", "#/payload/movements/3/amount", FieldErrors.ShapeCode),
                new ValidationProblemError("A name is required.", "#/name", "core.name_required"),
            ],
            Assert.IsType<ValidationProblemError[]>(body.Extensions["errors"]));
    }

    [Fact]
    public void A_rule_that_names_its_field_keeps_its_code_status_and_other_metadata()
    {
        var refused = new Failure(GoalsCodes.ShareExceeded, "Only 20 % is free.",
            new Dictionary<string, object?> { ["field"] = "shareBp", ["freeBp"] = 2000 });

        var (status, body) = Map(refused);

        Assert.Equal(409, status);
        Assert.Equal(GoalsCodes.ShareExceeded, body.Extensions["code"]);
        Assert.Equal(2000, body.Extensions["freeBp"]);
        Assert.Equal(
            [new ValidationProblemError("Only 20 % is free.", "#/shareBp", GoalsCodes.ShareExceeded)],
            Assert.IsType<ValidationProblemError[]>(body.Extensions["errors"]));
    }

    [Fact]
    public void A_typed_failure_is_no_field_s()
    {
        var notFound = new NotFoundFailure("goal", "x");

        Assert.Same(notFound, ProblemMapping.Located(notFound));
    }
}
