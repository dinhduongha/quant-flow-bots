using System.Globalization;
using QuantFlowBots.Infrastructure.Exchanges.Binance;
using SkiaSharp;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Renders a compact order-book "ladder" PNG for a wall alert: asks stacked above the mid line,
/// bids below, each level a horizontal bar sized by USD notional, with the detected wall row
/// outlined in gold and tagged. Pure server-side drawing (SkiaSharp) — mirrors the dashboard
/// order-book widget so the Telegram alert carries the same visual context.
/// </summary>
public static class WallSnapshotRenderer
{
    private const int Width = 540;
    private const int RowH = 20;
    private const int HeaderH = 34;
    private const int PadX = 12;
    private const int BarX0 = 150;   // bars start here (leaves room for price + WALL badge)
    private const int BarX1 = 432;   // bars end here (max width)
    private const int ValX = 528;    // right-aligned notional label
    private const int BadgeX = 100;  // WALL badge sits between price and bar

    private static readonly SKColor Bg = new(13, 17, 23);
    private static readonly SKColor BgAlt = new(18, 24, 32);
    private static readonly SKColor MidBg = new(28, 33, 41);
    private static readonly SKColor BidColor = new(34, 163, 74);     // green
    private static readonly SKColor AskColor = new(220, 56, 56);     // red
    private static readonly SKColor BidBar = new(34, 163, 74, 70);
    private static readonly SKColor AskBar = new(220, 56, 56, 70);
    private static readonly SKColor Gold = new(245, 200, 66);
    private static readonly SKColor TextDim = new(150, 158, 170);
    private static readonly SKColor TextBright = new(225, 230, 238);

    public static byte[]? TryRender(string symbol, string side, decimal wallPrice, decimal mid, DepthSnapshot depth, int levels = 11)
    {
        try
        {
            // Levels nearest the mid: lowest asks, highest bids.
            var asks = depth.Asks.Where(a => a.Qty > 0).OrderBy(a => a.Price).Take(levels).ToArray();
            var bids = depth.Bids.Where(b => b.Qty > 0).OrderByDescending(b => b.Price).Take(levels).ToArray();
            if (asks.Length == 0 && bids.Length == 0) return null;

            var maxNotional = Math.Max(
                asks.Length > 0 ? asks.Max(a => a.Price * a.Qty) : 0m,
                bids.Length > 0 ? bids.Max(b => b.Price * b.Qty) : 0m);
            if (maxNotional <= 0m) return null;

            var rowCount = asks.Length + 1 /*mid*/ + bids.Length;
            var height = HeaderH + rowCount * RowH + PadX;

            using var surface = SKSurface.Create(new SKImageInfo(Width, height));
            var c = surface.Canvas;
            c.Clear(Bg);

            using var fontMono = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;
            using var fontSans = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
            using var title = new SKPaint { Typeface = fontSans, TextSize = 14, Color = TextBright, IsAntialias = true, FakeBoldText = true };
            using var sub = new SKPaint { Typeface = fontSans, TextSize = 11, Color = TextDim, IsAntialias = true };
            using var priceP = new SKPaint { Typeface = fontMono, TextSize = 12.5f, IsAntialias = true };
            using var valP = new SKPaint { Typeface = fontMono, TextSize = 12.5f, IsAntialias = true, TextAlign = SKTextAlign.Right };
            using var barP = new SKPaint { IsAntialias = true };
            using var linkP = new SKPaint { Color = MidBg, IsAntialias = true };

            // Header
            var sideUp = side.Equals("Bid", StringComparison.OrdinalIgnoreCase);
            var sideWord = sideUp ? "BUY" : "SELL";
            var headColor = sideUp ? BidColor : AskColor;
            using (var dot = new SKPaint { Color = headColor, IsAntialias = true })
                c.DrawCircle(PadX + 5, 16, 5, dot);
            c.DrawText($"{symbol}", PadX + 16, 21, title);
            using (var sideP = new SKPaint { Typeface = fontSans, TextSize = 14, Color = headColor, IsAntialias = true, FakeBoldText = true })
                c.DrawText($"{sideWord} WALL", PadX + 16 + title.MeasureText(symbol) + 8, 21, sideP);
            c.DrawText($"order book · {FmtPrice(mid)} mid", Width - PadX, 21,
                new SKPaint { Typeface = fontSans, TextSize = 11, Color = TextDim, IsAntialias = true, TextAlign = SKTextAlign.Right });

            var y = HeaderH;

            // Asks (top → highest price first)
            foreach (var lvl in asks.Reverse())
                y = DrawRow(c, y, lvl.Price, lvl.Qty, maxNotional, AskColor, AskBar, IsWall(lvl.Price, wallPrice, !sideUp), priceP, valP, barP, linkP);

            // Mid divider
            using (var midp = new SKPaint { Color = MidBg })
                c.DrawRect(0, y, Width, RowH, midp);
            using (var midText = new SKPaint { Typeface = fontMono, TextSize = 11.5f, Color = TextDim, IsAntialias = true, TextAlign = SKTextAlign.Center })
                c.DrawText($"— mid {FmtPrice(mid)} —", Width / 2f, y + 14, midText);
            y += RowH;

            // Bids (highest price near mid first)
            foreach (var lvl in bids)
                y = DrawRow(c, y, lvl.Price, lvl.Qty, maxNotional, BidColor, BidBar, IsWall(lvl.Price, wallPrice, sideUp), priceP, valP, barP, linkP);

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 95);
            return data.ToArray();
        }
        catch
        {
            return null; // never let a rendering glitch block the text alert
        }
    }

    private static int DrawRow(SKCanvas c, int y, decimal price, decimal qty, decimal maxNotional,
        SKColor priceColor, SKColor barColor, bool isWall,
        SKPaint priceP, SKPaint valP, SKPaint barP, SKPaint linkP)
    {
        var notional = price * qty;
        var frac = (float)Math.Min(1.0, (double)(notional / maxNotional));
        var barW = (BarX1 - BarX0) * frac;

        // bar grows from the right edge inward (toward price col) — matches depth-ladder convention
        barP.Color = isWall ? Gold : barColor;
        c.DrawRect(BarX1 - barW, y + 4, barW, RowH - 8, barP);

        priceP.Color = isWall ? Gold : priceColor;
        c.DrawText(FmtPrice(price), PadX, y + 14.5f, priceP);

        valP.Color = isWall ? Gold : TextBright;
        c.DrawText(FmtUsd(notional), ValX, y + 14.5f, valP);

        if (isWall)
        {
            using var border = new SKPaint { Color = Gold, IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true };
            c.DrawRoundRect(2, y + 1, Width - 4, RowH - 2, 3, 3, border);
            using var badge = new SKPaint { Typeface = SKTypeface.Default, TextSize = 9.5f, Color = new SKColor(13, 17, 23), IsAntialias = true, FakeBoldText = true };
            using var badgeBg = new SKPaint { Color = Gold, IsAntialias = true };
            c.DrawRoundRect(BadgeX, y + 4, 40, RowH - 8, 2, 2, badgeBg);
            c.DrawText("WALL", BadgeX + 5, y + 14.5f, badge);
        }
        return y + RowH;
    }

    private static bool IsWall(decimal price, decimal wallPrice, bool sideMatches)
        => sideMatches && wallPrice > 0 && Math.Abs(price - wallPrice) <= wallPrice * 0.0001m;

    private static string FmtPrice(decimal p)
    {
        if (p >= 1000) return p.ToString("#,##0.00", CultureInfo.InvariantCulture);
        if (p >= 1) return p.ToString("0.0000", CultureInfo.InvariantCulture);
        return p.ToString("0.000000000", CultureInfo.InvariantCulture);
    }

    private static string FmtUsd(decimal n)
    {
        if (n >= 1_000_000_000m) return $"${n / 1_000_000_000m:0.00}B";
        if (n >= 1_000_000m) return $"${n / 1_000_000m:0.00}M";
        if (n >= 1_000m) return $"${n / 1_000m:0.00}K";
        return $"${n:0}";
    }
}
