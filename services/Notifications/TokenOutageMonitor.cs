using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;
using ShopifyProductApp.Services.PendingOperations;

namespace ShopifyProductApp.Services.Notifications
{
    public class TokenOutageStatus
    {
        public bool IsAvailable { get; set; }
        public DateTime? LastCheckAt { get; set; }
        public DateTime? OutageStartedAt { get; set; }
        public double? OutageMinutes { get; set; }
        public DateTime? AlertSentAt { get; set; }
        public bool AlertEnabled { get; set; }
        public int ThresholdMinutes { get; set; }
        public bool EmailConfigured { get; set; }
        public string EmailConfigurationProblem { get; set; }
        public string Recipient { get; set; }
        public string TokenMessage { get; set; }
        public DateTime? TokenExpiryTime { get; set; }
        public string LastEmailError { get; set; }
        public EmailSendResult LastEmailResult { get; set; }
    }

    /// <summary>
    /// Exact token kesintisini izler (singleton):
    ///  - token art arda ThresholdMinutes (varsayılan 30 dk) boyunca alınamazsa e-posta uyarısı gönderir
    ///    (kesinti başına 1 kez; gönderim başarısızsa bir sonraki turda tekrar dener),
    ///  - token geri gelince toparlanma e-postası gönderir ve bekleyen işlem havuzunu hemen tetikler.
    ///
    /// Kesinti başlangıcı GeneralSettings'e (kategori TokenAlert) yazılır; uygulama yeniden
    /// başlasa da süre sıfırlanmaz. TokenRefreshBackgroundService her turda (3 dk) CheckAsync çağırır.
    /// "Token müsait" tanımı: TokenManager geçerli bir access_token döndürüyor ve süresi dolmamış.
    /// </summary>
    public class TokenOutageMonitor
    {
        private const string SettingsCategory = "TokenAlert";
        private const string KeyOutageStartedAt = "TokenOutageStartedAt";
        private const string KeyAlertSentAt = "TokenAlertSentAt";

        private readonly IServiceProvider _serviceProvider;
        private readonly ITokenManager _tokenManager;
        private readonly EmailNotificationService _email;
        private readonly PendingOperationProcessor _processor;
        private readonly ILogger<TokenOutageMonitor> _logger;
        private readonly bool _alertEnabled;
        private readonly int _thresholdMinutes;
        private readonly string _panelUrl;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _stateLoaded;
        private DateTime? _outageStartedAt;
        private DateTime? _alertSentAt;
        private DateTime? _lastCheckAt;
        private bool _lastAvailable = true;
        private string _lastTokenMessage;
        private DateTime? _lastExpiryTime;
        private string _lastEmailError;

        public TokenOutageMonitor(
            IServiceProvider serviceProvider,
            ITokenManager tokenManager,
            EmailNotificationService email,
            PendingOperationProcessor processor,
            IConfiguration configuration,
            ILogger<TokenOutageMonitor> logger)
        {
            _serviceProvider = serviceProvider;
            _tokenManager = tokenManager;
            _email = email;
            _processor = processor;
            _logger = logger;

            var section = configuration.GetSection("Notifications:TokenAlert");
            _alertEnabled = !bool.TryParse(section["Enabled"], out var enabled) || enabled;
            _thresholdMinutes = int.TryParse(section["ThresholdMinutes"], out var t) && t > 0 ? t : 30;
            _panelUrl = section["PanelUrl"];

            _logger.LogInformation("🔧 Token kesinti izleyici: uyarı {State}, eşik {Threshold} dk, alıcı {To}",
                _alertEnabled ? "açık" : "kapalı", _thresholdMinutes, _email.Recipient ?? "-");
        }

        /// <summary>
        /// Exact'a istek atılabilir mi? TokenManager gerekiyorsa yenilemeyi dener;
        /// yenileme başarısızsa eski (süresi dolmuş) token döner → burada "yok" sayılır.
        /// </summary>
        public async Task<bool> IsTokenAvailableAsync()
        {
            try
            {
                var token = await _tokenManager.GetValidTokenAsync();
                return token != null
                       && !string.IsNullOrEmpty(token.access_token)
                       && token.ExpiryTime > DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Token kontrolü başarısız: {Error}", ex.Message);
                return false;
            }
        }

        /// <summary>Periyodik kontrol (TokenRefreshBackgroundService çağırır).</summary>
        public async Task CheckAsync(TokenHealthStatus health = null)
        {
            await _lock.WaitAsync();
            try
            {
                await EnsureStateLoadedAsync();

                var now = DateTime.UtcNow;
                var available = await IsTokenAvailableAsync();

                _lastCheckAt = now;
                _lastAvailable = available;
                _lastTokenMessage = health?.Message;
                _lastExpiryTime = health?.ExpiryTime;

                if (!available)
                {
                    if (_outageStartedAt == null)
                    {
                        _outageStartedAt = now;
                        await PersistAsync(KeyOutageStartedAt, now);
                        _logger.LogWarning("🚨 Exact token kesintisi başladı: {At:yyyy-MM-dd HH:mm:ss} UTC", now);
                    }

                    var minutes = (now - _outageStartedAt.Value).TotalMinutes;
                    _logger.LogWarning("🚨 Exact token {Minutes:F0} dakikadır alınamıyor", minutes);

                    if (_alertEnabled && _alertSentAt == null && minutes >= _thresholdMinutes)
                    {
                        var pendingCount = await CountPendingAsync();
                        var result = await _email.SendAsync(
                            $"[Exact Entegrasyon] Exact token {minutes:F0} dakikadır alınamıyor",
                            BuildAlertBody(minutes, pendingCount));

                        if (result.Success)
                        {
                            _alertSentAt = now;
                            _lastEmailError = null;
                            await PersistAsync(KeyAlertSentAt, now);
                        }
                        else
                        {
                            _lastEmailError = result.Error; // bir sonraki turda tekrar denenir
                        }
                    }
                }
                else if (_outageStartedAt != null)
                {
                    var duration = now - _outageStartedAt.Value;
                    _logger.LogInformation("✅ Exact token geri geldi (kesinti süresi: {Minutes:F0} dk)", duration.TotalMinutes);

                    if (_alertEnabled && _alertSentAt != null)
                    {
                        var pendingCount = await CountPendingAsync();
                        var result = await _email.SendAsync(
                            $"[Exact Entegrasyon] Exact token geri geldi ({duration.TotalMinutes:F0} dk kesinti)",
                            BuildRecoveryBody(duration, pendingCount));
                        if (!result.Success) _lastEmailError = result.Error;
                    }

                    _outageStartedAt = null;
                    _alertSentAt = null;
                    await PersistAsync(KeyOutageStartedAt, null);
                    await PersistAsync(KeyAlertSentAt, null);

                    // Havuzdaki siparişleri beklemeden işlemeye başla
                    _processor.TriggerNow();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Token kesinti kontrolü hatası");
            }
            finally
            {
                _lock.Release();
            }
        }

        public TokenOutageStatus GetStatus()
        {
            var now = DateTime.UtcNow;
            return new TokenOutageStatus
            {
                IsAvailable = _lastAvailable,
                LastCheckAt = _lastCheckAt,
                OutageStartedAt = _outageStartedAt,
                OutageMinutes = _outageStartedAt.HasValue ? Math.Round((now - _outageStartedAt.Value).TotalMinutes, 1) : null,
                AlertSentAt = _alertSentAt,
                AlertEnabled = _alertEnabled,
                ThresholdMinutes = _thresholdMinutes,
                EmailConfigured = _email.IsConfigured,
                EmailConfigurationProblem = _email.ConfigurationProblem,
                Recipient = _email.Recipient,
                TokenMessage = _lastTokenMessage,
                TokenExpiryTime = _lastExpiryTime,
                LastEmailError = _lastEmailError,
                LastEmailResult = _email.LastResult
            };
        }

        // ============= PRIVATE =============

        private async Task<int> CountPendingAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return await db.PendingOperations.CountAsync(p => p.Status == PendingOperationStatus.Pending);
            }
            catch
            {
                return -1;
            }
        }

        private string BuildAlertBody(double minutes, int pendingCount)
        {
            var startedLocal = _outageStartedAt?.ToLocalTime();
            var lines = new List<string>
            {
                $"Exact Online token'ı {minutes:F0} dakikadır alınamıyor.",
                "",
                $"Kesinti başlangıcı      : {startedLocal:dd.MM.yyyy HH:mm} (sunucu saati)",
                $"Son token durumu        : {_lastTokenMessage ?? "-"}",
                $"Havuzda bekleyen sipariş: {(pendingCount < 0 ? "?" : pendingCount.ToString())}",
                $"Sunucu                  : {Environment.MachineName}",
                "",
                "Yapılması gereken: Exact Online'da yeniden yetkilendirme yapıp token'ı güncelleyin.",
                "Token geldiğinde havuzdaki siparişler otomatik olarak Exact'a aktarılacaktır.",
                "Bu kesinti için başka uyarı gönderilmeyecek; token geri geldiğinde bilgi e-postası gelir."
            };
            if (!string.IsNullOrWhiteSpace(_panelUrl))
            {
                lines.Add("");
                lines.Add($"Panel: {_panelUrl}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        private string BuildRecoveryBody(TimeSpan duration, int pendingCount)
        {
            var lines = new List<string>
            {
                "Exact Online token'ı geri geldi.",
                "",
                $"Kesinti süresi          : {duration.TotalMinutes:F0} dakika",
                $"Havuzda bekleyen sipariş: {(pendingCount < 0 ? "?" : pendingCount.ToString())} (şimdi işlenmeye başlıyor)",
                $"Sunucu                  : {Environment.MachineName}"
            };
            if (!string.IsNullOrWhiteSpace(_panelUrl))
            {
                lines.Add("");
                lines.Add($"Panel: {_panelUrl}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        private async Task EnsureStateLoadedAsync()
        {
            if (_stateLoaded) return;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                _outageStartedAt = ParseUtc(await settings.GetSettingAsync(KeyOutageStartedAt));
                _alertSentAt = ParseUtc(await settings.GetSettingAsync(KeyAlertSentAt));

                if (_outageStartedAt != null)
                    _logger.LogWarning("🔁 Önceki kesinti kaydı yüklendi: başlangıç {At:yyyy-MM-dd HH:mm:ss} UTC", _outageStartedAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Token kesinti durumu DB'den okunamadı: {Error}", ex.Message);
            }
            _stateLoaded = true;
        }

        private async Task PersistAsync(string key, DateTime? value)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                if (value == null)
                    await settings.DeleteSettingAsync(key);
                else
                    await settings.SetSettingAsync(key, value.Value.ToString("o"), "Exact token kesinti takibi", SettingsCategory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Token kesinti durumu DB'ye yazılamadı ({Key}): {Error}", key, ex.Message);
            }
        }

        private static DateTime? ParseUtc(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
    }
}
