
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

// Thread-Safe Background Services
// Stok sync (günlük 09:30)
builder.Services.AddHostedService<StockSyncBackgroundService>();
//yeni prcice     dursun  bi
builder.Services.AddHostedService<PriceSyncBackgroundService>();        // Fiyat sync (her 10 dakika, son 15dk değişenler)
//--------------metafieldlara id yazmak içindi
builder.Services.AddHostedService<ExactProductIdMetafieldSyncService>();
builder.Services.AddHostedService<ExactCustomerIdMetafieldSyncService>(); // Customer exact_customer_id metafield sync (her gün 05:00)
//bu eklendi classification kontrolü içim
//builder.Services.AddHostedService<UpdateExactCustomerJob>();
//New product var ama ProductPriceAndTitleUpdateService bundan emin değilim açık şimdilik
builder.Services.AddHostedService<NewProductCreationService>();
//bunu stok ile birleştireceğim
// builder.Services.AddHostedService<ProductPriceAndTitleUpdate>();
builder.Services.AddScoped<ProductPriceAndTitleUpdateService>();

// Uygulama başlangıcında bir kez tüm Exact ürünlerini Shopify'da toplu fiyat günceller
builder.Services.AddHostedService<BulkPriceSyncBackgroundService>();
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

