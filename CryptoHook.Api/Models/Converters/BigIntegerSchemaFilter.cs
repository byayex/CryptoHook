using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Numerics;

namespace CryptoHook.Api.Models.Converters;

public class BigIntegerSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Type == typeof(BigInteger))
        {
            schema.Type = "string";
            schema.Format = null;
            schema.Reference = null;
            schema.Properties.Clear();
            schema.Items = null;
            schema.AllOf.Clear();
            schema.Description = null;
        }
    }
}