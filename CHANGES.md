# Quant Flow Bots — Nhật ký thay đổi quan trọng

Tài liệu này tóm tắt các thay đổi lớn theo **chủ đề + lý do** để khi đọc lại
nhiều tháng sau vẫn hiểu *vì sao* code thành ra như vậy. Sắp xếp theo nhóm
chức năng (không theo thứ tự thời gian).

Đại lộ rollback: tag `pre-net10-upgrade` trỏ vào commit ngay trước upgrade
(`git reset --hard pre-net10-upgrade`).

---

## 1. Sửa lỗi rò rỉ RAM nghiêm trọng ở Worker

**Triệu chứng:** Process `QuantFlowBots.Worker` phình tới **2.4–3.2 GB** rồi crash
(OOM). Postgres báo *"too many clients already"*.

**Chẩn đoán:** GC dump cho thấy **~68 000 instance `ServiceProviderEngineScope`**
+ ~10 triệu object EF Core nội bộ — rò rỉ ~1 500 DbContext/giây.

**Nguyên nhân gốc — [WallAlertWorker.cs](backend/src/QuantFlowBots.Worker/Workers/WallAlertWorker.cs):**
`OrderBookWallBus.OnWall` được bắn **mỗi mức giá có wall, mỗi lần depth cập nhật**
(~2 000 event/giây). Handler cũ `Task.Run(async () => { using var scope = ...; query UserSettings; ... })`
→ mỗi event tạo 1 DbContext + 1 connection Postgres → kẹt hàng chục nghìn scope EF → cạn pool → query treo → scope không bao giờ giải phóng.

**Đã sửa:**
- Cache eligible-user list trong RAM, refresh 30s **một timer** thay vì query DB mỗi event.
- Throttle in-memory `ConcurrentDictionary<symbol|side, ticks>` trước khi `Task.Run` — gộp firehose ~2 000/s xuống ≤ 1 dispatch / symbol|side / 5s.
- Redis dedupe per-user vẫn giữ (cooldown thật).

**Kết quả:** Worker 2.4–3.2 GB → **~170–210 MB phẳng**. Allocation rate 70 MB/s → 2-3 MB/s.

---

## 2. Bug "3 consumer giành 1 channel" — bot bỏ lỡ 2/3 nến đóng

**Triệu chứng:** Cảm giác "data chết", bot nhận tín hiệu yếu.

**Chẩn đoán:** Cùng một `Channel<KlineEvent>` được đọc bởi **3 consumer độc lập**:
`SignalScannerWorker`, `CandleIngestionWorker`, `SignalRBroadcaster`. Channel là
**queue at-most-once-delivery**, không phải pub/sub → mỗi event chỉ đến **1 trong 3** consumer.

**Hệ quả:** Bot nhận ~1/3 nến đóng → bỏ lỡ tín hiệu; DB ingestion thủng lỗ; FE qua SignalR chỉ thấy 1/3 update.

**Đã sửa** ([InMemoryEventBuses.cs](backend/src/QuantFlowBots.Infrastructure/Streaming/InMemoryEventBuses.cs) + [IMarketEventBus.cs](backend/src/QuantFlowBots.Application/Streaming/IMarketEventBus.cs)):
- Thay 1 channel chung bằng pattern **fan-out per subscriber**: `SubscribeKlines()` / `SubscribeTickers()` tạo channel riêng cho mỗi consumer (bounded + drop-oldest).
- `PublishAsync` ghi cho TẤT CẢ channel subscriber (`TryWrite`, không block publisher nếu 1 reader chậm).
- 3 consumer đổi từ `bus.Klines` → `bus.SubscribeKlines()`.

---

## 3. MarketStreamWorker: từ "subscribe 435 cặp lãng phí" → "watched + bot đang chạy"

**Vấn đề:** [MarketStreamWorker.cs](backend/src/QuantFlowBots.Worker/Workers/MarketStreamWorker.cs)
subscribe `kline_1m` cho **toàn bộ 435 cặp USDT** với comment cũ "for volume-spike detection",
mà volume-spike đã bị xóa từ lâu → 430 stream đó **không phục vụ gì**, chỉ ngốn parsing.

**Đã sửa:**
- Subscribe `kline_1m` chỉ cho **WatchSymbols ∪ symbol của bot đang chạy**.
- Refresh tập symbol mỗi **45s**; nếu đổi → reconnect WebSocket; nếu không → no-op.
- `BinanceMarketStreamClient` đổi sang API **declarative `SetSubscriptions()`** với reconnect-on-change qua `CancellationTokenSource`.
- Tự mở rộng khi bạn bật bot mới (không cần restart worker).

---

## 4. OrderBookWallCache: prune chủ động theo TTL

**Vấn đề:** `Snapshot()` mới prune entry hết hạn — nhưng trong Worker **không ai gọi `Snapshot()`** (chỉ API gọi). Dict tăng dần theo giá trôi.

**Đã sửa** ([OrderBookWallCache.cs](backend/src/QuantFlowBots.Infrastructure/Trading/OrderBookWallCache.cs)):
Time-gated prune trong `Upsert` (mỗi ~30s, dùng `Interlocked.CompareExchange` để chỉ 1 thread quét tại 1 thời điểm).

---

## 5. Cấu hình GC tiết kiệm RAM cho Worker

Worker streaming nặng → mặc định Server GC giữ heap to. Đổi sang Workstation + ConserveMemory ([Worker.csproj](backend/src/QuantFlowBots.Worker/QuantFlowBots.Worker.csproj)):
```xml
<ServerGarbageCollection>false</ServerGarbageCollection>
<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="5" />
```

---

## 6. RateLimitManager + tầng an toàn Binance REST

**Yêu cầu:** Bot trading thật cần kiểm soát weight chủ động (không chỉ chờ Binance ban).

**Kiến trúc** — mọi REST client (`BinanceRestClient`, `BinanceFuturesRestClient`,
`BinanceSpotSignedClient`) đều đi qua [BinanceGateHandler](backend/src/QuantFlowBots.Infrastructure/Exchanges/Binance/BinanceGateHandler.cs)
(`DelegatingHandler` gắn bằng `.AddHttpMessageHandler`). Đó là điểm nghẽn duy nhất:
```
BinanceRestClient / FuturesRestClient / SpotSignedClient
        ↓  (DelegatingHandler — không client nào tự gọi thẳng)
BinanceGateHandler  →  RateLimitManager (proactive) + IBinanceGate (reactive)
        ↓
Binance REST API
```

**[RateLimitManager.cs](backend/src/QuantFlowBots.Infrastructure/Exchanges/Binance/RateLimitManager.cs) làm:**
- **POST**: đọc header `X-MBX-USED-WEIGHT-1M`, lưu Redis key `binance:weight` (TTL 2 phút).
  Redis dùng làm chỗ chia sẻ vì **API + Worker là 2 process, cùng 1 IP, cùng 1 budget**.
- **PRE**: đánh giá trước khi gửi:
  - `< 70%` → Allow.
  - `70–85%` → Delay tỉ lệ (lệnh trading cap 0.4s; market data cap 2s).
  - `≥ 85%` → Reject *non-critical* (market data); lệnh trading vẫn đi.
- Phân loại critical/non-critical theo path (`/order`, `/account`, `/positionRisk`, … = critical).
- `RateLimitThrottledException` kế thừa `HttpRequestException` để `TickerSnapshotCache` tự fallback snapshot cũ thay vì lỗi.

**Cấu hình** [BinanceOptions](backend/src/QuantFlowBots.Infrastructure/Exchanges/Binance/BinanceOptions.cs):
`WeightLimitPerMinute=6000`, `WeightSlowDownPercent=70`, `WeightCriticalOnlyPercent=85`.

**Endpoint UI** `GET /api/market/rate-limit` — gộp snapshot weight + gate state cho widget dashboard.

---

## 7. Widget "Market Vitals" — 2 donut gauge cạnh nhau

**Lý do:** Trước đây Fear & Greed + API Weight là 2 card xếp dọc, chiếm chỗ + xấu.

**Đã làm** ([market-vitals.tsx](frontend/src/components/market-vitals.tsx)):
SVG donut chart 2 cột — F&G (trái, màu theo zone) + Weight (phải, màu theo level + badge BANNED nếu gate mở). Thay 2 widget cũ → 1 card gọn, đặt đầu cột phải dashboard.

---

## 8. Gộp Risk-blocked vào Sentiment

**Lý do:** Risk-blocked (delist/hack/suspend) **chính là tâm lý cực kỳ tiêu cực** —
cùng nhóm với sentiment, không nên là 2 card rời.

**Đã làm** ([sentiment-widget.tsx](frontend/src/components/sentiment-widget.tsx)):
- Card đổi tên "Sentiment" → "Sentiment & Risk", thêm badge đỏ 🛡 + số khi có risk flag.
- Risk-blocked giờ là **section đầu** trong card (chỉ hiện khi có flag).
- Xóa file `risk-flags-widget.tsx` (logic gộp hết vào sentiment).

---

## 9. VWAP Scanner: thêm thanh phân kỳ MA-VWAP

**Lý do:** Cột Symbol dùng `1fr` nên còn nhiều khoảng trống giữa Symbol và Side; chỉ đọc số `MA-VWAP` thì khó so sánh nhanh.

**Đã làm** ([vwap-cross-scanner.tsx](frontend/src/components/vwap-cross-scanner.tsx)):
Thanh ngang phân kỳ — vạch giữa = 0, lệch trái xanh (MA dưới VWAP = "bền"), lệch phải đỏ (MA trên VWAP = "pump"). Độ dài tỉ lệ |%|, **thang đo chung** giữa các dòng (so sánh được bằng mắt).

---

## 10. Mở rộng phổ quét Whale + Wall (bỏ giới hạn TOP)

**Vấn đề:** Whale chỉ quét top-100 theo volume; Wall chỉ top-50 → **lowcap/midcap không bao giờ được quét** dù user set min vol thấp.

**Đã sửa:**
- **Whale** ([WhaleAlertWorker.cs](backend/src/QuantFlowBots.Worker/Workers/WhaleAlertWorker.cs)):
  Bỏ `Take(100)`. Quét **mọi cặp USDT có 24h vol ≥ min thấp nhất của user** (`users.Min(WhaleAlertMinVolume24h)` = sàn quét thật). Cap an toàn 500.
- **Wall** ([OrderBookWallStreamWorker.cs](backend/src/QuantFlowBots.Worker/Workers/OrderBookWallStreamWorker.cs)):
  `MaxSymbols=0` nghĩa là "tất cả" (cap kỹ thuật 500). Verified: WS connect được với 419 stream.
- FE chip "🔝 top-100 USDT pairs" → "🌐 all USDT pairs ≥ min vol".

---

## 11. **Bug whale im lặng tuyệt đối** — direction lưu chuỗi rỗng

**Triệu chứng:** Đã hạ Multiplier xuống 2× mà Telegram **không nhận tín hiệu nào**. Log thấy "top spike 32.5× (HNTUSDT)" — nghĩa là có spike, nhưng `0 alert sent`.

**Bug:** [`UserSettings.WhaleAlertDirection`] trong DB lưu **chuỗi rỗng `""`** (không phải "both"). Code cũ:
```csharp
var userDir = (user.WhaleAlertDirection ?? "both").ToLowerInvariant();
if (userDir != "both" && userDir != direction) continue;  // luôn skip!
```
Toán tử `??` **chỉ thay `null`, không thay chuỗi rỗng** → `userDir=""` → fail cả check "both" lẫn check "buy/sell" → mọi alert bị bỏ qua.

**Đã sửa ở 3 chỗ** (defensive ở mọi layer):
- Backend ([WhaleAlertWorker.cs](backend/src/QuantFlowBots.Worker/Workers/WhaleAlertWorker.cs)): `string.IsNullOrWhiteSpace(direction) ? "both" : direction.Trim().ToLowerInvariant()`.
- Frontend ([settings.tsx](frontend/src/pages/settings.tsx)): đổi `?? 'both'` → `|| 'both'` (bắt cả rỗng).
- DB: `UPDATE user_settings SET "WhaleAlertDirection"='both' WHERE "WhaleAlertDirection"=''` (normalize data hiện tại).

---

## 12. Intrabar chuẩn hóa theo elapsed — chống vừa "im lặng" vừa "spam"

**Vấn đề 1 (under-fire):** Chế độ `intrabar` lấy nến *đang chạy* (volume mới tích một phần) đem so với baseline của các nến *đầy đủ* → để đạt 2× thì nến mới chạy phải vượt cả nến đầy đủ ⇒ tương đương spike 4-8× thực sự ⇒ gần như không bao giờ bắn.

**Vấn đề 2 (over-fire):** Khi áp dụng projection `volume / elapsedFraction` từ 12% trở đi, divisor quá nhỏ → khuếch đại nhiễu → **bùng 240 alert/vòng** lúc đầu.

**Đã sửa** trong WhaleAlertWorker:
- **Chỉ project khi nến trôi ≥ 50%** (frac ≥ 0.5 → khuếch đại tối đa 2× — không còn 8×).
- **Cap an toàn `MaxAlertsPerTick = 25`** — nếu vượt, log `N suppressed (raise multiplier ×)` (gợi ý user).
- **Log tổng kết mỗi vòng** (kể cả 0 alert): `scanned S symbols × I intervals · top spike X× (SYMBOL iv) · N sent` — biến "vì sao chưa bắn?" thành con số quan sát được.

**Kết quả:** 240 → 2 → 1 → 0 alert/vòng (dedupe Redis 30 phút làm settle).

---

## 13. Telegram alert kèm ảnh snapshot (Wall + Whale)

**Yêu cầu:** Mỗi alert phải có ảnh trực quan thay vì chỉ text.

**Render server-side bằng SkiaSharp** (MIT, không CVE — đã đổi từ ImageSharp vì bản đó dính advisory bảo mật).

**Wall** ([WallSnapshotRenderer.cs](backend/src/QuantFlowBots.Worker/Workers/WallSnapshotRenderer.cs) + [WallAlertWorker.cs](backend/src/QuantFlowBots.Worker/Workers/WallAlertWorker.cs)):
- Ladder order-book: asks đỏ trên, vạch mid, bids xanh dưới; mỗi mức = giá + bar tỉ lệ USD + value $.
- Hàng wall: viền vàng + badge **WALL** + bar vàng nổi bật.
- Lấy depth qua `GetDepthAsync(symbol, 20)` (đi qua RateLimitManager nên không lo weight).
- **Render lazy 1 lần / alert**, tái dùng cho mọi user nhận → tiết kiệm CPU + 1 depth call duy nhất.
- Gửi `sendPhoto` (multipart) thay `sendMessage`; caption = text alert cũ; fallback text nếu render lỗi (không vỡ alert).

**Whale** ([WhaleSnapshotRenderer.cs](backend/src/QuantFlowBots.Worker/Workers/WhaleSnapshotRenderer.cs)):
- Bar chart volume các nến gần (xanh tăng / đỏ giảm). Nến đột biến highlight vàng + label `$X.XK`.
- Đường baseline (avg) nét đứt + nhãn.
- Header: `symbol · interval · X.X× avg` (vàng).
- Dùng luôn list `candles` worker đã fetch để dò spike → **0 weight thêm**.

---

## 14. Nâng cấp .NET 8 → .NET 10

**Lý do:** .NET 10 là LTS, GC + hiệu năng tốt hơn (hợp worker streaming nặng).

**Đã làm:**
- 5 project: `net8.0 → net10.0`.
- `global.json` SDK pin `8.0.419 → 10.0.201`.
- Packages 8.0.x → 10.0.0:
  EF Core, Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.AspNetCore.*, Microsoft.Extensions.*, Microsoft.AspNetCore.SignalR.Client.
- `Swashbuckle.AspNetCore 6.8.1 → 7.2.0` (cần cho net10).
- **Bỏ** `Microsoft.AspNetCore.SignalR 1.2.0` (metapackage legacy — SignalR đã nằm trong `Microsoft.AspNetCore.App` từ 3.0).
- Giữ: SkiaSharp, StackExchange.Redis, Hangfire (đều multi-target).
- **Sửa compile** ([RedisBinanceGate.cs](backend/src/QuantFlowBots.Infrastructure/Exchanges/Binance/RedisBinanceGate.cs:112)): `int.TryParse(v, …)` ambiguous vì .NET 10 thêm overload `(ReadOnlySpan<byte>, …)` xung đột với `RedisValue` qua implicit conversion → cast `(string?)v` rõ ràng.

**Verify đã làm:**
- Build sạch, 0 error.
- EF Core 10 + Npgsql 10 init OK với schema hiện có.
- **0 migration được apply** → DB schema/data giữ nguyên 100% (chỉ đọc `qfb.__ef_migrations_history`).
- `/api/auth/login` → 401 đúng; `/api/market/rate-limit` trả data thật từ Worker qua Redis.
- Worker khởi động đầy đủ (RiskGate, BinanceAnnouncement, WallAlert, WhaleAlert, OrderBookWallStream …).

**Backup:**
- Tag `pre-net10-upgrade` trỏ vào commit ngay trước upgrade — rollback: `git reset --hard pre-net10-upgrade`.
- pg_dump tại `_backup/quantflowbots-20260528-100255.sql` (1.4 MB).

---

## 15. Vá lỗ hổng NU1903 sau upgrade .NET 10

.NET 10 SDK flag nghiêm hơn (NuGet audit). Có 2 cảnh báo high-severity:

**a) `Newtonsoft.Json 11.0.1`** (transitive từ JWT 8.1.2)
- Bump `System.IdentityModel.Tokens.Jwt 8.1.2 → 8.5.0` + **direct override `Newtonsoft.Json 13.0.3`** trong Worker.
- Kết quả: hết warning.

**b) `System.Security.Cryptography.Xml`** — vẫn dính advisory ở mọi version hiện có (Microsoft **chưa ship patched build** tại thời điểm này).
- Pin trực tiếp `10.0.5` (bản mới nhất) trong Infrastructure.
- Suppress **chính xác 2 advisory URL** (`GHSA-37gx-xxp4-5rgx`, `GHSA-w3x6-4m5h-cxqf`) qua `<NuGetAuditSuppress>` ở Infrastructure + Worker — **không blanket `NoWarn=NU1903`** để vuln khác vẫn surface.
- Lý do an toàn: cả 2 advisory đều ở **XML-signature parsing**; codebase không deserialise XML untrusted ⇒ không phải đường tấn công.
- **TODO:** khi Microsoft ship patched build, gỡ 2 dòng `NuGetAuditSuppress` (trong Infrastructure.csproj + Worker.csproj) và bump version để verify.

---

## Quy ước & ghi nhớ

- API và Worker là **2 process độc lập, cùng 1 IP** — mọi state chia sẻ (gate, weight, dedupe alert) phải qua Redis. Cache in-memory của Worker không tự đến API.
- DB container **không có volume** → tuyệt đối không `docker compose down -v` / recreate `qfb-timescaledb`. Đã có dump backup ở `_backup/`.
- Live trading bị giới hạn **TESTNET only**; `WITHDRAW` permission bị chặn nhiều lớp.
- API key tài khoản: mã hóa AES-GCM server-side, FE **không bao giờ** thấy key gốc.
