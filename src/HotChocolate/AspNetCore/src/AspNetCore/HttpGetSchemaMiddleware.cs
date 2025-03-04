using Microsoft.AspNetCore.Http;
using HotChocolate.AspNetCore.Serialization;
using HttpRequestDelegate = Microsoft.AspNetCore.Http.RequestDelegate;
using static HotChocolate.SchemaSerializer;
using static HotChocolate.AspNetCore.ErrorHelper;
using static System.Net.HttpStatusCode;

namespace HotChocolate.AspNetCore;

public class HttpGetSchemaMiddleware : MiddlewareBase
{
    private readonly MiddlewareRoutingType _routing;

    public HttpGetSchemaMiddleware(
        HttpRequestDelegate next,
        IRequestExecutorResolver executorResolver,
        IHttpResultSerializer resultSerializer,
        NameString schemaName,
        MiddlewareRoutingType routing)
        : base(next, executorResolver, resultSerializer, schemaName)
    {
        _routing = routing;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var handle = _routing == MiddlewareRoutingType.Integrated
            ? HttpMethods.IsGet(context.Request.Method) &&
              context.Request.Query.ContainsKey("SDL") &&
              (context.GetGraphQLServerOptions()?.EnableSchemaRequests ?? true)
            : HttpMethods.IsGet(context.Request.Method) &&
              (context.GetGraphQLServerOptions()?.EnableSchemaRequests ?? true);

        if (handle)
        {
            await HandleRequestAsync(context);
        }
        else
        {
            // if the request is not a get request or if the content type is not correct
            // we will just invoke the next middleware and do nothing.
            await NextAsync(context);
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        ISchema schema = await GetSchemaAsync(context.RequestAborted);

        bool indent =
            !(context.Request.Query.ContainsKey("indentation") &&
                string.Equals(
                    context.Request.Query["indentation"].FirstOrDefault(),
                    "none",
                    StringComparison.OrdinalIgnoreCase));

        if (context.Request.Query.TryGetValue("types", out var typesValue))
        {
            if (string.IsNullOrEmpty(typesValue))
            {
                await WriteResultAsync(context, TypeNameIsEmpty(), BadRequest);
                return;
            }

            await WriteTypesAsync(context, schema, typesValue, indent);
        }
        else
        {
            await WriteSchemaAsync(context, schema, indent);
        }
    }

    private async Task WriteTypesAsync(
        HttpContext context,
        ISchema schema,
        string typeNames,
        bool indent)
    {
        var types = new List<INamedType>();

        foreach (string typeName in typeNames.Split(','))
        {
            if (!SchemaCoordinate.TryParse(typeName, out var coordinate) ||
                coordinate.Value.MemberName is not null ||
                coordinate.Value.ArgumentName is not null)
            {
                await WriteResultAsync(context, InvalidTypeName(typeName), BadRequest);
                return;
            }

            if (!schema.TryGetType<INamedType>(coordinate.Value.Name, out var type))
            {
                await WriteResultAsync(context, TypeNotFound(typeName), NotFound);
                return;
            }

            types.Add(type);
        }

        context.Response.ContentType = ContentType.GraphQL;
        context.Response.Headers.SetContentDisposition(GetTypesFileName(types));
        await SerializeAsync(types, context.Response.Body, indent, context.RequestAborted);
        return;
    }

    private async Task WriteSchemaAsync(HttpContext context, ISchema schema, bool indent)
    {
        context.Response.ContentType = ContentType.GraphQL;
        context.Response.Headers.SetContentDisposition(GetSchemaFileName(schema));
        await SerializeAsync(schema, context.Response.Body, indent, context.RequestAborted);
    }

    private string GetTypesFileName(List<INamedType> types)
        => types.Count == 1
            ? $"{types[0].Name.Value}.graphql"
            : "types.graphql";

    private string GetSchemaFileName(ISchema schema)
        => schema.Name.IsEmpty || schema.Name.Equals(Schema.DefaultName)
            ? "schema.graphql"
            : schema.Name + ".schema.graphql";
}
