using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuantFlowBots.Application.Exchanges;
using QuantFlowBots.Domain.Enums;

namespace QuantFlowBots.Infrastructure.Exchanges.Binance;

public sealed class BinanceRestClient(HttpClient httpClient, ILogger<BinanceRestClient> logger) : IExchangeClient
{
    public string ExchangeCode => "binance";

    public async Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(CancellationToken cancellationToken)
    {
        using var doc = await httpClient.GetFromJsonAsync<JsonDocument>("/api/v3/exchangeInfo", cancellationToken)
            ?? throw new InvalidOperationException("Binance exchangeInfo returned null.");
        var list = new List<SymbolInfo>();
        foreach (var sym in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (sym.GetProperty("status").GetString() != "TRADING") continue;
            var code = sym.GetProperty("symbol").GetString()!;
            var baseAsset = sym.GetProperty("baseAsset").GetString()!;
            var quoteAsset = sym.GetProperty("quoteAsset").GetString()!;
            decimal minQty = 0, tickSize = 0, stepSize = 0, minNotional = 0;
            foreach (var f in sym.GetProperty("filters").EnumerateArray())
            {
                var type = f.GetProperty("filterType").GetString();
                switch (type)
                {
                    case "LOT_SIZE":
                        minQty = Parse(f.GetProperty("minQty").GetString());
                        stepSize = Parse(f.GetProperty("stepSize").GetString());
                        break;
                    case "PRICE_FILTER":
                        tickSize = Parse(f.GetProperty("tickSize").GetString());
                        break;
                    case "NOTIONAL":
                    case "MIN_NOTIONAL":
                        if (f.TryGetProperty("minNotional", out var mn))
                            minNotional = Parse(mn.GetString());
                        break;
                }
            }
            list.Add(new SymbolInfo(code, baseAsset, quoteAsset, minQty, tickSize, stepSize, minNotional));
        }
        return list;
    }

    public async Task<IReadOnlyList<TickerSnapshot>> GetAllTickersAsync(CancellationToken cancellationToken)
    {
        var tickers = await httpClient.GetFromJsonAsync<List<BinanceTicker24h>>("/api/v3/ticker/24hr", cancellationToken)
            ?? [];
        return tickers
            .Select(t => new TickerSnapshot(t.Symbol, Parse(t.LastPrice), Parse(t.PriceChangePercent), Parse(t.QuoteVolume), DateTimeOffset.UtcNow))
            .ToList();
    }

    public async Task<IReadOnlyList<TickerSnapshot>> GetRollingTickersAsync(
        IReadOnlyList<string> symbols,
        string windowSize,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return [];

        // Binance rejects (400) the whole batch if ANY symbol has non-ASCII chars (DB seed sometimes
        // contains promo/garbage like "币安人生USDT"). Strip those + uppercase + dedupe up front.
        var cleaned = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')))
            .Select(s => s.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cleaned.Count == 0) return [];

        var all = new List<TickerSnapshot>(cleaned.Count);
        foreach (var batch in cleaned.Chunk(100))
        {
            var symbolsJson = Uri.EscapeDataString(JsonSerializer.Serialize(batch));
            var url = $"/api/v3/ticker?symbols={symbolsJson}&windowSize={Uri.EscapeDataString(windowSize)}&type=FULL";
            List<BinanceRollingTicker>? tickers;
            try
            {
                tickers = await httpClient.GetFromJsonAsync<List<BinanceRollingTicker>>(url, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // One bad symbol fails the whole batch — skip this batch rather than 500 the caller.
                continue;
            }
            if (tickers is null) continue;
            all.AddRange(tickers.Select(t => new TickerSnapshot(
                t.Symbol,
                Parse(t.LastPrice),
                Parse(t.PriceChangePercent),
                Parse(t.QuoteVolume),
                t.CloseTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t.CloseTime) : DateTimeOffset.UtcNow)));
        }

        return all;
    }

    public async Task<IReadOnlyList<CandleData>> GetCandlesAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v3/klines?symbol={symbol}&interval={ToBinanceInterval(interval)}&limit={Math.Clamp(limit, 1, 1000)}";
        if (from.HasValue) url += $"&startTime={from.Value.ToUnixTimeMilliseconds()}";
        if (to.HasValue) url += $"&endTime={to.Value.ToUnixTimeMilliseconds()}";

        var data = await httpClient.GetFromJsonAsync<JsonElement[]>(url, cancellationToken) ?? [];
        var list = new List<CandleData>(data.Length);
        foreach (var row in data)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64());
            var closeTime = DateTimeOffset.FromUnixTimeMilliseconds(row[6].GetInt64());
            list.Add(new CandleData(
                symbol,
                interval,
                openTime,
                closeTime,
                Parse(row[1].GetString()),
                Parse(row[2].GetString()),
                Parse(row[3].GetString()),
                Parse(row[4].GetString()),
                Parse(row[5].GetString()),
                Parse(row[7].GetString()),
                row[8].GetInt32(),
                IsClosed: true));
        }
        return list;
    }

    public async Task<DepthSnapshot?> GetDepthAsync(string symbol, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"/api/v3/depth?symbol={symbol}&limit={Math.Clamp(limit, 5, 5000)}";
            var doc = await httpClient.GetFromJsonAsync<JsonDocument>(url, cancellationToken);
            if (doc is null) return null;
            using (doc)
            {
                var bids = ParseLevels(doc.RootElement.GetProperty("bids"));
                var asks = ParseLevels(doc.RootElement.GetProperty("asks"));
                return new DepthSnapshot(symbol, bids, asks, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GetDepth failed for {Symbol}", symbol);
            return null;
        }
    }

    private static (decimal Price, decimal Qty)[] ParseLevels(JsonElement arr)
    {
        var list = new List<(decimal, decimal)>(arr.GetArrayLength());
        foreach (var lvl in arr.EnumerateArray())
            list.Add((Parse(lvl[0].GetString()), Parse(lvl[1].GetString())));
        return list.ToArray();
    }

    public async Task<DateTimeOffset?> GetSymbolListingDateAsync(string symbol, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"/api/v3/klines?symbol={symbol}&interval=1d&limit=1&startTime=0";
            var data = await httpClient.GetFromJsonAsync<JsonElement[]>(url, cancellationToken);
            if (data is null || data.Length == 0) return null;
            return DateTimeOffset.FromUnixTimeMilliseconds(data[0][0].GetInt64());
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GetSymbolListingDate failed for {Symbol}", symbol);
            return null;
        }
    }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        logger.LogWarning("PlaceOrder called on BinanceRestClient — live trading is disabled in phase 1.");
        return Task.FromResult(new OrderResult(
            request.ClientOrderId,
            null,
            OrderStatus.Rejected,
            request.Price ?? 0m,
            request.Quantity,
            0m,
            0m,
            0m,
            "live_trading_disabled"));
    }

    public Task<OrderResult> CancelOrderAsync(string symbol, string clientOrderId, CancellationToken cancellationToken)
        => Task.FromResult(new OrderResult(clientOrderId, null, OrderStatus.Rejected, 0, 0, 0, 0, 0, "live_trading_disabled"));

    private static decimal Parse(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static string ToBinanceInterval(CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute => "1m",
        CandleInterval.FiveMinutes => "5m",
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.ThirtyMinutes => "30m",
        CandleInterval.OneHour => "1h",
        CandleInterval.TwoHours => "2h",
        CandleInterval.FourHours => "4h",
        CandleInterval.OneDay => "1d",
        _ => "1m"
    };

    private sealed record BinanceTicker24h(string Symbol, string LastPrice, string PriceChangePercent, string QuoteVolume);
    private sealed record BinanceRollingTicker(string Symbol, string LastPrice, string PriceChangePercent, string QuoteVolume, long CloseTime);
}

public sealed record DepthSnapshot(
    string Symbol,
    (decimal Price, decimal Qty)[] Bids,
    (decimal Price, decimal Qty)[] Asks,
    DateTimeOffset At);
