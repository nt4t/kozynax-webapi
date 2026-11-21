using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Kozynax.WebApi.Services;

namespace Kozynax.WebApi.Middleware;

/// <summary>Converts <see cref="ApiError"/> (and any unhandled exception) into a JSON error body.</summary>
public sealed class ApiErrorMiddleware
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiErrorMiddleware> _logger;

    public ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ApiError ex)
        {
            _logger.LogDebug("Request handled with API error {Code}: {Detail}", ex.Code.ToString(), ex.MessageDetail);
            await WriteError(context, StatusCodes.Status400BadRequest, ex.Code.ToString(), ex.MessageDetail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            await WriteError(context, StatusCodes.Status500InternalServerError, "Internal", ex.Message);
        }
    }

    private static async Task WriteError(HttpContext ctx, int status, string code, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new ApiErrorResponse(code, detail), Json);
        await ctx.Response.WriteAsync(body);
    }
}

public sealed record ApiErrorResponse(string Code, string Detail);
