using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class DbColumnsExistHealthCheck<TCtx> : IHealthCheck where TCtx : DataConnection
{
    private readonly TCtx _ctx;
    private readonly bool _respectQuotedIdentifiers;
    private readonly Func<Type, bool>? _filter;

    public DbColumnsExistHealthCheck(TCtx ctx,
                                     bool respectQuotedIdentifiers = false,
                                     Func<Type, bool>? filter = null)
    {
        _ctx = ctx;
        _respectQuotedIdentifiers = respectQuotedIdentifiers;
        _filter = filter;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var validator = new DbColumnExistenceValidator(_ctx, _respectQuotedIdentifiers);
            var results = await validator.ValidateAsync(_filter, cancellationToken);
            var missing = results.Where(r => !r.Ok).ToList();

            if (missing.Count == 0)
                return HealthCheckResult.Healthy("Todas as colunas mapeadas existem no banco.");

            var desc = string.Join("; ", missing.Select(m => m.ToString()));
            return HealthCheckResult.Unhealthy("Colunas faltando: " + desc);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Falha ao validar colunas.", ex);
        }
    }
}

//Program.cs
// builder.Services.AddHealthChecks()
//     .AddCheck("db-columns-exist",
//         sp => new DbColumnsExistHealthCheck<MyOracleDbContext>(
//             sp.GetRequiredService<MyOracleDbContext>(),
//             respectQuotedIdentifiers: false,
//             filter: t => t.Namespace?.EndsWith(".Entities") == true));