using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Nid.Platform.Infrastructure.Persistence;

/// <summary>The names every module's model uses: snake-case columns, <c>pk_{table}</c>, <c>ix_{table}_{columns}</c>, <c>fk_{table}_{principal}_{columns}</c>.</summary>
public static class ModelNaming
{
    /// <summary>Snake-case columns (the <c>xmin</c> version column keeps its name), then the constraint names.</summary>
    public static void Apply(ModelBuilder modelBuilder, Func<string, string>? columnName = null)
    {
        columnName ??= ToSnakeCase;
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            if (property.GetColumnName() != "xmin")
                property.SetColumnName(columnName(property.Name));

        NameConstraints(modelBuilder);
    }

    public static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    /// <summary>Names the keys, indexes and foreign keys of every entity, once the columns are named. An index named in the model
    /// (<c>HasDatabaseName</c>) keeps its name: the partial unique ones are named for what they guard.</summary>
    private static void NameConstraints(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            entity.FindPrimaryKey()?.SetName($"pk_{table}");

            foreach (var index in entity.GetIndexes().Where(i => i.FindAnnotation(RelationalAnnotationNames.Name) is null))
                index.SetDatabaseName($"ix_{table}_{Columns(index.Properties)}");

            foreach (var foreignKey in entity.GetForeignKeys())
                foreignKey.SetConstraintName($"fk_{table}_{foreignKey.PrincipalEntityType.GetTableName()}_{Columns(foreignKey.Properties)}");
        }
    }

    // EF Core 11 types index properties as IMutablePropertyBase (they can cross complex types); Nid's are plain columns.
    private static string Columns(IEnumerable<IMutablePropertyBase> properties)
        => string.Join('_', properties.Select(p => ((IReadOnlyProperty)p).GetColumnName()));
}
