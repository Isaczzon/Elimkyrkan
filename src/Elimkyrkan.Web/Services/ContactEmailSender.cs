using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Elimkyrkan.Web.Services;

/// <summary>
/// Sends contact-form submissions via SMTP (one.com). The visitor's address is set
/// as Reply-To so when the recipient hits Reply, it goes back to the visitor — the
/// actual From is the church's authenticated mailbox (required by one.com).
/// </summary>
public sealed class ContactEmailSender
{
    private readonly IOptionsMonitor<SmtpOptions> _options;
    private readonly ILogger<ContactEmailSender> _logger;

    public ContactEmailSender(IOptionsMonitor<SmtpOptions> options, ILogger<ContactEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => _options.CurrentValue.IsConfigured;

    public async Task<bool> SendAsync(
        string toAddress,
        string visitorName,
        string visitorEmail,
        string? visitorPhone,
        string subject,
        string messageBody,
        CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.IsConfigured)
        {
            _logger.LogWarning("SMTP not configured — contact form email NOT sent");
            return false;
        }
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            _logger.LogWarning("Contact form: no recipient address — email NOT sent");
            return false;
        }

        var safeName = (visitorName ?? "").Trim();
        var safeSubject = (subject ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safeSubject)) safeSubject = "Kontaktformulär";

        try
        {
            using var msg = new MailMessage();
            msg.From = new MailAddress(opts.FromAddress, opts.FromName);
            msg.To.Add(new MailAddress(toAddress));

            // Reply-To = the visitor's address, so hitting Reply in the inbox
            // lands back on them rather than on the church's own mailbox.
            if (!string.IsNullOrWhiteSpace(visitorEmail))
            {
                try { msg.ReplyToList.Add(new MailAddress(visitorEmail, safeName)); }
                catch { /* malformed address — silently skip; server-side validation should catch this */ }
            }

            msg.Subject = $"Kontaktformulär: {safeSubject} – {safeName}";
            msg.IsBodyHtml = false;
            msg.Body = BuildBody(safeName, visitorEmail, visitorPhone, safeSubject, messageBody);

#pragma warning disable SYSLIB0014 // SmtpClient is obsolete but adequate for STARTTLS on port 587
            using var smtp = new SmtpClient(opts.Host, opts.Port)
            {
                EnableSsl = opts.EnableSsl,
                Credentials = new NetworkCredential(opts.Username, opts.Password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
            await smtp.SendMailAsync(msg, ct);
#pragma warning restore SYSLIB0014

            _logger.LogInformation("Contact form: sent message from {Visitor} to {Recipient}", visitorEmail, toAddress);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contact form: SMTP send failed");
            return false;
        }
    }

    private static string BuildBody(string name, string email, string? phone, string subject, string message)
    {
        var lines = new List<string>
        {
            "Nytt meddelande från kontaktformuläret på elimmantorp.se",
            new string('=', 60),
            "",
            $"Från:      {name}",
            $"E-post:    {email}",
        };
        if (!string.IsNullOrWhiteSpace(phone))
        {
            lines.Add($"Telefon:   {phone}");
        }
        lines.Add($"Ämne:      {subject}");
        lines.Add("");
        lines.Add(new string('-', 60));
        lines.Add("");
        lines.Add(message ?? "");
        lines.Add("");
        lines.Add(new string('=', 60));
        lines.Add("Svara på detta mail för att skicka tillbaka till avsändaren.");
        return string.Join("\n", lines);
    }
}
