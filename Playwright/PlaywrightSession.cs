using Microsoft.Playwright;

namespace AgentEdgeBridge.Playwright;

// Playwright-backed browser session. One live Chromium, driven through
// Playwright's async API: auto-waiting selectors, real user agent,
// persistent profiles. Replaces the old raw-CDP driver.
internal sealed class PlaywrightSession : IAsyncDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    private readonly IBrowserContext _context;
    private readonly IBrowser? _browser; // null when using a persistent context
    private readonly IPage _page;
    private bool _disposed;

    internal static readonly string ProfilesDir = "/home/arduino/agent-edge-bridge/profiles";

    // Current Chrome UA string; bump when it goes stale.
    private const string UserAgent =
        "Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36";

    private static readonly string[] LaunchArgs = new[]
    {
        "--disable-blink-features=AutomationControlled",
    };

    // Playwright's default --password-store=basic + --use-mock-keychain make
    // Chromium encrypt cookies with a throwaway key. A real browser profile
    // (e.g. Edge's) was encrypted against the login keyring instead, so
    // bringing such a profile via userDataDir needs the real keyring. Ignore
    // the mock flags in that case. Requires the service to have
    // DBUS_SESSION_BUS_ADDRESS pointed at an unlocked login session.
    private static readonly string[] RealKeyringArgs = new[]
    {
        "--password-store=basic",
        "--use-mock-keychain",
    };

    private PlaywrightSession(IBrowserContext context, IPage page, IBrowser? browser)
    {
        _context = context;
        _page = page;
        _browser = browser;
    }

    public static async Task<PlaywrightSession> LaunchAsync(
        string? browserPath, string? userDataDir, bool headless, string? profile)
    {
        var pw = await Microsoft.Playwright.Playwright.CreateAsync();

        string? executablePath = string.IsNullOrEmpty(browserPath) ? null : browserPath;

        // Persistent user-data dir: the directory itself is the profile,
        // cookies and storage survive restarts with no extra work.
        // A real browser profile (e.g. Edge's) needs the login keyring,
        // so the mock-keychain defaults are ignored on this path.

        if (!string.IsNullOrEmpty(userDataDir))
        {
            var context = await pw.Chromium.LaunchPersistentContextAsync(userDataDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = headless,
                    IgnoreDefaultArgs = RealKeyringArgs,
                    Args = LaunchArgs,
                    ExecutablePath = executablePath,
                    UserAgent = UserAgent,
                });
            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
            return new PlaywrightSession(context, page, null);
        }

        // Ephemeral browser + context. A named storage-state profile picks
        // up cookies/session saved by an earlier run, if the file exists.
        var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args = LaunchArgs,
            ExecutablePath = executablePath,
        });

        var ctxOptions = new BrowserNewContextOptions { UserAgent = UserAgent };
        if (!string.IsNullOrEmpty(profile))
        {
            var p = Path.Combine(ProfilesDir, profile + ".json");
            if (File.Exists(p))
                ctxOptions.StorageStatePath = p;
        }

        var ctx = await browser.NewContextAsync(ctxOptions);
        var pg = await ctx.NewPageAsync();
        return new PlaywrightSession(ctx, pg, browser);
    }

    public async Task<(bool Ok, string Title, string Url)> GotoAsync(string url)
    {
        var resp = await _page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        return (resp?.Ok == true, await _page.TitleAsync(), _page.Url);
    }

    public Task FillAsync(string selector, string text) =>
        _page.FillAsync(selector, text);

    public Task ClickAsync(string selector) =>
        _page.ClickAsync(selector);

    public Task PressAsync(string selector, string key) =>
        _page.PressAsync(selector, key);

    public Task WaitAsync(string? selector, int timeoutMs) =>
        selector is null
            ? _page.WaitForTimeoutAsync(timeoutMs)
            : _page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { Timeout = timeoutMs });

    public Task<string> GetHtmlAsync() => _page.ContentAsync();

    public Task<byte[]> ScreenshotAsync() => _page.ScreenshotAsync();

    public Task<IReadOnlyList<BrowserContextCookiesResult>> GetCookiesAsync() =>
        _context.CookiesAsync();

    // Persist cookies/local storage under a name. A later session started
    // with {"profile": name} (or the same userDataDir) picks it up, so
    // logins survive restarts.
    public Task SaveStorageAsync(string name)
    {
        Directory.CreateDirectory(ProfilesDir);
        return _context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = Path.Combine(ProfilesDir, name + ".json"),
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _context.CloseAsync();
        if (_browser is not null)
            await _browser.CloseAsync();
    }
}
