using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

using System.Reflection;
using System.Text;

public sealed class DbColumnExistenceValidator
{
    private readonly DataConnection _ctx;
    private readonly bool _respectQuotedIdentifiers;

    public DbColumnExistenceValidator(DataConnection ctx, bool respectQuotedIdentifiers = false)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _respectQuotedIdentifiers = respectQuotedIdentifiers;
    }

    public sealed record TableCheckResult(
        string EntityName,
        string? Schema,
        string Table,
        IReadOnlyList<string> MissingColumns)
    {
        public bool Ok => MissingColumns.Count == 0;
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[{EntityName}] {Schema}.{Table} => ");
            if (Ok) sb.Append("✔ OK");
            else sb.Append("Faltando: " + string.Join(", ", MissingColumns));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Varre todas as propriedades ITable&lt;T&gt; do seu DbContext e valida se cada coluna mapeada existe no Oracle.
    /// </summary>
    /// <param name="tableFilter">Opcional: filtrar entidades/tabelas pelo tipo T (ex.: namespace).</param>
    public async Task<IReadOnlyList<TableCheckResult>> ValidateAsync(Func<Type, bool>? tableFilter = null, CancellationToken ct = default)
    {
        var results = new List<TableCheckResult>();

        var tableProps = _ctx.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(ITable<>))
            .ToList();

        // cache do USER atual
        string currentUser = await _ctx.ExecuteAsync<string>("SELECT USER FROM DUAL");

        foreach (var prop in tableProps)
        {
            var entityType = prop.PropertyType.GetGenericArguments()[0];
            if (tableFilter != null && !tableFilter(entityType))
                continue;

            var ed = _ctx.MappingSchema.GetEntityDescriptor(entityType);
            if (ed == null || string.IsNullOrWhiteSpace(ed.TableName))
                continue; // não é uma entity mapeada

            string owner = (ed.SchemaName ?? currentUser) ?? currentUser;
            string table = ed.TableName;

            // Normalização de nomes
            Func<string, string> norm = _respectQuotedIdentifiers
                ? s => s // preserva casing e aspas (assuma que mapping já bate 1:1)
                : s => s.ToUpperInvariant();

            owner = norm(owner);
            table = norm(table);

            // colunas mapeadas na entity (somente nome)
            var mappedCols = ed.Columns
                .Select(c => c.ColumnName ?? c.MemberName) // ColumnName cobre atributos e fluent mapping
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(norm)
                .Distinct()
                .ToArray();

            // busca colunas no Oracle
            var dbCols = await _ctx.QueryToListAsync<string>(
                @"SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS WHERE OWNER = :owner AND TABLE_NAME = :table",
                new DataParameter("owner", owner),
                new DataParameter("table", table));

            var dbColSet = new HashSet<string>(dbCols.Select(norm), StringComparer.Ordinal);

            var missing = mappedCols.Where(mc => !dbColSet.Contains(mc)).OrderBy(x => x).ToArray();

            results.Add(new TableCheckResult(
                EntityName: entityType.FullName ?? entityType.Name,
                Schema: owner,
                Table: table,
                MissingColumns: missing));
        }

        return results;
    }

    /// <summary>
    /// Útil para "quebrar" build/pipeline: lança exceção se faltar qualquer coluna.
    /// </summary>
    public async Task ThrowIfAnyMissingAsync(Func<Type, bool>? tableFilter = null, CancellationToken ct = default)
    {
        var res = await ValidateAsync(tableFilter, ct);
        var offenders = res.Where(r => !r.Ok).ToList();
        if (offenders.Count == 0) return;

        var msg = new StringBuilder("Colunas faltando no banco:\n");
        foreach (var r in offenders)
            msg.AppendLine(" - " + r.ToString());
        throw new InvalidOperationException(msg.ToString());
    }
}

// helpers LinqToDB bem simples (evita mapear DTO só para uma coluna)
internal static class Linq2DbTinyHelpers
{
    public static async Task<List<T>> QueryToListAsync<T>(this DataConnection dc, string sql, params DataParameter[] ps)
    {
        var list = new List<T>();
        await foreach (var item in dc.QueryAsync<T>(sql, ps))
            list.Add(item);
        return list;
    }
}

// await using var ctx = new MyOracleDbContext(); // herda DataConnection
// var validator = new DbColumnExistenceValidator(ctx /*, respectQuotedIdentifiers:false*/);

// Opcional: filtrar por namespace/padrão
// Func<Type,bool> filter = t => t.Namespace?.EndsWith(".Entities") == true;

// await validator.ThrowIfAnyMissingAsync(/*filter*/);