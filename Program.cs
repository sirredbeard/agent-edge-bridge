using System.Text.Json;
using AgentEdgeBridge.Api;
using AgentEdgeBridge.Playwright;

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
            body.BrowserPath, body.UserDataDir, body.Headless, body.Profile);
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
