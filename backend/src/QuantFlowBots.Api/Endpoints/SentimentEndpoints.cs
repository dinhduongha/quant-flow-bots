using Microsoft.EntityFrameworkCore;
using QuantFlowBots.Application.Sentiment;
using QuantFlowBots.Domain.Entities;
using QuantFlowBots.Infrastructure.Persistence;

namespace QuantFlowBots.Api.Endpoints;

public static class SentimentEndpoints
{
    public sealed record ManualSentimentRequest(
        string SymbolCode,
        string Headline,
        string? Url,
        string? Source,
        DateTimeOffset? At,
        decimal? OverrideScore,
        string? Tags);

    public sealed record SentimentEventDto(
        Guid Id,
        string SymbolCode,
        string Source,
        string Headline,
        string? Url,
        decimal Score,
        decimal Magnitude,
        string? Tags,
        DateTimeOffset At,
        DateTimeOffset IngestedAt);

    public sealed record SnapshotDto(
        string SymbolCode,
        decimal RollingScore,
        decimal RollingMagnitude,
        int SampleCount,
        decimal? LatestScore,
        DateTimeOffset? LatestAt);

    public static IEndpointRouteBuilder MapSentiment(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/sentiment").WithTags("sentiment");

        grp.MapGet("/recent", async (QuantFlowBotsDbContext db, int? limit, string? symbol, CancellationToken ct) =>
        {
            var q = db.SentimentEvents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(symbol))
                q = q.Where(s => s.SymbolCode == symbol.ToUpper());
            var n = Math.Clamp(limit ?? 50, 1, 200);
            var rows = await q.OrderByDescending(s => s.At).Take(n)
                .Select(s => new SentimentEventDto(s.Id, s.SymbolCode, s.Source, s.Headline, s.Url,
                    s.Score, s.Magnitude, s.Tags, s.At, s.IngestedAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/snapshot/{symbolCode}", (string symbolCode, ISentimentAggregator agg) =>
        {
            var s = agg.Get(symbolCode);
            return Results.Ok(new SnapshotDto(s.SymbolCode, s.RollingScore, s.RollingMagnitude, s.SampleCount, s.LatestScore, s.LatestAt));
        });

        grp.MapGet("/top", (ISentimentAggregator agg, int? n, string? direction) =>
        {
            var bullish = !string.Equals(direction, "bear", StringComparison.OrdinalIgnoreCase);
            var snaps = agg.Top(Math.Clamp(n ?? 10, 1, 50), bullish);
            return Results.Ok(snaps.Select(s => new SnapshotDto(s.SymbolCode, s.RollingScore, s.RollingMagnitude, s.SampleCount, s.LatestScore, s.LatestAt)));
        });

        grp.MapPost("/manual", async (
            ManualSentimentRequest req,
            QuantFlowBotsDbContext db,
            ISentimentScorer scorer,
            ISentimentAggregator agg,
            ISentimentBus bus,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SymbolCode) || string.IsNullOrWhiteSpace(req.Headline))
                return Results.BadRequest(new { error = "symbol_and_headline_required" });

            var scored = scorer.Score(new SentimentInput(
                req.SymbolCode.ToUpper(),
                string.IsNullOrWhiteSpace(req.Source) ? "manual" : req.Source,
                req.Headline,
                req.Url,
                req.At ?? DateTimeOffset.UtcNow,
                req.Tags));

            if (req.OverrideScore is decimal o)
                scored = scored with { Score = Math.Clamp(o, -1m, 1m), Magnitude = 1m };

            agg.Apply(scored);

            var symbolId = await db.Symbols
                .Where(s => s.Code == scored.SymbolCode)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);
            var row = new SentimentEvent
            {
                SymbolCode = scored.SymbolCode,
                SymbolId = symbolId,
                Source = scored.Source,
                Headline = scored.Headline.Length > 512 ? scored.Headline[..512] : scored.Headline,
                Url = scored.Url,
                Score = scored.Score,
                Magnitude = scored.Magnitude,
                Tags = scored.Tags,
                At = scored.At,
                IngestedAt = DateTimeOffset.UtcNow,
            };
            db.SentimentEvents.Add(row);
            await db.SaveChangesAsync(ct);
            await bus.PublishAsync(scored, ct);
            return Results.Ok(new SentimentEventDto(row.Id, row.SymbolCode, row.Source, row.Headline, row.Url,
                row.Score, row.Magnitude, row.Tags, row.At, row.IngestedAt));
        }).RequireAuthorization();

        // Delete one event, then rebuild the rolling state for that symbol from the rows
        // that remain. Required because the aggregator is in-memory and Apply() is additive.
        grp.MapDelete("/{id:guid}", async (Guid id, QuantFlowBotsDbContext db, ISentimentAggregator agg, CancellationToken ct) =>
        {
            var row = await db.SentimentEvents.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return Results.NotFound();
            var symbol = row.SymbolCode;
            db.SentimentEvents.Remove(row);
            await db.SaveChangesAsync(ct);

            // Replay remaining rows in chronological order to rebuild this symbol's EWMA.
            agg.Reset(symbol);
            var remaining = await db.SentimentEvents.AsNoTracking()
                .Where(s => s.SymbolCode == symbol)
                .OrderBy(s => s.At)
                .Select(s => new { s.SymbolCode, s.Source, s.Headline, s.Url, s.Score, s.Magnitude, s.Tags, s.At })
                .ToListAsync(ct);
            foreach (var r in remaining)
                agg.Apply(new ScoredSentiment(r.SymbolCode, r.Source, r.Headline, r.Url, r.Score, r.Magnitude, r.At, r.Tags));
            return Results.NoContent();
        }).RequireAuthorization();

        // Wipe everything — careful, this also drops scraped events, not just manual ones.
        grp.MapDelete("/all", async (QuantFlowBotsDbContext db, ISentimentAggregator agg, CancellationToken ct) =>
        {
            await db.SentimentEvents.ExecuteDeleteAsync(ct);
            agg.ResetAll();
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }
}
