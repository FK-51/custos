using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Agent.Common;
using Microsoft.Playwright;

// custos-browser：ブラウザの操作（指示書 タスク8、設計書 第14.6節）。
// 利用者側のネットワークにだけつなぎ、管理側から docker exec -i で操作する。
// - ユーザーエージェントは通常のブラウザと同じにする（自動操作と分かる値にしない。制約5）。
// - 画面を開く、操作する、スクリーンショットを撮る、コンソールのエラーを集める。
// - 利用者ごとに別のブラウザコンテキストにする（sessionStorage や Cookie を分ける）。
var state = new BrowserState();
return await AgentHost.RunAsync(args, server => Register(server, state)).ConfigureAwait(false);

static void Register(RpcServer server, BrowserState state)
{
    server.Register("browser.launch", (p, ct) => state.LaunchAsync(p));
    server.Register("browser.info", _ => state.Info());
    server.Register("browser.close", async (p, ct) =>
    {
        await state.CloseAsync().ConfigureAwait(false);
        return new JsonObject { ["closed"] = true };
    });
    server.Register("page.open", (p, ct) => state.OpenAsync(p));
    server.Register("page.goto", async (p, ct) =>
    {
        var page = state.Page(p);
        var response = await page.GotoAsync(Params.Str(p, "url"), new PageGotoOptions { WaitUntil = WaitUntil(p), Timeout = Timeout(p) }).ConfigureAwait(false);
        return new JsonObject { ["status"] = response?.Status, ["url"] = page.Url };
    });
    server.Register("page.fill", async (p, ct) =>
    {
        await state.Page(p).FillAsync(Params.Str(p, "selector"), Params.Str(p, "value"), new PageFillOptions { Timeout = Timeout(p) }).ConfigureAwait(false);
        return new JsonObject { ["ok"] = true };
    });
    server.Register("page.click", async (p, ct) =>
    {
        await state.Page(p).ClickAsync(Params.Str(p, "selector"), new PageClickOptions { Timeout = Timeout(p) }).ConfigureAwait(false);
        return new JsonObject { ["ok"] = true };
    });
    server.Register("page.press", async (p, ct) =>
    {
        await state.Page(p).PressAsync(Params.Str(p, "selector"), Params.Str(p, "key"), new PagePressOptions { Timeout = Timeout(p) }).ConfigureAwait(false);
        return new JsonObject { ["ok"] = true };
    });
    server.Register("page.wait_for", async (p, ct) =>
    {
        var stateName = Params.OptStr(p, "state") ?? "visible";
        var options = new PageWaitForSelectorOptions
        {
            Timeout = Timeout(p),
            State = stateName switch
            {
                "attached" => WaitForSelectorState.Attached,
                "detached" => WaitForSelectorState.Detached,
                "hidden" => WaitForSelectorState.Hidden,
                _ => WaitForSelectorState.Visible,
            },
        };
        try
        {
            await state.Page(p).WaitForSelectorAsync(Params.Str(p, "selector"), options).ConfigureAwait(false);
            return new JsonObject { ["found"] = true };
        }
        catch (TimeoutException)
        {
            return new JsonObject { ["found"] = false };
        }
    });
    server.Register("page.wait_url", async (p, ct) =>
    {
        var page = state.Page(p);
        var contains = Params.Str(p, "contains");
        try
        {
            await page.WaitForURLAsync(u => u.Contains(contains, StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = Timeout(p) }).ConfigureAwait(false);
            return new JsonObject { ["matched"] = true, ["url"] = page.Url };
        }
        catch (TimeoutException)
        {
            return new JsonObject { ["matched"] = false, ["url"] = page.Url };
        }
    });
    server.Register("page.read", async (p, ct) =>
    {
        var page = state.Page(p);
        var selector = Params.Str(p, "selector");
        var read = Params.OptStr(p, "read") ?? "text";
        var locator = page.Locator(selector);
        var count = await locator.CountAsync().ConfigureAwait(false);
        JsonNode? value = read switch
        {
            "count" => count,
            "exists" => count > 0,
            _ when count == 0 => null,
            "value" => await locator.First.InputValueAsync().ConfigureAwait(false),
            "text" => (await locator.First.InnerTextAsync().ConfigureAwait(false)).Trim(),
            _ when read.StartsWith("attr:", StringComparison.Ordinal) => await locator.First.GetAttributeAsync(read[5..]).ConfigureAwait(false),
            _ => throw new RpcException($"知らない読み方です：{read}"),
        };
        return new JsonObject { ["count"] = count, ["value"] = value };
    });
    server.Register("page.eval", async (p, ct) =>
    {
        var result = await state.Page(p).EvaluateAsync<JsonElement?>(Params.Str(p, "script")).ConfigureAwait(false);
        return new JsonObject { ["result"] = result is { } element ? JsonNode.Parse(element.GetRawText()) : null };
    });
    server.Register("page.screenshot", async (p, ct) =>
    {
        var page = state.Page(p);
        var bytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = Params.OptBool(p, "full_page", false), Type = ScreenshotType.Png }).ConfigureAwait(false);
        return new JsonObject { ["png_b64"] = Convert.ToBase64String(bytes), ["url"] = page.Url, ["bytes"] = bytes.Length };
    });
    server.Register("page.console", p => state.Console(Params.Str(p, "page"), Params.OptLong(p, "since", 0)));
    server.Register("page.url", p => new JsonObject { ["url"] = state.Page(p).Url });
    server.Register("page.layout", async (p, ct) =>
    {
        // DOM の座標によるレイアウトの検査（設計書 第2.2節）：画面外への飛び出し、親からのはみ出し、重なり
        var result = await state.Page(p).EvaluateAsync<JsonElement>(LayoutScript).ConfigureAwait(false);
        return JsonNode.Parse(result.GetRawText());
    });
    server.Register("page.clock", async (p, ct) =>
    {
        var page = state.Page(p);
        var action = Params.Str(p, "action");
        switch (action)
        {
            case "install":
                await page.Clock.InstallAsync().ConfigureAwait(false);
                break;
            case "fast_forward":
                await page.Clock.FastForwardAsync(Params.OptLong(p, "ms", 1000)).ConfigureAwait(false);
                break;
            case "pause_at":
                await page.Clock.PauseAtAsync(DateTimeOffset.FromUnixTimeMilliseconds(Params.OptLong(p, "epoch_ms", 0)).UtcDateTime).ConfigureAwait(false);
                break;
            case "resume":
                await page.Clock.ResumeAsync().ConfigureAwait(false);
                break;
            default:
                throw new RpcException($"知らない時計の操作です：{action}");
        }

        return new JsonObject { ["ok"] = true };
    });
    server.Register("page.close", async (p, ct) =>
    {
        await state.ClosePageAsync(Params.Str(p, "page")).ConfigureAwait(false);
        return new JsonObject { ["closed"] = true };
    });
    server.Register("pages.close_all", async (p, ct) =>
    {
        // リセットのとき：開いているページ（とその接続）をすべて閉じる。ブラウザは起動したまま
        var closed = await state.CloseAllPagesAsync().ConfigureAwait(false);
        return new JsonObject { ["closed"] = closed };
    });
}

static WaitUntilState WaitUntil(JsonObject p) => (Params.OptStr(p, "wait_until") ?? "load") switch
{
    "domcontentloaded" => WaitUntilState.DOMContentLoaded,
    "networkidle" => WaitUntilState.NetworkIdle,
    "commit" => WaitUntilState.Commit,
    _ => WaitUntilState.Load,
};

static float Timeout(JsonObject p) => Params.OptLong(p, "timeout_ms", 15000);

/// <summary>ブラウザとページの状態。</summary>
internal sealed partial class BrowserState
{
    private readonly ConcurrentDictionary<string, (IBrowserContext Context, IPage Page)> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<JsonObject>> _console = new(StringComparer.Ordinal);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string _userAgent = string.Empty;
    private JsonObject _launchOptions = [];
    private long _consoleSeq;

    public async Task<JsonNode?> LaunchAsync(JsonObject p)
    {
        if (_browser != null)
        {
            return Info();
        }

        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // 自動操作の目印（navigator.webdriver）を出さない（制約5）
            Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox"],
        }).ConfigureAwait(false);
        var major = _browser.Version.Split('.')[0];
        _userAgent = Params.OptStr(p, "user_agent")
            ?? $"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
        _launchOptions = (JsonObject)p.DeepClone();
        return Info();
    }

    public JsonObject Info() => new()
    {
        ["launched"] = _browser != null,
        ["version"] = _browser?.Version,
        ["user_agent"] = _userAgent,
        ["pages"] = new JsonArray(_pages.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
    };

    public async Task<JsonNode?> OpenAsync(JsonObject p)
    {
        var browser = _browser ?? throw new RpcException("browser.launch を先に呼んでください");
        var name = Params.Str(p, "page");
        if (_pages.TryRemove(name, out var old))
        {
            await old.Context.CloseAsync().ConfigureAwait(false);
        }

        var major = browser.Version.Split('.')[0];
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = _userAgent,
            Locale = Params.OptStr(_launchOptions, "locale") ?? "ja-JP",
            TimezoneId = Params.OptStr(_launchOptions, "timezone") ?? "Asia/Tokyo",
            ViewportSize = new ViewportSize { Width = (int)Params.OptLong(p, "width", 1280), Height = (int)Params.OptLong(p, "height", 800) },
            ExtraHTTPHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // ヘッドレスの印が付かないよう、通常の Chrome と同じ値にする
                ["sec-ch-ua"] = $"\"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\", \"Not?A_Brand\";v=\"99\"",
            },
        }).ConfigureAwait(false);
        await context.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', { get: () => undefined });").ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var log = _console.GetOrAdd(name, _ => []);
        page.Console += (_, message) => Add(log, "console", message.Type, message.Text, message.Location);
        page.PageError += (_, error) => Add(log, "pageerror", "error", error, null);
        page.Dialog += async (_, dialog) =>
        {
            Add(log, "dialog", dialog.Type, dialog.Message, null);
            await dialog.AcceptAsync().ConfigureAwait(false);
        };
        page.RequestFailed += (_, request) => Add(log, "requestfailed", "error", request.Url + " " + request.Failure, null);
        _pages[name] = (context, page);
        if (Params.OptStr(p, "url") is { } url)
        {
            await page.GotoAsync(url, new PageGotoOptions { Timeout = Params.OptLong(p, "timeout_ms", 20000) }).ConfigureAwait(false);
        }

        return new JsonObject { ["page"] = name, ["url"] = page.Url };
    }

    private void Add(List<JsonObject> log, string kind, string level, string text, string? location)
    {
        lock (log)
        {
            log.Add(new JsonObject
            {
                ["seq"] = Interlocked.Increment(ref _consoleSeq),
                ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["kind"] = kind,
                ["level"] = level,
                ["text"] = text,
                ["location"] = location,
            });
        }
    }

    public IPage Page(JsonObject p)
    {
        var name = Params.Str(p, "page");
        return _pages.TryGetValue(name, out var entry) ? entry.Page : throw new RpcException($"知らないページです：{name}（page.open を先に呼んでください）");
    }

    public JsonObject Console(string name, long since)
    {
        var list = new JsonArray();
        if (_console.TryGetValue(name, out var log))
        {
            lock (log)
            {
                foreach (var item in log.Where(i => i["seq"]?.GetValue<long>() > since))
                {
                    list.Add(item.DeepClone());
                }
            }
        }

        return new JsonObject { ["messages"] = list };
    }

    public async Task ClosePageAsync(string name)
    {
        if (_pages.TryRemove(name, out var entry))
        {
            await entry.Context.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<int> CloseAllPagesAsync()
    {
        var names = _pages.Keys.ToList();
        foreach (var name in names)
        {
            await ClosePageAsync(name).ConfigureAwait(false);
        }

        return names.Count;
    }

    /// <summary>ブラウザを閉じる。</summary>
    public async Task CloseAsync()
    {
        foreach (var name in _pages.Keys.ToList())
        {
            await ClosePageAsync(name).ConfigureAwait(false);
        }

        if (_browser != null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }
}

/// <summary>レイアウトの検査のスクリプト。</summary>
internal static partial class Program
{
    internal const string LayoutScript = """
        () => {
          const vw = document.documentElement.clientWidth, vh = document.documentElement.clientHeight;
          const issues = [];
          const els = Array.from(document.querySelectorAll('body *')).filter(e => {
            const s = getComputedStyle(e);
            return s.display !== 'none' && s.visibility !== 'hidden' && e.getClientRects().length > 0;
          });
          const name = e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '');
          for (const e of els) {
            const r = e.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            if (r.right > vw + 1 || r.left < -1) issues.push({ kind: 'offscreen_x', el: name(e), left: r.left, right: r.right, viewport: vw });
            const p = e.parentElement;
            if (p && p !== document.body) {
              const ps = getComputedStyle(p);
              if (ps.overflow === 'visible' && ps.overflowX === 'visible') {
                const pr = p.getBoundingClientRect();
                if (r.right > pr.right + 2 && pr.width > 0) issues.push({ kind: 'overflow_parent', el: name(e), parent: name(p), over: r.right - pr.right });
              }
            }
          }
          const texts = els.filter(e => e.children.length === 0 && (e.innerText || '').trim().length > 0).slice(0, 400);
          for (let i = 0; i < texts.length; i++) {
            const a = texts[i].getBoundingClientRect();
            for (let j = i + 1; j < texts.length; j++) {
              if (texts[i].contains(texts[j]) || texts[j].contains(texts[i])) continue;
              const b = texts[j].getBoundingClientRect();
              const w = Math.min(a.right, b.right) - Math.max(a.left, b.left);
              const h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
              if (w > 4 && h > 4) issues.push({ kind: 'text_overlap', a: name(texts[i]), b: name(texts[j]), area: w * h });
            }
          }
          return { viewport: { width: vw, height: vh }, elements: els.length, issues: issues.slice(0, 200), issue_count: issues.length };
        }
        """;
}
