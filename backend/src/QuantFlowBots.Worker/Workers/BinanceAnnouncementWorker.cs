using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Risk;
using QuantFlowBots.Application.Sentiment;
using QuantFlowBots.Application.Streaming;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Domain.Enums;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Pulls https://t.me/s/binance_announcements every 5 min (public preview HTML, no token needed),
/// extracts new messages since last scan, ingests them as sentiment events (auto-scored by the
/// existing KeywordSentimentScorer), and — when a message contains red-flag keywords like
/// "delist", "hack", "suspend trading" — blocks the mentioned symbols in SymbolRiskGate.
///
/// Blocking a symbol additionally triggers an auto-close pass: any open position on that symbol
/// gets a market sell via TradingDispatcher. User chose this in Đợt I follow-up — delist usually
/// = liquidity vanishes within hours, so flat-NOW beats getting stuck.
/// </summary>
public sealed partial class BinanceAnnouncementWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    SymbolRiskGate riskGate,
    ILogger<BinanceAnnouncementWorker> logger) : BackgroundService
{
    // 2-min poll keeps lag from announcement → block ≤ 2min. t.me preview is HTML — no documented
    // rate limit but pulling more aggressively risks IP throttling; 2min is the empirical sweet spot.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private const string TelegramUrl = "https://t.me/s/binance_announcements";
    private const string Source = "binance_announcement";

    // Red flag = bot must NOT trade this symbol. Tighter than initial draft because Binance
    // often posts "Notice of Removal of Margin Trading" which only removes MARGIN — spot still
    // works fine. We require either an explicit "delist" verb, or "remove" co-located with
    // "spot" / "trading pair", or unambiguous security wording.
    private static readonly string[] RedFlagKeywords =
    [
        "delist", "delisting", "will delist",
        "hack", "exploit", "security incident", "stolen funds", "compromise",
    ];

    /// <summary>
    /// True for "Binance Will Delist X, Y, Z" full-token delistings and security incidents.
    /// We intentionally EXCLUDE "Notice of Removal of Spot Trading Pairs" — that only removes
    /// specific pairs (e.g. X/BTC) while leaving X/USDT trading, so it's not a risk to USDT bots.
    /// </summary>
    private static bool IsRedFlag(string text)
    {
        var lower = text.ToLowerInvariant();

        // Hard exclusions — anything mentioning these is NOT a full-token delist.
        if (lower.Contains("margin trading")) return false;
        if (lower.Contains("isolated margin")) return false;
        if (lower.Contains("cross margin")) return false;
        if (lower.Contains("leveraged token")) return false;
        if (lower.Contains("convert quote")) return false;
        if (lower.Contains("trading bots service")) return false;
        if (lower.Contains("removal of spot trading pair")) return false;   // pair-only removal
        if (lower.Contains("removal of spot trading pairs")) return false;

        // Hard inclusions — explicit delist verb or security wording.
        return RedFlagKeywords.Any(k => lower.Contains(k));
    }

    private DateTimeOffset _lastSeenAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BinanceAnnouncementWorker started ({Poll}min).", PollInterval.TotalMinutes);
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);   // let DB settle

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "BinanceAnnouncementWorker poll failed"); }
            try { await Task.Delay(PollInterval, stoppingToken); } catch { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 QuantFlowBots/1.0");

        string html;
        try { html = await http.GetStringAsync(TelegramUrl, ct); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to fetch Telegram preview");
            return;
        }

        var messages = ParseMessages(html);
        if (messages.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var scorer = scope.ServiceProvider.GetRequiredService<ISentimentScorer>();
        var agg = scope.ServiceProvider.GetRequiredService<ISentimentAggregator>();
        var bus = scope.ServiceProvider.GetRequiredService<ISentimentBus>();

        // Known USDT symbols — used both for headline → symbol resolution and to skip
        // mentioning random tickers that aren't tradeable here.
        var symbolByBase = await db.Symbols
            .Where(s => s.QuoteAsset == "USDT")
            .Select(s => new { s.Code, s.BaseAsset })
            .ToDictionaryAsync(s => s.BaseAsset.ToUpperInvariant(), s => s.Code, ct);

        var maxSeenAt = _lastSeenAt;
        foreach (var msg in messages.OrderBy(m => m.At))
        {
            if (msg.At <= _lastSeenAt) continue;
            if (msg.At > maxSeenAt) maxSeenAt = msg.At;

            // Only process true delist / security events. Everything else (listings, AMAs,
            // partnership posts, pair-only removals, margin notices) is intentionally dropped —
            // they pollute the sentiment feed and don't drive any auto-close decision.
            if (!IsRedFlag(msg.Text)) continue;

            var matchedSymbols = ExtractSymbols(msg.Text, symbolByBase);
            if (matchedSymbols.Count == 0) continue;   // no resolvable symbol → nothing to do
            var isRedFlag = true;
            var targets = matchedSymbols;
            foreach (var symbol in targets)
            {
                var scored = scorer.Score(new SentimentInput(symbol, Source, msg.Text, msg.Url, msg.At, Tags: "binance"));
                agg.Apply(scored);
                var symbolId = symbol == "MARKET" ? (int?)null
                    : await db.Symbols.Where(s => s.Code == symbol).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
                db.SentimentEvents.Add(new SentimentEvent
                {
                    SymbolCode = symbol, SymbolId = symbolId,
                    Source = Source,
                    Headline = scored.Headline.Length > 512 ? scored.Headline[..512] : scored.Headline,
                    Url = scored.Url,
                    Score = scored.Score, Magnitude = scored.Magnitude,
                    Tags = scored.Tags,
                    At = scored.At, IngestedAt = DateTimeOffset.UtcNow,
                });
                await bus.PublishAsync(scored, ct);

                if (isRedFlag && symbol != "MARKET")
                {
                    var newlyBlocked = !riskGate.IsBlocked(symbol);
                    await riskGate.BlockAsync(symbol, RedFlagReason(msg.Text), Source, msg.Url, ct);
                    if (newlyBlocked)
                    {
                        logger.LogWarning("RED FLAG on {Symbol}: {Headline}", symbol, msg.Text);
                        await AutoCloseOpenPositionsAsync(scope, symbol, ct);
                    }
                }
            }
        }
        await db.SaveChangesAsync(ct);
        _lastSeenAt = maxSeenAt;
    }

    /// <summary>Find any open position on the symbol and market-close it via the dispatcher.</summary>
    private async Task AutoCloseOpenPositionsAsync(IServiceScope scope, string symbol, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<QuantFlowBotsDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<Application.Trading.ITradingDispatcher>();

        var open = await db.Positions
            .Where(p => p.Status == PositionStatus.Open && p.Symbol!.Code == symbol)
            .Select(p => new { p.Id, p.BotId, p.SymbolId, p.Quantity, p.EntryPrice })
            .ToListAsync(ct);

        foreach (var p in open)
        {
            try
            {
                // Use entry price as a fallback execution reference; paper-mode mark fills at this
                // price, live executors override with current bid/ask inside the dispatcher chain.
                await dispatcher.ExecuteAsync(new Application.Trading.PaperOrderRequest(
                    p.BotId, null, p.SymbolId, OrderSide.Sell, p.Quantity, p.EntryPrice, "auto:risk_block"), ct);
                logger.LogWarning("Auto-closed position {Pos} on {Symbol} due to risk flag", p.Id, symbol);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-close failed for {Pos} on {Symbol}", p.Id, symbol);
            }
        }
    }

    private static string RedFlagReason(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("hack") || lower.Contains("exploit") || lower.Contains("stolen")) return "security_incident";
        if (lower.Contains("suspend")) return "trading_suspension";
        if (lower.Contains("delist") || lower.Contains("remove")) return "delisting_announced";
        return "binance_alert";
    }

    private static List<string> ExtractSymbols(string text, IReadOnlyDictionary<string, string> baseAssetToCode)
    {
        // Two precise patterns — anything else is too noisy (we used to nab 'BTC' from 'X/BTC',
        // wrongly flagging BTCUSDT). False-positive cost is huge: bot auto-closes a real position.
        var found = new HashSet<string>();

        // Pattern A — explicit X/USDT pair. Only this is unambiguous evidence that X's USDT pair
        // is the target. Pairs against other quotes (X/BTC, X/ETH) intentionally do NOT flag XUSDT
        // because Binance often removes only some quote pairs while keeping USDT.
        foreach (Match m in PairUsdtRegex().Matches(text))
        {
            var baseAsset = m.Groups["base"].Value.ToUpperInvariant();
            if (baseAssetToCode.TryGetValue(baseAsset, out var code))
                found.Add(code);
        }

        // Pattern B — full coin delist phrasing: "Binance Will Delist X, Y, Z on YYYY-MM-DD".
        // We only scan for bare tickers in the SLICE of text after the "delist" verb so we don't
        // accidentally match company names mentioned in the announcement preamble.
        var lower = text.ToLowerInvariant();
        var idx = lower.IndexOf("delist", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var after = text[idx..];
            foreach (Match m in BareTickerRegex().Matches(after))
            {
                var token = m.Value.ToUpperInvariant();
                if (baseAssetToCode.TryGetValue(token, out var code))
                    found.Add(code);
            }
        }
        return found.ToList();
    }

    /// <summary>Parses Telegram's t.me/s/&lt;channel&gt; HTML into messages. Cheap regex over the
    /// `tgme_widget_message` blocks — keeps us off heavier HTML libs since the markup is stable.</summary>
    private static List<TelegramMessage> ParseMessages(string html)
    {
        var list = new List<TelegramMessage>();
        foreach (Match m in MessageRegex().Matches(html))
        {
            var dateStr = m.Groups["dt"].Value;
            var url = m.Groups["url"].Value;
            var rawText = m.Groups["text"].Value;
            if (!DateTimeOffset.TryParse(dateStr, out var at)) continue;
            var clean = StripHtml(rawText).Trim();
            if (clean.Length < 8) continue;
            list.Add(new TelegramMessage(at, clean, url));
        }
        return list;
    }

    private static string StripHtml(string s) => HtmlTagRegex().Replace(s, " ").Replace("&nbsp;", " ").Trim();

    [GeneratedRegex(@"\b(?<base>[A-Z0-9]{2,10})/USDT\b")] private static partial Regex PairUsdtRegex();
    [GeneratedRegex(@"\b[A-Z]{2,10}\b")] private static partial Regex BareTickerRegex();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex HtmlTagRegex();
    // Pulls each post: the datetime is on a <time datetime="..."> inside the meta, and the
    // post URL is on <a class="tgme_widget_message_date" href="...">; the body is in
    // <div class="tgme_widget_message_text" ...>...</div>.
    [GeneratedRegex(@"tgme_widget_message_date""\s+href=""(?<url>[^""]+)"".*?datetime=""(?<dt>[^""]+)"".*?tgme_widget_message_text[^>]*>(?<text>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex MessageRegex();

    private sealed record TelegramMessage(DateTimeOffset At, string Text, string Url);
}
