using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Kozynax.WebApi.Host;
using Kozynax.WebApi.Http;
using Kozynax.WebApi.Middleware;
using Kozynax.WebApi.Services;
using Kozynax.WebApi.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// Listen addresses: config > env KOZYNAX_API_URLS > default (all interfaces, port 5080).
var urls = builder.Configuration["WebApi:Urls"]
           ?? Environment.GetEnvironmentVariable("KOZYNAX_API_URLS")
           ?? "http://0.0.0.0:5080";
builder.WebHost.UseUrls(urls.Split(';', StringSplitOptions.RemoveEmptyEntries));

// The running emulator is a single persistent session; the WS hub fans out its frames.
builder.Services.AddSingleton<EmulatorSession>();
builder.Services.AddSingleton<StreamHub>();

var app = builder.Build();

app.Logger.LogInformation("Kozynax Web API listening on {Urls}", urls);

// Typed error handling must run before routing/endpoints.
app.UseMiddleware<ApiErrorMiddleware>();
app.UseWebSockets();

WebEndpoints.MapAll(app);
app.MapWebSocketStream();

app.Run();

// Exposed so integration tests can host the app in-process via WebApplicationFactory<Program>.
public partial class Program { }
