using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotNetSigningServer.Data;

/// <summary>Quoted, schema-qualified table and column names from the EF model, for raw SQL.</summary>
internal sealed class SqlNames
{
    private readonly IEntityType _entity;
    private readonly StoreObjectIdentifier _store;

    private SqlNames(IEntityType entity)
    {
        _entity = entity;
        _store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var schema = entity.GetSchema();
        Table = (schema is null ? "" : Quote(schema) + ".") + Quote(entity.GetTableName()!);
    }

    public static SqlNames Of<T>(DbContext db) => new(db.Model.FindEntityType(typeof(T))!);

    public string Table { get; }

    public string this[string property] => Quote(_entity.FindProperty(property)!.GetColumnName(_store)!);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
