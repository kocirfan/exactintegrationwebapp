
using ShopifyProductApp.Services;
using ShopifyProductApp.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddMemoryCache();

// ✨ CORS ekle
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Entity Framework Configuration
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("ApplicationConnection")));

// ✅ DÜZELTME: Services'leri DI container'a ekle
// Sadece interface ile kaydet, concrete class'ı ayrıca kaydetmeye gerek yok
builder.Services.AddScoped<ISettingsService, SettingsService>();

// 1️⃣ TokenManager - Singleton (tek instance, tüm uygulama için)
builder.Services.AddSingleton<ITokenManager, TokenManagerService>();

// 2️⃣ ExactService - Scoped (her request için yeni instance)
builder.Services.AddScoped<ExactService>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactService>();

    var exactSection = configuration.GetSection("ExactOnline");

    return new ExactService(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager
    );
});

// 3️⃣ Background Service - Token'ı proaktif yeniler
builder.Services.AddHostedService<TokenRefreshBackgroundService>();

// ShopifyService'i appsettings.json'dan okuyarak kaydet (REST API - Eski versiyon)
builder.Services.AddScoped<ShopifyService>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var shopifySection = configuration.GetSection("Shopify");

    return new ShopifyService(
        shopifyStoreUrl: shopifySection["StoreUrl"] ?? throw new InvalidOperationException("Shopify:StoreUrl is missing"),
        accessToken: shopifySection["AccessToken"] ?? throw new InvalidOperationException("Shopify:AccessToken is missing")
    );
});

// ✨ HttpClientFactory ekle (GraphQL için gerekli)
builder.Services.AddHttpClient();

// ✨ ShopifyGraphQLService'i ekle (GraphQL - Hızlı versiyon)
builder.Services.AddScoped<ShopifyGraphQLService>();

// Configuration sınıfını da ekle
builder.Services.AddSingleton<AppConfiguration>();

// Thread-Safe Background Services
builder.Services.AddHostedService<StockSyncBackgroundService>();        // Stok sync (günlük 09:30)
builder.Services.AddHostedService<NewProductCreationService>();

var app = builder.Build();

// App configuration'dan ayarları oku
var appConfig = app.Configuration.GetSection("App");
var dataDirectory = appConfig["DataDirectory"] ?? "Data";
var enableAutoMigration = bool.Parse(appConfig["EnableAutoMigration"] ?? "true");

// Database Migration'ı otomatik çalıştır
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        if (enableAutoMigration)
        {
            dbContext.Database.EnsureCreated();
            Console.WriteLine("✅ Veritabanı bağlantısı başarılı!");
        }
        else
        {
            Console.WriteLine("ℹ️ Auto migration devre dışı");
        }

        // İlk token durumu kontrolü
        var tokenManager = scope.ServiceProvider.GetRequiredService<ITokenManager>();
        var hasToken = await tokenManager.IsTokenValidAsync();
        Console.WriteLine($"🔐 Token durumu: {(hasToken ? "Geçerli" : "Geçersiz")}");

        if (!hasToken)
        {
            Console.WriteLine("⚠️ Token geçersiz, background service tarafından yenilenecek");
        }

        // ✅ Token health check
        var tokenHealth = await tokenManager.GetTokenHealthAsync();
        Console.WriteLine($"💊 Token Health:");
        Console.WriteLine($"   - Durum: {(tokenHealth.IsHealthy ? "Sağlıklı" : "Sağlıksız")}");
        Console.WriteLine($"   - Mesaj: {tokenHealth.Message}");
        if (tokenHealth.RemainingMinutes.HasValue)
        {
            Console.WriteLine($"   - Kalan Süre: {tokenHealth.RemainingMinutes.Value:F1} dakika");
        }
        Console.WriteLine($"   - Ardışık Hata: {tokenHealth.ConsecutiveFailures}");
        Console.WriteLine($"   - Cache'de: {(tokenHealth.IsCached ? "Evet" : "Hayır")}");

        // Configuration değerlerini göster (güvenlik için sadece ilk/son karakterleri)
        var exactClientId = app.Configuration["ExactOnline:ClientId"];
        var shopifyStore = app.Configuration["Shopify:StoreUrl"];

        if (!string.IsNullOrEmpty(exactClientId))
        {
            Console.WriteLine($"⚙️ Exact Client ID: {exactClientId[..Math.Min(8, exactClientId.Length)]}...{exactClientId[^Math.Min(4, exactClientId.Length)..]}");
        }

        if (!string.IsNullOrEmpty(shopifyStore))
        {
            Console.WriteLine($"🏪 Shopify Store: {shopifyStore}");
        }

        // GraphQL servis test
        Console.WriteLine("🔍 GraphQL servisi test ediliyor...");
        var graphqlService = scope.ServiceProvider.GetRequiredService<ShopifyGraphQLService>();
        Console.WriteLine("✅ GraphQL servisi başarıyla yüklendi!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Başlangıç hatası: {ex.Message}");
        Console.WriteLine($"   Stack Trace: {ex.StackTrace}");
    }
}

// Configure the HTTP request pipeline
app.UseRouting();
app.UseCors("AllowAll");

// OPTIONS isteklerini handle et
app.Use(async (context, next) =>
{
    if (context.Request.Method == "OPTIONS")
    {
        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        context.Response.Headers.Add("Access-Control-Allow-Headers", "*");
        context.Response.StatusCode = 200;
        await context.Response.CompleteAsync();
        return;
    }
    await next();
});

app.MapControllers();

// Data klasörünü configuration'dan oku ve oluştur
var fullDataPath = Path.Combine(Directory.GetCurrentDirectory(), dataDirectory);
if (!Directory.Exists(fullDataPath))
{
    Directory.CreateDirectory(fullDataPath);
    Console.WriteLine($"📁 {dataDirectory} klasörü oluşturuldu");
}

Console.WriteLine("🚀 Uygulama başlatıldı");
Console.WriteLine($"📁 Data Directory: {dataDirectory}");

// Background service ayarlarını göster
var tokenRefreshInterval = app.Configuration["App:BackgroundServices:TokenRefreshInterval"] ?? "00:03:00";
var productSyncInterval = app.Configuration["App:BackgroundServices:ProductSyncInterval"] ?? "00:05:00";
var stockSyncTime = app.Configuration["App:BackgroundServices:StockSyncTime"] ?? "09:30:00";

Console.WriteLine("🔄 Background Services:");
Console.WriteLine($"   - Token Refresh: Her {tokenRefreshInterval}");
Console.WriteLine($"   - Product Sync: Her {productSyncInterval}");
Console.WriteLine($"   - Stock Sync: Günlük {stockSyncTime}");

Console.WriteLine("📊 API Endpoints:");
Console.WriteLine("   GET /api/settings/exact/token - Token bilgileri");
Console.WriteLine("   GET /api/shopify/shopify-items - Shopify ürünleri (GraphQL - Hızlı)");
Console.WriteLine("   GET /api/order/exact-orders-by-email/{email} - Email ile siparişler");

Console.WriteLine("🚀 Uygulama hazır!");

app.Run();