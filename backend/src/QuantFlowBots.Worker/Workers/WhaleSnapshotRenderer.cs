using System.Globalization;
using QuantFlowBots.Application.Exchanges;
using SkiaSharp;

namespace QuantFlowBots.Worker.Workers;

/// <summary>
/// Renders a whale-alert volume chart PNG: per-candle quote-volume bars (oldest → newest), the
/// average baseline drawn as a dashed line, and the spiking candle (the latest, which tripped the
/// N× threshold) highlighted in gold with its multiplier. Pure SkiaSharp drawing — gives the
/// Telegram alert the same visual context as a TradingView volume pane.
/// </summary>
public static class WhaleSnapshotRenderer
{
    private const int Width = 560;
    private const int Height = 300;
    private const int PadX = 14;
    private const int HeaderH = 46;
    private const int PlotTop = 58;
    private const int PlotBottom = Height - 30;

    private static readonly SKColor Bg = new(13, 17, 23);
    private static readonly SKColor UpColor = new(34, 163, 74);
    private static readonly SKColor DownColor = new(220, 56, 56);
    private static readonly SKColor Gold = new(245, 200, 66);
    private static readonly SKColor TextDim = new(150, 158, 170);
    private static readonly SKColor TextBright = new(225, 230, 238);
    private static readonly SKColor Grid = new(40, 46, 56);

    public static byte[]? TryRender(
        string symbol, string interval, IReadOnlyList<CandleData> candles,
        decimal avgBaseline, decimal ratio, string direction, int maxBars = 24)
    {
        try
        {
            if (candles.Count < 2) return null;
            var shown = candles.Count > maxBars ? candles.Skip(candles.Count - maxBars).ToList() : candles.ToList();
            var maxVol = shown.Max(c => c.QuoteVolume);
            if (maxVol <= 0m) return null;
            var spikeIdx = shown.Count - 1; // latest candle is the spike

            using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
            var c = surface.Canvas;
            c.Clear(Bg);

            using var fontMono = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;
            using var fontSans = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;

            var isBuy = direction.Equals("buy", StringComparison.OrdinalIgnoreCase);
            var dirColor = isBuy ? UpColor : DownColor;

            // Header
            using (var dot = new SKPaint { Color = dirColor, IsAntialias = true })
                c.DrawCircle(PadX + 5, 18, 5, dot);
            using (var title = new SKPaint { Typeface = fontSans, TextSize = 15, Color = TextBright, IsAntialias = true, FakeBoldText = true })
                c.DrawText($"{symbol}", PadX + 16, 23, title);
            using (var meta = new SKPaint { Typeface = fontSans, TextSize = 12, Color = TextDim, IsAntialias = true })
                c.DrawText($"{interval} · whale {(isBuy ? "BUY" : "SELL")} volume", PadX + 16 + 90, 23, meta);
            using (var ratioP = new SKPaint { Typeface = fontSans, TextSize = 17, Color = Gold, IsAntialias = true, FakeBoldText = true, TextAlign = SKTextAlign.Right })
                c.DrawText($"{ratio:0.0}× avg", Width - PadX, 24, ratioP);

            var plotH = PlotBottom - PlotTop;
            var plotW = Width - PadX * 2;
            var n = shown.Count;
            var slot = plotW / (float)n;
            var barW = Math.Max(3f, slot * 0.7f);

            // Baseline (avg) dashed line
            var baselineY = PlotBottom - (float)((double)(avgBaseline / maxVol) * plotH);
            using (var dash = new SKPaint { Color = TextDim, IsStroke = true, StrokeWidth = 1.2f, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) })
                c.DrawLine(PadX, baselineY, Width - PadX, baselineY, dash);
            using (var lbl = new SKPaint { Typeface = fontMono, TextSize = 11, Color = TextDim, IsAntialias = true })
                c.DrawText($"avg {FmtUsd(avgBaseline)}", PadX + 2, baselineY - 4, lbl);

            // Bars
            for (var i = 0; i < n; i++)
            {
                var cd = shown[i];
                var h = (float)((double)(cd.QuoteVolume / maxVol) * plotH);
                var x = PadX + i * slot + (slot - barW) / 2f;
                var y = PlotBottom - h;
                var isSpike = i == spikeIdx;
                var col = isSpike ? Gold : (cd.Close >= cd.Open ? UpColor : DownColor);
                using var p = new SKPaint { Color = isSpike ? col : col.WithAlpha(150), IsAntialias = true };
                c.DrawRect(x, y, barW, h, p);

                if (isSpike)
                {
                    using var border = new SKPaint { Color = Gold, IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true };
                    c.DrawRect(x - 1.5f, y - 1.5f, barW + 3, h + 1.5f, border);
                    using var sv = new SKPaint { Typeface = fontMono, TextSize = 11, Color = Gold, IsAntialias = true, TextAlign = SKTextAlign.Center, FakeBoldText = true };
                    c.DrawText(FmtUsd(cd.QuoteVolume), x + barW / 2f, y - 6, sv);
                }
            }

            // Baseline axis line
            using (var axis = new SKPaint { Color = Grid, StrokeWidth = 1 })
                c.DrawLine(PadX, PlotBottom, Width - PadX, PlotBottom, axis);

            // Footer
            using (var foot = new SKPaint { Typeface = fontSans, TextSize = 11, Color = TextDim, IsAntialias = true })
                c.DrawText($"last {n} × {interval} candles · spike vs {FmtUsd(avgBaseline)} baseline", PadX, Height - 10, foot);

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 95);
            return data.ToArray();
        }
        catch
        {
            return null; // never block the text alert on a render glitch
        }
    }

    private static string FmtUsd(decimal n)
    {
        if (n >= 1_000_000_000m) return $"${n / 1_000_000_000m:0.00}B";
        if (n >= 1_000_000m) return $"${n / 1_000_000m:0.00}M";
        if (n >= 1_000m) return $"${n / 1_000m:0.0}K";
        return $"${n:0}";
    }
}
