using System.Text.Json;
using AgentEdgeBridge.Api;
using AgentEdgeBridge.Playwright;
using System.Net.Sockets;
using System.Net.WebSockets;

// agent-edge-bridge: a key-authenticated HTTP relay that drives a real
// Chromium-based browser on this box. The browser egresses via the local
// network, so a remote client gets residential-IP browsing with full JS.
//
// Driven by Playwright for .NET (native ARM64 Chromium it downloads
// itself). Published self-contained, no AOT: Playwright's driver model
// needs runtime reflection.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:8899");
// The API speaks camelCase JSON, matching the original contract.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
var app = builder.Build();

var apiKey = Environment.GetEnvironmentVariable("AGENT_EDGE_BRIDGE_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine("AGENT_EDGE_BRIDGE_API_KEY is not set. Refusing to start without auth.");
    return 1;
}

PlaywrightSession? active = null;
var sessionLock = new SemaphoreSlim(1, 1);

// Auth: everything except /health needs the key.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/health")
    {
        await next();
        return;
    }
    if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var got) || got != apiKey)
    {
        ctx.Response.StatusCode = 401;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Json(new SimpleOk()));

app.UseWebSockets();

// ADB relay: an authenticated WebSocket that pipes raw bytes to the
// Android container's adbd and back. ADB is a stream protocol, so the
// socket carries it unchanged. This is what makes the container
// reachable through the tunnel when nobody is on the local network:
// open a WebSocket here, bridge it to a local TCP port on the client
// side, and point adb at that port. Nothing but the ADB stream goes
// on the socket. The API key middleware above covers this route too.
app.MapGet("/adb", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var target = AdbEndpoint.Resolve();
    if (target is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    using var tcp = new TcpClient();
    try
    {
        await tcp.ConnectAsync(target.Value.Host, target.Value.Port, ctx.RequestAborted);
    }
    catch (Exception)
    {
        await ws.CloseAsync(WebSocketCloseStatus.InternalServerError,
            "adb endpoint unreachable", CancellationToken.None);
        return;
    }
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    var stream = tcp.GetStream();
    var wsToTcp = Task.Run(async () =>
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
            }
        }
        catch (Exception) { /* peer went away; shut the pair down */ }
        finally { cts.Cancel(); }
    });
    var tcpToWs = Task.Run(async () =>
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, cts.Token)) > 0)
                await ws.SendAsync(buffer.AsMemory(0, n),
                    WebSocketMessageType.Binary, true, cts.Token);
        }
        catch (Exception) { /* peer went away; shut the pair down */ }
        finally { cts.Cancel(); }
    });
    await Task.WhenAll(wsToTcp, tcpToWs);
    if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
    {
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure,
                "adb stream ended", CancellationToken.None);
        }
        catch (Exception) { /* already gone */ }
    }
});

app.MapPost("/sessions", async (HttpRequest req, CancellationToken ct) =>
{
    var body = await req.ReadFromJsonAsync<CreateSessionRequest>(ct)
               ?? new CreateSessionRequest();
    await sessionLock.WaitAsync(ct);
    try
    {
        if (active is not null)
            return Results.Conflict("A session is already active. Delete it first.");
        var session = await PlaywrightSession.LaunchAsync(
            body.BrowserPath, body.UserDataDir, body.Headless, body.Profile, body.CdpEndpoint);
        active = session;
        return Results.Json(new CreateSessionResponse { SessionId = session.Id });
    }
    finally { sessionLock.Release(); }
});

// Small helper: 404 when the id does not match the live session.
PlaywrightSession? Get(string id) => active is not null && active.Id == id ? active : null;

app.MapPost("/sessions/{id}/goto", async (string id, HttpRequest req, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    var body = await req.ReadFromJsonAsync<GotoRequest>(ct);
    if (body is null || string.IsNullOrEmpty(body.Url)) return Results.BadRequest("url is required");
    try
    {
        var (ok, title, url) = await s.GotoAsync(body.Url);
        return Results.Json(new GotoResponse { Ok = ok, Title = title, Url = url });
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/sessions/{id}/fill", async (string id, HttpRequest req, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    var body = await req.ReadFromJsonAsync<FillRequest>(ct);
    if (body is null) return Results.BadRequest();
    try { await s.FillAsync(body.Selector, body.Text); return Results.Json(new SimpleOk()); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/sessions/{id}/click", async (string id, HttpRequest req, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    var body = await req.ReadFromJsonAsync<ClickRequest>(ct);
    if (body is null) return Results.BadRequest();
    try { await s.ClickAsync(body.Selector); return Results.Json(new SimpleOk()); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/sessions/{id}/press", async (string id, HttpRequest req, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    var body = await req.ReadFromJsonAsync<PressRequest>(ct);
    if (body is null) return Results.BadRequest();
    try { await s.PressAsync(body.Selector, body.Key); return Results.Json(new SimpleOk()); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/sessions/{id}/wait", async (string id, HttpRequest req, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    var body = await req.ReadFromJsonAsync<WaitRequest>(ct)
               ?? new WaitRequest();
    try { await s.WaitAsync(body.Selector, body.TimeoutMs); return Results.Json(new SimpleOk()); }
    catch (TimeoutException ex) { return Results.Problem(ex.Message, statusCode: 504); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapGet("/sessions/{id}/html", async (string id, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    try { return Results.Text(await s.GetHtmlAsync(), "text/html"); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapGet("/sessions/{id}/screenshot", async (string id, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    try { return Results.Bytes(await s.ScreenshotAsync(), "image/png"); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapGet("/sessions/{id}/cookies", async (string id, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    try { return Results.Json(new { cookies = await s.GetCookiesAsync() }); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

// Persist the session's storage state (cookies, local storage) under a
// name. A later session started with {"profile": name} picks it up,
// so logins survive restarts.
app.MapPost("/sessions/{id}/storage/save", async (string id, HttpRequest req, CancellationToken ct) =>
{
    var s = Get(id);
    if (s is null) return Results.NotFound();
    var body = await req.ReadFromJsonAsync<SaveStorageRequest>(ct);
    if (body is null || string.IsNullOrEmpty(body.Name)) return Results.BadRequest("name is required");
    try { await s.SaveStorageAsync(body.Name); return Results.Json(new SimpleOk()); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapDelete("/sessions/{id}", async (string id, CancellationToken ct) =>
{
    await sessionLock.WaitAsync(ct);
    try
    {
        var s = Get(id);
        if (s is null) return Results.NotFound();
        await s.DisposeAsync();
        active = null;
        return Results.Json(new SimpleOk());
    }
    finally { sessionLock.Release(); }
});

app.Run();
return 0;


// Where the Android container's adbd listens. An explicit
// AGENT_EDGE_BRIDGE_ADB_ADDRESS ("host:port") wins. Otherwise the
// address comes from the dnsmasq lease file the container runtime
// maintains on its bridge interface, newest lease last in the file.
static class AdbEndpoint
{
    public static (string Host, int Port)? Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("AGENT_EDGE_BRIDGE_ADB_ADDRESS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parts = configured.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var p))
                return (parts[0], p);
        }
        try
        {
            var lines = File.ReadAllLines("/var/lib/misc/dnsmasq.waydroid0.leases");
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var fields = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 3)
                    return (fields[2], 5555);
            }
        }
        catch (IOException) { /* no lease file: no container, no relay */ }
        catch (UnauthorizedAccessException) { /* same */ }
        return null;
    }
}
