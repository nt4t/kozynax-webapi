using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;

using Kozynax.WebApi.Host;
using Kozynax.WebApi.Services;

namespace Kozynax.WebApi.Http;

public static class WebEndpoints
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapAll(this WebApplication app)
    {
        var session = app.Services.GetRequiredService<EmulatorSession>();

        // --- Status / CPU -----------------------------------------------------
        app.MapGet("/status", () => Results.Ok(session.Status()));
        app.MapGet("/cpu", () => Results.Ok(session.CpuState()));

        // --- Lifecycle --------------------------------------------------------
        app.MapPost("/run", () => { session.Run(); return Results.Ok(new { status = "running" }); });
        app.MapPost("/stop", () => { session.Stop(); return Results.Ok(new { status = "stopped" }); });
        app.MapPost("/reset", () => { session.Reset(); return Results.Ok(new { status = "reset" }); });
        app.MapPost("/step", async (StepRequest? body) =>
        {
            var count = Math.Clamp(body?.Count ?? 1, 1, 1_000_000);
            session.Step(count);
            return Results.Ok(new { stepped = count });
        });

        // --- Loading ----------------------------------------------------------
        app.MapPost("/snapshot", async (HttpContext ctx) =>
        {
            if ((ctx.Request.ContentType ?? "").Contains("multipart"))
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                if (ms.Length == 0)
                    throw new ApiError(ApiErrorCode.UnsupportedFormat, "empty upload");
                var name = session.LoadFromBytes(ctx.Request.Headers["Filename"].ToString() ?? "snapshot", ms.ToArray());
                return Results.Ok(new { status = "loaded", machine = name });
            }
            var json = await ctx.Request.ReadFromJsonAsync<LoadByUrl?>();
            if (json?.Url is not null)
            {
                using var http = new HttpClient();
                using var ms = new MemoryStream(await http.GetByteArrayAsync(json.Url));
                var name = session.LoadFromBytes(Path.GetFileNameWithoutExtension(json.Url) ?? "snapshot", ms.ToArray());
                return Results.Ok(new { status = "loaded", machine = name });
            }
            throw new ApiError(ApiErrorCode.UnsupportedFormat, "expected multipart file or { url }");
        });

        app.MapPost("/config", async (HttpContext ctx) =>
        {
            if ((ctx.Request.ContentType ?? "").Contains("multipart"))
            {
                var tmp = Path.GetTempFileName();
                try
                {
                    using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                    await ctx.Request.Body.CopyToAsync(fs);
                    session.LoadConfig(tmp);
                    return Results.Ok(new { status = "config_loaded" });
                }
                finally { File.Delete(tmp); }
            }
            throw new ApiError(ApiErrorCode.UnsupportedFormat, "expected multipart config file");
        });

        // --- Memory -----------------------------------------------------------
        app.MapGet("/memory/dump", (ushort addr, int len = 256) =>
        {
            len = Math.Clamp(len, 1, 0x10000);
            var data = session.ReadMemory(addr, len);
            return Results.File(data, "application/octet-stream");
        });

        app.MapGet("/mem/{addr:int}", (ushort addr, int len = 256) =>
        {
            len = Math.Clamp(len, 1, 0x10000);
            var data = session.ReadMemory(addr, len);
            return Results.Ok(new { addr, len, hex = ToHex(data) });
        });

        app.MapPost("/mem/{addr:int}", async (ushort addr, WriteMemRequest? body) =>
        {
            var bytes = body?.ToBytes();
            if (bytes is null) throw new ApiError(ApiErrorCode.UnsupportedFormat, "expected { hex:\"..\" } or { bytes:[..] }");
            session.WriteMemory(addr, bytes);
            return Results.Ok(new { addr, written = bytes.Length });
        });

        // --- Breakpoints ------------------------------------------------------
        app.MapGet("/breakpoints", () => Results.Ok(session.BreakpointList()));
        app.MapPost("/breakpoints", (BreakpointRequest req) => { session.AddBreakpoint(req.Address); return Results.Ok(new { ok = true }); });
        app.MapDelete("/breakpoints/{addr:int}", (ushort addr) => { session.RemoveBreakpoint(addr); return Results.Ok(new { ok = true }); });
        app.MapPost("/breakpoints/clear", () => { session.ClearBreakpoints(); return Results.Ok(new { ok = true }); });

        // --- Screen -----------------------------------------------------------
        app.MapGet("/screen", (HttpContext ctx) =>
        {
            var frame = session.LatestFrame();
            if (!frame.HasValue) throw new ApiError(ApiErrorCode.NoImageLoaded, "no frame available");
            var ppm = ToPpm(frame.Value.Buffer, frame.Value.Width, frame.Value.Height);
            return Results.File(ppm, "image/pnm");
        });
    }

    // --- helpers ------------------------------------------------------------
    private static string ToHex(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 2);
        foreach (var b in data) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    /// <summary>Encode an int32 (RGBA) pixel buffer as a PPM P6 image. Dependency-free.</summary>
    private static byte[] ToPpm(int[] pixels, int w, int h)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < pixels.Length && i < rgb.Length / 3; i++)
        {
            uint p = (uint)pixels[i];
            rgb[i * 3] = (byte)(p & 0xFF);          // R
            rgb[i * 3 + 1] = (byte)((p >> 8) & 0xFF);// G
            rgb[i * 3 + 2] = (byte)((p >> 16) & 0xFF); // B
        }
        var outBytes = new byte[header.Length + rgb.Length];
        Buffer.BlockCopy(header, 0, outBytes, 0, header.Length);
        BlockCopy(rgb, 0, outBytes, header.Length, rgb.Length);
        return outBytes;
    }

    private static void BlockCopy(byte[] src, int so, byte[] dst, int doff, int count)
        => System.Buffer.BlockCopy(src, so, dst, doff, count);
}

// --- request DTOs ----------------------------------------------------------
public sealed record StepRequest(int? Count);
public sealed record LoadByUrl(string Url);
public sealed record BreakpointRequest(ushort Address);
public sealed record WriteMemRequest(string? Hex = null, byte[]? Bytes = null)
{
    public byte[]? ToBytes()
    {
        if (Hex is not null)
        {
            if (Hex.Length % 2 != 0) return null;
            var b = new byte[Hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                if (!byte.TryParse(Hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, null, out b[i])) return null;
            }
            return b;
        }
        return Bytes?.ToArray();
    }
}
