using LinqToDB.Data;

await using var ctx = new MyOracleDbContext(); // herda DataConnection
var validator = new DbColumnExistenceValidator(ctx, respectQuotedIdentifiers: false, useUserTabCols: false);

// Ex.: validar apenas entidades do namespace .Entities
// Func<Type,bool> filter = t => t.Namespace?.EndsWith(".Entities") == true;

validator.ThrowIfAnyMissing(/*filter*/);

// registra o seu DbContext normalmente
builder.Services.AddTransient<MyOracleDbContext>();

// registra o healthcheck como classe
builder.Services.AddHealthChecks()
    .AddCheck<DbColumnsExistHealthCheck<MyOracleDbContext>>(
        "db-columns-exist");