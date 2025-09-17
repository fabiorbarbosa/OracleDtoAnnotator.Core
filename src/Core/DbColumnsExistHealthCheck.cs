using LinqToDB.Data;

await using var ctx = new MyOracleDbContext(); // herda DataConnection
var validator = new DbColumnExistenceValidator(ctx, respectQuotedIdentifiers: false, useUserTabCols: false);

// Ex.: validar apenas entidades do namespace .Entities
// Func<Type,bool> filter = t => t.Namespace?.EndsWith(".Entities") == true;

validator.ThrowIfAnyMissing(/*filter*/);

builder.Services.AddHealthChecks()
    .AddCheck("db-columns-exist",
        sp => new DbColumnsExistHealthCheck<MyOracleDbContext>(
            sp.GetRequiredService<MyOracleDbContext>(),
            respectQuotedIdentifiers: false,
            useUserTabCols: false,
            filter: t => t.Namespace?.EndsWith(".Entities") == true));