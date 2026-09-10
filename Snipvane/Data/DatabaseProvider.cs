namespace Snipvane.Data;

public static class DatabaseProvider
{
    public static (bool IsPostgres, string ConnectionString) Resolve(IConfiguration config)
    {
        var raw = FirstNonEmpty(
            Environment.GetEnvironmentVariable("DATABASE_URL"),
            config["DATABASE_URL"],
            config.GetConnectionString("DefaultConnection"));

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "No database configured. Set ConnectionStrings:DefaultConnection or DATABASE_URL.");
        }

        if (IsPostgres(raw))
        {
            return (true, ToNpgsql(raw));
        }

        return (false, raw);
    }

    public static bool IsPostgres(string connectionString)
    {
        var value = connectionString.Trim();
        return value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("SQLEXPRESS", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("Initial Catalog", StringComparison.OrdinalIgnoreCase);
    }

    public static string ToNpgsql(string connectionString)
    {
        var value = connectionString.Trim();
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var uri = new Uri(value);
        var user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        var password = uri.UserInfo.Contains(':')
            ? Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..])
            : string.Empty;
        var database = uri.AbsolutePath.Trim('/');
        var port = uri.Port > 0 ? uri.Port : 5432;

        return $"Host={uri.Host};Port={port};Database={database};Username={user};Password={password};SSL Mode=Require;Trust Server Certificate=true";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
