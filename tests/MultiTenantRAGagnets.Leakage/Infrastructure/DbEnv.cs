using System.Globalization;
using Npgsql;

namespace MultiTenantRAGagnets.Leakage.Infrastructure;

/// <summary>
/// Where the database comes from, and what to do when it is absent.
///   env present                 -> tests run.
///   env absent, CI not "true"   -> DB tests SKIP with a clear message (local laptop without Docker).
///   env absent, CI == "true"    -> FAIL. A green run with no database behind it is hollow.
/// Same convention as db/tests/run.sh: OWNER_URL (superuser, seeds and breaks things) and APP_URL (app_user).
/// </summary>
public static class DbEnv
{
    public const string OwnerVar = "OWNER_URL";
    public const string AppVar = "APP_URL";

    public static string? OwnerUrl => Clean(Environment.GetEnvironmentVariable(OwnerVar));
    public static string? AppUrl => Clean(Environment.GetEnvironmentVariable(AppVar));

    public static bool Configured => OwnerUrl is not null && AppUrl is not null;

    public static bool IsCi => IsCiValue(Environment.GetEnvironmentVariable("CI"));

    public static bool IsCiValue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    public static string SkipMessage =>
        $"SKIPPED: {OwnerVar} and {AppVar} are not set, so there is no database to test against. " +
        "These integration tests need a real Postgres (pgvector/pgvector:pg16 with db/migrations applied). " +
        "They are UNVERIFIED until they run in CI (which fails, not skips, when the vars are missing).";

    /// <summary>Why the run must fail instead of skip, or null when it is fine.</summary>
    public static string? HollowRunError =>
        IsCi && !Configured
            ? $"CI=true but {OwnerVar}/{AppVar} are missing: refusing to go green without a database. " +
              "A leakage suite that never touched Postgres proves nothing."
            : null;

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// Accepts postgres://user:pw@host:port/db (the db/tests/run.sh form) or a plain Npgsql key=value string.
    /// Npgsql does not parse URLs itself.
    /// </summary>
    public static string ToNpgsqlConnectionString(string urlOrConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlOrConnectionString);
        var s = urlOrConnectionString.Trim();
        if (!s.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return s;
        }

        var uri = new Uri(s);
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            csb.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 2) csb.Password = Uri.UnescapeDataString(parts[1]);
        }
        return csb.ConnectionString;
    }
}

/// <summary>
/// [Fact] that skips (with a message) when there is no database, but NOT in CI: in CI it runs, the fixture
/// throws, and the test fails.
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (!DbEnv.Configured && !DbEnv.IsCi)
        {
            Skip = DbEnv.SkipMessage;
        }
    }
}
