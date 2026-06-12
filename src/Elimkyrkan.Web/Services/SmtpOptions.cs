namespace Elimkyrkan.Web.Services;

/// <summary>
/// SMTP settings bound from the "Smtp" section of appsettings.json.
/// For one.com hosted mailboxes: Host=send.one.com, Port=587, EnableSsl=true,
/// Username/Password = the full mailbox address and its password, FromAddress
/// must match Username (one.com rejects mismatched senders).
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>The address all outgoing mail is sent FROM. Must match Username on one.com.</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>Optional display name shown alongside FromAddress (e.g. "Elimkyrkan Mantorp").</summary>
    public string FromName { get; set; } = "Elimkyrkan Mantorp";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(FromAddress);
}
