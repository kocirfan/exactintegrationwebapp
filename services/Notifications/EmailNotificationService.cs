using System.Net;
using System.Net.Mail;
using System.Text;

namespace ShopifyProductApp.Services.Notifications
{
    public class EmailSendResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public DateTime At { get; set; }
        public string Subject { get; set; }
    }

    /// <summary>
    /// Basit SMTP gönderici (System.Net.Mail — hollandaemailservice ile aynı yaklaşım).
    ///
    /// Ayarlar HER gönderimde appsettings.json → Notifications:Email'den okunur; appsettings
    /// reloadOnChange ile yüklendiği için ayar değişikliği restart gerektirmez.
    /// Ayar eksikse IsConfigured=false döner ve gönderim denenmez; sorun ConfigurationProblem
    /// alanında panelde görünür.
    ///
    /// Not: relay.mailarsivi.com 587'de STARTTLS sunmuyor → EnableSsl=false olmalı
    /// (aksi halde "Server does not support secure connections" hatası alınır).
    /// </summary>
    public class EmailNotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailNotificationService> _logger;

        private sealed class EmailSettings
        {
            public bool Enabled;
            public string Host;
            public int Port;
            public bool EnableSsl;
            public string Username;
            public string Password;
            public string FromEmail;
            public string FromName;
            public string To;

            public string Problem =>
                !Enabled ? "Notifications:Email:Enabled = false"
                : string.IsNullOrWhiteSpace(Host) ? "Notifications:Email:SmtpHost boş"
                : string.IsNullOrWhiteSpace(FromEmail) ? "Notifications:Email:FromEmail boş"
                : string.IsNullOrWhiteSpace(To) ? "Notifications:Email:To boş"
                : (!string.IsNullOrWhiteSpace(Username) && string.IsNullOrEmpty(Password)) ? "Notifications:Email:Password boş"
                : null;

            public bool IsConfigured => Problem == null;
        }

        public EmailSendResult LastResult { get; private set; }

        public bool Enabled => Load().Enabled;
        public string Recipient => Load().To;
        public bool IsConfigured => Load().IsConfigured;
        public string ConfigurationProblem => Load().Problem;

        public EmailNotificationService(IConfiguration configuration, ILogger<EmailNotificationService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            var cfg = Load();
            _logger.LogInformation("📧 E-posta bildirimi: {State} (alıcı: {To}, sunucu: {Host}:{Port}, ssl: {Ssl})",
                cfg.IsConfigured ? "hazır" : "yapılandırılmadı — " + cfg.Problem,
                cfg.To ?? "-", cfg.Host ?? "-", cfg.Port, cfg.EnableSsl);
        }

        private EmailSettings Load()
        {
            var section = _configuration.GetSection("Notifications:Email");
            return new EmailSettings
            {
                Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
                Host = section["SmtpHost"],
                Port = int.TryParse(section["SmtpPort"], out var port) ? port : 587,
                EnableSsl = !bool.TryParse(section["EnableSsl"], out var ssl) || ssl, // ayar yoksa varsayılan: SSL açık
                Username = section["Username"],
                Password = section["Password"],
                FromEmail = section["FromEmail"],
                FromName = section["FromName"],
                To = section["To"]
            };
        }

        public async Task<EmailSendResult> SendAsync(string subject, string body)
        {
            var cfg = Load();
            var result = new EmailSendResult { At = DateTime.UtcNow, Subject = subject };

            if (!cfg.IsConfigured)
            {
                result.Error = "E-posta ayarları eksik: " + cfg.Problem;
                LastResult = result;
                _logger.LogWarning("📧 E-posta gönderilmedi: {Error}", result.Error);
                return result;
            }

            try
            {
                using var client = new SmtpClient(cfg.Host, cfg.Port)
                {
                    EnableSsl = cfg.EnableSsl,
                    Timeout = 30000,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false
                };
                if (!string.IsNullOrEmpty(cfg.Username))
                {
                    client.Credentials = new NetworkCredential(cfg.Username, cfg.Password);
                }

                using var message = new MailMessage
                {
                    From = new MailAddress(cfg.FromEmail, string.IsNullOrWhiteSpace(cfg.FromName) ? cfg.FromEmail : cfg.FromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false,
                    BodyEncoding = Encoding.UTF8,
                    SubjectEncoding = Encoding.UTF8
                };
                foreach (var to in cfg.To.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    message.To.Add(to.Trim());
                }

                await client.SendMailAsync(message);

                result.Success = true;
                _logger.LogInformation("📧 E-posta gönderildi: \"{Subject}\" → {To}", subject, cfg.To);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _logger.LogError(ex, "❌ E-posta gönderilemedi: \"{Subject}\" ({Host}:{Port}, ssl: {Ssl})",
                    subject, cfg.Host, cfg.Port, cfg.EnableSsl);
            }

            LastResult = result;
            return result;
        }
    }
}
