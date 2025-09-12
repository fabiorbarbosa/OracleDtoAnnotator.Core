
using System.Text;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Data;

namespace OracleDtoAnnotator.Core;

internal record AnnotateResult(string File, string? Table, string Status)
{
    public override string ToString()
        => $"{Path.GetFileName(File)}: {Status}"
            + (Table is null ? "" : $" (Table: {Table})");
}

internal class Annotator(DataConnection dbContext, string schema, string suffix)
{
    private readonly DataConnection _db = dbContext;
    private readonly string _schema = schema.ToUpperInvariant();
    private readonly string _suffix = suffix;

    public async Task<List<AnnotateResult>> RunAsync(string rootDir, bool dryRun)
    {
        var results = new List<AnnotateResult>();
        var files = Directory.EnumerateFiles(rootDir, $"*{_suffix}", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            Console.WriteLine($"Nenhum arquivo encontrado com sufixo '{_suffix}' em {rootDir}.");
            return results;
        }

        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                var table = ParseTableName(content);
                if (string.IsNullOrWhiteSpace(table))
                {
                    results.Add(new AnnotateResult(file, null, "Sem [Table] - ignorado"));
                    continue;
                }

                var meta = LoadTableMeta(table!);

                var updated = InjectAnnotations(content, meta);
                if (updated == content)
                {
                    results.Add(new AnnotateResult(file, table, "Nada a injetar"));
                    continue;
                }

                var newPath = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".novo.cs");
                if (!dryRun)
                    await File.WriteAllTextAsync(newPath, updated, Encoding.UTF8);
                results.Add(new AnnotateResult(file, table, dryRun ? "DRY-RUN: geraria .novo.cs com annotations" : "Annotations injetadas em .novo.cs"));
            }
            catch (Exception ex)
            {
                results.Add(new AnnotateResult(file, null, "Erro: " + ex.Message));
            }
        }

        return results;
    }

    // ---- Parsing helpers ----

    private static string? ParseTableName(string content)
    {
        var m = Regex.Match(
            content,
            @"\[Table\s*\(\s*(Name\s*=\s*)?""(?<t>[^""]+)""",
            RegexOptions.IgnoreCase);

        return m.Success ? m.Groups["t"].Value : null;
    }

    private static string ToPascal(string s)
    {
        var parts = Regex
            .Split(s, @"[_\s]+")
            .Where(p => p.Length > 0)
            .ToArray();

        return string.Join("",
            parts.Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..].ToLowerInvariant() : "")));
    }

    // ---- DB model ----
    private sealed record PkRow(string ColumnName);
    private sealed record FkHeaderRow(string ConstraintName, string ThisTable, string RefTable);
    private sealed record FkColRow(string ConstraintName, string ThisColumn, string RefColumn);

    private TableMeta LoadTableMeta(string table)
    {
        var pk = _db.Query<PkRow>(@"
SELECT acc.column_name AS ColumnName
FROM all_constraints ac
JOIN all_cons_columns acc ON acc.owner = ac.owner AND acc.constraint_name = ac.constraint_name
WHERE ac.owner = :p_owner AND ac.constraint_type = 'P' AND ac.table_name = :p_table
ORDER BY acc.position",
            new DataParameter("p_owner", _schema),
            new DataParameter("p_table", table))
            .Select(r => r.ColumnName)
            .ToList();

        var hdr = _db.Query<FkHeaderRow>(@"
SELECT a.constraint_name AS ConstraintName,
       a.table_name       AS ThisTable,
       a_r.table_name     AS RefTable
FROM all_constraints a
JOIN all_constraints a_r ON a_r.owner = a.r_owner AND a_r.constraint_name = a.r_constraint_name
WHERE a.owner = :p_owner AND a.constraint_type = 'R' AND a.table_name = :p_table",
            new DataParameter("p_owner", _schema),
            new DataParameter("p_table", table))
            .ToDictionary(x => x.ConstraintName, x => (x.ThisTable, x.RefTable), StringComparer.OrdinalIgnoreCase);

        var fk = new List<FkMeta>();
        var fkCols = _db.Query<FkColRow>(@"
SELECT acc.constraint_name AS ConstraintName,
       acc.column_name     AS ThisColumn,
       acc_r.column_name   AS RefColumn
FROM all_cons_columns acc
JOIN all_constraints ac ON ac.owner = acc.owner AND ac.constraint_name = ac.constraint_name
JOIN all_cons_columns acc_r ON acc_r.owner = ac.r_owner AND acc_r.constraint_name = ac.r_constraint_name AND acc_r.position = acc.position
WHERE ac.owner = :p_owner AND ac.constraint_type = 'R' AND acc.table_name = :p_table
ORDER BY acc.constraint_name, acc.position",
            new DataParameter("p_owner", _schema),
            new DataParameter("p_table", table))
            .ToList();

        foreach (var r in fkCols)
        {
            if (!hdr.TryGetValue(r.ConstraintName, out var h))
                continue;

            var f = fk.FirstOrDefault(x => x.Name.Equals(r.ConstraintName, StringComparison.OrdinalIgnoreCase));
            if (f is null)
            {
                f = new FkMeta { Name = r.ConstraintName, ThisTable = h.ThisTable, RefTable = h.RefTable, Pairs = new() };
                fk.Add(f);
            }
            f.Pairs!.Add((r.ThisColumn, r.RefColumn));
        }

        return new TableMeta { Table = table, PrimaryKeys = pk, ForeignKeys = fk };
    }

    // ---- Injection logic ----

    private static string InjectAnnotations(string content, TableMeta meta)
    {
        var lines = content.SplitlinesPreserve();
        var changed = false

;        // Regex to find property lines: e.g., public int CustomerId { get; set; }
        var propRegex = new Regex(@"^\s*public\s+[^\s]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get;\s*set;\s*\}\s*$");

        // Map property name -> line index
        var propIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Length; i++)
        {
            var m = propRegex.Match(lines[i]);
            if (m.Success)
            {
                propIndex[m.Groups["name"].Value] = i;
            }
        }

        // Helper to inject attribute above a property
        void InjectAbove(string propName, string attributeLine)
        {
            if (!propIndex.TryGetValue(propName, out var idx)) return;
            // avoid duplicate if already present
            if (idx > 0 && lines[idx - 1].Contains(attributeLine)) return;
            var indent = new string(lines[idx].TakeWhile(char.IsWhiteSpace).ToArray());
            var insertion = indent + attributeLine;
            lines = lines.InsertAt(idx, insertion);
            // shift indices after insertion
            propIndex = propIndex.ToDictionary(kv => kv.Key, kv => kv.Value + (kv.Value >= idx ? 1 : 0), StringComparer.OrdinalIgnoreCase);
            changed = true;
        }

        // PKs
        foreach (var col in meta.PrimaryKeys)
        {
            var propName = ToPascal(col);
            InjectAbove(propName, "[PrimaryKey]");
            InjectAbove(propName, $"[Column(Name = \"{col}\")]");
        }

        // FKs + Associations
        foreach (var fk in meta.ForeignKeys)
        {
            // FK column attributes
            foreach (var (thisCol, _) in fk.Pairs)
            {
                var p = ToPascal(thisCol);
                InjectAbove(p, $"[Column(Name = \"{thisCol}\")]");
            }

            // Try to find a navigation property matching referenced table type or name
            var parentType = ToPascal(fk.RefTable);
            var navRegex = new Regex($@"^\s*public\s+{parentType}\??\s+(?<name>{parentType})\s*\{{\s*get;\s*set;\s*\}}\s*$");
            int navIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (navRegex.IsMatch(lines[i])) { navIdx = i; break; }
            }
            if (navIdx >= 0)
            {
                var thisKeys = string.Join(",", fk.Pairs.Select(p => ToPascal(p.ThisCol)));
                var otherKeys = string.Join(",", fk.Pairs.Select(p => ToPascal(p.RefCol)));
                var indent = new string(lines[navIdx].TakeWhile(char.IsWhiteSpace).ToArray());
                var attr = $"{indent}[Association(ThisKey = \"{thisKeys}\", OtherKey = \"{otherKeys}\", CanBeNull = true)]";
                // Avoid duplicate
                if (navIdx == 0 || !lines[navIdx - 1].Contains("[Association("))
                {
                    lines = lines.InsertAt(navIdx, attr);
                    changed = true;
                }
            }
        }

        return changed ? string.Join("\n", lines) : content;
    }
}

internal class TableMeta
{
    public string Table { get; set; } = "";
    public List<string> PrimaryKeys { get; set; } = [];
    public List<FkMeta> ForeignKeys { get; set; } = [];
}

internal class FkMeta
{
    public string Name { get; set; } = "";
    public string ThisTable { get; set; } = "";
    public string RefTable { get; set; } = "";
    public List<(string ThisCol, string RefCol)> Pairs { get; set; } = new();
}

// -------- small helpers ----------
internal static class StringArrayExtensions
{
    public static string[] SplitlinesPreserve(this string s)
        => s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

    public static string[] InsertAt(this string[] arr, int index, string line)
    {
        var list = arr.ToList();
        list.Insert(index, line);
        return list.ToArray();
    }
}
