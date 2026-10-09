namespace ShopifyProductApp.Services;

/// <summary>
/// bol.com siparişlerini Exact'ta tek cariye (varsayılan 1300928 bol.com) yönlendirir.
///
/// Müşteri onayı (2026-10): Ordered by / Invoice to / Deliver to = bol.com hesabı; yeni cari açılmaz.
/// Fatura adresi bol.com hesabının kendi adresidir. Her siparişin alıcısı bol.com hesabı altında
/// ilgili kişi (contact) olarak, teslimat adresi de bu kişiye bağlı Type=4 adres olarak tutulur.
/// Aynı alıcı + aynı adres tekrar gelirse mevcut kayıtlar kullanılır. Teslimat adresleri hiçbir
/// zaman ana adres (Main) yapılmaz.
///
/// Ayarlar (appsettings, opsiyonel):
///   ExactOnline:BolSingleAccountEnabled  (varsayılan true; false ise eski akış)
///   ExactOnline:BolAccountCode           (varsayılan "1300928")
/// </summary>
public class BolOrderRouter
{
    private const string DefaultBolAccountCode = "1300928";

    // Hesap GUID'i değişmez; uygulama ömrü boyunca bir kez çözülür.
    private static Guid? _cachedAccountId;
    private static string _cachedForCode;

    private readonly ExactService _exactService;
    private readonly ExactAddressCrud _exactAddressCrud;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BolOrderRouter> _logger;

    public BolOrderRouter(ExactService exactService, ExactAddressCrud exactAddressCrud,
        IConfiguration configuration, ILogger<BolOrderRouter> logger)
    {
        _exactService = exactService;
        _exactAddressCrud = exactAddressCrud;
        _configuration = configuration;
        _logger = logger;
    }

    private bool Enabled =>
        !bool.TryParse(_configuration["ExactOnline:BolSingleAccountEnabled"], out var enabled) || enabled;

    private string AccountCode =>
        string.IsNullOrWhiteSpace(_configuration["ExactOnline:BolAccountCode"])
            ? DefaultBolAccountCode
            : _configuration["ExactOnline:BolAccountCode"].Trim();

    /// <summary>Sipariş bol.com'dan mı geldi? Kaynak "bol" veya sipariş adı "BOL" ile başlıyorsa evet.</summary>
    public bool IsBolOrder(ShopifyOrder order)
    {
        if (!Enabled || order == null) return false;

        if (string.Equals(order.SourceName?.Trim(), "bol", StringComparison.OrdinalIgnoreCase))
            return true;

        return order.Name != null && order.Name.StartsWith("BOL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>bol.com cari hesabının GUID'i; bulunamazsa / müşteri değilse null.</summary>
    public async Task<Guid?> GetBolAccountIdAsync()
    {
        var code = AccountCode;
        if (_cachedAccountId.HasValue && _cachedForCode == code)
            return _cachedAccountId;

        var id = await _exactService.GetCustomerAccountIdByCodeAsync(code);
        if (id.HasValue)
        {
            _cachedAccountId = id;
            _cachedForCode = code;
        }
        return id;
    }

    /// <summary>
    /// Alıcıyı bol.com hesabı altında ilgili kişi olarak bulur/oluşturur, ardından bu kişiye bağlı
    /// teslimat adresini bulur/oluşturur (Main=false). Hata durumunda null'lar döner; sipariş yine yazılır.
    /// </summary>
    public async Task<(Guid? deliveryAddressId, Guid? contactId)> ResolveDeliveryAsync(ShopifyAddress delivery, Guid bolAccountId)
    {
        if (delivery == null)
        {
            _logger.LogWarning("📦 Bol siparişinde teslimat adresi yok; hesabın varsayılan adresi kullanılacak.");
            return (null, null);
        }

        Guid? contactId = null;
        var firstName = delivery.FirstName?.Trim();
        var lastName = delivery.LastName?.Trim();

        if (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName))
        {
            try
            {
                // Bol e-postaları anonim ve sipariş başına farklı; eşleştirme yalnızca isimle yapılır.
                contactId = await _exactService.GetOrCreateContactAsync(
                    bolAccountId, firstName ?? "", string.IsNullOrWhiteSpace(lastName) ? firstName : lastName,
                    filterByLastName: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚠️ Bol alıcısı ilgili kişi olarak hazırlanamadı: {First} {Last}", firstName, lastName);
            }
        }
        else
        {
            _logger.LogWarning("⚠️ Bol teslimat adresinde isim yok, ilgili kişi bağlanmadı.");
        }

        try
        {
            var existing = await _exactAddressCrud.FindDeliveryAddressAsync(
                bolAccountId, delivery.Address1, delivery.Zip, contactId);

            if (existing != null)
            {
                _logger.LogInformation("   ✅ Bol: mevcut teslimat adresi kullanılıyor ({AddressId}) {Address}",
                    existing.Id, existing.FullAddress);
                return (existing.Id, contactId);
            }

            var newAddress = new ExactAddress
            {
                AccountId = bolAccountId,
                Type = 4, // Teslimat adresi
                AddressLine1 = delivery.Address1 ?? "",
                AddressLine2 = delivery.Address2 ?? "",
                City = delivery.City ?? "",
                PostalCode = delivery.Zip ?? "",
                CountryCode = delivery.CountryCode ?? "",
                IsMain = false, // bol.com hesabının ana adresi asla değişmesin
                ContactId = contactId,
                Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
            };

            var created = await _exactAddressCrud.CreateAddress(newAddress);
            if (created != null)
            {
                _logger.LogInformation("   ✅ Bol: yeni teslimat adresi oluşturuldu ({AddressId}) {Address}",
                    created.Id, newAddress.FullAddress);
                return (created.Id, contactId);
            }

            _logger.LogWarning("   ⚠️ Bol: teslimat adresi oluşturulamadı, sipariş adres bağlanmadan yazılacak.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "   ⚠️ Bol teslimat adresi hazırlanırken hata.");
        }

        return (null, contactId);
    }
}
