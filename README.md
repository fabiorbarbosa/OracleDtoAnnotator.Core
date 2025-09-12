
# Oracle DTO Annotator (DbCtx v3 - in-place injection)

Agora o Annotator **não cria classe nova**. Ele:
- lê o arquivo de origem,
- injeta linhas de **`[PrimaryKey]`**, **`[Column(Name="...")]`** e **`[Association(...)]`** **acima das propriedades existentes**, e
- salva como **`<arquivo>.novo.cs`** no **mesmo diretório**.

> Observação: a anotação de `[Association]` só é injetada se o DTO já tiver a *navigation property* correspondente.
> Não criamos novas propriedades, apenas inserimos as **annotations**.

## Uso
```bash
dotnet build src/OracleDtoAnnotator.DbCtx.v3.csproj -c Release

dotnet run --project src/OracleDtoAnnotator.DbCtx.v3.csproj -- \
  --rootDir "./MeuProjeto" \
  --suffix "DTO.cs" \
  --schema "MEU_SCHEMA" \
  --conn "User Id=USER;Password=PWD;Data Source=HOST:1521/DB" \
  --dryRun false
```
