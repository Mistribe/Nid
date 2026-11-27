using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>#450: <c>banking.pattern_embedding</c> and <c>banking.category_embedding</c>, in SQL: pgvector's <c>vector</c> has no mapping here
/// (as Documents' chunks, #559).</summary>
public sealed class PatternIndexStore(BankingDbContext db) : IPatternIndexStore
{
    public async Task<IReadOnlyList<string>> UnembeddedAsync(string model, IReadOnlyCollection<string> heldBack, int limit, CancellationToken ct)
    {
        var words = heldBack.ToArray();
        return await db.Database.SqlQuery<string>($"""
            select distinct t.suggested_pattern as "Value" from banking.transaction t
            where t.suggested_pattern is not null and t.link_state not in ('ignored', 'internal')
              and not exists (select 1 from banking.pattern_embedding e where e.pattern = t.suggested_pattern and e.model = {model})
              and not exists (select 1 from unnest({words}) as w(word) where ' ' || t.suggested_pattern || ' ' like '% ' || w.word || ' %')
            order by 1 limit {limit}
            """).ToListAsync(ct);
    }

    public async Task SaveAsync(string model, int dimension, IReadOnlyList<(string Pattern, IReadOnlyList<float> Vector)> vectors, Instant at,
        CancellationToken ct)
    {
        var patterns = vectors.Select(v => v.Pattern).ToArray();
        var values = vectors.Select(v => Vector(v.Vector)).ToArray();
        await db.Database.ExecuteSqlAsync($"""
            insert into banking.pattern_embedding (pattern, model, dimension, embedding, created_at)
            select u.p, {model}, {dimension}, u.v::vector, {at} from unnest({patterns}, {values}) as u(p, v)
            on conflict do nothing
            """, ct);
    }

    public async Task<IReadOnlyList<CategorySuggestions.Neighbour>> NeighboursAsync(string model, IReadOnlyCollection<string> patterns,
        IReadOnlyCollection<string> evidence, int take, CancellationToken ct)
    {
        var open = patterns.ToArray();
        var known = evidence.ToArray();
        return await db.Database.SqlQuery<CategorySuggestions.Neighbour>($"""
            select q.pattern as "Pattern", n.pattern as "Near", n.similarity as "Similarity"
            from banking.pattern_embedding q
            cross join lateral (
                select e.pattern, 1 - (e.embedding <=> q.embedding)::float8 as similarity from banking.pattern_embedding e
                where e.model = q.model and e.dimension = q.dimension and e.pattern = any({known}) and e.pattern <> q.pattern
                order by e.embedding <=> q.embedding limit {take}) n
            where q.model = {model} and q.pattern = any({open})
            """).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid Category, string Text)>> UnembeddedCategoriesAsync(string model, IReadOnlyList<(Guid Category, string Text)> current,
        CancellationToken ct)
    {
        var ids = current.Select(c => c.Category).ToArray();
        var texts = current.Select(c => c.Text).ToArray();
        await db.Database.ExecuteSqlAsync($"""
            delete from banking.category_embedding e where e.model = {model}
              and not exists (select 1 from unnest({ids}, {texts}) as c(id, text) where c.id = e.category_id and c.text = e.text)
            """, ct);
        return [.. (await db.Database.SqlQuery<CategoryText>($"""
            select c.id as "Category", c.text as "Text" from unnest({ids}, {texts}) as c(id, text)
            where not exists (select 1 from banking.category_embedding e where e.model = {model} and e.category_id = c.id)
            order by 2
            """).ToListAsync(ct)).Select(c => (c.Category, c.Text))];
    }

    public async Task SaveCategoriesAsync(string model, int dimension, IReadOnlyList<(Guid Category, string Text, IReadOnlyList<float> Vector)> vectors,
        Instant at, CancellationToken ct)
    {
        var ids = vectors.Select(v => v.Category).ToArray();
        var texts = vectors.Select(v => v.Text).ToArray();
        var values = vectors.Select(v => Vector(v.Vector)).ToArray();
        await db.Database.ExecuteSqlAsync($"""
            insert into banking.category_embedding (category_id, model, text, dimension, embedding, created_at)
            select u.c, {model}, u.t, {dimension}, u.v::vector, {at} from unnest({ids}, {texts}, {values}) as u(c, t, v)
            on conflict do nothing
            """, ct);
    }

    public async Task<IReadOnlyList<CategorySuggestions.CategoryNeighbour>> NearestCategoriesAsync(string model, IReadOnlyCollection<string> patterns,
        IReadOnlyCollection<Guid> debit, IReadOnlyCollection<Guid> credit, CancellationToken ct)
    {
        var open = patterns.ToArray();
        var expense = debit.ToArray();
        var income = credit.ToArray();
        // Two per side: the nearest, and the second its margin is measured against.
        return await db.Database.SqlQuery<CategorySuggestions.CategoryNeighbour>($"""
            select q.pattern as "Pattern", s.debit as "Debit", n.category_id as "Category", n.similarity as "Similarity"
            from banking.pattern_embedding q
            cross join (values (true), (false)) as s(debit)
            cross join lateral (
                select e.category_id, 1 - (e.embedding <=> q.embedding)::float8 as similarity from banking.category_embedding e
                where e.model = q.model and e.dimension = q.dimension
                  and e.category_id = any(case when s.debit then {expense} else {income} end)
                order by e.embedding <=> q.embedding limit 2) n
            where q.model = {model} and q.pattern = any({open})
            """).ToListAsync(ct);
    }

    private static string Vector(IReadOnlyList<float> vector) => "[" + string.Join(',', vector.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private sealed record CategoryText(Guid Category, string Text);
}
