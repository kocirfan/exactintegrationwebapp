# Müşteriye Özel Fiyat (Exact → Shopify B2B)

**Durum:** Keşif tamamlandı, altyapı hazır, pilot bekliyor
**Son güncelleme:** 30 Eylül 2026
**Kapsam:** Exact'taki müşteriye özel fiyatların Shopify B2B kataloğuna aktarılması

---

## 1. Amaç

Belirli müşterilerin Shopify'da **kendilerine özel sabit fiyat** görmesi. Diğer
müşteriler normal fiyattan (Exact `StandardSalesPrice`) alışverişe devam eder.

İki gereksinim:
- Fiyat **yalnızca** o müşteriye görünmeli (sızıntı olmamalı)
- Mevcut indirim kurallarıyla çakışmamalı

---

## 2. Temel bulgu: fiyatın kaynağı

İlk varsayım `Account.PriceList` alanıydı — **yanlış çıktı**. Keşif raporu boş döndü.

Doğru kaynak: **`logistics/SalesItemPrices` içinde `Account` alanı dolu satırlar.**

```
GET /api/v1/{division}/logistics/SalesItemPrices
    ?$filter=Account ne null
    &$select=ID,Account,AccountName,Item,ItemCode,ItemDescription,
             Price,Currency,Quantity,StartDate,EndDate
```

Örnek kayıt:

```json
{
  "Account": "fcf717f0-...", "AccountName": "Lockerland",
  "ItemCode": "INPM160807035", "Price": 91.5,
  "Quantity": 1, "StartDate": "/Date(1764547200000)/", "EndDate": null
}
```

### Dikkat edilecek Exact davranışları

| Konu | Not |
|---|---|
| `$filter` + `$select` | `$filter` kullanılıyorsa `$select` **zorunlu**, yoksa HTTP 400 |
| `sales/SalesPriceLists` | Ayrı kampanya nesnesi, müşteri fiyatı **içermez** (877 müşteriden 1'i bağlı, o da test) |
| `SalesItemPrices` içinde `PriceList` | Alan **yok**; iki nesne birbirine bağlı değil |
| Tarih formatı | `/Date(1699596233367)/` — milisaniye epoch |
| Mevcut fiyat sync'i | `Account eq null` filtresiyle müşteri fiyatlarını **bilinçli olarak dışarıda bırakıyor** — bu ayrım korunmalı |

---

## 3. Keşif raporu sonuçları (16 Eylül 2026)

| Ölçüm | Değer |
|---|---|
| Toplam müşteri fiyat satırı | 22.340 |
| **Aktif** | **15.803** |
| Süresi geçmiş | 6.537 (%29) |
| Gelecek tarihli | 0 |
| Özel fiyatı olan müşteri | **616** |
| Farklı SKU | 3.559 |
| Adet kademeli (`Quantity > 1`) | 137 |
| Normal fiyatın üstünde | 46 |
| EUR dışı para birimi | yok |
| Exact'ta karşılığı bulunamayan SKU | 0 |

### Dağılım

Üç Viking hesabı aktif satırların **%58'ini** tutuyor:

| Müşteri | Aktif satır |
|---|---|
| Viking Office International B.V. | 3.071 |
| Viking Netherlands B.V. | 3.056 |
| Viking Office International BV (BE) | 3.056 |
| *(kalan 613 müşteri)* | ortalama 11 |

Küçük müşterilerle pilot yapmak bu yüzden kolay.

---

## 4. Shopify B2B mimarisi

```
Company → CompanyLocation → Catalog → PriceList → fixed price (variant + fiyat)
```

**İzolasyon:** Fiyat kataloğa bağlı; katalog da company location'a. Başka müşteri
o kataloğa bağlı olmadığı için fiyatı ne görür ne checkout'ta alır. Tag tabanlı
çözümlerdeki "yanlış tag = yanlış fiyat" riski **yapısal olarak yok**.

**Görünürlük ≠ fiyat:** Katalog hangi ürünlerin *görüneceğini*, price list hangi
fiyattan *satılacağını* belirler. Price list'e yazılmayan ürün **gizlenmez**,
normal fiyatından görünür. Yani tüm ürünlere fiyat yazmak gerekmiyor.

> Katalog publication'ı **tüm webshop ürünlerini** içermeli, price list ise
> yalnızca özel fiyatlı SKU'ları. Aksi halde müşteri yarım mağaza görür.

### Mevcut indirim kurallarıyla ilişki

| Mekanizma | Kaynak | B2B fiyatıyla çakışır mı |
|---|---|---|
| `nodiscount` ürün tag'i | Exact `isNoDiscount` → [ShopifyService.cs:3336](services/ShopifyService.cs#L3336) | **Hayır.** B2B price list bir "discount" değil, fiyatın kendisi |
| `exact_discount_code` müşteri tag'i | `ClassificationDescription` → [ShopifyCustomerCrud.cs:151](services/ShopifyCustomer/ShopifyCustomerCrud.cs#L151) | **Olabilir.** Bu tag'e bağlı otomatik indirim varsa B2B fiyatının üstüne biner → kontrol edilmeli |

**Açık kural boşluğu:** `isNoDiscount` işaretli bir ürüne özel fiyat tanımlanırsa
Shopify engellemez. İstenirse sync'te "isNoDiscount olan SKU'ları atla" filtresi eklenebilir.

---

## 5. Eklenen kod

Mevcut hiçbir dosya değiştirilmedi; yalnızca [Program.cs](Program.cs) içine 3 satır DI kaydı eklendi.

| Dosya | Satır | İş |
|---|---|---|
| [services/ExactPriceList/PriceListDiscoveryDtos.cs](services/ExactPriceList/PriceListDiscoveryDtos.cs) | 120 | DTO'lar |
| [services/ExactPriceList/ExactPriceListDiscoveryService.cs](services/ExactPriceList/ExactPriceListDiscoveryService.cs) | 373 | Exact'tan **salt okuma** + analiz |
| [services/ExactPriceList/ShopifyB2BCatalogService.cs](services/ExactPriceList/ShopifyB2BCatalogService.cs) | 437 | Shopify B2B GraphQL (**2025-01**) |
| [services/ExactPriceList/CustomerPriceSyncService.cs](services/ExactPriceList/CustomerPriceSyncService.cs) | 205 | Dry-run / canlı yazım akışı |
| [Controller/PriceListDiscoveryController.cs](Controller/PriceListDiscoveryController.cs) | 177 | Keşif uçları |
| [Controller/CustomerPriceSyncController.cs](Controller/CustomerPriceSyncController.cs) | 103 | Pilot uçları |

> **API sürümü:** B2B mutation'ları (`catalogCreate`, `priceListFixedPricesAdd`)
> 2025-01 gerektirir. Mevcut [ShopifyGraphQLService.cs:28](services/ShopifyGraphQLService.cs#L28)
> 2024-01'de kaldığı için **ayrı servis** yazıldı, ona dokunulmadı.

### Uç noktalar

**Keşif (salt okunur, hiçbir yere yazmaz)**

| Uç | İş |
|---|---|
| `GET /api/price-list-discovery/counts` | Yalnızca sayılar — büyük çıktı okumak için |
| `GET /api/price-list-discovery/summary` | Sayılar + müşteri dökümü |
| `GET /api/price-list-discovery/full` | Tüm satırlar ve anomaliler |
| `GET /api/price-list-discovery/find?q=isim` | Müşteri ara → AccountId + fiyatları |
| `GET /api/price-list-discovery/customer/{accountId}` | Tek müşteri detayı |

**Pilot**

| Uç | İş |
|---|---|
| `GET /api/customer-price-sync/preview/{accountId}` | **Dry-run** — ne yazılacağını gösterir, yazmaz |
| `POST /api/customer-price-sync/apply/{accountId}?apply=true&confirm=EVET` | **Canlı yazım** — iki onay parametresi zorunlu |

### Otomatik atlanan satırlar

| Durum | Sebep |
|---|---|
| `EndDate` geçmiş | Geçersiz fiyat, müşteriye yansıtılmamalı |
| `StartDate` gelecekte | Henüz yürürlükte değil |
| `Quantity > 1` | Shopify B2B sabit fiyatında adet kademesi yok |
| Shopify'da variant bulunamadı | `unmatchedSkus` olarak raporlanır |

Normal fiyatın üstündeki 46 satır **olduğu gibi yazılır** (kullanıcı kararı —
Exact'taki anlaşma esas alınır).

---

## 6. Alınan kararlar

| Karar | Seçim | Gerekçe |
|---|---|---|
| Kapsam | Vitrin fiyatı da değişsin | Müşteri kendi fiyatını Shopify'da görsün |
| Kaynak | Exact | Fiyatın tek kaynağı Exact kalır |
| Granülerlik | Müşteri + SKU | En yaygın senaryo |
| İlk yazım | Tek müşteri pilot | Risk minimum |
| Yüksek fiyatlar | Olduğu gibi yaz | Exact'taki anlaşma esas |
| Şahıs/şirket ayrımı | **Kaldırıldı** | Özel fiyatı varsa katalog kurulur; ayrım gereksiz |

### Neden şahıs/şirket kontrolü kaldırıldı

VAT/KvK numarası olmayan 65 müşteri bulundu. Ama çoğu **açıkça şirket**
("Venzo Trading Company BV", "Filex by Fellowes Holdings B.V.", "Breitinger AG",
"Ahrend NV/SA") — Exact'ta alanlar sadece doldurulmamış. Kontrol engelleyici
kalsaydı 60+ gerçek B2B müşterisi dışarıda kalırdı.

Gerçek B2C sayısı elle sayılabilecek kadar az: `irfan-kocaksoy` (test),
`B2B Shop - Karlijn (Test)`, `khalid Harkach`, `Kristin Plasmans`.

**Yeni kural:** Özel fiyatı varsa katalog kurulur. Gerçek sınır zaten Shopify'da
company olup olmaması — yoksa sistem durur, karar kullanıcıda.

---

## 7. Sipariş tarafı — değişiklik gerekmiyor

Sipariş Shopify'dan geri gelirken fiyat `lineItem.Price`'tan okunup Exact'a
yazılıyor ([ShopifyOrderCrud.cs:262-340](services/ShopifyOrderService/ShopifyOrderCrud.cs#L262-L340)).
B2B fiyatı da bu alandan geleceği için **kod değişikliği gerekmez**.

> **Not:** B2B fiyatı `price` alanında gelir, `DiscountAllocations`'ta değil.
> Yani Exact'a `Discount = 0` ile net fiyat yazılır. Muhasebe bunu "indirimsiz
> düşük fiyat" olarak görür. Sorun değilse olduğu gibi kalabilir.

---

## 8. SONRAKİ ADIMLAR

### Adım 1 — Exact'ta pilot fiyatları tanımla ⬅️ ŞU AN BURADA

Pilot hesap: **`irfan-kocaksoy`**
`accountId`: `29f5bdae-06dc-4c38-8780-6cfc214d1b98`

Mevcut durum: **aktif fiyat yok** (1 satır var, süresi geçmiş) → pilot çalışamaz.

Exact'ta bu hesaba 5-10 fiyat satırı eklenmeli:

- `EndDate` **boş** ya da ileri tarih
- `Quantity` = **1**
- SKU'lar **Shopify'da mevcut** ürünler olsun (variant eşleşmesi için)

> Exact'a yazma işlemi kullanıcıya ait — sistem Exact'tan yalnızca okur.

Doğrulama: `GET /api/price-list-discovery/find?q=irfan` → `activePriceCount > 0`

### Adım 2 — Shopify'da B2B company aç

`irfan-kocaksoy` için **Company + Location** oluşturulmalı.
Katalog bir company location'a bağlanmak zorunda.

> **Kritik:** Company adı Exact'taki `AccountName` ile **birebir aynı** olmalı —
> servis company'yi isimle arıyor.

Şu an müşteriler normal Shopify customer'ı olarak yaratılıyor
([ShopifyCustomerCrud.cs:305](services/ShopifyCustomer/ShopifyCustomerCrud.cs#L305)),
company olarak değil.

### Adım 3 — Dry-run

```
GET /api/customer-price-sync/preview/29f5bdae-06dc-4c38-8780-6cfc214d1b98
```

Kontrol edilecekler: `matchedCount`, `unmatchedSkus`, `samplePrices`.

### Adım 4 — Canlı yazım (onaydan sonra)

```
POST /api/customer-price-sync/apply/29f5bdae-06dc-4c38-8780-6cfc214d1b98
     ?apply=true&confirm=EVET
```

### Adım 5 — Gözle doğrula

Shopify'da `irfan-kocaksoy` ile giriş yapıp fiyatların göründüğünü,
diğer ürünlerin normal fiyatta kaldığını kontrol et.

---

## 9. Pilot sonrası yapılacaklar

| İş | Not |
|---|---|
| **`companyCreate` otomasyonu** | 616 müşteri için elle company açmak mümkün değil; Exact hesaplarından otomatik üretim gerekir |
| **Silme/güncelleme desteği** | Exact'tan çıkan SKU Shopify price list'ten de silinmeli (`priceListFixedPricesDelete`), yoksa geçersiz fiyat kalır |
| **Artımlı sync** | `Modified gt datetime'...'` ile yalnızca değişenleri işle; mevcut `GetRecentlyChangedSalesItemPricesAsync` şablon olabilir |
| **Background service** | Gecelik çalışan sync (mevcut `NightlyPriceSyncBackgroundService` deseni) |
| **`exact_discount_code` çakışma kontrolü** | Bu tag'e bağlı otomatik indirimler B2B fiyatının üstüne biniyor mu, test edilmeli |
| **137 adet kademeli satır** | Shopify'a aktarılamıyor — elle yönetim veya ayrı çözüm |
| **6.537 süresi geçmiş satır** | Exact'ta temizlik yapılabilir (Tint Office 596, Via-Direct 374, OfficeCity 279) |
| **46 yüksek fiyat satırı** | Yazılacak; istenirse sonradan gözden geçirilir |

---

## 10. Riskler

| Risk | Durum |
|---|---|
| Canlı mağazada toplu yazım | Dry-run + çift onay (`apply=true` **ve** `confirm=EVET`) zorunlu |
| Exact'a yanlışlıkla yazma | Sistem Exact'tan **yalnızca okur** |
| Katalog kapsamı yanlış kurulumu | Publication tüm ürünleri içermeli, yoksa müşteri yarım mağaza görür |
| Süresi geçmiş fiyat yazılması | Otomatik atlanıyor |
| Shopify Plus bağımlılığı | B2B company/catalog/price list yalnızca Plus'ta var — **mevcut** ✅ |
