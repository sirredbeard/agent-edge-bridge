namespace AgentEdgeBridge.Api;

// Request DTOs for the HTTP API. JSON is camelCase (configured in Program.cs).
internal sealed class CreateSessionRequest
{
    public string? BrowserPath { get; set; }
    public string? UserDataDir { get; set; }
    public bool Headless { get; set; } = true;
    // Named storage-state profile to load (cookies/session from profiles/<name>.json).
    public string? Profile { get; set; }
}

internal sealed class GotoRequest
{
    public string Url { get; set; } = "";
}

internal sealed class FillRequest
{
    public string Selector { get; set; } = "";
    public string Text { get; set; } = "";
}

internal sealed class ClickRequest
{
    public string Selector { get; set; } = "";
}

internal sealed class PressRequest
{
    public string Selector { get; set; } = "";
    public string Key { get; set; } = "";
}

internal sealed class WaitRequest
{
    public string? Selector { get; set; }
    public int TimeoutMs { get; set; } = 10000;
}

internal sealed class SaveStorageRequest
{
    public string Name { get; set; } = "";
}

// Response DTOs.
internal sealed class CreateSessionResponse
{
    public string SessionId { get; set; } = "";
}

internal sealed class GotoResponse
{
    public bool Ok { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

internal sealed class SimpleOk
{
    public bool Ok { get; set; } = true;
}
