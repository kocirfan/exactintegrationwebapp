
using ShopifyProductApp.Services;
using ShopifyProductApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using ShopifyProductApp.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddMemoryCache();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "ExactWebApp API", Version = "v1" });

    // JWT Authorization configuration
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. \r\n\r\n Enter 'Bearer' [space] and then your token in the text input below.\r\n\r\nExample: \"Bearer 12345abcdef\"",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement()
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                },
                Scheme = "oauth2",
                Name = "Bearer",
                In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            },
            new List<string>()
        }
    });
});

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

// ✨ Identity Configuration
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

// ✨ JWT Authentication Configuration
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.SaveToken = true;
    options.RequireHttpsMetadata = false;
    options.TokenValidationParameters = new TokenValidationParameters()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "http://localhost:5000",
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "http://localhost:5000",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? "ByYM000OLlMQG6VVVp1OH7Xzyr7gHuw1qvUC5dcGt3SNM"))
    };
});

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

builder.Services.AddScoped<ExactCustomerCrud>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactCustomerCrud>();


    var exactSection = configuration.GetSection("ExactOnline");

    return new ExactCustomerCrud(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager,
        serviceProvider: serviceProvider
    );
});
builder.Services.AddScoped<ExactAddressCrud>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactCustomerCrud>();


    var exactSection = configuration.GetSection("ExactOnline");

    return new ExactAddressCrud(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager,
        serviceProvider: serviceProvider
    );
});
builder.Services.AddScoped<ExactProductCrud>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactCustomerCrud>();


    var exactSection = configuration.GetSection("ExactOnline");

    return new ExactProductCrud(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager,
        serviceProvider: serviceProvider
    );
});

//raporlar exact
builder.Services.AddScoped<ExactSalesReports>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactCustomerCrud>();


    var exactSection = configuration.GetSection("ExactOnline");

    return new ExactSalesReports(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager,
        serviceProvider: serviceProvider
    );
});


builder.Services.AddScoped<ExactSalesReportsUltraOptimized>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactCustomerCrud>();


    var exactSection = configuration.GetSection("ExactOnline");

    return new ExactSalesReportsUltraOptimized(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager,
        serviceProvider: serviceProvider
    );
});


builder.Services.AddScoped<CustomerReports>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactCustomerCrud>();


    var exactSection = configuration.GetSection("ExactOnline");

    return new CustomerReports(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager,
        serviceProvider: serviceProvider
    );
});
builder.Services.AddScoped<QuotationReports>(serviceProvider =>
{
    // ✅ DÜZELTME: ISettingsService kullan (SettingsService yerine)
    var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tokenManager = serviceProvider.GetRequiredService<ITokenManager>();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var logger = loggerFactory.CreateLogger<ExactCustomerCrud>();


    var exactSection = configuration.GetSection("ExactOnline");

    return new QuotationReports(
        clientId: exactSection["ClientId"] ?? throw new InvalidOperationException("ExactOnline:ClientId is missing"),
        clientSecret: exactSection["ClientSecret"] ?? throw new InvalidOperationException("ExactOnline:ClientSecret is missing"),
        redirectUri: exactSection["RedirectUri"] ?? throw new InvalidOperationException("ExactOnline:RedirectUri is missing"),
        baseUrl: exactSection["BaseUrl"] ?? "https://start.exactonline.nl",
        divisionCode: exactSection["DivisionCode"] ?? throw new InvalidOperationException("ExactOnline:DivisionCode is missing"),
        tokenFile: exactSection["TokenFile"] ?? "token.json",
        logger: logger,
        settingsService: settingsService,
        tokenManager: tokenManager,
        serviceProvider: serviceProvider
    );
});



// 3️⃣ Background Service - Token'ı proaktif yeniler
builder.Services.AddHostedService<TokenRefreshBackgroundService>();
builder.Services.AddScoped<AddressMatchingService>();


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

builder.Services.AddScoped<ShopifyCustomerCrud>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var shopifySection = configuration.GetSection("Shopify");

    return new ShopifyCustomerCrud(
        shopifyStoreUrl: shopifySection["StoreUrl"] ?? throw new InvalidOperationException("Shopify:StoreUrl is missing"),
        accessToken: shopifySection["AccessToken"] ?? throw new InvalidOperationException("Shopify:AccessToken is missing"),
        graphqlService: serviceProvider.GetRequiredService<ShopifyGraphQLService>(),
        logger: serviceProvider.GetRequiredService<ILogger<ShopifyCustomerCrud>>(),
        serviceProvider: serviceProvider
    );
});
builder.Services.AddScoped<ShopifyOrderCrud>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var shopifySection = configuration.GetSection("Shopify");

    return new ShopifyOrderCrud(
        shopifyStoreUrl: shopifySection["StoreUrl"] ?? throw new InvalidOperationException("Shopify:StoreUrl is missing"),
        accessToken: shopifySection["AccessToken"] ?? throw new InvalidOperationException("Shopify:AccessToken is missing"),
        graphqlService: serviceProvider.GetRequiredService<ShopifyGraphQLService>(),
        logger: serviceProvider.GetRequiredService<ILogger<ShopifyCustomerCrud>>(),
        serviceProvider: serviceProvider
    );
});




// ✨ HttpClientFactory ekle (GraphQL için gerekli)
builder.Services.AddHttpClient();

// ✨ ShopifyGraphQLService'i ekle (GraphQL - Hızlı versiyon)
builder.Services.AddScoped<ShopifyGraphQLService>();

// Configuration sınıfını da ekle
builder.Services.AddSingleton<AppConfiguration>();

// Stok sync loglarını DB'ye yazan servis (background service + test endpoint'i kullanır)
builder.Services.AddSingleton<StockSyncLogService>();

// Manuel stok senkronu tetikleyici (monitoring controller kullanır)
builder.Services.AddSingleton<ManualStockSyncRunner>();

// Fiyat sync logları + manuel fiyat senkronu tetikleyici
builder.Services.AddSingleton<PriceSyncLogService>();
builder.Services.AddSingleton<ManualPriceSyncRunner>();

// Müşteri sync logları + manuel müşteri senkronu tetikleyici (dashboard kullanır)
builder.Services.AddSingleton<CustomerSyncLogService>();
builder.Services.AddSingleton<ManualCustomerSyncRunner>();

// Thread-Safe Background Services
// Stok sync (günlük 09:30)
builder.Services.AddHostedService<StockSyncBackgroundService>();
//yeni prcice     dursun  bi
//builder.Services.AddHostedService<PriceSyncBackgroundService>();        // Fiyat sync (her 10 dakika, son 15dk değişenler)
//--------------metafieldlara id yazmak içindi
builder.Services.AddHostedService<ExactProductIdMetafieldSyncService>();
// Aşağıdaki 3 müşteri servisi KAPALI kalacak: yazdıkları metafield'lar (customer_id,
// exact_discount_code, customer_code) zaten UpdateCustomerAsync ile her müşteri
// senkronunda güncelleniyor. Müşteri güncelleme yolları: gece servisi + webhook + manuel tetikleme.
//builder.Services.AddHostedService<ExactCustomerIdMetafieldSyncService>(); // Customer exact_customer_id metafield sync (her gün 05:00)
//builder.Services.AddHostedService<ExactDiscountCodeSyncService>(); // Customer exact_discount_code metafield sync (başlangıçta çalışır)
//builder.Services.AddHostedService<UpdateExactCustomerJob>(); // 5 dakikada bir çalışıyordu, DB'ye yazmıyordu
//New product var ama ProductPriceAndTitleUpdateService bundan emin değilim açık şimdilik
builder.Services.AddHostedService<NewProductCreationService>();
builder.Services.AddHostedService<NoDiscountTagSyncService>(); // Son 10dk'da modified webshop ürünlerin isNoDiscount tag'ını senkronize eder
//bunu stok ile birleştireceğim
// builder.Services.AddHostedService<ProductPriceAndTitleUpdate>();
builder.Services.AddScoped<ProductPriceAndTitleUpdateService>();

// Uygulama başlangıcında bir kez tüm Exact ürünlerini Shopify'da toplu fiyat günceller
//builder.Services.AddHostedService<BulkPriceSyncBackgroundService>(); // KAPATILDI: yerine NightlyPriceSyncBackgroundService (her gece tüm ürünler + PriceSyncLogs)

// Her gece 03:00'te TÜM ürünlerin fiyatını Exact'tan Shopify'a senkronlar (PriceSyncLogs'a yazar)
builder.Services.AddHostedService<NightlyPriceSyncBackgroundService>();

// Her gece 04:30'da son 24 saatte değişen müşterileri senkronlar (CustomerSyncLogs'a yazar)
builder.Services.AddHostedService<NightlyCustomerSyncBackgroundService>();
builder.Services.AddSingleton<ITokenBlacklistService, TokenBlacklistService>();

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
app.UseAuthentication(); // ✨ Authentication Middleware
app.UseMiddleware<ShopifyProductApp.Middleware.TokenBlacklistMiddleware>(); // ✨ Token Blacklist Middleware
app.UseAuthorization();  // ✨ Authorization Middleware

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Production'da da Swagger görmek isterseniz burayı açabilirsiniz
    app.UseSwagger();
    app.UseSwaggerUI();
}

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

// Dashboard - log dosyalarını okuyarak durum gösterir, mevcut servislere dokunmaz
app.MapGet("/dashboard/status", async (IWebHostEnvironment env) =>
{
    var dataPath = Path.Combine(Directory.GetCurrentDirectory(), "Data");

    static (string timestamp, string status, int count) ReadLastEntry(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return ("-", "Dosya yok", 0);
            var text = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(text)) return ("-", "Boş", 0);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            System.Text.Json.JsonElement last;
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var arr = doc.RootElement;
                if (arr.GetArrayLength() == 0) return ("-", "Kayıt yok", 0);
                last = arr[arr.GetArrayLength() - 1];
            }
            else
            {
                last = doc.RootElement;
            }
            var ts = last.TryGetProperty("Timestamp", out var t) ? t.GetString() :
                     last.TryGetProperty("timestamp", out var t2) ? t2.GetString() : "-";
            var st = last.TryGetProperty("Status", out var s) ? s.GetString() :
                     last.TryGetProperty("status", out var s2) ? s2.GetString() : "-";
            var cnt = last.TryGetProperty("UpdatedCount", out var c) ? c.GetInt32() : 0;
            return (ts ?? "-", st ?? "-", cnt);
        }
        catch { return ("-", "Okunamadı", 0); }
    }

    static string FileSize(string filePath)
    {
        try { return File.Exists(filePath) ? $"{new FileInfo(filePath).Length / 1024.0:F1} KB" : "-"; }
        catch { return "-"; }
    }

    var services = new[]
    {
        new { Ad = "Price Sync", Dosya = "price_sync_log.json" },
        new { Ad = "Webhook Update", Dosya = "webhook_update.json" },
        new { Ad = "Background Process", Dosya = "background_process_log.json" },
        new { Ad = "Stock Sync", Dosya = "daily_stock_sync.json" },
        new { Ad = "New Products", Dosya = "newproducts.json" },
        new { Ad = "Webhook Logs", Dosya = "webhook_logs.json" },
        new { Ad = "Item Changes", Dosya = "item_changes.json" },
    };

    var rows = services.Select(s =>
    {
        var path = Path.Combine(dataPath, s.Dosya);
        var (ts, st, cnt) = ReadLastEntry(path);
        var size = FileSize(path);
        return new { s.Ad, s.Dosya, Timestamp = ts, Status = st, Count = cnt, Size = size };
    });

    return Results.Json(rows);
}).AllowAnonymous();

app.MapGet("/dashboard", () =>
{
    var html = """
<!DOCTYPE html>
<html lang="tr">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Servis Durumu</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: system-ui, sans-serif; background: #0f172a; color: #e2e8f0; min-height: 100vh; padding: 2rem; }
  h1 { font-size: 1.5rem; font-weight: 700; margin-bottom: 0.25rem; }
  .subtitle { color: #64748b; font-size: 0.85rem; margin-bottom: 2rem; }
  .subtitle span { color: #38bdf8; }
  table { width: 100%; border-collapse: collapse; background: #1e293b; border-radius: 12px; overflow: hidden; }
  thead { background: #0f172a; }
  th { padding: 0.85rem 1.25rem; text-align: left; font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.05em; color: #64748b; }
  td { padding: 0.85rem 1.25rem; font-size: 0.85rem; border-top: 1px solid #0f172a; }
  tr:hover td { background: #263347; }
  .ts { color: #94a3b8; font-size: 0.78rem; }
  .status { max-width: 340px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: #cbd5e1; }
  .badge { display: inline-block; padding: 0.2rem 0.6rem; border-radius: 9999px; font-size: 0.72rem; font-weight: 600; }
  .ok { background: #052e16; color: #4ade80; }
  .warn { background: #451a03; color: #fb923c; }
  .err { background: #450a0a; color: #f87171; }
  .count { color: #38bdf8; font-weight: 600; }
  .size { color: #475569; font-size: 0.78rem; }
  .refresh { color: #64748b; font-size: 0.78rem; margin-top: 1.25rem; }
  .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: #4ade80; animation: pulse 2s infinite; margin-right: 6px; }
  @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:0.4} }
</style>
</head>
<body>
<h1>Servis Durumu</h1>
<p class="subtitle">Son log kayıtlarına göre &mdash; <span id="refreshin">10</span>s sonra yenilenir &nbsp;<span class="dot"></span></p>
<table>
  <thead>
    <tr>
      <th>Servis</th>
      <th>Son Çalışma</th>
      <th>Durum</th>
      <th>Güncellenen</th>
      <th>Dosya</th>
    </tr>
  </thead>
  <tbody id="tbody">
    <tr><td colspan="5" style="color:#475569;padding:2rem">Yükleniyor...</td></tr>
  </tbody>
</table>
<p class="refresh" id="lastfetch"></p>

<script>
function relativeTime(isoStr) {
  if (!isoStr || isoStr === '-') return '-';
  const diff = (Date.now() - new Date(isoStr).getTime()) / 1000;
  if (diff < 60) return Math.round(diff) + 's önce';
  if (diff < 3600) return Math.round(diff/60) + 'dk önce';
  if (diff < 86400) return Math.round(diff/3600) + 'sa önce';
  return Math.round(diff/86400) + 'g önce';
}

function badge(status) {
  if (!status || status === '-' || status === 'Dosya yok' || status === 'Boş' || status === 'Kayıt yok')
    return `<span class="badge warn">${status || '-'}</span>`;
  if (status.toLowerCase().includes('hata') || status.toLowerCase().includes('error') || status === 'Okunamadı')
    return `<span class="badge err">${status}</span>`;
  return `<span class="badge ok">✓</span>`;
}

async function load() {
  try {
    const r = await fetch('/dashboard/status');
    const data = await r.json();
    const tbody = document.getElementById('tbody');
    tbody.innerHTML = data.map(s => `
      <tr>
        <td><strong>${s.ad}</strong></td>
        <td class="ts" title="${s.timestamp}">${relativeTime(s.timestamp)}</td>
        <td class="status" title="${s.status}">${badge(s.status)} ${s.status !== '-' ? s.status.substring(0,60) + (s.status.length>60?'…':'') : ''}</td>
        <td class="count">${s.count > 0 ? s.count : '-'}</td>
        <td class="size">${s.dosya}<br><small>${s.size}</small></td>
      </tr>`).join('');
    document.getElementById('lastfetch').textContent = 'Son güncelleme: ' + new Date().toLocaleTimeString('tr-TR');
  } catch(e) {
    document.getElementById('tbody').innerHTML = `<tr><td colspan="5" style="color:#f87171">Yüklenemedi: ${e.message}</td></tr>`;
  }
}

load();
let countdown = 10;
setInterval(() => {
  countdown--;
  document.getElementById('refreshin').textContent = countdown;
  if (countdown <= 0) { countdown = 10; load(); }
}, 1000);
</script>
</body>
</html>
""";
    return Results.Content(html, "text/html");
}).AllowAnonymous();

// Data klasörünü configuration'dan oku ve oluştur
var fullDataPath = Path.Combine(Directory.GetCurrentDirectory(), dataDirectory);
if (!Directory.Exists(fullDataPath))
{
    Directory.CreateDirectory(fullDataPath);
    Console.WriteLine($"📁 {dataDirectory} klasörü oluşturuldu");
}

Console.WriteLine("🚀 Uygulama başlatıldı");
Console.WriteLine($"📁 Data Directory: {dataDirectory}");

// 🏷️ TEK SEFERLİK: Tüm Shopify müşterilerine "corporate" tag'i ekler. (ŞU AN DEVRE DIŞI - yoruma alındı)
// Başarıyla tamamlanınca Data/corporate_tag_migration_done.json oluşturur, sonraki açılışlarda çalışmaz.
// Tekrar çalıştırmak için bu dosyayı silmeniz yeterli.
/*
var corporateTagMarkerFile = Path.Combine(fullDataPath, "corporate_tag_migration_done.json");
if (!File.Exists(corporateTagMarkerFile))
{
    _ = Task.Run(async () =>
    {
        try
        {
            Console.WriteLine("🏷️ [CorporateTag] Tek seferlik migration başlıyor: tüm müşterilere 'corporate' tag'i eklenecek...");

            var storeUrl = app.Configuration["Shopify:StoreUrl"]
                ?? throw new InvalidOperationException("Shopify:StoreUrl is missing");
            var accessToken = app.Configuration["Shopify:AccessToken"]
                ?? throw new InvalidOperationException("Shopify:AccessToken is missing");

            using var http = new HttpClient { BaseAddress = new Uri(storeUrl) };
            http.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);
            const string endpoint = "admin/api/2024-01/graphql.json";

            async Task<System.Text.Json.JsonDocument> RunGraphQLAsync(object payload)
            {
                while (true)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await http.PostAsync(endpoint, content);

                    if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        Console.WriteLine("   ⏳ [CorporateTag] Rate limit, 10sn bekleniyor...");
                        await Task.Delay(10000);
                        continue;
                    }

                    var body = await resp.Content.ReadAsStringAsync();
                    return System.Text.Json.JsonDocument.Parse(body);
                }
            }

            string cursor = null;
            bool hasNextPage = true;
            bool fatalError = false;
            int tagged = 0, alreadyTagged = 0, failed = 0, page = 0;

            const string customersQuery = @"query($cursor: String) {
                customers(first: 100, after: $cursor) {
                    pageInfo { hasNextPage endCursor }
                    edges { node { id tags } }
                }
            }";

            const string tagsAddMutation = @"mutation($id: ID!, $tags: [String!]!) {
                tagsAdd(id: $id, tags: $tags) {
                    userErrors { field message }
                }
            }";

            while (hasNextPage && !fatalError)
            {
                page++;
                using var doc = await RunGraphQLAsync(new { query = customersQuery, variables = new { cursor } });

                if (doc.RootElement.TryGetProperty("errors", out var errors))
                {
                    Console.WriteLine($"❌ [CorporateTag] GraphQL hatası: {errors}");
                    fatalError = true;
                    break;
                }

                var customers = doc.RootElement.GetProperty("data").GetProperty("customers");
                var pageInfo = customers.GetProperty("pageInfo");
                hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();
                cursor = pageInfo.TryGetProperty("endCursor", out var ec) && ec.ValueKind == System.Text.Json.JsonValueKind.String
                    ? ec.GetString()
                    : null;

                foreach (var edge in customers.GetProperty("edges").EnumerateArray())
                {
                    var node = edge.GetProperty("node");
                    var customerId = node.GetProperty("id").GetString();

                    var hasTag = node.GetProperty("tags").EnumerateArray()
                        .Any(t => string.Equals(t.GetString(), "corporate", StringComparison.OrdinalIgnoreCase));

                    if (hasTag)
                    {
                        alreadyTagged++;
                        continue;
                    }

                    using var mDoc = await RunGraphQLAsync(new
                    {
                        query = tagsAddMutation,
                        variables = new { id = customerId, tags = new[] { "corporate" } }
                    });

                    var userErrors = mDoc.RootElement.GetProperty("data").GetProperty("tagsAdd").GetProperty("userErrors");
                    if (userErrors.GetArrayLength() > 0)
                    {
                        failed++;
                        Console.WriteLine($"   ⚠️ [CorporateTag] {customerId} eklenemedi: {userErrors}");
                    }
                    else
                    {
                        tagged++;
                    }

                    await Task.Delay(250); // Shopify throttle koruması
                }

                Console.WriteLine($"   📄 [CorporateTag] Sayfa {page} bitti — eklenen: {tagged}, zaten var: {alreadyTagged}, hata: {failed}");
            }

            if (!fatalError)
            {
                var result = new
                {
                    Timestamp = DateTime.Now.ToString("o"),
                    Status = "Tamamlandı",
                    Tagged = tagged,
                    AlreadyTagged = alreadyTagged,
                    Failed = failed
                };
                await File.WriteAllTextAsync(corporateTagMarkerFile,
                    System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"✅ [CorporateTag] Tamamlandı: {tagged} müşteriye eklendi, {alreadyTagged} zaten vardı, {failed} hata. Marker dosyası yazıldı, bir daha çalışmayacak.");
            }
            else
            {
                Console.WriteLine("❌ [CorporateTag] Hata nedeniyle yarıda kaldı, marker yazılmadı — sonraki açılışta kaldığı yerden (idempotent) tekrar dener.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [CorporateTag] Beklenmeyen hata: {ex.Message}");
        }
    });
}
*/

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

