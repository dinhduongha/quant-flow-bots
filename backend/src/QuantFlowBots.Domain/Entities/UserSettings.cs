namespace QuantFlowBots.Domain.Entities;

public sealed class UserSettings
{
    public Guid UserId { get; set; }
    // Existing channel: bot risk + auto_close events.
    public string? TelegramBotToken { get; set; }
    public string? TelegramChatId { get; set; }
    public bool TelegramAlertsEnabled { get; set; }

    // Whale Alerts — independent channel so the user can route market alerts to a
    // separate Telegram bot from the bot-execution one.
    public string? WhaleAlertBotToken { get; set; }
    public string? WhaleAlertChatId { get; set; }
    public bool WhaleAlertEnabled { get; set; }
    /// <summary>CSV of intervals to watch: e.g. "5m,15m,1h".</summary>
    public string? WhaleAlertIntervals { get; set; }
    public decimal WhaleAlertMultiplier { get; set; } = 5m;
    public decimal WhaleAlertMinVolume24h { get; set; } = 500_000m;
    /// <summary>"intrabar" (live open candle) or "candle_close" (only after candle finalizes).</summary>
    public string WhaleAlertMode { get; set; } = "candle_close";
    public int WhaleAlertCooldownMinutes { get; set; } = 60;
    /// <summary>Number of historical candles averaged as the baseline (excluding current candle).
    /// Comparing to a single prev candle is noisy; 20 is a common standard for volume z-score style rules.</summary>
    public int WhaleAlertLookback { get; set; } = 20;
    /// <summary>"buy" | "sell" | "both" — filter by inferred direction.</summary>
    public string WhaleAlertDirection { get; set; } = "both";

    // Order-book Wall Alerts — fires when a single price level holds >= MinNotional USDT
    // within MaxDistancePct of mid. Separate bot so it can be routed independently.
    public string? WallAlertBotToken { get; set; }
    public string? WallAlertChatId { get; set; }
    public bool WallAlertEnabled { get; set; }
    public decimal WallAlertMinNotional { get; set; } = 500_000m;
    public decimal WallAlertMaxDistancePct { get; set; } = 2m;
    /// <summary>"Bid" | "Ask" | "" (both) — filter side of the wall.</summary>
    public string WallAlertSide { get; set; } = "";
    public int WallAlertCooldownMinutes { get; set; } = 30;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
