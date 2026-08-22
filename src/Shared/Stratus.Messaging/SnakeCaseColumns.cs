using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Stratus.Messaging;

/// <summary>
/// Names every column, key and index in snake_case.
///
/// This is not a style preference. EF's default column name is the CLR
/// property name, which Npgsql emits as a QUOTED identifier — "CreatedAt" —
/// and quoted identifiers in Postgres are case-sensitive. Every hand-written
/// fragment of SQL in this repo spells columns unquoted, which Postgres folds
/// to lower case, so the two conventions silently fail to meet: the outbox
/// dispatcher's FOR UPDATE SKIP LOCKED query and the outbox index's filter
/// both addressed columns that did not exist. One fails when the migration is
/// applied; the other only on the first drain tick, in production.
///
/// Applying it centrally, rather than per property, is the point: a convention
/// enforced in one place cannot drift, and the next entity added to a context
/// inherits it without anyone remembering to.
///
/// Call it LAST in OnModelCreating, after every entity is configured, so it
/// sees the final model.
/// </summary>
public static class SnakeCaseColumns
{
    public static void UseSnakeCaseColumns(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()));
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                fk.SetConstraintName(ToSnakeCase(fk.GetConstraintName()));
            }
        }
    }

    /// <summary>
    /// TenantId → tenant_id, IsLocked → is_locked, PK_outbox_messages →
    /// pk_outbox_messages. An underscore already present is preserved rather
    /// than doubled, which is what keeps EF's own PK_/IX_/FK_ prefixes and the
    /// table names already embedded in them readable.
    /// </summary>
    internal static string? ToSnakeCase(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';

                // Boundary before an upper-case run that follows a lower-case
                // letter or digit (tenantId → tenant_id), and at the end of a
                // run that precedes a lower-case letter (HTTPServer →
                // http_server) — but never straight after an underscore.
                var startsWord = previous != '\0'
                    && previous != '_'
                    && (!char.IsUpper(previous) || (char.IsUpper(previous) && char.IsLower(next)));

                if (startsWord)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
