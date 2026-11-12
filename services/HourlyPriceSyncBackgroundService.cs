using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShopifyProductApp.Models;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Gün içinde (varsayılan 08:00–22:00, her saat başı) SADECE değişen ürünlerin satış fiyatını
    /// Exact'tan Shopify'a yazar.
    ///
    /// - Pencere: bir önceki başarılı çalışmanın başlangıcından bu yana değişenler (+5 dk örtüşme).
    ///   Son çalışma zamanı GeneralSettings'te ("HourlyPriceSync:LastSuccessfulRunStartUtc") kalıcı tutulur;
    ///   uygulama yeniden başlasa da arada değişenler kaçmaz. Hiç kayıt yoksa son 24 saat taranır.
    ///   Böylece 08:30'da değişen ürün 09:00'da yazılır; tekrar değişmedikçe 10:00'da listeye girmez.
    /// - Değişen ürün tespiti: Items.Modified VEYA SalesItemPrices.Modified (Account eq null — müşteriye
    ///   özel fiyatlar hariç).
    /// - Yazılan fiyat: yalnızca Items.StandardSalesPrice (gece senkronu ile aynı fiyat). Müşteri/katalog
    ///   fiyatlarına dokunulmaz.
    /// - Referans: Shopify'daki mevcut variant fiyatı. Aynıysa API çağrısı yapılmaz (Unchanged);
    ///   Boş/negatif fiyat yazılmaz (0 fiyat yazılır). Sonuçlar PriceSyncLogs tablosuna yazılır.
    ///
    /// Ayarlar (appsettings, opsiyonel):
    ///   App:BackgroundServices:HourlyPriceSyncStart  (varsayılan 08:00)
    ///   App:BackgroundServices:HourlyPriceSyncEnd    (varsayılan 22:00, dahil)
    /// </summary>
    public class HourlyPriceSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PriceSyncLogService _priceSyncLogService;
        private readonly ManualPriceSyncRunner _manualRunner;
        private readonly IConfiguration _configuration;
        private readonly ILogger<HourlyPriceSyncBackgroundService> _logger;

        private const int EXACT_PAGE_SIZE = 60;
        private const int ID_FILTER_CHUNK = 15;
        // Bu sayının üzerinde değişen ürün varsa Shopify'ın tam eşleştirme indeksi bir kez kurulur;
        // altındaysa her ürün için hedefli (hafif) arama yapılır.
        private const int FULL_INDEX_THRESHOLD = 25;
        private static readonly TimeSpan WINDOW_OVERLAP = TimeSpan.FromMinutes(5);

        // Bir önceki başarılı çalışmanın başlangıç zamanı (UTC). GeneralSettings'te kalıcı tutulur;
        // uygulama yeniden başlasa da pencere kaldığı yerden devam eder.
        private const string LAST_RUN_SETTING_KEY = "HourlyPriceSync:LastSuccessfulRunStartUtc";
        private const string SETTINGS_CATEGORY = "PriceSync";
        // DB'de hiç kayıt yoksa (ilk kurulum) veya okunamazsa geriye bakılacak süre.
        // 24 saat: bir önceki günün 22:00 sonrası + gece boşluğu kesin kapsanır.
        private static readonly TimeSpan FIRST_RUN_LOOKBACK = TimeSpan.FromHours(24);
        private DateTime? _lastSuccessfulRunStartUtc;

        public HourlyPriceSyncBackgroundService(
            IServiceProvider serviceProvider,
            PriceSyncLogService priceSyncLogService,
            ManualPriceSyncRunner manualRunner,
            IConfiguration configuration,
            ILogger<HourlyPriceSyncBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _priceSyncLogService = priceSyncLogService;
            _manualRunner = manualRunner;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var (start, end) = GetWindow();
            _logger.LogInformation("⏱️ Hourly Price Sync Service başlatıldı - {Start}-{End} arası her saat başı değişen ürünlerin satış fiyatı senkronlanacak",
                start.ToString(@"hh\:mm"), end.ToString(@"hh\:mm"));

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            _lastSuccessfulRunStartUtc = await LoadLastRunAsync();
            _logger.LogInformation("📌 Saatlik fiyat senkronu son başarılı çalışma: {LastRun}",
                _lastSuccessfulRunStartUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "kayıt yok (ilk çalışmada son 24 saat taranacak)");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var nextRun = GetNextRunTime(now, start, end);
                    _logger.LogInformation("⏰ Sonraki saatlik fiyat kontrolü: {NextRun}", nextRun.ToString("dd.MM.yyyy HH:mm"));

                    await Task.Delay(nextRun - now, stoppingToken);
                    if (stoppingToken.IsCancellationRequested) break;

                    if (_manualRunner.GetStatus().IsRunning)
                    {
                        _logger.LogWarning("⚠️ Tam fiyat senkronu çalışıyor, bu saatlik kontrol atlanıyor (pencere korunuyor, sonraki çalışmada kapsanacak)");
                        continue;
                    }

                    var runStartUtc = DateTime.UtcNow;
                    var sinceUtc = (_lastSuccessfulRunStartUtc ?? runStartUtc - FIRST_RUN_LOOKBACK) - WINDOW_OVERLAP;

                    var ok = await RunOnceAsync(sinceUtc, stoppingToken);
                    if (ok)
                    {
                        // Önce bellekte, sonra DB'de ilerlet. DB yazılamazsa bellekteki değer kullanılmaya
                        // devam eder; yeniden başlatmada DB'deki eski değerden (daha geniş pencere) başlanır.
                        _lastSuccessfulRunStartUtc = runStartUtc;
                        await SaveLastRunAsync(runStartUtc);
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Pencere ilerletilmez: bir sonraki saatte aynı aralık tekrar kapsanır
                    _logger.LogError(ex, "❌ Saatlik fiyat senkronu hatası: {Error}", ex.Message);
                }
            }
        }

        /// <summary>
        /// Tek bir saatlik çalışma. Pencerenin ilerletilebilmesi için başarılıysa true döner.
        /// (Tekil ürün hataları başarısızlık sayılmaz; Exact'tan veri alınamaması sayılır.)
        /// </summary>
        private async Task<bool> RunOnceAsync(DateTime sinceUtc, CancellationToken ct)
        {
            var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
            using var scope = _serviceProvider.CreateScope();
            var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
            var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

            var token = await exactService.GetValidToken();
            if (token == null || string.IsNullOrEmpty(token.access_token))
            {
                _logger.LogWarning("⚠️ Geçerli Exact token yok, saatlik fiyat kontrolü atlandı");
                return false;
            }

            _logger.LogInformation("💶 Saatlik fiyat kontrolü başlıyor - {Since} sonrası değişen ürünler", since.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));

            // 1) Items.Modified ile değişen webshop ürünleri (StandardSalesPrice dahil gelir)
            var sinceStr = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss");
            var changedItems = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

            int skip = 0;
            while (!ct.IsCancellationRequested)
            {
                var page = await exactService.GetWebshopItemsPageAsync(skip, EXACT_PAGE_SIZE, $"Modified gt datetime'{sinceStr}'");
                if (page == null)
                {
                    _logger.LogWarning("⚠️ Exact'tan değişen ürünler alınamadı, pencere ilerletilmeyecek");
                    return false;
                }

                foreach (var item in page)
                {
                    var id = item.GetValueOrDefault("ID")?.ToString();
                    if (!string.IsNullOrWhiteSpace(id)) changedItems[id] = item;
                }

                if (page.Count < EXACT_PAGE_SIZE) break;
                skip += page.Count;
                await Task.Delay(500, ct);
            }

            var itemModifiedCount = changedItems.Count;
            var itemModifiedIds = new HashSet<string>(changedItems.Keys, StringComparer.OrdinalIgnoreCase);

            // 2) Fiyat satırı değişen ama Item kaydı değişmemiş olabilecek ürünler
            var priceChangedIds = await exactService.GetItemIdsWithChangedSalesPricesSinceAsync(since);
            if (priceChangedIds == null)
            {
                _logger.LogWarning("⚠️ Exact'tan değişen fiyat satırları alınamadı, pencere ilerletilmeyecek");
                return false;
            }

            var missingIds = priceChangedIds
                .Where(g => !changedItems.ContainsKey(g.ToString()))
                .ToList();

            foreach (var chunk in missingIds.Chunk(ID_FILTER_CHUNK))
            {
                var idFilter = string.Join(" or ", chunk.Select(g => $"ID eq guid'{g}'"));
                var page = await exactService.GetWebshopItemsPageAsync(0, EXACT_PAGE_SIZE, idFilter);
                if (page == null)
                {
                    _logger.LogWarning("⚠️ Fiyatı değişen ürünlerin detayları alınamadı, pencere ilerletilmeyecek");
                    return false;
                }

                foreach (var item in page)
                {
                    var id = item.GetValueOrDefault("ID")?.ToString();
                    if (!string.IsNullOrWhiteSpace(id)) changedItems[id] = item;
                }
                await Task.Delay(300, ct);
            }

            if (changedItems.Count == 0)
            {
                _logger.LogInformation("ℹ️ Saatlik fiyat kontrolü: değişen webshop ürünü yok");
                return true;
            }

            _logger.LogInformation("🔎 {Count} değişen webshop ürünü bulundu (Item: {ItemCount}, fiyat satırı: {PriceCount}), Shopify fiyatlarıyla karşılaştırılıyor...",
                changedItems.Count, itemModifiedCount, priceChangedIds.Count);

            // 3) Shopify ile karşılaştır, sadece fiyatı farklı olanları yaz
            var items = changedItems.Values.ToList();
            var totals = new PriceBatchUpdateResult();

            if (items.Count > FULL_INDEX_THRESHOLD)
            {
                var (byExactId, bySku, byBarcode) = await shopifyService.BuildShopifyMatchIndexAsync();
                if (byExactId.Count == 0 && bySku.Count == 0 && byBarcode.Count == 0)
                {
                    _logger.LogWarning("⚠️ Shopify eşleştirme indeksi boş, pencere ilerletilmeyecek");
                    return false;
                }

                foreach (var batch in items.Chunk(50))
                {
                    var r = await shopifyService.UpdateMultiplePricesBatchAsync(batch.ToList(), byExactId, bySku, byBarcode);
                    Accumulate(totals, r);
                    AnnotateLogEntries(r.LogEntries, itemModifiedIds, priceChangedIds);
                    await _priceSyncLogService.SaveAsync(r.LogEntries);
                }
            }
            else
            {
                foreach (var item in items)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var (byExactId, bySku, byBarcode) = await shopifyService.BuildSingleItemMatchIndexAsync(item);
                        var r = await shopifyService.UpdateMultiplePricesBatchAsync(
                            new List<Dictionary<string, object>> { item }, byExactId, bySku, byBarcode);
                        Accumulate(totals, r);
                        AnnotateLogEntries(r.LogEntries, itemModifiedIds, priceChangedIds);
                        await _priceSyncLogService.SaveAsync(r.LogEntries);
                    }
                    catch (Exception ex)
                    {
                        totals.ErrorCount++;
                        _logger.LogError(ex, "❌ {Code} saatlik fiyat güncellemesinde hata", item.GetValueOrDefault("Code"));
                    }
                }
            }

            _logger.LogInformation("✅ Saatlik fiyat kontrolü bitti - Kontrol edilen: {Checked}, Güncellenen: {Updated}, Değişmeyen: {Unchanged}, Atlanan(0 fiyat): {Skipped}, Hatalı: {Error}",
                items.Count, totals.SuccessCount, totals.UnchangedCount, totals.SkippedZeroPriceCount, totals.ErrorCount);

            return true;
        }

        /// <summary>
        /// PriceSyncLogs'ta yanlış anlaşılmasın diye saatlik kontrolün kayıtlarına sebep notu ekler.
        /// Özellikle "değişmeyen" kayıtlar: ürün neden kontrole girdi, neden Shopify'a yazılmadı.
        /// (Not ErrorMessage kolonuna yazılır; dashboard durumu Success alanından okunduğu için
        /// başarılı kayıtlar hatalı görünmez.)
        /// </summary>
        private static void AnnotateLogEntries(List<PriceSyncLog> entries, HashSet<string> itemModifiedIds, HashSet<Guid> priceChangedIds)
        {
            foreach (var e in entries)
            {
                bool byPriceRow = Guid.TryParse(e.ExactItemId, out var g) && priceChangedIds.Contains(g);
                bool byItem = e.ExactItemId != null && itemModifiedIds.Contains(e.ExactItemId);

                var trigger = byPriceRow && byItem ? "ürün kaydı ve fiyat satırı değişti"
                            : byPriceRow ? "Exact'ta fiyat satırı (SalesItemPrices) değişti"
                            : "Exact'ta ürün kaydı (Items.Modified) değişti";

                bool unchanged = e.Success && e.ShopifyVariantId != null &&
                                 e.OldPrice.HasValue && e.OldPrice.Value == e.NewPrice;

                string note;
                if (unchanged)
                {
                    note = byPriceRow
                        ? $"[Saatlik kontrol] Fiyat DEĞİŞMEDİ, Shopify'a yazılmadı. Sebep: {trigger}, ancak ürünün satış fiyatı (StandardSalesPrice) Shopify'daki ile aynı ({e.NewPrice:0.00}). Değişen muhtemelen başka bir fiyat satırı (ör. fiyat listesi / miktar kademesi / tarih)."
                        : $"[Saatlik kontrol] Fiyat DEĞİŞMEDİ, Shopify'a yazılmadı. Sebep: {trigger} - fiyat dışı bir alan (ad, açıklama, stok bilgisi vb.) güncellenmiş; satış fiyatı Shopify'daki ile aynı ({e.NewPrice:0.00}).";
                }
                else if (e.Success)
                {
                    note = $"[Saatlik kontrol] Fiyat güncellendi ({e.OldPrice:0.00} -> {e.NewPrice:0.00}). Tetikleyici: {trigger}.";
                }
                else
                {
                    note = $"[Saatlik kontrol] {e.ErrorMessage} (Tetikleyici: {trigger})";
                }

                e.ErrorMessage = note;
            }
        }

        private async Task<DateTime?> LoadLastRunAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var raw = await settings.GetSettingAsync(LAST_RUN_SETTING_KEY);
                if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    return parsed.ToUniversalTime();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Saatlik fiyat senkronu son çalışma zamanı DB'den okunamadı, son 24 saat taranacak");
            }
            return null;
        }

        private async Task SaveLastRunAsync(DateTime runStartUtc)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                await settings.SetSettingAsync(LAST_RUN_SETTING_KEY, runStartUtc.ToString("o"),
                    "Saatlik fiyat senkronunun son başarılı çalışma başlangıcı (UTC). Bir sonraki çalışma bu andan itibaren değişenlere bakar.",
                    SETTINGS_CATEGORY);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Saatlik fiyat senkronu son çalışma zamanı DB'ye yazılamadı (bellekteki değerle devam ediliyor)");
            }
        }

        private static void Accumulate(PriceBatchUpdateResult total, PriceBatchUpdateResult r)
        {
            total.SuccessCount += r.SuccessCount;
            total.UnchangedCount += r.UnchangedCount;
            total.SkippedZeroPriceCount += r.SkippedZeroPriceCount;
            total.ErrorCount += r.ErrorCount;
        }

        private (TimeSpan Start, TimeSpan End) GetWindow()
        {
            var start = TimeSpan.TryParse(_configuration["App:BackgroundServices:HourlyPriceSyncStart"], out var s) ? s : new TimeSpan(8, 0, 0);
            var end = TimeSpan.TryParse(_configuration["App:BackgroundServices:HourlyPriceSyncEnd"], out var e) ? e : new TimeSpan(22, 0, 0);
            return (start, end);
        }

        /// <summary>
        /// Bir sonraki saat başını bulur; pencere dışındaysa ertesi pencere başlangıcına atlar.
        /// </summary>
        private static DateTime GetNextRunTime(DateTime now, TimeSpan start, TimeSpan end)
        {
            var candidate = now.Date.AddHours(now.Hour + 1); // bir sonraki saat başı
            if (candidate.TimeOfDay < start)
                return candidate.Date.Add(start);
            if (candidate.TimeOfDay > end)
                return candidate.Date.AddDays(1).Add(start);
            return candidate;
        }
    }
}
