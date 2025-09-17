// DbColumnExistenceValidator.cs
using LinqToDB;
using LinqToDB.Data;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class DbColumnExistenceValidator
{
    private readonly DataConnection _ctx;
    private readonly bool _respectQuotedIdentifiers;
    private readonly bool _useUserTabCols;

    /// <param name="respectQuotedIdentifiers">true para não fazer ToUpper (caso use nomes com aspas)</param>
    /// <param name="useUserTabCols">true para consultar USER_TAB_COLS (schema atual) ao invés de ALL_TAB_COLUMNS</param>
    public DbColumnExistenceValidator(DataConnection ctx, bool respectQuotedIdentifiers = false, bool useUserTabCols = false)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _respectQuotedIdentifiers = respectQuotedIdentifiers;
        _useUserTabCols = useUserTabCols;
    }

    public sealed record TableCheckResult(
        string EntityName,
        string Schema,
        string Table,
        IReadOnlyList<string> MissingColumns)
    {
        public bool Ok => MissingColumns.Count == 0;
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[{EntityName}] {Schema}.{Table} => ");
            sb.Append(Ok ? "✔ OK" : "Faltando: " + string.Join(", ", MissingColumns));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Varre as propriedades ITable&lt;T&gt; do DbContext, extrai os nomes de colunas mapeadas e valida se existem no Oracle.
    /// </summary>
    /// <param name="tableFilter">Opcional: filtra os tipos T das tabelas (ex.: por namespace)</param>
    public IReadOnlyList<TableCheckResult> Validate(Func<Type, bool>? tableFilter = null)
    {
        var results = new List<TableCheckResult>();

        // Propriedades públicas ITable<T>
        var tableProps = _ctx.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(ITable<>))
            .ToList();

        // Usuário/schema atual
        string currentUser = _ctx.Execute<string>("SELECT USER FROM DUAL");

        Func<string, string> norm = _respectQuotedIdentifiers ? s => s : s => s.ToUpperInvariant();

        foreach (var prop in tableProps)
        {
            var entityType = prop.PropertyType.GetGenericArguments()[0];
            if (tableFilter != null && !tableFilter(entityType))
                continue;

            var ed = _ctx.MappingSchema.GetEntityDescriptor(entityType);
            if (ed == null || string.IsNullOrWhiteSpace(ed.TableName))
                continue; // não mapeado como tabela

            string owner = norm((ed.SchemaName ?? currentUser) ?? currentUser);
            string table = norm(ed.TableName);

            // Colunas mapeadas na entity
            var mappedCols = ed.Columns
                .Select(c => c.ColumnName ?? c.MemberName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(norm)
                .Distinct()
                .ToArray();

            // Colunas existentes no Oracle
            var dbColSet = GetColumnsFromOracle(owner, table, norm);

            // Diferenças
            var missing = mappedCols.Where(mc => !dbColSet.Contains(mc))
                                    .OrderBy(x => x)
                                    .ToArray();

            results.Add(new TableCheckResult(
                EntityName: entityType.FullName ?? entityType.Name,
                Schema: owner,
                Table: table,
                MissingColumns: missing));
        }

        return results;
    }

    /// <summary> Lança exceção se houver qualquer coluna faltando — útil para "quebrar" o build/CI. </summary>
    public void ThrowIfAnyMissing(Func<Type, bool>? tableFilter = null)
    {
        var res = Validate(tableFilter);
        var offenders = res.Where(r => !r.Ok).ToList();
        if (offenders.Count == 0) return;

        var sb = new StringBuilder("Colunas faltando no banco:\n");
        foreach (var r in offenders)
            sb.AppendLine(" - " + r.ToString());
        throw new InvalidOperationException(sb.ToString());
    }

    // --------- Internals ---------

    private HashSet<string> GetColumnsFromOracle(string owner, string table, Func<string, string> norm)
    {
        // Para mapear por nome sem precisar de async/ExecuteReaderAsync
        var sqlAll = @"SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS WHERE OWNER = :owner AND TABLE_NAME = :table";
        var sqlUser = @"SELECT COLUMN_NAME FROM USER_TAB_COLS WHERE TABLE_NAME = :table";

        if (_useUserTabCols)
        {
            return _ctx.Query<DbColName>(sqlUser,
                        new DataParameter("table", table))
                       .Select(r => norm(r.COLUMN_NAME))
                       .ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            return _ctx.Query<DbColName>(sqlAll,
                        new DataParameter("owner", owner),
                        new DataParameter("table", table))
                       .Select(r => norm(r.COLUMN_NAME))
                       .ToHashSet(StringComparer.Ordinal);
        }
    }

    private sealed class DbColName
    {
        // Nome igual ao do SELECT para LinqToDB mapear por convenção
        public string COLUMN_NAME { get; set; } = "";
    }
}

// ------------------ (Opcional) HealthCheck síncrono ------------------

public sealed class DbColumnsExistHealthCheck<TCtx> : IHealthCheck where TCtx : DataConnection
{
    private readonly TCtx _ctx;

    public DbColumnsExistHealthCheck(TCtx ctx) // aqui o DI injeta seu DbContext
    {
        _ctx = ctx;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var validator = new DbColumnExistenceValidator(_ctx);
            var results = validator.Validate();
            var missing = results.Where(r => !r.Ok).ToList();

            if (missing.Count == 0)
                return Task.FromResult(
                    HealthCheckResult.Healthy("Todas as colunas mapeadas existem no banco."));

            var desc = string.Join("; ", missing.Select(m => m.ToString()));
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Colunas faltando: " + desc));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Erro ao validar colunas.", ex));
        }
    }
}