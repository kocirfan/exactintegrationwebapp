using System.Text.Json;
using System.Text.RegularExpressions;
using ExactOnline.Models;

namespace ShopifyProductApp.Services;

public class CustomerTaxTagFixChange
{
    public long ShopifyId { get; set; }
    public string Email { get; set; }
    public string ExactCode { get; set; }
    public string ExactStatus { get; set; }
    public string VatNumber { get; set; }
    public string CountryCode { get; set; }
    public bool TaxExemptBefore { get; set; }
    public bool TaxExemptAfter { get; set; }
    public string TagsBefore { get; set; }
    public string TagsAfter { get; set; }
    public bool Applied { get; set; }
    public string Error { get; set; }
}

public class CustomerTaxTagFixStatus
{
    public bool IsRunning { get; set; }
    public bool DryRun { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int ExactCustomers { get; set; }
    public int ShopifyCustomers { get; set; }
    public int Processed { get; set; }
    /// <summary>Kurala göre değişmesi gereken müşteri sayısı (dry-run'da da dolar).</summary>
    public int NeedsChange { get; set; }
    /// <summary>Shopify'a gerçekten yazılan değişiklik sayısı (dry-run'da 0).</summary>
    public int Applied { get; set; }
    public int Unchanged { get; set; }
    public int ExactNotFound { get; set; }
    public int Errors { get; set; }
    public string CurrentStep { get; set; }
    public string LastError { get; set; }
    public string LogFile { get; set; }
    public List<CustomerTaxTagFixChange> Changes { get; set; } = new();
    public List<string> NotFoundEmails { get; set; } = new();

    public CustomerTaxTagFixStatus Clone(bool includeDetails)
    {
        var clone = (CustomerTaxTagFixStatus)MemberwiseClone();
        clone.Changes = includeDetails ? Changes.ToList() : new List<CustomerTaxTagFixChange>();
        clone.NotFoundEmails = includeDetails ? NotFoundEmails.ToList() : new List<string>();
        return clone;
    }
}

/// <summary>
/// Shopify'daki TÜM müşteriler için <c>tax_exempt</c> ve <c>betaling-factuur</c> tag'ini
/// <see cref="VatTaxRules"/> / <see cref="CustomerTagRules"/>'a göre tek seferlik düzeltir.
///
/// <list type="bullet">
///   <item>Müşteri oluşturmaz, mail göndermez; isim/adres/not/metafield gibi diğer alanlara dokunmaz.</item>
///   <item>Tag listesinde yalnızca <c>betaling-factuur</c> eklenir/kaldırılır, diğer tag'ler korunur.</item>
///   <item>Exact eşleşmesi: önce Shopify notundaki "Exact Online ID: {guid}", sonra e-posta.
///         Eşleşmeyen Shopify müşterileri atlanır (ExactNotFound).</item>
///   <item><c>dryRun=true</c> ile hiçbir şey yazılmadan değişiklik listesi üretilir.</item>
///   <item>Her koşuda önce/sonra değerleri <c>logs/customer-tax-tag-fix-*.json</c> dosyasına yazılır.</item>
/// </list>
/// </summary>
public class CustomerTaxTagFixRunner
{
    private static readonly Regex ExactIdRegex = new(@"Exact Online ID:\s*([0-9a-fA-F-]{36})", RegexOptions.Compiled);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CustomerTaxTagFixRunner> _logger;
    private readonly object _lock = new();
    private CustomerTaxTagFixStatus _status = new() { CurrentStep = "Henüz çalıştırılmadı" };

    public CustomerTaxTagFixRunner(IServiceProvider serviceProvider, ILogger<CustomerTaxTagFixRunner> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public CustomerTaxTagFixStatus GetStatus(bool includeDetails = false)
    {
        lock (_lock)
        {
            return _status.Clone(includeDetails);
        }
    }

    /// <summary>Düzeltmeyi arka planda başlatır. Zaten çalışıyorsa false döner.</summary>
    public bool TryStart(bool dryRun)
    {
        lock (_lock)
        {
            if (_status.IsRunning)
                return false;

            _status = new CustomerTaxTagFixStatus
            {
                IsRunning = true,
                DryRun = dryRun,
                StartedAt = DateTime.Now,
                CurrentStep = "Başlatılıyor"
            };
        }

        _ = Task.Run(() => RunAsync(dryRun));
        return true;
    }

    private async Task RunAsync(bool dryRun)
    {
        try
        {
            _logger.LogInformation("🧾 Vergi/tag düzeltmesi başladı (dryRun: {DryRun})", dryRun);

            using var scope = _serviceProvider.CreateScope();
            var exactCrud = scope.ServiceProvider.GetRequiredService<ExactCustomerCrud>();
            var shopifyCrud = scope.ServiceProvider.GetRequiredService<ShopifyCustomerCrud>();

            // 1. Exact hesapları (hafif) + indeksler
            SetStep("Exact hesapları çekiliyor");
            var exactAccounts = await exactCrud.GetAllCustomersLiteAsync();

            var byId = new Dictionary<Guid, Account>();
            var byEmail = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in exactAccounts)
            {
                byId[account.ID] = account;

                if (string.IsNullOrWhiteSpace(account.Email))
                    continue;

                var key = account.Email.Trim();
                // Aynı e-posta birden fazla hesapta varsa Status=C olanı tercih et
                if (!byEmail.TryGetValue(key, out var existing) || (existing.Status != "C" && account.Status == "C"))
                    byEmail[key] = account;
            }

            lock (_lock) { _status.ExactCustomers = exactAccounts.Count; }

            // 2. Shopify müşterileri
            SetStep("Shopify müşterileri çekiliyor");
            var shopifyCustomers = await shopifyCrud.GetAllCustomerSummariesAsync();
            lock (_lock) { _status.ShopifyCustomers = shopifyCustomers.Count; }

            // 3. Karşılaştır ve (dryRun değilse) uygula
            foreach (var sc in shopifyCustomers)
            {
                SetStep($"İşleniyor: {_status.Processed + 1}/{shopifyCustomers.Count}");

                Account account = null;
                var match = ExactIdRegex.Match(sc.Note ?? "");
                if (match.Success && Guid.TryParse(match.Groups[1].Value, out var exactId))
                    byId.TryGetValue(exactId, out account);

                if (account == null && !string.IsNullOrWhiteSpace(sc.Email))
                    byEmail.TryGetValue(sc.Email.Trim(), out account);

                if (account == null)
                {
                    lock (_lock)
                    {
                        _status.Processed++;
                        _status.ExactNotFound++;
                        _status.NotFoundEmails.Add(sc.Email ?? $"id:{sc.Id}");
                    }
                    continue;
                }

                var countryCode = ShopifyCustomerCrud.ConvertToCountryCode(account.Country, account.CountryName);
                var expectedTaxExempt = VatTaxRules.ShouldBeTaxExempt(account.VATNumber, countryCode);

                var currentTags = SplitTags(sc.Tags);
                var newTags = currentTags
                    .Where(t => !t.Equals(CustomerTagRules.InvoicePaymentTag, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (CustomerTagRules.HasVatNumber(account.VATNumber))
                    newTags.Add(CustomerTagRules.InvoicePaymentTag);

                var tagsChanged = !new HashSet<string>(currentTags, StringComparer.OrdinalIgnoreCase).SetEquals(newTags);
                var taxChanged = expectedTaxExempt != sc.TaxExempt;

                if (!taxChanged && !tagsChanged)
                {
                    lock (_lock) { _status.Processed++; _status.Unchanged++; }
                    continue;
                }

                var change = new CustomerTaxTagFixChange
                {
                    ShopifyId = sc.Id,
                    Email = sc.Email,
                    ExactCode = account.Code?.Trim(),
                    ExactStatus = account.Status,
                    VatNumber = account.VATNumber,
                    CountryCode = countryCode,
                    TaxExemptBefore = sc.TaxExempt,
                    TaxExemptAfter = expectedTaxExempt,
                    TagsBefore = string.Join(",", currentTags),
                    TagsAfter = string.Join(",", tagsChanged ? newTags : currentTags)
                };

                if (!dryRun)
                {
                    var (success, error) = await shopifyCrud.UpdateTaxExemptAndTagsAsync(sc.Id, expectedTaxExempt, change.TagsAfter);
                    change.Applied = success;
                    change.Error = error;
                    if (!success)
                        _logger.LogWarning("❌ Vergi/tag güncellenemedi ({Email} / {Id}): {Error}", sc.Email, sc.Id, error);

                    await Task.Delay(300); // Shopify REST rate limit
                }

                lock (_lock)
                {
                    _status.Processed++;
                    _status.NeedsChange++;
                    if (!dryRun)
                    {
                        if (change.Applied) _status.Applied++;
                        else _status.Errors++;
                    }
                    _status.Changes.Add(change);
                }
            }

            // 4. Önce/sonra kaydını dosyaya yaz
            Directory.CreateDirectory("logs");
            var logFile = Path.Combine("logs", $"customer-tax-tag-fix-{DateTime.Now:yyyyMMdd-HHmmss}{(dryRun ? "-dryrun" : "")}.json");
            lock (_lock) { _status.LogFile = logFile; }
            await File.WriteAllTextAsync(logFile, JsonSerializer.Serialize(GetStatus(includeDetails: true),
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            SetStep("Tamamlandı");
            var s = GetStatus();
            _logger.LogInformation("🎉 Vergi/tag düzeltmesi tamamlandı (dryRun: {DryRun}) - Shopify: {Shopify}, Exact: {Exact}, Değişecek: {Needs}, Uygulanan: {Applied}, Değişmeyen: {Unchanged}, Exact'ta yok: {NotFound}, Hata: {Errors}",
                dryRun, s.ShopifyCustomers, s.ExactCustomers, s.NeedsChange, s.Applied, s.Unchanged, s.ExactNotFound, s.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Vergi/tag düzeltmesi hatası: {Error}", ex.Message);
            lock (_lock)
            {
                _status.LastError = ex.Message;
                _status.CurrentStep = "Hata ile durdu";
            }
        }
        finally
        {
            lock (_lock)
            {
                _status.IsRunning = false;
                _status.FinishedAt = DateTime.Now;
            }
        }
    }

    private static List<string> SplitTags(string tags)
        => (tags ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private void SetStep(string step)
    {
        lock (_lock) { _status.CurrentStep = step; }
    }
}
