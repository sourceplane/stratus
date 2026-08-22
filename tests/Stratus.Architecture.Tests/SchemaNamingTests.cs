using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Stratus.Billing.Infrastructure;
using Stratus.Identity.Infrastructure;
using Stratus.Tenancy.Infrastructure;
using Xunit;

namespace Stratus.Architecture.Tests;

/// <summary>
/// Every mapped identifier must be snake_case.
///
/// This is enforced because the alternative already bit. EF's default column
/// name is the CLR property name, which Npgsql emits QUOTED — "CreatedAt" —
/// and quoted identifiers in Postgres are case-sensitive, while an unquoted
/// one in hand-written SQL folds to lower case. The outbox dispatcher's
/// FOR UPDATE SKIP LOCKED query and the outbox index's filter both name
/// columns unquoted; against the default mapping they address columns that do
/// not exist. The index filter fails when the migration is applied. The
/// dispatcher fails on its first drain tick, in production, having compiled,
/// passed every test, and deployed green.
///
/// Nothing else in the build can catch that, which is exactly why it is a test.
/// </summary>
public class SchemaNamingTests
{
    private static readonly Regex SnakeCase = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public static TheoryData<string, DbContext> Contexts() => new()
    {
        { "identity", new IdentityDbContextFactory().CreateDbContext([]) },
        { "tenancy", new TenancyDbContextFactory().CreateDbContext([]) },
        { "billing", new BillingDbContextFactory().CreateDbContext([]) },
    };

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Every_mapped_identifier_is_snake_case(string name, DbContext context)
    {
        using (context)
        {
            var offenders = new List<string>();

            foreach (var entity in context.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table is not null && !SnakeCase.IsMatch(table))
                {
                    offenders.Add($"table {table}");
                }

                offenders.AddRange(
                    from property in entity.GetProperties()
                    let column = property.GetColumnName()
                    where column is not null && !SnakeCase.IsMatch(column)
                    select $"{table}.{column}");

                offenders.AddRange(
                    from index in entity.GetIndexes()
                    let indexName = index.GetDatabaseName()
                    where indexName is not null && !SnakeCase.IsMatch(indexName)
                    select $"index {indexName}");

                offenders.AddRange(
                    from key in entity.GetKeys()
                    let keyName = key.GetName()
                    where keyName is not null && !SnakeCase.IsMatch(keyName)
                    select $"key {keyName}");
            }

            Assert.True(
                offenders.Count == 0,
                $"{name}: {offenders.Count} identifier(s) are not snake_case — raw SQL in this repo "
                + $"spells them unquoted, and Postgres will not find them:\n  "
                + string.Join("\n  ", offenders));
        }
    }

    /// <summary>
    /// The outbox is the one place raw SQL and the model must agree by name, so
    /// the agreement is asserted directly rather than inferred from the
    /// convention holding.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contexts))]
    public void Outbox_columns_match_the_names_the_dispatcher_queries(string name, DbContext context)
    {
        using (context)
        {
            var outbox = context.Model.GetEntityTypes()
                .Single(e => e.GetTableName() == "outbox_messages");

            var columns = outbox.GetProperties()
                .Select(p => p.GetColumnName())
                .ToHashSet(StringComparer.Ordinal);

            // The literal names in OutboxDispatcher's FROM/WHERE/ORDER BY.
            foreach (var required in new[] { "dispatched_at", "created_at" })
            {
                Assert.True(
                    columns.Contains(required),
                    $"{name}: outbox_messages has no column '{required}', which OutboxDispatcher's "
                    + $"raw SQL selects on. Columns are: {string.Join(", ", columns.Order())}");
            }
        }
    }
}
