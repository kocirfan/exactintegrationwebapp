using ShopifyProductApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Shopify adresi ile ExactOnline adresleri karşılaştırmak için
/// </summary>
public class AddressMatchingService
{
    private readonly ILogger<AddressMatchingService> _logger;

    public AddressMatchingService(ILogger<AddressMatchingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Shopify adresini ExactOnline adreslerine karşılaştırır
    /// En uygun adresi bulur, bulamazsa null döner
    /// </summary>
    public ExactAddress FindMatchingAddress(
        ShopifyAddress shopifyAddress,
        List<ExactAddress> exactAddresses,
        int requiredType = 3)
    {
        if (shopifyAddress == null)
        {
            _logger.LogWarning("🚨 Shopify adresi null");
            return null;
        }

        if (!exactAddresses?.Any() == true)
        {
            _logger.LogWarning("🚨 ExactOnline adreslerinde kayıt yok");
            return null;
        }

        _logger.LogInformation($"🔍 Shopify adresi araştırılıyor - Tip: {requiredType}");
        _logger.LogInformation($"   Shopify: {shopifyAddress.Address1}, {shopifyAddress.Zip} {shopifyAddress.City}, {shopifyAddress.Country}");

        // Adım 1: Tip (Type) filtrelemesi
        var addressesByType = exactAddresses
            .Where(x => x.Type == requiredType)
            .ToList();

        if (!addressesByType.Any())
        {
            _logger.LogWarning($"⚠️ Type {requiredType} olan adres bulunamadı");
            _logger.LogInformation($"   Mevcut tipler: {string.Join(", ", exactAddresses.Select(x => x.Type).Distinct())}");
            return null;
        }

        _logger.LogInformation($"   ℹ️ {addressesByType.Count} adet Type {requiredType} adresi bulundu");

        // Adım 2: Tam eşleşme araması
        var exactMatch = FindExactMatch(shopifyAddress, addressesByType);
        if (exactMatch != null)
        {
            _logger.LogInformation($"   ✅ TAM EŞLEŞİME BULUNDU: {exactMatch.Id}");
            return exactMatch;
        }

        // Adım 3: Kısmi eşleşme araması (en yüksek skor)
        var partialMatch = FindBestPartialMatch(shopifyAddress, addressesByType);
        if (partialMatch.match != null)
        {
            _logger.LogInformation($"   ⚠️ KISMI EŞLEŞİME BULUNDU: {partialMatch.match.Id}");
            _logger.LogInformation($"      Uyum Yüzdesi: {partialMatch.score}%");
            _logger.LogInformation($"      Eşleşen Alanlar: {partialMatch.matchedFields}");
            return partialMatch.match;
        }

        _logger.LogWarning($"   ❌ Eşleşen adres bulunamadı");
        return null;
    }

    /// <summary>
    /// TAM EŞLEŞİME KONTROLÜ
    /// Tüm kritik alanlar birebir eşleşmeli
    /// </summary>
    private ExactAddress FindExactMatch(ShopifyAddress shopify, List<ExactAddress> exactAddresses)
    {
        var shopifyNormalized = NormalizeAddress(shopify);

        foreach (var exact in exactAddresses)
        {
            // Kritik alanları kontrol et
            bool matchLine1 = NormalizeString(exact.AddressLine1) == shopifyNormalized.AddressLine1;
            bool matchCity = NormalizeString(exact.City) == shopifyNormalized.City;
            bool matchZip = NormalizeString(exact.PostalCode) == shopifyNormalized.Postcode;
            bool matchCountry = NormalizeString(exact.CountryCode) == shopifyNormalized.Country;

            if (matchLine1 && matchCity && matchZip && matchCountry)
            {
                _logger.LogDebug($"✅ Tam eşleşme: {exact.AddressLine1}, {exact.PostalCode} {exact.City}");
                return exact;
            }
        }

        return null;
    }

    /// <summary>
    /// KISMI EŞLEŞİME KONTROLÜ
    /// Hangi alanlar eşleşiyorsa bulur ve puan verir
    /// </summary>
    private (ExactAddress match, int score, string matchedFields) FindBestPartialMatch(
        ShopifyAddress shopify,
        List<ExactAddress> exactAddresses)
    {
        var shopifyNormalized = NormalizeAddress(shopify);
        ExactAddress bestMatch = null;
        int highestScore = 0;
        List<string> bestMatchedFields = new();

        foreach (var exact in exactAddresses)
        {
            int score = 0;
            List<string> matchedFields = new();

            // Adres satırı (en önemli) - 40 puan
            if (NormalizeString(exact.AddressLine1) == shopifyNormalized.AddressLine1)
            {
                score += 40;
                matchedFields.Add("AddressLine1");
            }

            // Şehir - 30 puan
            if (NormalizeString(exact.City) == shopifyNormalized.City)
            {
                score += 30;
                matchedFields.Add("City");
            }

            // Posta kodu - 20 puan
            if (NormalizeString(exact.PostalCode) == shopifyNormalized.Postcode)
            {
                score += 20;
                matchedFields.Add("Postcode");
            }

            // Ülke - 10 puan
            if (NormalizeString(exact.CountryCode) == shopifyNormalized.Country)
            {
                score += 10;
                matchedFields.Add("Country");
            }

            // Address2 (ikincil) - 5 puan
            if (!string.IsNullOrEmpty(exact.AddressLine2) &&
                NormalizeString(exact.AddressLine2) == shopifyNormalized.AddressLine2)
            {
                score += 5;
                matchedFields.Add("AddressLine2");
            }

            // _logger.LogDebug($"   📊 {exact.fullAddress} → Skor: {score}% ({string.Join(", ", matchedFields)})");

            if (score > highestScore)
            {
                highestScore = score;
                bestMatch = exact;
                bestMatchedFields = matchedFields;
            }
        }

        // Minimum 50% eşleşme gerekli (kritik alanlar en az adres + şehir)
        if (highestScore >= 50)
        {
            return (bestMatch, highestScore, string.Join(", ", bestMatchedFields));
        }

        return (null, 0, "");
    }

    /// <summary>
    /// Shopify adresini normalize et
    /// </summary>
    private NormalizedAddress NormalizeAddress(ShopifyAddress address)
    {
        return new NormalizedAddress
        {
            AddressLine1 = NormalizeString(address.Address1),
            AddressLine2 = NormalizeString(address.Address2),
            City = NormalizeString(address.City),
            Postcode = NormalizeString(address.Zip),
            Country = NormalizeString(address.CountryCode ?? address.Country)
        };
    }

    /// <summary>
    /// String'i normalize et (karşılaştırma için)
    /// </summary>
    private string NormalizeString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        return input
            .Trim()
            .ToLowerInvariant()
            .Replace("  ", " ")  // Çift boşluk → tek boşluk
            .Replace(",", "")    // Virgül kaldır
            .Replace(".", "");   // Nokta kaldır
    }

    /// <summary>
    /// Adres karşılaştırması için normalize edilmiş model
    /// </summary>
    private class NormalizedAddress
    {
        public string AddressLine1 { get; set; }
        public string AddressLine2 { get; set; }
        public string City { get; set; }
        public string Postcode { get; set; }
        public string Country { get; set; }
    }

    /// <summary>
    /// Shopify ile Exact adreslerini karşılaştır ve rapor yap
    /// (DEBUG için)
    /// </summary>
    public void LogAddressComparison(ShopifyAddress shopify, List<ExactAddress> exactAddresses)
    {
        _logger.LogInformation("📋 ═══════════════════════════════════════════════════════════");
        _logger.LogInformation("📋 ADRES KARŞILAŞTIRMA RAPORU");
        _logger.LogInformation("📋 ═══════════════════════════════════════════════════════════");

        _logger.LogInformation($"🛍️  SHOPIFY ADRESİ:");
        _logger.LogInformation($"    {shopify.FirstName} {shopify.LastName}");
        _logger.LogInformation($"    {shopify.Address1} {shopify.Address2 ?? ""}");
        _logger.LogInformation($"    {shopify.Zip} {shopify.City}");
        _logger.LogInformation($"    {shopify.Country} (Kod: {shopify.CountryCode})");

        _logger.LogInformation($"\n💾 EXACTONLINE ADRESLERİ ({exactAddresses.Count} kayıt):");

        int index = 1;
        foreach (var address in exactAddresses)
        {
            _logger.LogInformation($"   [{index}] ID: {address}");
            _logger.LogInformation($"       Type: {address.Type}");
            // _logger.LogInformation($"       {address.fullAddress}");
            index++;
        }

        _logger.LogInformation("📋 ═══════════════════════════════════════════════════════════\n");
    }
}