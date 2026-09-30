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
    /// Ayarlar appsettings.json → Notifications:Email. Ayar eksikse IsConfigured=false döner
    /// ve gönderim denenmez; sorun ConfigurationProblem alanında panelde görünür.
    /// </summary>
    public class EmailNotificationService
    {
        private readonly ILogger<EmailNotificationService> _logger;
        private readonly bool _enabled;
        private readonly string _host;
        private readonly int _port;
        private readonly bool _enableSsl;
        private readonly string _username;
        private readonly string _password;
        private readonly string _fromEmail;
        private readonly string _fromName;
        private readonly string _to;

        public EmailSendResult LastResult { get; private set; }

        public bool Enabled => _enabled;
        public string Recipient => _to;

        public bool IsConfigured => ConfigurationProblem == null;

        public string ConfigurationProblem =>
            !_enabled ? "Notifications:Email:Enabled = false"
            : string.IsNullOrWhiteSpace(_host) ? "Notifications:Email:SmtpHost boş"
            : string.IsNullOrWhiteSpace(_fromEmail) ? "Notifications:Email:FromEmail boş"
            : string.IsNullOrWhiteSpace(_to) ? "Notifications:Email:To boş"
            : (!string.IsNullOrWhiteSpace(_username) && string.IsNullOrEmpty(_password)) ? "Notifications:Email:Password boş"
            : null;

        public EmailNotificationService(IConfiguration configuration, ILogger<EmailNotificationService> logger)
        {
            _logger = logger;

            var section = configuration.GetSection("Notifications:Email");
            _enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled;
            _host = section["SmtpHost"];
            _port = int.TryParse(section["SmtpPort"], out var port) ? port : 587;
            _enableSsl = !bool.TryParse(section["EnableSsl"], out var ssl) || ssl;
            _username = section["Username"];
            _password = section["Password"];
            _fromEmail = section["FromEmail"];
            _fromName = section["FromName"];
            _to = section["To"];

            _logger.LogInformation("📧 E-posta bildirimi: {State} (alıcı: {To})",
                IsConfigured ? "hazır" : "yapılandırılmadı — " + ConfigurationProblem, _to ?? "-");
        }

        public async Task<EmailSendResult> SendAsync(string subject, string body)
        {
            var result = new EmailSendResult { At = DateTime.UtcNow, Subject = subject };

            if (!IsConfigured)
            {
                result.Error = "E-posta ayarları eksik: " + ConfigurationProblem;
                LastResult = result;
                _logger.LogWarning("📧 E-posta gönderilmedi: {Error}", result.Error);
                return result;
            }

            try
            {
                using var client = new SmtpClient(_host, _port)
                {
                    EnableSsl = _enableSsl,
                    Timeout = 30000,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false
                };
                if (!string.IsNullOrEmpty(_username))
                {
                    client.Credentials = new NetworkCredential(_username, _password);
                }

                using var message = new MailMessage
                {
                    From = new MailAddress(_fromEmail, string.IsNullOrWhiteSpace(_fromName) ? _fromEmail : _fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false,
                    BodyEncoding = Encoding.UTF8,
                    SubjectEncoding = Encoding.UTF8
                };
                foreach (var to in _to.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    message.To.Add(to.Trim());
                }

                await client.SendMailAsync(message);

                result.Success = true;
                _logger.LogInformation("📧 E-posta gönderildi: \"{Subject}\" → {To}", subject, _to);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _logger.LogError(ex, "❌ E-posta gönderilemedi: \"{Subject}\"", subject);
            }

            LastResult = result;
            return result;
        }
    }
}
