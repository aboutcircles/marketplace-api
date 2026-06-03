using Microsoft.Extensions.Logging;
using Npgsql;

namespace Circles.Market.Shared.Db;

/// <summary>
/// Immunizes identifier columns against glibc/ICU collation drift by converting index-key
/// <c>text</c> columns to <c>COLLATE "C"</c> (raw byte ordering, libc-version-independent).
///
/// Collation drift can silently corrupt a btree on a locale-collated text column: after a libc
/// upgrade the index navigates to the wrong leaf, a uniqueness check misses an existing key, and
/// duplicate rows slip in under a "valid" unique index. <c>"C"</c> ordering removes that exposure.
///
/// The migration is best-effort, non-fatal, and idempotent — safe to run on every startup. It only
/// touches columns that are part of an index (the only ones a collation can corrupt) and that are
/// not already <c>"C"</c>. <c>ALTER COLUMN ... TYPE text COLLATE "C"</c> is collation-only on the
/// same base type, so it rewrites dependent indexes (ACCESS EXCLUSIVE, brief) but not the heap.
/// </summary>
public static class IdentifierCollation
{
    // Generic across all adapter-owned tables in the public schema (no table whitelist): each adapter
    // database is fully owned by its adapter and has no trigger-bound columns, so scoping to every
    // index-key text column is correct. Per-column sub-transactions keep one bad column from aborting
    // the rest; a unique_violation is re-raised because it means drift already struck (live duplicates).
    private const string MigrationSql = @"
DO $$ DECLARE r record; BEGIN
  PERFORM set_config('lock_timeout', '3s', true);
  FOR r IN
    SELECT DISTINCT c.relname AS tbl, a.attname AS col
    FROM pg_attribute a
    JOIN pg_class c ON c.oid = a.attrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_collation co ON co.oid = a.attcollation
    JOIN pg_index i ON i.indrelid = c.oid AND a.attnum = ANY (i.indkey)
    WHERE n.nspname = 'public'
      AND a.attnum > 0 AND NOT a.attisdropped
      AND a.atttypid = 'text'::regtype
      AND co.collname <> 'C'
  LOOP
    BEGIN
      EXECUTE format('ALTER TABLE public.%I ALTER COLUMN %I TYPE text COLLATE ""C""', r.tbl, r.col);
      RAISE NOTICE 'collation->C: %.%', r.tbl, r.col;
    EXCEPTION
      WHEN unique_violation THEN
        RAISE;  -- live duplicate rows under a unique index = real collation drift: surface it
      WHEN others THEN
        RAISE WARNING 'collation->C skipped %.% (dependency): %', r.tbl, r.col, SQLERRM;
    END;
  END LOOP;
END $$;";

    /// <summary>
    /// Converts index-key <c>text</c> columns in the <c>public</c> schema to <c>COLLATE "C"</c>.
    /// Never throws — failures are logged and retried on the next startup.
    /// </summary>
    /// <param name="connString">Connection string for the adapter database.</param>
    /// <param name="logger">Logger; the real-drift case (live duplicates) is logged at Error level — alert on it.</param>
    /// <param name="store">Short label for the adapter/store, used in log messages.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task EnsureIndexKeyTextColumnsCollatedToCAsync(
        string connString, ILogger logger, string store, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = MigrationSql;
            await cmd.ExecuteNonQueryAsync(ct);
            logger.LogInformation(
                "Identifier collation (COLLATE \"C\") ensured for {Store} index-key text columns.", store);
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Drift already struck: live duplicate rows exist under a unique index. Non-fatal (the
            // service still starts) but this needs investigation/dedup — alert on this Error line.
            logger.LogError(pg,
                "COLLATE \"C\" migration for {Store} hit a unique violation: live duplicate rows exist " +
                "under a unique index (glibc collation drift). Identifier columns remain on a locale " +
                "collation. Investigate and de-duplicate, then re-run.", store);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Non-fatal: could not ensure COLLATE \"C\" on {Store} identifier columns; " +
                "will retry next start.", store);
        }
    }
}
