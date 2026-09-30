namespace ShopifyProductApp.Services.ExactPriceList;

/// <summary>
/// Exact'taki müşteriye özel fiyatları Shopify B2B kataloğuna aktarır.
///
/// Her zaman önce <b>dry-run</b> çalıştırılır: ne yazılacağı raporlanır, hiçbir şey yazılmaz.
/// Gerçek yazım yalnızca açıkça <c>applyChanges: true</c> verildiğinde yapılır.
/// </summary>
public class CustomerPriceSyncService
{
    private readonly ExactPriceListDiscoveryService _discoveryService;
    private readonly ShopifyB2BCatalogService _catalogService;
    private readonly ILogger<CustomerPriceSyncService> _logger;

    public CustomerPriceSyncService(
        ExactPriceListDiscoveryService discoveryService,
        ShopifyB2BCatalogService catalogService,
        ILogger<CustomerPriceSyncService> logger)
    {
        _discoveryService = discoveryService;
        _catalogService = catalogService;
        _logger = logger;
    }

    /// <summary>
    /// Tek bir müşteri için planı hazırlar ve isteğe bağlı olarak uygular.
    /// </summary>
    /// <param name="accountId">Exact Account GUID'i.</param>
    /// <param name="applyChanges">false ise hiçbir şey yazılmaz (dry-run).</param>
    public async Task<CustomerSyncPlan> BuildPlanAsync(
        string accountId,
        bool applyChanges,
        CancellationToken cancellationToken = default)
    {
        var plan = new CustomerSyncPlan { AccountId = accountId, Applied = false };

        // 1) Exact'tan bu müşterinin fiyatlarını al
        var report = await _discoveryService.RunDiscoveryAsync(cancellationToken);

        var customer = report.Customers
            .FirstOrDefault(c => string.Equals(c.AccountId, accountId, StringComparison.OrdinalIgnoreCase));

        if (customer == null)
        {
            plan.Blocked = true;
            plan.BlockReason = $"'{accountId}' için Exact'ta özel fiyat bulunamadı";
            return plan;
        }

        plan.AccountName = customer.AccountName;
        plan.VatNumber = customer.VatNumber;
        plan.ChamberOfCommerce = customer.ChamberOfCommerce;

        var now = DateTime.UtcNow;

        // 2) Yalnızca bugün geçerli ve adet kademesiz satırlar aktarılabilir
        var eligible = customer.Prices
            .Where(p => p.IsActiveOn(now))
            .Where(p => p.Quantity <= 1)
            .Where(p => !string.IsNullOrWhiteSpace(p.ItemCode))
            .ToList();

        plan.SkippedExpired = customer.Prices.Count(p => !p.IsActiveOn(now));
        plan.SkippedQuantityTiered = customer.Prices.Count(p => p.IsActiveOn(now) && p.Quantity > 1);
        plan.EligibleCount = eligible.Count;

        if (eligible.Count == 0)
        {
            plan.Blocked = true;
            plan.BlockReason = "Aktarılabilir aktif fiyat satırı yok";
            return plan;
        }

        // 3) SKU -> Shopify variant eşlemesi
        var variantMap = await _catalogService.ResolveVariantIdsBySkuAsync(
            eligible.Select(p => p.ItemCode), cancellationToken);

        foreach (var price in eligible)
        {
            if (variantMap.TryGetValue(price.ItemCode, out var variantId))
            {
                plan.Prices.Add(new FixedPriceInput
                {
                    VariantId = variantId,
                    Sku = price.ItemCode,
                    Amount = price.Price,
                    CurrencyCode = string.IsNullOrWhiteSpace(price.Currency) ? "EUR" : price.Currency
                });
            }
            else
            {
                plan.UnmatchedSkus.Add(price.ItemCode);
            }
        }

        plan.MatchedCount = plan.Prices.Count;

        // 4) Shopify B2B company'sini bul
        var company = await _catalogService.FindCompanyByNameAsync(customer.AccountName, cancellationToken);

        if (company == null)
        {
            plan.Blocked = true;
            plan.BlockReason =
                $"Shopify'da '{customer.AccountName}' adında B2B company bulunamadı. " +
                "Company ve location Shopify tarafında oluşturulmalı.";
            return plan;
        }

        plan.CompanyId = company.Id;
        plan.CompanyName = company.Name;

        if (company.LocationIds.Count == 0)
        {
            plan.Blocked = true;
            plan.BlockReason = $"'{company.Name}' company'sinin location'ı yok; katalog bağlanamaz.";
            return plan;
        }

        plan.CompanyLocationId = company.LocationIds[0];

        // 5) Dry-run burada biter
        if (!applyChanges)
        {
            _logger.LogInformation(
                "🧪 DRY-RUN: {Customer} için {Count} sabit fiyat yazılacaktı (yazılmadı)",
                customer.AccountName, plan.MatchedCount);
            return plan;
        }

        // 6) Gerçek yazım
        _logger.LogWarning("✍️ CANLI YAZIM: {Customer} için katalog oluşturuluyor...", customer.AccountName);

        var catalogTitle = $"Exact – {customer.AccountName}";
        var catalog = await _catalogService.CreateCatalogWithPriceListAsync(
            catalogTitle, plan.CompanyLocationId, "EUR", cancellationToken);

        if (catalog == null || string.IsNullOrWhiteSpace(catalog.PriceListId))
        {
            plan.Blocked = true;
            plan.BlockReason = "Katalog veya price list oluşturulamadı (log'a bakın)";
            return plan;
        }

        plan.CatalogId = catalog.CatalogId;
        plan.PriceListId = catalog.PriceListId;

        var writeResult = await _catalogService.AddFixedPricesAsync(
            catalog.PriceListId, plan.Prices, cancellationToken);

        plan.Applied = true;
        plan.WrittenCount = writeResult.AddedCount;
        plan.FailedCount = writeResult.FailedCount;
        plan.WriteErrors = writeResult.Errors;

        _logger.LogInformation("✅ {Customer}: {Written} fiyat yazıldı, {Failed} başarısız",
            customer.AccountName, writeResult.AddedCount, writeResult.FailedCount);

        return plan;
    }
}

/// <summary>
/// Tek müşteri için hazırlanan aktarım planı ve (uygulandıysa) sonucu.
/// </summary>
public class CustomerSyncPlan
{
    public string AccountId { get; set; }
    public string AccountName { get; set; }

    /// <summary>Exact VAT numarası — B2B uygunluğunun göstergesi.</summary>
    public string VatNumber { get; set; }

    /// <summary>Ticaret sicil (KvK) numarası.</summary>
    public string ChamberOfCommerce { get; set; }

    /// <summary>true ise gerçekten yazıldı; false ise dry-run.</summary>
    public bool Applied { get; set; }

    /// <summary>true ise plan uygulanamaz; sebebi BlockReason'da.</summary>
    public bool Blocked { get; set; }
    public string BlockReason { get; set; }

    public string CompanyId { get; set; }
    public string CompanyName { get; set; }
    public string CompanyLocationId { get; set; }
    public string CatalogId { get; set; }
    public string PriceListId { get; set; }

    /// <summary>Aktarılabilir (aktif + adet kademesiz) satır sayısı.</summary>
    public int EligibleCount { get; set; }

    /// <summary>Shopify'da variant'ı bulunan satır sayısı.</summary>
    public int MatchedCount { get; set; }

    public int SkippedExpired { get; set; }
    public int SkippedQuantityTiered { get; set; }

    public int WrittenCount { get; set; }
    public int FailedCount { get; set; }

    public List<string> UnmatchedSkus { get; set; } = new();
    public List<string> WriteErrors { get; set; } = new();
    public List<FixedPriceInput> Prices { get; set; } = new();
}
