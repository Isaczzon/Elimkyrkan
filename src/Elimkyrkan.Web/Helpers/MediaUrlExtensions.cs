namespace Elimkyrkan.Web.Helpers;

/// <summary>
/// Helpers for rendering Media URLs through Umbraco's built-in ImageSharp.Web pipeline.
/// Appending ?format=webp&amp;quality=80 to a media URL makes ImageSharp transcode the
/// original to webp on first request, cache the result on disk, and serve the cached
/// webp variant on subsequent requests. The stored Media file is never mutated.
/// </summary>
public static class MediaUrlExtensions
{
    /// <summary>
    /// Returns the URL with ImageSharp query params for webp + quality.
    /// No-op for empty URLs, absolute URLs to external origins, or SVG (vector, can't transcode).
    /// </summary>
    public static string Webp(this string? url, int quality = 80)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? "";

        // Skip absolute URLs (likely external CDN/origin — ImageSharp middleware only
        // processes requests served by this app).
        if (url.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("//"))
        {
            return url;
        }

        // ImageSharp can't transcode vector — pass SVG through.
        var pathOnly = url.Split('?', '#')[0];
        if (pathOnly.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}format=webp&quality={quality}";
    }
}
