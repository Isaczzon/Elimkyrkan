using Elimkyrkan.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;

namespace Elimkyrkan.Web.Controllers;

/// <summary>
/// Receives contact-form POSTs from <see cref="Views.ContactPage"/> and forwards them
/// via SMTP to whatever email is configured on the Contact page in Umbraco (the
/// <c>email</c> property). Allows the address to be changed in the backoffice without
/// editing code or config.
/// </summary>
[ApiController]
[Route("api/contact")]
[AllowAnonymous]
public sealed class ContactApiController : ControllerBase
{
    private readonly ContactEmailSender _email;
    private readonly IContentService _contentService;
    private readonly ILogger<ContactApiController> _logger;

    public ContactApiController(
        ContactEmailSender email,
        IContentService contentService,
        ILogger<ContactApiController> logger)
    {
        _email = email;
        _contentService = contentService;
        _logger = logger;
    }

    public sealed class ContactFormRequest
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Subject { get; set; }
        public string? Message { get; set; }

        /// <summary>Honeypot — hidden field that should be empty for genuine humans.</summary>
        public string? Website { get; set; }
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Submit([FromBody] ContactFormRequest req, CancellationToken ct)
    {
        // Honeypot — silently accept and pretend success so bots don't retry.
        if (!string.IsNullOrWhiteSpace(req.Website))
        {
            _logger.LogInformation("Contact form: honeypot triggered, dropping submission");
            return Ok(new { ok = true });
        }

        // Basic validation
        if (string.IsNullOrWhiteSpace(req.Name)
            || string.IsNullOrWhiteSpace(req.Email)
            || string.IsNullOrWhiteSpace(req.Message))
        {
            return BadRequest(new { ok = false, error = "missing_fields" });
        }
        if (!IsLikelyEmail(req.Email))
        {
            return BadRequest(new { ok = false, error = "invalid_email" });
        }

        if (!_email.IsConfigured)
        {
            return StatusCode(503, new { ok = false, error = "smtp_not_configured" });
        }

        var recipient = ResolveRecipientFromContactPage();
        if (string.IsNullOrWhiteSpace(recipient))
        {
            _logger.LogWarning("Contact form: no email configured on Contact page");
            return StatusCode(503, new { ok = false, error = "recipient_not_configured" });
        }

        var ok = await _email.SendAsync(
            toAddress: recipient,
            visitorName: req.Name!,
            visitorEmail: req.Email!,
            visitorPhone: req.Phone,
            subject: req.Subject ?? "Kontaktformulär",
            messageBody: req.Message!,
            ct: ct);

        return ok
            ? Ok(new { ok = true })
            : StatusCode(500, new { ok = false, error = "send_failed" });
    }

    private string? ResolveRecipientFromContactPage()
    {
        // Walk root → children to find the Contact page by content-type alias.
        // Using IContentService (back-office API) rather than the published cache
        // so we don't depend on cache being warm or routes being culture-specific.
        foreach (var root in _contentService.GetRootContent())
        {
            var contactPage = _contentService.GetPagedChildren(root.Id, 0, 200, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "contactPage");
            if (contactPage == null) continue;

            // heroImage and email properties are invariant — no culture argument needed.
            var email = contactPage.GetValue<string>("email");
            return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        }
        return null;
    }

    private static bool IsLikelyEmail(string s)
    {
        // Cheap shape check — server-side defense against the truly malformed.
        // We're not trying to be RFC-compliant; the real validator is whether
        // SMTP accepts the address.
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1 && s.IndexOf('.', at) > at;
    }
}
