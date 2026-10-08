using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Core.Security;
using Npgsql;

namespace MultiTenantRAGagnets.Core;

public static class RagServiceCollectionExtensions
{
    /// <summary>
    /// Registers retrieval, ingestion, audit hashing, JWT and the Npgsql store. Secrets (Jwt:SigningKey,
    /// Audit:HashKey) and the connection string are validated at host start: a missing one stops the app
    /// from booting instead of failing on the first request. Providers are added separately by AddRagProviders.
    /// </summary>
    public static IServiceCollection AddRagCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection("Jwt")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services.AddOptions<AuditOptions>().Bind(configuration.GetSection("Audit")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>();

        services.AddOptions<RetrievalOptions>().Bind(configuration.GetSection("Retrieval"));

        services.AddOptions<DatabaseOptions>()
            .Configure(o => o.AppConnectionString = configuration.GetConnectionString("App"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.AppConnectionString),
                "ConnectionStrings:App is not configured. Provide it via user-secrets or ConnectionStrings__App; it is never committed.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IQueryHasher, HmacQueryHasher>();
        services.AddSingleton<JwtTokenService>();

        // Lazy: nothing connects until a request needs the database.
        services.AddSingleton(sp => NpgsqlDataSourceFactory.Create(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.AppConnectionString!));
        services.AddSingleton<IChunkStore>(sp => new NpgsqlChunkStore(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<CallerAccessor>();
        services.AddScoped<IChunkWriter, StoreBackedChunkWriter>();
        services.AddScoped<IngestionService>();
        services.AddScoped<RetrievalService>();
        return services;
    }
}

public sealed class DatabaseOptions
{
    /// <summary>Request-path connection: MUST be the locked-down app_user role, never the owner.</summary>
    public string? AppConnectionString { get; set; }
}
