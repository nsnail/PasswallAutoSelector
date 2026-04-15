using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

var config = LoadConfig(args);
ValidateConfig(config);

Log("Passwall 自动选点程序已启动。按 Ctrl+C 可退出。");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

try
{
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = config.Headless
    });

    while (!cts.Token.IsCancellationRequested)
    {
        var roundStartedAt = DateTimeOffset.Now;
        try
        {
            await ExecuteOneRoundAsync(browser, config, cts.Token);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            Log($"本轮执行失败: {ex.Message}");
        }

        var elapsed = DateTimeOffset.Now - roundStartedAt;
        var wait = TimeSpan.FromSeconds(config.IntervalSeconds) - elapsed;
        if (wait > TimeSpan.Zero)
        {
            Log($"等待 {wait.TotalSeconds:F0} 秒后进入下一轮。");
            await Task.Delay(wait, cts.Token);
        }
    }
}
catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("未检测到 Playwright 浏览器内核，请先安装:");
    Console.WriteLine("  pwsh .\\bin\\Debug\\net8.0\\playwright.ps1 install chromium");
}
catch (OperationCanceledException)
{
}

Log("程序已退出。");

static async Task ExecuteOneRoundAsync(IBrowser browser, AppConfig config, CancellationToken ct)
{
    Log("开始新一轮检测...");

    await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
    {
        IgnoreHTTPSErrors = true
    });

    var page = await context.NewPageAsync();
    page.SetDefaultTimeout(config.ActionTimeoutMs);
    page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();

    await EnsureLoggedInAndOpenNodePageAsync(page, config);
    await TriggerTestsAsync(page, config, ct);

    var best = await WaitForBestNodeAsync(page, config, ct);
    if (best is null)
    {
        Log("没有找到三项全通的节点，跳过切换。");
        return;
    }

    Log($"最佳节点: {best.Name} | Ping={FormatMs(best.PingMs)} TCP={FormatMs(best.TcpMs)} URL={FormatMs(best.UrlMs)} Score={best.Score:F2}");
    await UseNodeAsync(page, best, config, ct);
    Log($"已触发使用节点: {best.Name}");
}

static async Task EnsureLoggedInAndOpenNodePageAsync(IPage page, AppConfig config)
{
    await page.GotoAsync(config.NodeListUrl, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded
    });

    if (!await IsLoginPageAsync(page))
    {
        return;
    }

    Log("检测到登录页，执行登录...");
    await FillIfExistsAsync(page, "input[name='luci_username'], #luci_username, input[name='username']", config.Username);
    await FillIfExistsAsync(page, "input[type='password'], input[name='luci_password'], #luci_password", config.Password);

    var submit = page.Locator("button[type='submit'], input[type='submit'], button:has-text('登录'), button:has-text('Login')").First;
    if (await submit.CountAsync() > 0)
    {
        await submit.ClickAsync();
    }
    else
    {
        await page.Keyboard.PressAsync("Enter");
    }

    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    await page.GotoAsync(config.NodeListUrl, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded
    });

    if (await IsLoginPageAsync(page))
    {
        throw new InvalidOperationException("登录失败，请检查用户名/密码是否正确。");
    }
}

static async Task<bool> IsLoginPageAsync(IPage page)
{
    var hasPassword = await page.Locator("input[type='password']").CountAsync() > 0;
    var hasUser = await page.Locator("input[name='luci_username'], #luci_username, input[name='username'], input[type='text']").CountAsync() > 0;
    return hasPassword && hasUser;
}

static async Task FillIfExistsAsync(IPage page, string selector, string value)
{
    var locator = page.Locator(selector).First;
    if (await locator.CountAsync() > 0)
    {
        await locator.FillAsync(value);
    }
}

static async Task TriggerTestsAsync(IPage page, AppConfig config, CancellationToken ct)
{
    var rowTriggeredCount = 0;
    if (config.UseRowLevelTest)
    {
        rowTriggeredCount = await TriggerRowLevelTestsAsync(page, config, ct);
    }

    if (rowTriggeredCount == 0)
    {
        var clickedPing = await ClickByKeywordsAsync(page, config.PingTriggerKeywords);
        var clickedTcp = await ClickByKeywordsAsync(page, config.TcpTriggerKeywords);
        var clickedUrl = await ClickByKeywordsAsync(page, config.UrlTriggerKeywords);

        if (clickedPing || clickedTcp || clickedUrl)
        {
            Log("已触发批量测速按钮，等待结果更新...");
        }
        else
        {
            Log("未找到批量测速按钮，也未触发逐行测速，将直接读取当前页面已有测速结果。");
        }
    }

    await Task.Delay(TimeSpan.FromSeconds(config.InitialTestWaitSeconds), ct);
}

static async Task<int> TriggerRowLevelTestsAsync(IPage page, AppConfig config, CancellationToken ct)
{
    var rows = await GetNodeRowsAsync(page, config);
    if (rows.Count == 0)
    {
        Log("未识别到可测速节点行。");
        return 0;
    }

    Log($"开始逐行测速，节点数: {rows.Count}");
    var totalTriggered = 0;

    foreach (var row in rows)
    {
        ct.ThrowIfCancellationRequested();

        var currentRowTriggered = 0;
        if (await ClickRowMetricAsync(page, row.RowSelector, config.RowPingTriggerKeywords, "ping"))
        {
            currentRowTriggered++;
            totalTriggered++;
            await Task.Delay(config.RowTestInterClickDelayMs, ct);
        }

        if (await ClickRowMetricAsync(page, row.RowSelector, config.RowTcpTriggerKeywords, "tcp"))
        {
            currentRowTriggered++;
            totalTriggered++;
            await Task.Delay(config.RowTestInterClickDelayMs, ct);
        }

        if (await ClickRowMetricAsync(page, row.RowSelector, config.RowUrlTriggerKeywords, "url"))
        {
            currentRowTriggered++;
            totalTriggered++;
            await Task.Delay(config.RowTestInterClickDelayMs, ct);
        }

        if (currentRowTriggered > 0)
        {
            Log($"已触发节点测速: {row.Name} ({currentRowTriggered}/3)");
        }

        if (config.RowTestInterNodeDelayMs > 0)
        {
            await Task.Delay(config.RowTestInterNodeDelayMs, ct);
        }
    }

    Log($"逐行测速触发完成，共触发 {totalTriggered} 次测试动作。");
    return totalTriggered;
}

static async Task<List<NodeRow>> GetNodeRowsAsync(IPage page, AppConfig config)
{
    var json = await page.EvaluateAsync<JsonElement>(
        @"(input) => {
            const useKeywords = (input?.useKeywords ?? ['使用', 'use']).map(x => (x || '').toString().toLowerCase());
            const toText = (value) => (value || '').toString().replace(/\s+/g, ' ').trim();
            const hasSetNode = (el) => ((el.getAttribute('onclick') || '') + ' ' + (el.getAttribute('href') || '')).toLowerCase().includes('set_node(');
            const isSwitchButton = (el) => {
                if (hasSetNode(el)) return true;
                const text = toText(el.innerText || el.value).toLowerCase();
                return useKeywords.some(kw => kw && text.includes(kw));
            };

            const cssPath = (el) => {
                if (!el) return '';
                const parts = [];
                let current = el;
                while (current && current.nodeType === 1 && current.tagName.toLowerCase() !== 'html') {
                    const tag = current.tagName.toLowerCase();
                    if (current.id) {
                        const escaped = current.id.replace(/\\/g, '\\\\').replace(/'/g, '\\\'');
                        parts.unshift(`${tag}[id='${escaped}']`);
                        break;
                    }
                    let index = 1;
                    let sibling = current;
                    while ((sibling = sibling.previousElementSibling) != null) {
                        if (sibling.tagName === current.tagName) index++;
                    }
                    parts.unshift(`${tag}:nth-of-type(${index})`);
                    current = current.parentElement;
                }
                return parts.length ? parts.join(' > ') : '';
            };

            const rows = [];
            for (const table of Array.from(document.querySelectorAll('table'))) {
                for (const row of Array.from(table.querySelectorAll('tr'))) {
                    const cells = Array.from(row.querySelectorAll('td'));
                    if (cells.length === 0) continue;

                    const controls = Array.from(row.querySelectorAll('button,a,input[type=button],input[type=submit]'));
                    const hasSwitch = controls.some(isSwitchButton);
                    if (!hasSwitch) continue;

                    const cellTexts = cells.map(c => toText(c.innerText)).filter(Boolean);
                    const firstText = cellTexts.find(t => /[a-zA-Z\u4e00-\u9fa5]/.test(t) && t.length >= 4) || cellTexts[0] || '';
                    rows.push({
                        id: (row.id || '').replace(/^cbi-passwall-/, ''),
                        name: firstText || `Node-${rows.length + 1}`,
                        rowSelector: cssPath(row)
                    });
                }
            }
            return rows;
        }",
        new { useKeywords = config.UseButtonKeywords });

    var rows = new List<NodeRow>();
    if (json.ValueKind != JsonValueKind.Array)
    {
        return rows;
    }

    foreach (var item in json.EnumerateArray())
    {
        var rowSelector = GetString(item, "rowSelector", string.Empty);
        if (string.IsNullOrWhiteSpace(rowSelector))
        {
            continue;
        }

        rows.Add(new NodeRow
        {
            Id = GetString(item, "id", string.Empty),
            Name = GetString(item, "name", "未知节点"),
            RowSelector = rowSelector
        });
    }

    return rows;
}

static async Task<bool> ClickRowMetricAsync(IPage page, string rowSelector, IReadOnlyList<string> keywords, string metricType)
{
    if (keywords.Count == 0 || string.IsNullOrWhiteSpace(rowSelector))
    {
        return false;
    }

    return await page.EvaluateAsync<bool>(
        @"(input) => {
            const toText = (value) => (value || '').toString().replace(/\s+/g, ' ').trim().toLowerCase();
            const row = document.querySelector(input.rowSelector);
            if (!row) return false;

            const keywords = (input.keywords ?? []).map(k => toText(k)).filter(Boolean);
            const metricType = (input.metricType || '').toLowerCase();
            const controls = Array.from(row.querySelectorAll('button,a,input[type=button],input[type=submit]'));
            const actionWords = ['使用', 'use', 'edit', 'delete', 'remove', '复制', 'copy', '启用', '禁用', '详情', 'detail'];

            const describe = (el) => {
                const text = toText(el.innerText || el.value);
                const attrs = toText(`${el.id || ''} ${el.className || ''} ${el.name || ''} ${el.getAttribute('title') || ''} ${el.getAttribute('data-title') || ''} ${el.getAttribute('onclick') || ''} ${el.getAttribute('href') || ''}`);
                return { el, text, attrs };
            };

            const isAction = (item) => actionWords.some(w => item.text.includes(w));
            const described = controls.map(describe).filter(item => item.text || item.attrs);
            const testCandidates = described.filter(item => !isAction(item) && (item.text.includes('test') || item.attrs.includes('ping') || item.attrs.includes('tcp') || item.attrs.includes('url')));

            const clickIfValid = (item) => {
                if (!item || !item.el || item.el.disabled) return false;
                item.el.click();
                return true;
            };

            for (const kw of keywords) {
                for (const item of described) {
                    if (!item.text) continue;
                    if (isAction(item)) continue;
                    if (!item.text.includes(kw)) continue;
                    if (metricType === 'ping' && kw === 'ping' && item.text.includes('tcp')) continue;
                    if (clickIfValid(item)) return true;
                }
            }

            const attrMatched = described.find(item => {
                if (isAction(item)) return false;
                if (!item.attrs) return false;
                if (metricType === 'ping') return item.attrs.includes('ping') && !item.attrs.includes('tcp') && !item.attrs.includes('url');
                if (metricType === 'tcp') return item.attrs.includes('tcp');
                if (metricType === 'url') return item.attrs.includes('url');
                return false;
            });
            if (clickIfValid(attrMatched)) return true;

            if (testCandidates.length > 0) {
                const index = metricType === 'ping' ? 0 : metricType === 'tcp' ? 1 : 2;
                if (index < testCandidates.length && clickIfValid(testCandidates[index])) return true;
            }

            return false;
        }",
        new
        {
            rowSelector,
            keywords,
            metricType
        });
}

static async Task<bool> ClickByKeywordsAsync(IPage page, IReadOnlyList<string> keywords)
{
    foreach (var keyword in keywords)
    {
        var clicked = await page.EvaluateAsync<bool>(
            @"(kw) => {
                const target = kw.toLowerCase();
                const candidates = Array.from(document.querySelectorAll('button,a,input[type=button],input[type=submit]'));
                for (const el of candidates) {
                    const text = ((el.innerText || el.value || '') + '').toLowerCase().trim();
                    if (!text) continue;
                    if (!text.includes(target)) continue;
                    if (el.disabled) continue;
                    el.click();
                    return true;
                }
                return false;
            }",
            keyword);

        if (clicked)
        {
            Log($"已点击按钮关键词: {keyword}");
            return true;
        }
    }

    return false;
}

static async Task<NodeResult?> WaitForBestNodeAsync(IPage page, AppConfig config, CancellationToken ct)
{
    var deadline = DateTimeOffset.Now.AddSeconds(config.TestResultTimeoutSeconds);
    NodeResult? previousBest = null;
    var stableRounds = 0;

    while (DateTimeOffset.Now < deadline)
    {
        var all = await CollectNodeResultsAsync(page, config);
        var passed = all.Where(node => node.AllPassed).OrderBy(node => node.Score).ToList();

        if (passed.Count > 0)
        {
            var best = passed[0];
            Log($"候选最优: {best.Name} | Ping={FormatMs(best.PingMs)} TCP={FormatMs(best.TcpMs)} URL={FormatMs(best.UrlMs)}");

            if (previousBest is not null &&
                string.Equals(previousBest.Name, best.Name, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(previousBest.Score - best.Score) < 0.1)
            {
                stableRounds++;
            }
            else
            {
                stableRounds = 1;
            }

            previousBest = best;
            if (stableRounds >= config.StableRoundsRequired)
            {
                return best;
            }
        }
        else
        {
            if (all.Count > 0)
            {
                var sample = string.Join(" | ", all.Take(3).Select(x => $"{x.Name}[P:{TrimForLog(x.PingRaw)} T:{TrimForLog(x.TcpRaw)} U:{TrimForLog(x.UrlRaw)}]"));
                Log($"当前还没有三项全通节点，继续等待... 样例: {sample}");
            }
            else
            {
                Log("当前未解析到任何节点行，继续等待...");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), ct);
    }

    return previousBest;
}

static async Task<List<NodeResult>> CollectNodeResultsAsync(IPage page, AppConfig config)
{
    var json = await page.EvaluateAsync<JsonElement>(
        @"(input) => {
            const failWords = ['超时', '失败', 'error', 'fail', '不可用', 'unavailable', 'disconnect', 'closed', '阻断', 'x'];
            const passWords = ['成功', 'ok', 'pass', '可用', 'alive', 'connected', 'connect', '200'];
            const pendingWords = ['检测中', 'testing', 'pending', '...'];
            const useKeywords = (input?.useKeywords ?? ['使用', 'use']).map(x => (x || '').toString().toLowerCase());

            const toText = (value) => (value || '').toString().replace(/\s+/g, ' ').trim();
            const parseMetric = (rawInput) => {
                const raw = toText(rawInput);
                if (!raw) return { ok: false, ms: null, raw: '' };
                if (raw.includes('<') && raw.includes('>')) return { ok: false, ms: null, raw };

                const lower = raw.toLowerCase();
                if (pendingWords.some(w => lower.includes(w))) return { ok: false, ms: null, raw };
                if (failWords.some(w => lower.includes(w))) return { ok: false, ms: null, raw };

                const msMatch = lower.match(/([0-9]+(?:\.[0-9]+)?)\s*ms/);
                const ms = msMatch ? Number(msMatch[1]) : null;
                const slashPass = /([1-9]\d*)\s*\/\s*([1-9]\d*)/.test(lower);
                const wordPass = passWords.some(w => lower.includes(w));
                const ok = ms !== null || slashPass || wordPass;
                return { ok, ms, raw };
            };

            const cssPath = (el) => {
                if (!el) return '';
                const parts = [];
                let current = el;
                while (current && current.nodeType === 1 && current.tagName.toLowerCase() !== 'html') {
                    const tag = current.tagName.toLowerCase();
                    if (current.id) {
                        const escaped = current.id.replace(/\\/g, '\\\\').replace(/'/g, '\\\'');
                        parts.unshift(`${tag}[id='${escaped}']`);
                        break;
                    }
                    let index = 1;
                    let sibling = current;
                    while ((sibling = sibling.previousElementSibling) != null) {
                        if (sibling.tagName === current.tagName) index++;
                    }
                    parts.unshift(`${tag}:nth-of-type(${index})`);
                    current = current.parentElement;
                }
                return parts.length ? parts.join(' > ') : '';
            };

            const hasSetNode = (el) => ((el.getAttribute('onclick') || '') + ' ' + (el.getAttribute('href') || '')).toLowerCase().includes('set_node(');
            const isUseButton = (el) => {
                if (hasSetNode(el)) return true;
                const text = toText(el.innerText || el.value).toLowerCase();
                return useKeywords.some(kw => kw && text.includes(kw));
            };

            const findColumnIndex = (headers, rule) => headers.findIndex(h => rule(h));
            const isFinalLikeText = (text) => {
                const lower = toText(text).toLowerCase();
                if (!lower) return false;
                if (pendingWords.some(w => lower.includes(w))) return false;
                return /([0-9]+(?:\.[0-9]+)?\s*ms)|([1-9]\d*\s*\/\s*[1-9]\d*)/.test(lower) ||
                    failWords.some(w => lower.includes(w)) ||
                    passWords.some(w => lower.includes(w));
            };

            const metricRawFromMarkedElements = (row, metricType) => {
                const selectors = metricType === 'ping'
                    ? ['[id*=\'ping\']', '[class*=\'ping\']', '[data-title*=\'ping\']', '[title*=\'ping\']']
                    : metricType === 'tcp'
                        ? ['[id*=\'tcp\']', '[class*=\'tcp\']', '[data-title*=\'tcp\']', '[title*=\'tcp\']']
                        : ['[id*=\'url\']', '[class*=\'url\']', '[data-title*=\'url\']', '[title*=\'url\']'];

                const candidates = [];
                selectors.forEach(selector => {
                    row.querySelectorAll(selector).forEach(el => candidates.push(el));
                });

                for (const el of candidates) {
                    const attrs = `${el.id || ''} ${el.className || ''} ${el.getAttribute('title') || ''} ${el.getAttribute('data-title') || ''}`.toLowerCase();
                    if (metricType === 'ping' && attrs.includes('tcp')) continue;
                    const raw = toText(el.innerText || el.textContent || el.value);
                    if (!raw) continue;
                    if (isFinalLikeText(raw)) return raw;
                }

                for (const el of candidates) {
                    const raw = toText(el.innerText || el.textContent || el.value);
                    if (raw && !(raw.includes('<') && raw.includes('>'))) return raw;
                }

                return '';
            };
            const nodes = [];
            const tables = Array.from(document.querySelectorAll('table'));

            tables.forEach((table) => {
                const rows = Array.from(table.querySelectorAll('tr'));
                const headerCells = Array.from(table.querySelectorAll('th')).map(th => toText(th.innerText).toLowerCase());
                const pingIndex = findColumnIndex(headerCells, h => h.includes('ping') && !h.includes('tcp'));
                const tcpIndex = findColumnIndex(headerCells, h => h.includes('tcp'));
                const urlIndex = findColumnIndex(headerCells, h => h.includes('url'));
                let nameIndex = findColumnIndex(headerCells, h => h.includes('节点') || h.includes('node') || h.includes('名称') || h.includes('备注') || h.includes('remark'));
                if (nameIndex < 0) nameIndex = 0;

                rows.forEach((row, rowIndex) => {
                    const cells = Array.from(row.querySelectorAll('td'));
                    if (!cells.length) return;

                    const buttons = Array.from(row.querySelectorAll('button,a,input[type=button],input[type=submit]'));
                    const switchButton =
                        buttons.find(el => {
                            const attrs = ((el.getAttribute('onclick') || '') + ' ' + (el.getAttribute('href') || '')).toLowerCase();
                            const normalized = attrs.replace(/'/g, '').replace(/\x22/g, '');
                            return hasSetNode(el) && normalized.includes('set_node(tcp');
                        }) ||
                        buttons.find(hasSetNode) ||
                        buttons.find(isUseButton);
                    if (!switchButton) return;

                    const texts = cells.map(cell => toText(cell.innerText));
                    const name = texts[nameIndex] || `Node-${rowIndex + 1}`;

                    let pingRaw = pingIndex >= 0 ? texts[pingIndex] : '';
                    let tcpRaw = tcpIndex >= 0 ? texts[tcpIndex] : '';
                    let urlRaw = urlIndex >= 0 ? texts[urlIndex] : '';

                    if (!pingRaw || !isFinalLikeText(pingRaw)) pingRaw = metricRawFromMarkedElements(row, 'ping') || pingRaw;
                    if (!tcpRaw || !isFinalLikeText(tcpRaw)) tcpRaw = metricRawFromMarkedElements(row, 'tcp') || tcpRaw;
                    if (!urlRaw || !isFinalLikeText(urlRaw)) urlRaw = metricRawFromMarkedElements(row, 'url') || urlRaw;

                    const ping = parseMetric(pingRaw);
                    const tcp = parseMetric(tcpRaw);
                    const url = parseMetric(urlRaw);

                    nodes.push({
                        id: (row.id || '').replace(/^cbi-passwall-/, ''),
                        name,
                        pingOk: ping.ok,
                        tcpOk: tcp.ok,
                        urlOk: url.ok,
                        pingMs: ping.ms,
                        tcpMs: tcp.ms,
                        urlMs: url.ms,
                        pingRaw: ping.raw,
                        tcpRaw: tcp.raw,
                        urlRaw: url.raw,
                        useSelector: cssPath(switchButton)
                    });
                });
            });

            return nodes;
        }",
        new { useKeywords = config.UseButtonKeywords });

    var list = new List<NodeResult>();
    if (json.ValueKind != JsonValueKind.Array)
    {
        return list;
    }

    foreach (var item in json.EnumerateArray())
    {
        var result = new NodeResult
        {
            Id = GetString(item, "id", string.Empty),
            Name = GetString(item, "name", "未知节点"),
            PingOk = GetBoolean(item, "pingOk"),
            TcpOk = GetBoolean(item, "tcpOk"),
            UrlOk = GetBoolean(item, "urlOk"),
            PingMs = GetNullableDouble(item, "pingMs"),
            TcpMs = GetNullableDouble(item, "tcpMs"),
            UrlMs = GetNullableDouble(item, "urlMs"),
            PingRaw = GetString(item, "pingRaw", string.Empty),
            TcpRaw = GetString(item, "tcpRaw", string.Empty),
            UrlRaw = GetString(item, "urlRaw", string.Empty),
            UseSelector = GetString(item, "useSelector", string.Empty)
        };

        if (string.IsNullOrWhiteSpace(result.UseSelector))
        {
            continue;
        }

        result.Score = (result.PingMs ?? 10_000d) + (result.TcpMs ?? 10_000d) + (result.UrlMs ?? 10_000d);
        list.Add(result);
    }

    return list;
}

static async Task UseNodeAsync(IPage page, NodeResult node, AppConfig config, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(node.Id))
    {
        var byApi = await TrySetNodeByApiAndVerifyAsync(page, node, config, ct);
        if (byApi)
        {
            return;
        }
    }

    var clicked = await page.EvaluateAsync<bool>(
        @"(input) => {
            const toText = (v) => (v || '').toString().replace(/\s+/g, ' ').trim().toLowerCase();
            const target = document.querySelector(input.selector);
            if (!target) return false;
            const row = target.closest('tr') || target.parentElement || target;

            const controls = Array.from(row.querySelectorAll('button,a,input[type=button],input[type=submit]'));
            const attrs = (el) => ((el.getAttribute('onclick') || '') + ' ' + (el.getAttribute('href') || '')).toLowerCase();

            const targetTcp = controls.find(el => {
                const normalized = attrs(el).replace(/'/g, '').replace(/\x22/g, '');
                return normalized.includes('set_node(tcp');
            });
            if (targetTcp && !targetTcp.disabled) {
                targetTcp.click();
                return true;
            }

            const anySetNode = controls.find(el => attrs(el).includes('set_node('));
            if (anySetNode && !anySetNode.disabled) {
                anySetNode.click();
                return true;
            }

            const useKeywords = (input.useKeywords || []).map(k => toText(k)).filter(Boolean);
            for (const el of controls) {
                const txt = toText(el.innerText || el.value);
                if (!txt) continue;
                if (!useKeywords.some(k => txt.includes(k))) continue;
                if (el.disabled) continue;
                el.click();
                return true;
            }

            return false;
        }",
        new { selector = node.UseSelector, useKeywords = config.UseButtonKeywords });

    if (!clicked)
    {
        throw new InvalidOperationException($"未找到可用的切换按钮(set_node/use): {node.Name}");
    }

    Log($"已点击切换按钮(优先 set_node('tcp')): {node.Name}");

    if (!string.IsNullOrWhiteSpace(node.Id))
    {
        await VerifyTcpNodeAfterSwitchAsync(page, node.Id, config, ct);
    }

    await Task.Delay(1000, ct);

    if (!config.ClickSaveApplyAfterUse)
    {
        return;
    }

    var clickedSave = await ClickByKeywordsAsync(page, config.SaveApplyKeywords);
    if (clickedSave)
    {
        Log("已点击保存/应用按钮。");
    }
}

static async Task<bool> TrySetNodeByApiAndVerifyAsync(IPage page, NodeResult node, AppConfig config, CancellationToken ct)
{
    try
    {
        var uri = new Uri(config.NodeListUrl);
        var baseUrl = $"{uri.Scheme}://{uri.Authority}";
        var endpoint = $"{baseUrl}/cgi-bin/luci/admin/services/passwall/set_node?protocol=tcp&section={Uri.EscapeDataString(node.Id)}";

        var status = await page.EvaluateAsync<int>(
            @"async (url) => {
                const r = await fetch(url, {
                    method: 'GET',
                    credentials: 'same-origin',
                    redirect: 'follow'
                });
                return r.status;
            }",
            endpoint);

        if (status < 200 || status >= 400)
        {
            Log($"set_node 接口调用异常，HTTP {status}，回退到按钮点击。");
            return false;
        }

        Log($"已通过 set_node 接口切换 TCP 节点: {node.Name} ({node.Id})");
        await VerifyTcpNodeAfterSwitchAsync(page, node.Id, config, ct);
        return true;
    }
    catch (Exception ex)
    {
        Log($"set_node 接口调用失败，回退按钮方式: {ex.Message}");
        return false;
    }
}

static async Task VerifyTcpNodeAfterSwitchAsync(IPage page, string expectedNodeId, AppConfig config, CancellationToken ct)
{
    if (!config.VerifyTcpNodeAfterSwitch)
    {
        return;
    }

    var deadline = DateTimeOffset.Now.AddSeconds(config.VerifyTimeoutSeconds);
    while (DateTimeOffset.Now < deadline)
    {
        ct.ThrowIfCancellationRequested();

        var html = await GetSettingsHtmlAsync(page, config);
        var tcpNode = ParseTcpNodeIdFromSettingsHtml(html);
        if (!string.IsNullOrWhiteSpace(tcpNode) &&
            string.Equals(tcpNode, expectedNodeId, StringComparison.Ordinal))
        {
            Log($"切换校验通过，settings TCP 节点={tcpNode}");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(config.VerifyPollIntervalSeconds), ct);
    }

    throw new InvalidOperationException($"切换后校验失败，settings TCP 节点未变为目标节点: {expectedNodeId}");
}

static async Task<string> GetSettingsHtmlAsync(IPage page, AppConfig config)
{
    var uri = new Uri(config.NodeListUrl);
    var baseUrl = $"{uri.Scheme}://{uri.Authority}";
    var settingsUrl = $"{baseUrl}/cgi-bin/luci/admin/services/passwall/settings";
    var html = await page.EvaluateAsync<string>(
        @"async (url) => {
            const r = await fetch(url, {
                method: 'GET',
                credentials: 'same-origin',
                redirect: 'follow'
            });
            return await r.text();
        }",
        settingsUrl);

    return html ?? string.Empty;
}

static string ParseTcpNodeIdFromSettingsHtml(string html)
{
    if (string.IsNullOrWhiteSpace(html))
    {
        return string.Empty;
    }

    var marker = "name&#34;:&#34;cbid.passwall.";
    var lines = html.Split('\n');
    foreach (var rawLine in lines)
    {
        if (!rawLine.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            !rawLine.Contains(".tcp_node&#34;", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var match = Regex.Match(rawLine, "Select&#34;,&#34;([^&]+)&#34;,", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }
    }

    return string.Empty;
}

static AppConfig LoadConfig(string[] args)
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddEnvironmentVariables(prefix: "PASSWALL_")
        .AddCommandLine(args)
        .Build();

    return configuration.Get<AppConfig>() ?? new AppConfig();
}

static void ValidateConfig(AppConfig config)
{
    if (string.IsNullOrWhiteSpace(config.NodeListUrl))
    {
        throw new InvalidOperationException("配置项 NodeListUrl 不能为空。");
    }

    if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
    {
        throw new InvalidOperationException("请在 appsettings.json 中配置 Username / Password。");
    }
}

static string GetString(JsonElement element, string propertyName, string fallback)
{
    if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
    {
        return value.GetString() ?? fallback;
    }

    return fallback;
}

static bool GetBoolean(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var value))
    {
        return false;
    }

    if (value.ValueKind == JsonValueKind.True)
    {
        return true;
    }

    if (value.ValueKind == JsonValueKind.False)
    {
        return false;
    }

    return false;
}

static double? GetNullableDouble(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var value))
    {
        return null;
    }

    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
    {
        return number;
    }

    if (value.ValueKind == JsonValueKind.String &&
        double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
    {
        return parsed;
    }

    return null;
}

static string FormatMs(double? value) => value is null ? "N/A" : $"{value.Value:F1}ms";
static string TrimForLog(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "-";
    }

    var text = value.Trim();
    return text.Length <= 30 ? text : $"{text[..30]}...";
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

public sealed class AppConfig
{
    public string NodeListUrl { get; set; } = "http://10.4.147.182/cgi-bin/luci/admin/services/passwall/node_list";
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "your-password";
    public bool Headless { get; set; } = true;
    public int IntervalSeconds { get; set; } = 300;
    public int ActionTimeoutMs { get; set; } = 15000;
    public int InitialTestWaitSeconds { get; set; } = 3;
    public int TestResultTimeoutSeconds { get; set; } = 90;
    public int PollIntervalSeconds { get; set; } = 5;
    public int StableRoundsRequired { get; set; } = 2;
    public bool ClickSaveApplyAfterUse { get; set; } = true;
    public bool VerifyTcpNodeAfterSwitch { get; set; } = true;
    public int VerifyTimeoutSeconds { get; set; } = 12;
    public int VerifyPollIntervalSeconds { get; set; } = 1;
    public bool UseRowLevelTest { get; set; } = true;
    public int RowTestInterClickDelayMs { get; set; } = 700;
    public int RowTestInterNodeDelayMs { get; set; } = 300;

    public List<string> PingTriggerKeywords { get; set; } = new()
    {
        "一键Ping", "Ping测试", "Ping"
    };

    public List<string> TcpTriggerKeywords { get; set; } = new()
    {
        "一键TCPing", "TCPing", "TCP Ping"
    };

    public List<string> UrlTriggerKeywords { get; set; } = new()
    {
        "一键URLTest", "URLTest", "URL测试", "URL Test"
    };

    public List<string> SaveApplyKeywords { get; set; } = new()
    {
        "保存并应用", "保存&应用", "应用", "Save & Apply", "Apply"
    };

    public List<string> UseButtonKeywords { get; set; } = new()
    {
        "使用", "use"
    };

    public List<string> RowPingTriggerKeywords { get; set; } = new()
    {
        "Ping测试", "Ping", "延迟测试"
    };

    public List<string> RowTcpTriggerKeywords { get; set; } = new()
    {
        "TCPing测试", "TCPing", "TCP Ping", "TCP"
    };

    public List<string> RowUrlTriggerKeywords { get; set; } = new()
    {
        "URLTest", "URL测试", "URL Test", "URL"
    };
}

public sealed class NodeResult
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required bool PingOk { get; set; }
    public required bool TcpOk { get; set; }
    public required bool UrlOk { get; set; }
    public required double? PingMs { get; set; }
    public required double? TcpMs { get; set; }
    public required double? UrlMs { get; set; }
    public required string PingRaw { get; set; }
    public required string TcpRaw { get; set; }
    public required string UrlRaw { get; set; }
    public required string UseSelector { get; set; }
    public double Score { get; set; }
    public bool AllPassed => PingOk && TcpOk && UrlOk;
}

public sealed class NodeRow
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string RowSelector { get; set; }
}
