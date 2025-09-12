
using System.CommandLine;
using LinqToDB.Data;
using OracleDtoAnnotator.Core;

var rootDir = new Option<string>("--rootDir", "Root directory to scan") { IsRequired = true };
var suffix = new Option<string>("--suffix", () => "DTO.cs", "Suffix for C# files to include (e.g. DTO.cs)");
var schema = new Option<string>("--schema", "Oracle schema/owner") { IsRequired = true };
var conn = new Option<string>("--conn", "Oracle connection string") { IsRequired = true };
var dryRun = new Option<bool>("--dryRun", () => false, "Don't write files, only report");

var cmd = new RootCommand("Annotate DTOs in-place: inject [PrimaryKey]/[Column]/[Association] and save as *.novo.cs");
cmd.AddOption(rootDir);
cmd.AddOption(suffix);
cmd.AddOption(schema);
cmd.AddOption(conn);
cmd.AddOption(dryRun);

cmd.SetHandler(async (string dir, string sfx, string owner, string cs, bool dr) =>
{
    await using var db = new OracleDbContext(cs);
    var annotator = new Annotator(db, owner, sfx);
    var results = await annotator.RunAsync(dir, dr);

    Console.WriteLine("\nResumo:");
    foreach (var r in results) Console.WriteLine($"- {r}");
}, rootDir, suffix, schema, conn, dryRun);

return await cmd.InvokeAsync(args);

public sealed class OracleDbContext : DataConnection
{
    public OracleDbContext(string connectionString)
        : base(LinqToDB.ProviderName.OracleManaged, connectionString) { }
}
