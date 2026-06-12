using Elimkyrkan.Web.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace Elimkyrkan.Web.Composers;

public class SiteSetupComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.AddNotificationHandler<UmbracoApplicationStartedNotification, SiteSetupHandler>();
        builder.AddNotificationHandler<ContentSavedNotification, EventRecurrenceHandler>();
        builder.AddNotificationHandler<UserSavingNotification, BackofficeLanguageHandler>();
    }
}

/// <summary>
/// Auto-generates a series of recurring Event nodes when an Event is saved with
/// recurrenceInterval + seriesEndDate set (and seriesId still empty).
/// Also propagates field updates to all siblings sharing the same seriesId when
/// applyToSeries is checked.
/// </summary>
public class EventRecurrenceHandler : INotificationHandler<ContentSavedNotification>
{
    private readonly IContentService _contentService;
    private readonly IContentTypeService _contentTypeService;
    private readonly ILogger<EventRecurrenceHandler> _logger;

    // Thread-local guard so the saves we trigger from inside Handle don't recurse.
    private static readonly ThreadLocal<bool> _inFlight = new(() => false);

    public EventRecurrenceHandler(
        IContentService contentService,
        IContentTypeService contentTypeService,
        ILogger<EventRecurrenceHandler> logger)
    {
        _contentService = contentService;
        _contentTypeService = contentTypeService;
        _logger = logger;
    }

    public void Handle(ContentSavedNotification notification)
    {
        if (_inFlight.Value) return;

        foreach (var content in notification.SavedEntities)
        {
            if (content.ContentType.Alias != "event") continue;

            try
            {
                _inFlight.Value = true;

                var seriesId = content.GetValue<string>("seriesId");
                var endDate = content.GetValue<DateTime?>("seriesEndDate");
                var startDate = content.GetValue<DateTime?>("eventDate");

                // Recurrence interval is derived from the eventType dropdown.
                var eventTypeValue = (content.GetValue<string>("eventType") ?? "").Trim().ToLowerInvariant();
                var interval = eventTypeValue switch
                {
                    "weekly" => "weekly",
                    "even weeks" => "biweekly",
                    "odd weeks" => "biweekly",
                    "every third week" => "triweekly",
                    "monthly" => "monthly",
                    _ => "",
                };

                // 1) Generate series if recurrence is configured and no seriesId yet.
                if (string.IsNullOrWhiteSpace(seriesId)
                    && !string.IsNullOrEmpty(interval)
                    && endDate.HasValue
                    && startDate.HasValue)
                {
                    seriesId = GenerateSeries(content, startDate.Value, endDate.Value, interval);
                }

                // 2) Propagate to series if requested.
                var apply = content.GetValue<bool>("applyToSeries");
                if (apply && !string.IsNullOrWhiteSpace(seriesId))
                {
                    PropagateToSeries(content, seriesId);
                    content.SetValue("applyToSeries", false);
                    _contentService.Save(content);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EventRecurrenceHandler failed for content {Id}", content.Id);
            }
            finally
            {
                _inFlight.Value = false;
            }
        }
    }

    private string GenerateSeries(IContent original, DateTime startDate, DateTime endDate, string interval)
    {
        var newSeriesId = Guid.NewGuid().ToString("N");
        original.SetValue("seriesId", newSeriesId);
        _contentService.Save(original);

        var dates = new List<DateTime>();
        var cursor = startDate;
        while (true)
        {
            cursor = NextOccurrence(cursor, interval);
            if (cursor > endDate) break;
            dates.Add(cursor);
            if (dates.Count > 366) break; // safety
        }

        if (dates.Count == 0) return newSeriesId;

        _logger.LogInformation("Generating {Count} recurring events for series {SeriesId}", dates.Count, newSeriesId);

        var eventType = _contentTypeService.Get("event");
        if (eventType == null) return newSeriesId;

        var parentId = original.ParentId;

        // Capture the values to copy
        var icon = original.GetValue<string>("icon");
        var time = original.GetValue<string>("time");
        var eventTypeValue = original.GetValue<string>("eventType");
        var dayOfWeek = original.GetValue<string>("dayOfWeek");

        // Capture variant values per culture
        var perCulture = new Dictionary<string, (string? title, string? desc, string? name)>();
        foreach (var (iso, _, _) in I18n.Languages)
        {
            perCulture[iso] = (
                original.GetValue<string>("title", iso),
                original.GetValue<string>("description", iso),
                original.GetCultureName(iso));
        }

        foreach (var date in dates)
        {
            var copyName = $"{(perCulture[I18n.Swedish].title ?? "Event")} {date:yyyy-MM-dd}";
            var copy = _contentService.Create(copyName, parentId, eventType);
            copy.SetValue("icon", icon);
            copy.SetValue("time", time);
            copy.SetValue("eventType", eventTypeValue);
            copy.SetValue("dayOfWeek", dayOfWeek);
            copy.SetValue("eventDate", date);
            copy.SetValue("seriesId", newSeriesId);
            copy.SetValue("applyToSeries", false);

            foreach (var (iso, _, _) in I18n.Languages)
            {
                var (title, desc, name) = perCulture[iso];
                if (!string.IsNullOrWhiteSpace(title)) copy.SetValue("title", title, iso);
                if (!string.IsNullOrWhiteSpace(desc)) copy.SetValue("description", desc, iso);
                copy.SetCultureName($"{name ?? title ?? "Event"} {date:yyyy-MM-dd}", iso);
            }

            _contentService.Save(copy);
            _contentService.Publish(copy, new[] { "*" });
        }

        return newSeriesId;
    }

    private static DateTime NextOccurrence(DateTime from, string interval) => interval switch
    {
        "weekly" => from.AddDays(7),
        "biweekly" => from.AddDays(14),
        "triweekly" => from.AddDays(21),
        "monthly" => from.AddMonths(1),
        _ => from.AddDays(7),
    };

    private void PropagateToSeries(IContent original, string seriesId)
    {
        var parentId = original.ParentId;
        var siblings = _contentService.GetPagedChildren(parentId, 0, 1000, out _, null)
            .Where(c => c.ContentType.Alias == "event"
                     && c.Id != original.Id
                     && c.GetValue<string>("seriesId") == seriesId)
            .ToList();

        if (siblings.Count == 0) return;

        _logger.LogInformation("Propagating edits from {Id} to {Count} siblings in series {SeriesId}",
            original.Id, siblings.Count, seriesId);

        var icon = original.GetValue<string>("icon");
        var time = original.GetValue<string>("time");
        var eventTypeValue = original.GetValue<string>("eventType");
        var dayOfWeek = original.GetValue<string>("dayOfWeek");

        foreach (var sibling in siblings)
        {
            sibling.SetValue("icon", icon);
            sibling.SetValue("time", time);
            sibling.SetValue("eventType", eventTypeValue);
            sibling.SetValue("dayOfWeek", dayOfWeek);

            foreach (var (iso, _, _) in I18n.Languages)
            {
                var title = original.GetValue<string>("title", iso);
                var desc = original.GetValue<string>("description", iso);
                if (title != null) sibling.SetValue("title", title, iso);
                if (desc != null) sibling.SetValue("description", desc, iso);
            }

            _contentService.Save(sibling);
            _contentService.Publish(sibling, new[] { "*" });
        }
    }
}

/// <summary>
/// Sets new backoffice users' UI language to Swedish (sv-SE) on creation,
/// unless they already have an explicit value. Existing users get migrated
/// separately by <c>SiteSetupHandler.EnsureBackofficeLanguageSwedish</c>.
/// </summary>
public class BackofficeLanguageHandler : INotificationHandler<UserSavingNotification>
{
    private const string DefaultLanguage = "sv-SE";

    public void Handle(UserSavingNotification notification)
    {
        foreach (var user in notification.SavedEntities)
        {
            // HasIdentity == false means this user has no Id yet → being created.
            if (!user.HasIdentity && string.IsNullOrWhiteSpace(user.Language))
            {
                user.Language = DefaultLanguage;
            }
        }
    }
}

public class SiteSetupHandler : INotificationHandler<UmbracoApplicationStartedNotification>
{
    private readonly IContentTypeService _contentTypeService;
    private readonly IDataTypeService _dataTypeService;
    private readonly IFileService _fileService;
    private readonly IContentService _contentService;
    private readonly IMediaService _mediaService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly MediaUrlGeneratorCollection _mediaUrlGenerators;
    private readonly IContentTypeBaseServiceProvider _contentTypeBaseServiceProvider;
    private readonly ILocalizationService _localizationService;
    private readonly IDomainService _domainService;
    private readonly IUserGroupService _userGroupService;
    private readonly IUserService _userService;
    private readonly IKeyValueService _keyValueService;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly IWebHostEnvironment _env;
    private readonly Umbraco.Cms.Core.PropertyEditors.PropertyEditorCollection _propertyEditors;
    private readonly Umbraco.Cms.Core.Serialization.IConfigurationEditorJsonSerializer _configEditorSerializer;
    private readonly ILogger<SiteSetupHandler> _logger;

    private IDataType? _textstring;
    private IDataType? _textarea;
    private IDataType? _richtext;
    private IDataType? _mediaPicker;
    private IDataType? _datePicker;
    private IDataType? _trueFalse;
    private IDataType? _eventTypeDropdown;
    private IDataType? _dayOfWeekDropdown;
    private IDataType? _blockListContent;
    private IContentType? _elementMissionCountry;
    private IContentType? _elementBibleVerse;
    private IContentType? _elementCounselingPrinciple;
    private IContentType? _elementAxplocketArea;
    private IContentType? _elementYoutubeVideo;
    private IContentType? _elementResourceCard;

    public SiteSetupHandler(
        IContentTypeService contentTypeService,
        IDataTypeService dataTypeService,
        IFileService fileService,
        IContentService contentService,
        IMediaService mediaService,
        MediaFileManager mediaFileManager,
        MediaUrlGeneratorCollection mediaUrlGenerators,
        IContentTypeBaseServiceProvider contentTypeBaseServiceProvider,
        ILocalizationService localizationService,
        IDomainService domainService,
        IUserGroupService userGroupService,
        IUserService userService,
        IKeyValueService keyValueService,
        IShortStringHelper shortStringHelper,
        IWebHostEnvironment env,
        Umbraco.Cms.Core.PropertyEditors.PropertyEditorCollection propertyEditors,
        Umbraco.Cms.Core.Serialization.IConfigurationEditorJsonSerializer configEditorSerializer,
        ILogger<SiteSetupHandler> logger)
    {
        _contentTypeService = contentTypeService;
        _dataTypeService = dataTypeService;
        _fileService = fileService;
        _contentService = contentService;
        _mediaService = mediaService;
        _mediaFileManager = mediaFileManager;
        _mediaUrlGenerators = mediaUrlGenerators;
        _contentTypeBaseServiceProvider = contentTypeBaseServiceProvider;
        _localizationService = localizationService;
        _domainService = domainService;
        _userGroupService = userGroupService;
        _userService = userService;
        _keyValueService = keyValueService;
        _shortStringHelper = shortStringHelper;
        _env = env;
        _propertyEditors = propertyEditors;
        _configEditorSerializer = configEditorSerializer;
        _logger = logger;
    }

    private record Trans(string Sv, string En, string Uk, string Es, string Th);

    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        try
        {
            EnsureLanguages();
            ResolveDataTypes();
            ConfigureRichTextToolbar();
            EnsureContentBlocks();
            var templates = EnsureTemplates();
            var types = EnsureContentTypes(templates);
            EnsureContentBlocksProperty(types.Activity);
            EnsureContentBlocksProperty(types.TeachingResource);
            var home = EnsureContentTree(types);
            EnsureDomains(home);
            SeedMediaLibrary();
            AutoAssignHeroImages(home);
            EnsureUserGroups(home);
            EnsureBackofficeLanguageSwedish();
            MigrateActivityContent(home);
            MigrateContactDetails(home);
            SeedMissionPageBlocks(home);
            SeedActivityPageBlocks(home, "Förbön", _elementBibleVerse, BibleVerseSeeds);
            SeedActivityPageBlocks(home, "Familjerådgivning", _elementCounselingPrinciple, CounselingPrincipleSeeds);
            SeedActivityPageBlocks(home, "Loppis Axplocket", _elementAxplocketArea, AxplocketAreaSeeds);
            SeedForeldrarVideos(home);
            SeedPredikanVideos(home);
            MigrateHemgrupperTeachingContent(home);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Site setup failed");
        }
    }

    private void MigrateContactDetails(IContent home)
    {
        // Contact node
        var contact = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
            .FirstOrDefault(c => c.ContentType.Alias == "contactPage");
        if (contact != null)
        {
            var changed = false;

            var currentPhone = contact.GetValue<string>("phone") ?? "";
            if (currentPhone.Contains("XXX") || string.IsNullOrWhiteSpace(currentPhone))
            {
                contact.SetValue("phone", "073-064 01 71");
                changed = true;
            }

            var currentPastor = contact.GetValue<string>("pastorName") ?? "";
            if (string.IsNullOrWhiteSpace(currentPastor))
            {
                contact.SetValue("pastorName", "Holger Schmidt");
                changed = true;
            }

            var currentAddress = contact.GetValue<string>("address") ?? "";
            if (currentAddress.Contains("590 36"))
            {
                contact.SetValue("address", currentAddress.Replace("590 36", "595 97"));
                changed = true;
            }

            if (changed)
            {
                _logger.LogInformation("Migrating Contact node details");
                _contentService.Save(contact);
                _contentService.Publish(contact, new[] { "*" });
            }
        }

        // Site Settings footer address
        var settings = _contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "siteSettings");
        if (settings != null)
        {
            var currentFooterAddress = settings.GetValue<string>("footerAddress") ?? "";
            if (currentFooterAddress.Contains("590 36"))
            {
                _logger.LogInformation("Migrating Site Settings footer address");
                settings.SetValue("footerAddress", currentFooterAddress.Replace("590 36", "595 97"));
                _contentService.Save(settings);
                _contentService.Publish(settings, new[] { "*" });
            }
        }
    }

    private void MigrateActivityContent(IContent home)
    {
        // One-shot migrations: replace the original seeded Swedish mainContent if it still matches the
        // old text. If an editor has changed the content via the backoffice, the `Contains` check fails
        // and we leave their edit alone.
        var activities = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
            .FirstOrDefault(c => c.ContentType.Alias == "activitiesPage");
        if (activities == null) return;

        ReplaceUnlessUpToDate(activities, "Mansfrukost", MansfrukostHtmlSv,
            newContentMarker: "mansfrukost-content\" data-v=\"2");

        ReplaceUnlessUpToDate(activities, "Barn", BarnHtmlSv,
            newContentMarker: "barn-content\" data-v=\"2");

        ReplaceUnlessUpToDate(activities, "Next Generation", NextGenerationHtmlSv,
            newContentMarker: "nextgen-content\" data-v=\"1");

        ReplaceIfAnyMatch(activities, "Hemgrupper",
            HemgrupperHtmlSv,
            "Hemgrupperna är hjärtat i vår församling");

        ReplaceUnlessUpToDate(activities, "Mission", MissionHtmlSv,
            newContentMarker: "data-v=\"6\"");

        ReplaceUnlessUpToDate(activities, "Familjerådgivning", FamiljeradgivningHtmlSv,
            newContentMarker: "familjeradgivning-content\" data-v=\"2");

        ReplaceUnlessUpToDate(activities, "Förbön", ForboenHtmlSv,
            newContentMarker: "foerboen-content\" data-v=\"2");

        ReplaceUnlessUpToDate(activities, "Tisdagscafé", TisdagscafeHtmlSv,
            newContentMarker: "tisdagscafe-content\" data-v=\"2");

        ReplaceUnlessUpToDate(activities, "Loppis Axplocket", AxplocketHtmlSv,
            newContentMarker: "axplocket-content\" data-v=\"3",
            newHeroSubtitle: new Trans(
                "250 m² med fina fynd mitt i Mantorp",
                "250 m² of great finds in the heart of Mantorp",
                "250 м² чудових знахідок у центрі Манторпа",
                "250 m² de buenos hallazgos en el centro de Mantorp",
                "250 ตร.ม. ของสินค้ามือสองคุณภาพดีกลางเมืองมันทอร์ป"));
    }

    private void ReplaceIfAnyMatch(IContent activitiesParent, string swedishName, string newHtml, params string[] oldFragments)
    {
        var node = _contentService.GetPagedChildren(activitiesParent.Id, 0, 100, out _, null)
            .FirstOrDefault(c => c.ContentType.Alias == "activity" && c.GetCultureName(I18n.Swedish) == swedishName);
        if (node == null) return;

        var current = node.GetValue<string>("mainContent", I18n.Swedish);
        if (current == null) return;
        if (!oldFragments.Any(f => current.Contains(f))) return;

        _logger.LogInformation("Migrating mainContent for {Name}", swedishName);
        node.SetValue("mainContent", newHtml, I18n.Swedish);
        _contentService.Save(node);
        _contentService.Publish(node, new[] { "*" });
    }

    /// <summary>
    /// Replaces mainContent with newHtml unless the current content already contains the marker
    /// (e.g. a unique CSS class only present in the latest structure). Idempotent across reboots.
    /// </summary>
    private void ReplaceUnlessUpToDate(
        IContent activitiesParent,
        string swedishName,
        string newHtml,
        string newContentMarker,
        Trans? newHeroSubtitle = null)
    {
        var node = _contentService.GetPagedChildren(activitiesParent.Id, 0, 100, out _, null)
            .FirstOrDefault(c => c.ContentType.Alias == "activity" && c.GetCultureName(I18n.Swedish) == swedishName);
        if (node == null) return;

        var current = node.GetValue<string>("mainContent", I18n.Swedish);
        if (current == null) return;
        if (current.Contains(newContentMarker)) return;

        _logger.LogInformation("Migrating mainContent for {Name}", swedishName);
        node.SetValue("mainContent", newHtml, I18n.Swedish);
        if (newHeroSubtitle != null) SetVariantValue(node, "heroSubtitle", newHeroSubtitle);
        _contentService.Save(node);
        _contentService.Publish(node, new[] { "*" });
    }

    /// <summary>
    /// Copies every PNG in wwwroot/images into the Media Library under a "Site Images" folder
    /// so the Hero Image picker has assets to choose from. Idempotent — each file is created
    /// once (matched by name within the folder). Editor renames/deletes/moves are respected:
    /// once a media item leaves this exact folder/name, the seeder treats it as already done
    /// and won't recreate it.
    /// </summary>
    private void SeedMediaLibrary()
    {
        try
        {
            var imagesDir = System.IO.Path.Combine(_env.WebRootPath ?? "", "images");
            if (!System.IO.Directory.Exists(imagesDir))
            {
                _logger.LogInformation("Media seed: wwwroot/images not found at {Dir} — skipping", imagesDir);
                return;
            }

            const string folderName = "Site Images";
            var folder = _mediaService.GetRootMedia()
                .FirstOrDefault(m => m.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
                                  && m.Name == folderName);
            if (folder == null)
            {
                _logger.LogInformation("Creating Media folder '{Name}'", folderName);
                folder = _mediaService.CreateMedia(folderName, Constants.System.Root,
                    Constants.Conventions.MediaTypes.Folder);
                _mediaService.Save(folder);
            }

            // Snapshot existing children once (avoids paged lookup per file).
            var existing = _mediaService.GetPagedChildren(folder.Id, 0, int.MaxValue, out _)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pngFiles = System.IO.Directory.GetFiles(imagesDir, "*.png");
            var created = 0;
            foreach (var path in pngFiles)
            {
                var fileName = System.IO.Path.GetFileName(path);
                if (existing.Contains(fileName)) continue;

                try
                {
                    var media = _mediaService.CreateMedia(fileName, folder.Id,
                        Constants.Conventions.MediaTypes.Image);

                    using (var stream = System.IO.File.OpenRead(path))
                    {
                        media.SetValue(_mediaFileManager, _mediaUrlGenerators, _shortStringHelper,
                            _contentTypeBaseServiceProvider,
                            Constants.Conventions.Media.File, fileName, stream);
                    }
                    _mediaService.Save(media);
                    created++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to seed media item {File}", fileName);
                }
            }

            if (created > 0)
            {
                _logger.LogInformation("Media seed: created {Count} image(s) under '{Folder}'", created, folderName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Media library seeding failed (non-fatal)");
        }
    }

    /// <summary>
    /// Maps each seeded image in "Site Images" to the right page by Swedish name and
    /// fills in the heroImage Media Picker value — only on pages where heroImage is empty,
    /// so editor picks are never overwritten. Idempotent across restarts.
    /// </summary>
    private void AutoAssignHeroImages(IContent home)
    {
        try
        {
            var folder = _mediaService.GetRootMedia()
                .FirstOrDefault(m => m.ContentType.Alias == Constants.Conventions.MediaTypes.Folder
                                  && m.Name == "Site Images");
            if (folder == null)
            {
                _logger.LogInformation("Auto-assign hero: 'Site Images' folder not found — skipping");
                return;
            }

            // Filename → media Key lookup.
            var imagesByName = _mediaService.GetPagedChildren(folder.Id, 0, int.MaxValue, out _)
                .Where(m => m.ContentType.Alias == Constants.Conventions.MediaTypes.Image)
                .GroupBy(m => m.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

            // Swedish page name → filename (default hero for that page).
            var heroByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Top-level
                ["Hem"] = "Elimkyrkan_bulding.png",
                ["Om oss"] = "People_talking_inside_church.png",
                ["Verksamheter"] = "Activities.png",
                ["Kalender"] = "Calendar.png",
                ["Undervisning"] = "Sermon.png",
                ["Kontakt"] = "Contact.png",
                // Activity sub-pages
                ["Barn"] = "Children_playing.png",
                ["Next Generation"] = "Next_Generation.png",
                ["Hemgrupper"] = "Home_group.png",
                ["Mission"] = "Missionary_work.png",
                ["Mansfrukost"] = "Mens_breakfast.png",
                ["Tisdagscafé"] = "Thuseday_cafe.png",
                ["Loppis Axplocket"] = "Axplocket_entry.png",
                ["Familjerådgivning"] = "Teaching_parents.png",
                ["Förbön"] = "Prayer.png",
                // Teaching sub-pages
                ["Predikan"] = "Sermon.png",
                ["Föräldrar"] = "Teaching_parents.png",
            };

            var assigned = 0;
            AssignHeroIfMissing(home, heroByName, imagesByName, ref assigned);

            // Walk children + grandchildren (covers all pages with a heroImage property).
            var level2 = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null).ToList();
            foreach (var c in level2)
            {
                AssignHeroIfMissing(c, heroByName, imagesByName, ref assigned);
                var level3 = _contentService.GetPagedChildren(c.Id, 0, 200, out _, null).ToList();
                foreach (var gc in level3)
                {
                    AssignHeroIfMissing(gc, heroByName, imagesByName, ref assigned);
                }
            }

            if (assigned > 0)
            {
                _logger.LogInformation("Auto-assigned hero images on {Count} page(s)", assigned);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-assign hero images failed (non-fatal)");
        }
    }

    private void AssignHeroIfMissing(
        IContent page,
        Dictionary<string, string> heroByName,
        Dictionary<string, Guid> imagesByName,
        ref int assigned)
    {
        if (!page.HasProperty("heroImage")) return;

        // Idempotency: skip if heroImage already set (non-empty, non-empty-array).
        var current = (page.GetValue<string>("heroImage") ?? "").Trim();
        if (!string.IsNullOrEmpty(current) && current != "[]" && current != "null") return;

        // Match by Swedish name (back-office "primary" name, falls back to node.Name).
        var pageName = page.GetCultureName(I18n.Swedish) ?? page.Name ?? "";
        if (!heroByName.TryGetValue(pageName, out var fileName)) return;
        if (!imagesByName.TryGetValue(fileName, out var mediaKey)) return;

        // MediaPicker3 stored format: JSON array of picker entries.
        var pickerValue = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                key = Guid.NewGuid().ToString(),
                mediaKey = mediaKey.ToString(),
                focalPoint = (object?)null,
                crops = Array.Empty<object>(),
            }
        });

        page.SetValue("heroImage", pickerValue);
        _contentService.Save(page);
        _contentService.Publish(page, new[] { "*" });

        assigned++;
        _logger.LogInformation("Auto-assigned hero {File} → '{Name}'", fileName, pageName);
    }

    /// <summary>
    /// Creates two custom user groups idempotently:
    /// - <b>ChurchAdmin</b>: full content actions across Content/Media/Members/Settings.
    /// - <b>ChurchEditor</b>: limited actions (browse/create/update/publish) on Content/Media only,
    ///   start node pinned to Home so they can't navigate outside the site tree.
    /// Existing groups are left untouched — re-running the composer won't clobber edits
    /// made through Settings → User Groups in the backoffice.
    /// </summary>
    private void EnsureUserGroups(IContent home)
    {
        try
        {
            EnsureUserGroup(
                alias: "churchAdmin",
                name: "Church Admin",
                icon: "icon-users",
                fullActions: true,
                startContentId: null);

            EnsureUserGroup(
                alias: "churchEditor",
                name: "Church Editor",
                icon: "icon-edit",
                fullActions: false,
                startContentId: home.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnsureUserGroups failed (non-fatal)");
        }
    }

    private void EnsureUserGroup(string alias, string name, string icon, bool fullActions, int? startContentId)
    {
        var existing = _userGroupService.GetAsync(alias).GetAwaiter().GetResult();
        if (existing != null)
        {
            // Respect any backoffice edits — never mutate an existing group.
            return;
        }

        _logger.LogInformation("Creating user group '{Name}' ({Alias})", name, alias);

        var group = new UserGroup(_shortStringHelper)
        {
            Alias = alias,
            Name = name,
            Icon = icon,
            HasAccessToAllLanguages = true,
        };

        // Sections.
        group.AddAllowedSection(Constants.Applications.Content);
        group.AddAllowedSection(Constants.Applications.Media);
        if (fullActions)
        {
            group.AddAllowedSection(Constants.Applications.Members);
            group.AddAllowedSection(Constants.Applications.Settings);
        }

        // Permissions — ActionLetter is a v17 alias string like "Umb.Document.Read".
        var perms = new HashSet<string>
        {
            ActionBrowse.ActionLetter,
            ActionNew.ActionLetter,
            ActionUpdate.ActionLetter,
            ActionPublish.ActionLetter,
        };
        if (fullActions)
        {
            perms.Add(ActionUnpublish.ActionLetter);
            perms.Add(ActionDelete.ActionLetter);
            perms.Add(ActionSort.ActionLetter);
            perms.Add(ActionMove.ActionLetter);
            perms.Add(ActionCopy.ActionLetter);
            perms.Add(ActionRollback.ActionLetter);
        }
        group.Permissions = perms;

        if (startContentId.HasValue)
        {
            group.StartContentId = startContentId.Value;
        }

        var attempt = _userGroupService
            .CreateAsync(group, Constants.Security.SuperUserKey)
            .GetAwaiter()
            .GetResult();

        if (!attempt.Success)
        {
            _logger.LogWarning(
                "Creating user group '{Name}' returned status {Status}",
                name, attempt.Status);
        }
    }

    /// <summary>
    /// One-shot migration: sets every existing backoffice user's UI language to Swedish
    /// (sv-SE). Tracked via a key-value flag so this runs exactly once. If a user later
    /// changes their language back to English in their profile, this method won't undo
    /// it on subsequent boots — the flag prevents re-execution.
    /// New users get sv-SE via <see cref="BackofficeLanguageHandler"/> at save time.
    /// </summary>
    private void EnsureBackofficeLanguageSwedish()
    {
        const string flagKey = "elim.backoffice-language-sv.v1";
        const string targetLanguage = "sv-SE";

        try
        {
            var existing = _keyValueService.GetValue(flagKey);
            if (!string.IsNullOrEmpty(existing))
            {
                return; // migration already ran
            }

            var users = _userService.GetAll(0, int.MaxValue, out _);
            var updated = 0;
            foreach (var user in users)
            {
                if (string.Equals(user.Language, targetLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                user.Language = targetLanguage;
                _userService.Save(user);
                updated++;
            }

            if (updated > 0)
            {
                _logger.LogInformation(
                    "Set backoffice language to {Lang} for {Count} existing user(s)",
                    targetLanguage, updated);
            }

            _keyValueService.SetValue(flagKey, "completed-" + DateTime.UtcNow.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnsureBackofficeLanguageSwedish failed (non-fatal)");
        }
    }

    private void EnsureLanguages()
    {
        EnsureLanguage(I18n.Swedish, "Svenska", isDefault: true);
        EnsureLanguage(I18n.English, "English");
        EnsureLanguage(I18n.Ukrainian, "Українська");
        EnsureLanguage(I18n.Spanish, "Español");
        EnsureLanguage(I18n.Thai, "ไทย");
    }

    private void EnsureLanguage(string iso, string name, bool isDefault = false)
    {
        var existing = _localizationService.GetLanguageByIsoCode(iso);
        if (existing != null)
        {
            if (isDefault && !existing.IsDefault)
            {
                existing.IsDefault = true;
                _localizationService.Save(existing);
            }
            return;
        }

        _logger.LogInformation("Creating language {Iso}", iso);
        var lang = new Language(iso, name) { IsDefault = isDefault };
        _localizationService.Save(lang);
    }

    private void ResolveDataTypes()
    {
        var all = _dataTypeService.GetAll().ToList();
        _textstring = all.FirstOrDefault(d => d.Name == "Textstring")
            ?? throw new InvalidOperationException("'Textstring' data type not found");
        _textarea = all.FirstOrDefault(d => d.Name == "Textarea")
            ?? throw new InvalidOperationException("'Textarea' data type not found");
        _richtext = all.FirstOrDefault(d => d.EditorAlias == "Umbraco.RichText")
            ?? all.FirstOrDefault(d => d.Name == "Richtext editor")
            ?? throw new InvalidOperationException("Rich Text Editor data type not found");
        _mediaPicker = all.FirstOrDefault(d => d.Name == "Image Media Picker")
            ?? all.FirstOrDefault(d => d.EditorAlias == "Umbraco.MediaPicker3");
        if (_mediaPicker == null)
        {
            _logger.LogWarning("No Media Picker data type found - heroImage property will be skipped");
        }
        _datePicker = all.FirstOrDefault(d => d.Name == "Date Picker with time")
            ?? all.FirstOrDefault(d => d.Name == "Date Picker")
            ?? all.FirstOrDefault(d => d.EditorAlias == "Umbraco.DateTime");
        _trueFalse = all.FirstOrDefault(d => d.Name == "True/false")
            ?? all.FirstOrDefault(d => d.EditorAlias == "Umbraco.TrueFalse");
        if (_datePicker == null || _trueFalse == null)
        {
            _logger.LogWarning("Date Picker or True/false data type missing - Event content type will be skipped");
        }

        _eventTypeDropdown = EnsureEventTypeDropdown(all);
        _dayOfWeekDropdown = EnsureSimpleDropdown(
            "Day of Week Dropdown",
            new[] { "Måndag", "Tisdag", "Onsdag", "Torsdag", "Fredag", "Lördag", "Söndag" });
    }

    /// <summary>
    /// Trims the Tiptap-based Rich Text Editor toolbar down to a minimal set:
    /// Bold, Italic, H2, H3, Bullet list, Ordered list, Link. Idempotent — counts the
    /// current toolbar buttons via regex on the serialized config; if already at the
    /// target count (7), assumes the trim is in place and skips. This means an editor
    /// can manually adjust the toolbar in the backoffice (Settings → Data Types →
    /// Richtext editor) and the change sticks across restarts, as long as they don't
    /// happen to leave it at exactly 7 different buttons.
    /// </summary>
    private void ConfigureRichTextToolbar()
    {
        try
        {
            if (_richtext == null) return;

            const int targetButtonCount = 7;

            // Count buttons in the current toolbar by regex over the serialized JSON —
            // robust against whatever runtime type the deserialized config is in
            // (string[][][], List<...>, JsonElement, JArray, etc.).
            object? currentToolbar = _richtext.ConfigurationData.TryGetValue("toolbar", out var tb) ? tb : null;
            var currentJson = currentToolbar == null
                ? ""
                : System.Text.Json.JsonSerializer.Serialize(currentToolbar);
            var currentCount = System.Text.RegularExpressions.Regex
                .Matches(currentJson, @"Umb\.Tiptap\.Toolbar\.")
                .Count;

            if (currentCount == targetButtonCount)
            {
                // Already trimmed (either by this code on a previous boot, or by an editor).
                return;
            }

            _logger.LogInformation(
                "Trimming Rich Text Editor toolbar from {Before} to {After} buttons",
                currentCount, targetButtonCount);

            // Tiptap toolbar shape: rows[] → groups[] → buttons[] (3 levels).
            // One row, one group, 7 buttons.
            _richtext.ConfigurationData["toolbar"] = new List<List<List<string>>>
            {
                new()
                {
                    new()
                    {
                        "Umb.Tiptap.Toolbar.Bold",
                        "Umb.Tiptap.Toolbar.Italic",
                        "Umb.Tiptap.Toolbar.Heading2",
                        "Umb.Tiptap.Toolbar.Heading3",
                        "Umb.Tiptap.Toolbar.BulletList",
                        "Umb.Tiptap.Toolbar.OrderedList",
                        "Umb.Tiptap.Toolbar.Link",
                    }
                }
            };

            // Toolbar buttons require their underlying Tiptap extensions to also be enabled,
            // otherwise the buttons are hidden at render time.
            _richtext.ConfigurationData["extensions"] = new[]
            {
                "Umb.Tiptap.Bold",
                "Umb.Tiptap.Italic",
                "Umb.Tiptap.Heading",
                "Umb.Tiptap.BulletList",
                "Umb.Tiptap.OrderedList",
                "Umb.Tiptap.Link",
            };

            var attempt = _dataTypeService
                .UpdateAsync(_richtext, Constants.Security.SuperUserKey)
                .GetAwaiter()
                .GetResult();

            if (!attempt.Success)
            {
                _logger.LogWarning(
                    "Rich Text Editor UpdateAsync returned status {Status}",
                    attempt.Status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Configuring Rich Text Editor toolbar failed (non-fatal)");
        }
    }

    private void EnsureLinkUrlProperty(IContentType eventCt)
    {
        if (_textstring == null) return;
        if (eventCt.PropertyTypes.Any(p => p.Alias == "linkUrl")) return;

        try
        {
            var group = eventCt.PropertyGroups.FirstOrDefault(g => g.Alias == "event")
                ?? eventCt.PropertyGroups.FirstOrDefault();
            if (group == null) return;

            _logger.LogInformation("Adding linkUrl property to Event content type");
            group.PropertyTypes!.Add(MakeProp(_textstring, "linkUrl",
                "Link URL (optional — internal like /verksamheter/next-generation, or external https://...)",
                (group.PropertyTypes?.Count ?? 0), variant: false));
            _contentTypeService.Save(eventCt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add linkUrl property");
        }
    }

    private static readonly Dictionary<string, string> DefaultEventLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tisdagscafé"] = "/verksamheter/tisdagscafe",
        ["Tisdagscafe"] = "/verksamheter/tisdagscafe",
        ["Tuesday Café"] = "/verksamheter/tisdagscafe",
        ["Hemgrupper"] = "/verksamheter/hemgrupper",
        ["Home Groups"] = "/verksamheter/hemgrupper",
        ["Mansfrukost"] = "/verksamheter/mansfrukost",
        ["Men's Breakfast"] = "/verksamheter/mansfrukost",
        ["Next Generation"] = "/verksamheter/next-generation",
        ["Loppis Axplocket"] = "/verksamheter/loppis-axplocket",
        ["Loppis Axplocket Saturday"] = "/verksamheter/loppis-axplocket",
        ["Loppis Axplocket Wednesday"] = "/verksamheter/loppis-axplocket",
        ["Öppet café"] = "/verksamheter/tisdagscafe",
        ["Open Café"] = "/verksamheter/tisdagscafe",
        ["Barn"] = "/verksamheter/barn",
        ["Children"] = "/verksamheter/barn",
        ["Mission"] = "/verksamheter/mission",
        ["Familjerådgivning"] = "/verksamheter/familjeraadgivning",
        ["Family Counseling"] = "/verksamheter/familjeraadgivning",
        ["Förbön"] = "/verksamheter/foerboen",
        ["Prayer"] = "/verksamheter/foerboen",
    };

    private void SeedDefaultEventLinks(IContent calendar)
    {
        var fixedCount = 0;
        foreach (var ev in _contentService.GetPagedChildren(calendar.Id, 0, 1000, out _, null)
                     .Where(c => c.ContentType.Alias == "event"))
        {
            if (!string.IsNullOrEmpty(ev.GetValue<string>("linkUrl"))) continue; // already set

            // Match by Swedish or English culture name (with copy-suffix stripped).
            string? matchedUrl = null;
            foreach (var iso in new[] { I18n.Swedish, I18n.English })
            {
                var name = StripCopySuffixes(ev.GetCultureName(iso) ?? "");
                if (string.IsNullOrEmpty(name)) continue;
                if (DefaultEventLinks.TryGetValue(name, out matchedUrl)) break;
            }
            if (string.IsNullOrEmpty(matchedUrl))
            {
                // Fallback to invariant Name
                var fallback = StripCopySuffixes(ev.Name ?? "");
                DefaultEventLinks.TryGetValue(fallback, out matchedUrl);
            }
            if (string.IsNullOrEmpty(matchedUrl)) continue;

            ev.SetValue("linkUrl", matchedUrl);
            _contentService.Save(ev);
            _contentService.Publish(ev, new[] { "*" });
            fixedCount++;
        }
        if (fixedCount > 0)
        {
            _logger.LogInformation("Seeded default linkUrl on {Count} event node(s)", fixedCount);
        }
    }

    private void RemoveRedundantRecurrenceProperty(IContentType eventCt)
    {
        var prop = eventCt.PropertyTypes.FirstOrDefault(p => p.Alias == "recurrenceInterval");
        if (prop == null) return;

        try
        {
            _logger.LogInformation("Removing redundant recurrenceInterval property from Event content type");
            eventCt.RemovePropertyType("recurrenceInterval");
            _contentTypeService.Save(eventCt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove recurrenceInterval property");
        }
    }

    private void RevertEventTypeToTextstringIfNeeded(IContentType eventCt)
    {
        if (_textstring == null) return;
        var prop = eventCt.PropertyTypes.FirstOrDefault(p => p.Alias == "eventType");
        if (prop == null) return;
        if (prop.DataTypeId == _textstring.Id) return; // already Textstring

        try
        {
            _logger.LogInformation("Reverting eventType property back to Textstring");
            prop.DataTypeId = _textstring.Id;
            prop.PropertyEditorAlias = _textstring.EditorAlias;
            prop.Name = "Event Type (\"Weekly\", \"Even Weeks\", \"Odd Weeks\", \"Every third week\", \"Monthly\")";
            _contentTypeService.Save(eventCt);

            // Unwrap any JSON-array eventType values written during the dropdown attempt
            UnwrapEventTypeJsonValues();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not revert eventType property");
        }
    }

    private void UnwrapEventTypeJsonValues()
    {
        var fixedCount = 0;
        foreach (var root in _contentService.GetRootContent())
        {
            fixedCount += UnwrapEventTypeJsonUnder(root);
        }
        if (fixedCount > 0)
        {
            _logger.LogInformation("Unwrapped JSON eventType values on {Count} event node(s)", fixedCount);
        }
    }

    private int UnwrapEventTypeJsonUnder(IContent parent)
    {
        var fixedCount = 0;
        foreach (var child in _contentService.GetPagedChildren(parent.Id, 0, 1000, out _, null))
        {
            if (child.ContentType.Alias == "event")
            {
                var current = (child.GetValue<string>("eventType") ?? "").Trim();
                if (current.StartsWith("[") && current.EndsWith("]"))
                {
                    // Strip array wrapper and quotes; e.g. ["Weekly"] -> Weekly
                    try
                    {
                        var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(current);
                        var first = arr?.FirstOrDefault();
                        if (!string.IsNullOrEmpty(first))
                        {
                            child.SetValue("eventType", first);
                            _contentService.Save(child);
                            _contentService.Publish(child, new[] { "*" });
                            fixedCount++;
                        }
                    }
                    catch
                    {
                        // ignore malformed values
                    }
                }
            }
            fixedCount += UnwrapEventTypeJsonUnder(child);
        }
        return fixedCount;
    }

    private void MigrateDayOfWeekPropertyToDropdown(IContentType eventCt)
    {
        if (_dayOfWeekDropdown == null) return;
        var prop = eventCt.PropertyTypes.FirstOrDefault(p => p.Alias == "dayOfWeek");
        if (prop == null) return;

        var swapping = prop.DataTypeId != _dayOfWeekDropdown.Id;
        try
        {
            if (swapping)
            {
                _logger.LogInformation("Migrating dayOfWeek property from {Old} to dropdown", prop.PropertyEditorAlias);
                prop.DataTypeId = _dayOfWeekDropdown.Id;
                prop.PropertyEditorAlias = _dayOfWeekDropdown.EditorAlias;
                prop.Name = "Day of Week";
                _contentTypeService.Save(eventCt);
            }
            // Reformat any existing bare-string values to the dropdown's JSON-array format.
            ReformatPlainStringPropToJsonArray("dayOfWeek");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not migrate dayOfWeek property to dropdown");
        }
    }

    private void ReformatPlainStringPropToJsonArray(string propertyAlias)
    {
        var fixedCount = 0;
        foreach (var root in _contentService.GetRootContent())
        {
            fixedCount += ReformatUnder(root, propertyAlias);
        }
        if (fixedCount > 0)
        {
            _logger.LogInformation("Reformatted {Alias} values on {Count} event node(s)", propertyAlias, fixedCount);
        }
    }

    private int ReformatUnder(IContent parent, string propertyAlias)
    {
        var fixedCount = 0;
        foreach (var child in _contentService.GetPagedChildren(parent.Id, 0, 1000, out _, null))
        {
            if (child.ContentType.Alias == "event")
            {
                var current = (child.GetValue<string>(propertyAlias) ?? "").Trim();
                if (!string.IsNullOrEmpty(current) && !current.StartsWith("["))
                {
                    var escaped = current.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    child.SetValue(propertyAlias, $"[\"{escaped}\"]");
                    _contentService.Save(child);
                    _contentService.Publish(child, new[] { "*" });
                    fixedCount++;
                }
            }
            fixedCount += ReformatUnder(child, propertyAlias);
        }
        return fixedCount;
    }

    private void MigrateEventTypePropertyToDropdown(IContentType eventCt)
    {
        if (_eventTypeDropdown == null) return;
        var prop = eventCt.PropertyTypes.FirstOrDefault(p => p.Alias == "eventType");
        if (prop == null) return;

        var swapping = prop.DataTypeId != _eventTypeDropdown.Id;
        try
        {
            if (swapping)
            {
                _logger.LogInformation("Migrating eventType property from {Old} to dropdown", prop.PropertyEditorAlias);
                prop.DataTypeId = _eventTypeDropdown.Id;
                prop.PropertyEditorAlias = _eventTypeDropdown.EditorAlias;
                prop.Name = "Event Type";
                _contentTypeService.Save(eventCt);
            }
            // Always check the existing values; they may have been written as bare strings before
            // the swap and need to be re-formatted as the JSON array the dropdown expects.
            MigrateEventTypeStoredValues();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not migrate eventType property to dropdown - leaving as Textstring");
        }
    }

    private void MigrateEventTypeStoredValues()
    {
        // Walk the content tree, find any Event nodes whose eventType value is still a plain
        // string (e.g. "Weekly") and rewrite it as a JSON array ("[\"Weekly\"]") which is the
        // format Umbraco.DropDown.Flexible's value converter expects.
        var fixedCount = 0;
        foreach (var root in _contentService.GetRootContent())
        {
            fixedCount += FixEventTypeValuesUnder(root);
        }
        if (fixedCount > 0)
        {
            _logger.LogInformation("Reformatted eventType values on {Count} event node(s)", fixedCount);
        }
    }

    private int FixEventTypeValuesUnder(IContent parent)
    {
        var fixedCount = 0;
        foreach (var child in _contentService.GetPagedChildren(parent.Id, 0, 1000, out _, null))
        {
            if (child.ContentType.Alias == "event")
            {
                var current = child.GetValue<string>("eventType");
                if (!string.IsNullOrEmpty(current) && !current.TrimStart().StartsWith("["))
                {
                    // Wrap bare string as JSON array. Escape any quotes/backslashes inside.
                    var escaped = current.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    child.SetValue("eventType", $"[\"{escaped}\"]");
                    _contentService.Save(child);
                    _contentService.Publish(child, new[] { "*" });
                    fixedCount++;
                }
            }
            fixedCount += FixEventTypeValuesUnder(child);
        }
        return fixedCount;
    }

    private IDataType? EnsureEventTypeDropdown(List<IDataType> allDataTypes)
    {
        return EnsureSimpleDropdown(
            "Event Type Dropdown",
            new[] { "Weekly", "Even Weeks", "Odd Weeks", "Every third week", "Monthly" },
            allDataTypes);
    }

    private IDataType? EnsureSimpleDropdown(string name, string[] items, List<IDataType>? cached = null)
    {
        var all = cached ?? _dataTypeService.GetAll().ToList();
        var existing = all.FirstOrDefault(d => d.Name == name);
        if (existing != null)
        {
            if (string.IsNullOrEmpty(existing.EditorUiAlias))
            {
                try
                {
                    existing.EditorUiAlias = "Umb.PropertyEditorUi.Dropdown";
                    _dataTypeService.Save(existing);
                    _logger.LogInformation("Set EditorUiAlias on existing '{Name}' data type", name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not set EditorUiAlias on '{Name}'", name);
                }
            }
            return existing;
        }

        try
        {
            var editor = _propertyEditors.FirstOrDefault(e => e.Alias == "Umbraco.DropDown.Flexible");
            if (editor == null)
            {
                _logger.LogWarning("Umbraco.DropDown.Flexible editor not found - skipping '{Name}'", name);
                return null;
            }

            _logger.LogInformation("Creating '{Name}' data type", name);
            var dt = new DataType(editor, _configEditorSerializer)
            {
                Name = name,
                EditorUiAlias = "Umb.PropertyEditorUi.Dropdown",
                ConfigurationData = new Dictionary<string, object>
                {
                    ["items"] = items,
                    ["multiple"] = false,
                },
            };
            _dataTypeService.Save(dt);
            return _dataTypeService.GetAll().FirstOrDefault(d => d.Name == name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create '{Name}' data type - will fall back to Textstring", name);
            return null;
        }
    }

    /// <summary>
    /// Creates 4 reusable Element content types (Mission country, Bible verse, Counseling principle,
    /// Axplocket area) and one shared "Activity Content Blocks" Block List data type that allows
    /// any of them. The Block List property gets added to the Activity content type in
    /// <see cref="EnsureContentBlocksProperty"/> so each activity sub-page can use whichever
    /// block type fits its structured content.
    /// </summary>
    private void EnsureContentBlocks()
    {
        try
        {
            _elementMissionCountry = EnsureElementType(
                "missionCountry", "Mission country", "icon-globe",
                ("countryName", "Country (e.g. \"Argentina – Evangelium i bergen\")", false),
                ("flagUrl", "Flag image URL (e.g. https://flagcdn.com/w320/ar.png)", false),
                ("description", "Description", true),
                ("giftMark", "Gift mark (single country name, e.g. \"Argentina\")", false));

            _elementBibleVerse = EnsureElementType(
                "bibleVerse", "Bible verse", "icon-book-alt",
                ("reference", "Reference (e.g. \"Fil. 4:6–7\")", false),
                ("verseText", "Verse text", true));

            _elementCounselingPrinciple = EnsureElementType(
                "counselingPrinciple", "Counseling principle", "icon-check",
                ("icon", "Icon (emoji)", false),
                ("title", "Title", false));

            _elementAxplocketArea = EnsureElementType(
                "axplocketArea", "Axplocket area", "icon-picture",
                ("imageUrl", "Image URL (e.g. /images/Axplocket_book_area.png)", false),
                ("caption", "Caption", false));

            _elementYoutubeVideo = EnsureElementType(
                "youtubeVideo", "YouTube video", "icon-video",
                ("youtubeUrl", "YouTube URL (any format: watch?v=…, youtu.be/…, embed/…, shorts/…)", false),
                ("title", "Title (optional, shown above the embed)", false),
                ("description", "Description (optional, shown below the embed)", true));

            _elementResourceCard = EnsureElementType(
                "resourceCard", "Resource card", "icon-book-alt",
                ("icon", "Icon (emoji, e.g. 📖 or 📘)", false),
                ("title", "Title (e.g. \"Upptäckande bibelläsning\")", false),
                ("description", "Description (short paragraph)", true),
                ("ctaUrl", "Link URL (where the button goes)", false),
                ("ctaLabel", "Button label (e.g. \"Ladda ner PDF\" or \"Köp boken\")", false));

            var elements = new[]
            {
                _elementMissionCountry,
                _elementBibleVerse,
                _elementCounselingPrinciple,
                _elementAxplocketArea,
                _elementYoutubeVideo,
                _elementResourceCard,
            }.Where(e => e != null).Select(e => e!).ToArray();

            if (elements.Length > 0)
            {
                _blockListContent = EnsureBlockListDataType("Activity Content Blocks", elements);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnsureContentBlocks failed (non-fatal)");
        }
    }

    private IContentType? EnsureElementType(
        string alias, string name, string icon,
        params (string Alias, string Name, bool UseTextarea)[] props)
    {
        var existing = _contentTypeService.Get(alias);
        if (existing != null) return existing;
        if (_textstring == null || _textarea == null) return null;

        _logger.LogInformation("Creating element content type '{Name}'", name);

        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = alias,
            Name = name,
            Icon = icon,
            IsElement = true,
            Variations = ContentVariation.Nothing,
        };

        var group = new PropertyGroup(true)
        {
            Name = "Content",
            Alias = "content",
            SortOrder = 0,
        };

        var sort = 0;
        foreach (var (propAlias, propName, useTextarea) in props)
        {
            var dt = useTextarea ? _textarea : _textstring;
            group.PropertyTypes!.Add(new PropertyType(_shortStringHelper, dt)
            {
                Alias = propAlias,
                Name = propName,
                SortOrder = sort++,
                Variations = ContentVariation.Nothing,
            });
        }

        ct.PropertyGroups.Add(group);
        _contentTypeService.Save(ct);
        return _contentTypeService.Get(alias);
    }

    private IDataType? EnsureBlockListDataType(string name, IContentType[] elementTypes)
    {
        var existing = _dataTypeService.GetAll().FirstOrDefault(d => d.Name == name);

        var editor = _propertyEditors.FirstOrDefault(e => e.Alias == "Umbraco.BlockList");
        if (editor == null)
        {
            _logger.LogWarning("Umbraco.BlockList property editor not found — skipping '{Name}'", name);
            return existing;
        }

        try
        {
            if (existing != null)
            {
                // Reconcile: add any element type whose key isn't already in the blocks
                // array. This lets later code add new block types (e.g. youtubeVideo)
                // to a data type that was first created with fewer types.
                var serializedBlocks = existing.ConfigurationData.TryGetValue("blocks", out var raw)
                    ? System.Text.Json.JsonSerializer.Serialize(raw)
                    : "";
                var expectedKeys = elementTypes.Select(et => et.Key.ToString()).ToArray();
                var missing = expectedKeys
                    .Where(k => !serializedBlocks.Contains(k, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (missing.Length == 0) return existing;

                _logger.LogInformation(
                    "Adding {Count} missing element type(s) to Block List '{Name}'",
                    missing.Length, name);

                existing.ConfigurationData["blocks"] = elementTypes
                    .Select(et => (object)BuildBlockConfigEntry(et.Key))
                    .ToArray();
                _dataTypeService.Save(existing);
                return existing;
            }

            _logger.LogInformation("Creating Block List data type '{Name}'", name);

            var blocksArr = elementTypes
                .Select(et => (object)BuildBlockConfigEntry(et.Key))
                .ToArray();

            var dt = new DataType(editor, _configEditorSerializer)
            {
                Name = name,
                EditorUiAlias = "Umb.PropertyEditorUi.BlockList",
                ConfigurationData = new Dictionary<string, object>
                {
                    ["blocks"] = blocksArr,
                    ["validationLimit"] = new Dictionary<string, object?>
                    {
                        ["min"] = null,
                        ["max"] = null,
                    },
                    ["useInlineEditingAsDefault"] = false,
                    ["useLiveEditing"] = false,
                },
            };
            _dataTypeService.Save(dt);
            return _dataTypeService.GetAll().FirstOrDefault(d => d.Name == name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create or update Block List data type '{Name}'", name);
            return existing;
        }
    }

    private static Dictionary<string, object?> BuildBlockConfigEntry(Guid elementTypeKey) => new()
    {
        ["contentElementTypeKey"] = elementTypeKey.ToString(),
        ["settingsElementTypeKey"] = null,
        ["labelTemplate"] = null,
        ["view"] = null,
        ["stylesheet"] = null,
        ["iconColor"] = null,
        ["backgroundColor"] = null,
        ["editorSize"] = null,
        ["forceHideContentEditorInOverlay"] = false,
        ["allowAtRoot"] = true,
        ["allowInAreas"] = false,
        ["thumbnail"] = null,
    };

    private void EnsureContentBlocksProperty(IContentType activityCt)
    {
        if (_blockListContent == null) return;
        if (activityCt.PropertyTypes.Any(p => p.Alias == "contentBlocks")) return;

        try
        {
            _logger.LogInformation("Adding contentBlocks property to activity content type");

            var group = activityCt.PropertyGroups.FirstOrDefault(g => g.Alias == "content")
                ?? activityCt.PropertyGroups.FirstOrDefault();
            if (group == null) return;

            group.PropertyTypes!.Add(new PropertyType(_shortStringHelper, _blockListContent)
            {
                Alias = "contentBlocks",
                Name = "Content Blocks",
                SortOrder = (group.PropertyTypes?.Count ?? 0),
                Variations = ContentVariation.Nothing,
            });
            _contentTypeService.Save(activityCt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add contentBlocks property");
        }
    }

    private record MissionCountrySeed(string CountryName, string FlagUrl, string Description, string GiftMark);

    private static readonly MissionCountrySeed[] MissionCountrySeeds = new[]
    {
        new MissionCountrySeed("Argentina – Evangelium i bergen", "https://flagcdn.com/w320/ar.png",
            "Vi stödjer Marcures, som reser ut i bergen i Argentina för att sprida evangeliet i avlägsna byar och samhällen. Hans arbete bär frukt på platser där det annars är svårt att nå.",
            "Argentina"),
        new MissionCountrySeed("Estland – Village of Hope", "https://flagcdn.com/w320/ee.png",
            "Under senare år har vi haft ett särskilt fokus på Estland, där vi har många goda kontakter. Ett arbete som ligger oss extra varmt om hjärtat är Village of Hope, där vi ser hur människor får hjälp, hopp och nya möjligheter.",
            "Estland"),
        new MissionCountrySeed("Indien – Stöd till lokalt församlingsarbete", "https://flagcdn.com/w320/in.png",
            "I Indien är vi med och stödjer lokalt församlingsarbete som når människor i sårbara situationer. Genom våra kontakter får evangeliet och praktisk hjälp nå nya områden.",
            "Indien"),
        new MissionCountrySeed("Israel – Stödpaket", "https://flagcdn.com/w320/il.png",
            "Vi skickar regelbundet stödpaket till Israel för att hjälpa människor i behov. Det är ett konkret sätt att vara med och göra skillnad på en plats som ligger nära Bibelns hjärta.",
            "Israel"),
        new MissionCountrySeed("Portugal – Fängelsemission", "https://flagcdn.com/w320/pt.png",
            "I Portugal stödjer vi två missionärer som arbetar med fängelsemission. Genom dem får vi vara med och nå människor i en av samhällets allra mest utsatta miljöer med evangeliet, hopp och praktisk omsorg.",
            "Portugal"),
        new MissionCountrySeed("Thailand – Ingvar och Anna Fredriksson", "https://flagcdn.com/w320/th.png",
            "Vi stödjer missionärerna Ingvar och Anna Fredriksson i Thailand. De är en del av vårt långsiktiga engagemang för mission utanför Sveriges gränser.",
            "Thailand"),
        new MissionCountrySeed("Ukraina – Stödpaket och hjälp till flyktingar", "https://flagcdn.com/w320/ua.png",
            "Vi skickar då och då stödpaket till Ukraina och hjälper människor från Ukraina – både på plats och flyktingar här i Sverige.",
            "Ukraina"),
    };

    /// <summary>
    /// Seeds the Mission activity page with 7 country blocks. Idempotent — only writes
    /// the value if contentBlocks is currently empty. The existing mainContent HTML is
    /// left untouched so the page still renders correctly until templates are updated.
    /// </summary>
    private void SeedMissionPageBlocks(IContent home)
    {
        if (_elementMissionCountry == null || _blockListContent == null) return;

        try
        {
            var activitiesPage = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "activitiesPage");
            if (activitiesPage == null) return;

            var mission = _contentService.GetPagedChildren(activitiesPage.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "activity"
                                  && (c.GetCultureName(I18n.Swedish) ?? c.Name) == "Mission");
            if (mission == null) return;

            var current = mission.GetValue<string>("contentBlocks");
            if (!string.IsNullOrWhiteSpace(current) && current.Contains("contentTypeKey"))
            {
                return; // already populated
            }

            _logger.LogInformation("Seeding Mission page with {Count} country blocks", MissionCountrySeeds.Length);

            var json = BuildBlockListValue(
                _elementMissionCountry.Key,
                MissionCountrySeeds.Select(s => new (string alias, string value)[]
                {
                    ("countryName", s.CountryName),
                    ("flagUrl", s.FlagUrl),
                    ("description", s.Description),
                    ("giftMark", s.GiftMark),
                }));

            mission.SetValue("contentBlocks", json);
            _contentService.Save(mission);
            _contentService.Publish(mission, new[] { "*" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeedMissionPageBlocks failed (non-fatal)");
        }
    }

    private static readonly (string alias, string value)[][] BibleVerseSeeds = new[]
    {
        new (string alias, string value)[]
        {
            ("reference", "Fil. 4:6–7"),
            ("verseText", "Gör er inga bekymmer för något utan låt Gud i allt få veta era önskningar genom åkallan och bön med tacksägelse. Då skall Guds frid, som övergår allt förstånd, bevara era hjärtan och era tankar i Kristus Jesus."),
        },
        new (string alias, string value)[]
        {
            ("reference", "Matt. 7:7–11"),
            ("verseText", "Bed och ni skall få, sök och ni skall finna, bulta och dörren skall öppnas för er. Ty var och en som ber, han får, och den som söker, han finner, och för den som bultar skall dörren öppnas. Vem bland er ger sin son en sten, när han ber om bröd, eller en orm, när han ber om en fisk? Om ni som är onda förstår att ge era barn goda gåvor, hur mycket mer skall då inte er Fader i himlen ge det som är gott åt dem som ber honom."),
        },
    };

    private static readonly (string alias, string value)[][] CounselingPrincipleSeeds = new[]
    {
        new (string alias, string value)[]
        {
            ("icon", "✝️"),
            ("title", "Kristen värdegrund"),
        },
        new (string alias, string value)[]
        {
            ("icon", "🔒"),
            ("title", "Tystnadsplikt"),
        },
        new (string alias, string value)[]
        {
            ("icon", "🎓"),
            ("title", "Certifierade familjerådgivare"),
        },
    };

    private static readonly (string alias, string value)[][] AxplocketAreaSeeds = new[]
    {
        new (string alias, string value)[]
        {
            ("imageUrl", "/images/Axplocket_decoration_area.png"),
            ("caption", "Dekorationer"),
        },
        new (string alias, string value)[]
        {
            ("imageUrl", "/images/Axplocket_tableware_and_music_area.png"),
            ("caption", "Servisgods & musik"),
        },
        new (string alias, string value)[]
        {
            ("imageUrl", "/images/Axplocket_toys_area.png"),
            ("caption", "Leksaker"),
        },
        new (string alias, string value)[]
        {
            ("imageUrl", "/images/Axplocket_technical_area.png"),
            ("caption", "Verktyg & prylar"),
        },
        new (string alias, string value)[]
        {
            ("imageUrl", "/images/Axplocket_book_area.png"),
            ("caption", "Böcker"),
        },
    };

    /// <summary>
    /// Generic seeder for any activity sub-page's contentBlocks property. Idempotent —
    /// only writes when the property is empty. Used by Förbön, Familjerådgivning, and
    /// Loppis Axplocket; Mission has its own dedicated seeder for historical reasons.
    /// </summary>
    private void SeedActivityPageBlocks(
        IContent home,
        string activitySwedishName,
        IContentType? elementType,
        (string alias, string value)[][] blockData)
    {
        if (elementType == null || _blockListContent == null) return;

        try
        {
            var activitiesPage = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "activitiesPage");
            if (activitiesPage == null) return;

            var page = _contentService.GetPagedChildren(activitiesPage.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "activity"
                                  && (c.GetCultureName(I18n.Swedish) ?? c.Name) == activitySwedishName);
            if (page == null) return;

            var current = page.GetValue<string>("contentBlocks");
            if (!string.IsNullOrWhiteSpace(current) && current.Contains("contentTypeKey"))
            {
                return; // already populated
            }

            _logger.LogInformation(
                "Seeding {Page} page with {Count} block(s)",
                activitySwedishName, blockData.Length);

            var json = BuildBlockListValue(elementType.Key, blockData);
            page.SetValue("contentBlocks", json);
            _contentService.Save(page);
            _contentService.Publish(page, new[] { "*" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seeding {Page} blocks failed (non-fatal)", activitySwedishName);
        }
    }

    // YouTube videos for the "Rusta dina barn" series under Undervisning → Föräldrar.
    // Order matters: index 0 = first/oldest video, future additions go to the end via
    // the backoffice "+ Add youtubeVideo" button.
    private static readonly (string alias, string value)[][] RustaDinaBarnVideos = new[]
    {
        new (string alias, string value)[] { ("title", "Avsnitt 1"), ("youtubeUrl", "https://www.youtube.com/watch?v=AXshLkKoXB4"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 2"), ("youtubeUrl", "https://www.youtube.com/watch?v=AM0s9eVvbVY"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 3"), ("youtubeUrl", "https://www.youtube.com/watch?v=QfgFGr4gibw"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 4"), ("youtubeUrl", "https://www.youtube.com/watch?v=zVJSsNqxKls"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 5"), ("youtubeUrl", "https://www.youtube.com/watch?v=QOyTuk8ZbIU"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 6"), ("youtubeUrl", "https://www.youtube.com/watch?v=iU3RAyQjd0E"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 7"), ("youtubeUrl", "https://www.youtube.com/watch?v=k7QB8AsIW08"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 8"), ("youtubeUrl", "https://www.youtube.com/watch?v=lpMahf1C4HA"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 9"), ("youtubeUrl", "https://www.youtube.com/watch?v=HUB6Z-hDL9g"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 10"), ("youtubeUrl", "https://www.youtube.com/watch?v=-_UhyoyveN8"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 11"), ("youtubeUrl", "https://www.youtube.com/watch?v=MS_23I3R1M4"), ("description", "") },
        new (string alias, string value)[] { ("title", "Avsnitt 12"), ("youtubeUrl", "https://www.youtube.com/watch?v=SzQYodqqyCg"), ("description", "") },
    };

    // v1: introductory copy + ELIM-BLOCKS marker where the video list renders.
    private const string ForeldrarHtmlSv = """
        <div class="foreldrar-content" data-v="1">
            <h2>Rusta dina barn</h2>
            <p>En videoserie för dig som är förälder och vill ge dina barn en stabil grund för livet och tron. Avsnitten är korta och praktiska – titta i din egen takt.</p>

            <!-- ELIM-BLOCKS -->

            <p style="margin-top:24px;"><em>Vill du följa serien från början? Börja med Avsnitt 1 och arbeta dig framåt.</em></p>
        </div>
        """;

    /// <summary>
    /// Updates the Föräldrar (Parents) teaching resource page with the "Rusta dina barn"
    /// series — both the Swedish mainContent (intro + ELIM-BLOCKS marker) and the
    /// contentBlocks property (the 12 video blocks). Idempotent on both: mainContent is
    /// guarded by a data-v marker, blocks by an empty-or-populated check.
    /// </summary>
    private void SeedForeldrarVideos(IContent home)
    {
        if (_elementYoutubeVideo == null || _blockListContent == null) return;

        try
        {
            // Walk: home → Undervisning → Föräldrar
            var teachingPage = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "teachingPage");
            if (teachingPage == null) return;

            var foreldrar = _contentService.GetPagedChildren(teachingPage.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "teachingResource"
                                  && (c.GetCultureName(I18n.Swedish) ?? c.Name) == "Föräldrar");
            if (foreldrar == null) return;

            // 1. Update Swedish mainContent (only if version marker missing — preserves manual edits).
            var currentContent = foreldrar.GetValue<string>("mainContent", I18n.Swedish) ?? "";
            var contentChanged = false;
            if (!currentContent.Contains("foreldrar-content\" data-v=\"1"))
            {
                foreldrar.SetValue("mainContent", ForeldrarHtmlSv, I18n.Swedish);
                contentChanged = true;
            }

            // 2. Seed contentBlocks if empty.
            var blocksChanged = false;
            var currentBlocks = foreldrar.GetValue<string>("contentBlocks");
            if (string.IsNullOrWhiteSpace(currentBlocks) || !currentBlocks.Contains("contentTypeKey"))
            {
                _logger.LogInformation(
                    "Seeding Föräldrar page with {Count} YouTube video block(s)",
                    RustaDinaBarnVideos.Length);
                var json = BuildBlockListValue(_elementYoutubeVideo.Key, RustaDinaBarnVideos);
                foreldrar.SetValue("contentBlocks", json);
                blocksChanged = true;
            }

            if (contentChanged || blocksChanged)
            {
                _contentService.Save(foreldrar);
                _contentService.Publish(foreldrar, new[] { "*" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeedForeldrarVideos failed (non-fatal)");
        }
    }

    // Sermons for Undervisning → Predikan. Stored in user-provided order
    // (index 0 = oldest, last index = newest). The Predikan page uses the
    // <!-- ELIM-BLOCKS-REVERSED --> marker so the template reverses the list
    // at render time — newest on top, oldest at bottom. This means future
    // sermons added via backoffice append at the end (storage) and appear
    // at the top (display) automatically.
    private static readonly (string alias, string value)[][] PredikanSermonVideos = new[]
    {
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=Nq8cxDtnik0"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=oZBrV53c8pM"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=fmo5Ly7HLG4"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=ZZ-TyR0q2I4"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=B5BRyEJH2F8"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=ra8f9EgL3Wc"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=nNwmqr-xo6A"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=yXq_x1CpzmM"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=v2GdxxmFdJQ"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=MZyHeRABpVc"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=s4tXwWG8JZY"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=LoUzz6PDPFw"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=6nhujUDGBFo"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=KM8p9NraJn8"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=rGSnRE5u2IY"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=k6HwyUVTJfk"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=LI8WyE2sI8I"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=28oilW7v5JU"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=u157gwfnmOU"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=BhoUE8dGouo"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=tQCXAnW_pYM"), ("description", "") },
        new (string alias, string value)[] { ("title", ""), ("youtubeUrl", "https://www.youtube.com/watch?v=_vNdJAHKC2A"), ("description", "") },
    };

    // v1: video list + recordings section. Uses the REVERSED marker so the template
    // displays sermons newest-first while keeping storage chronological (admin's
    // natural "add to end" workflow appends new sermons that auto-appear at the top).
    private const string PredikanHtmlSv = """
        <div class="predikan-content" data-v="1">
            <h2>Predikan</h2>
            <p>Här samlar vi tidigare och aktuella predikningar från Elimkyrkan – både videor och äldre inspelningar.</p>

            <h2>Videor</h2>
            <p>Den senaste predikan ligger högst upp. Bläddra nedåt för äldre tillfällen.</p>
            <!-- ELIM-BLOCKS-REVERSED -->

            <h2>Inspelningar</h2>
            <p>Vi har också äldre ljudinspelningar i vårt arkiv. Hör av dig till <a href="mailto:info@elimmantorp.se">info@elimmantorp.se</a> om du vill ha tillgång till ett särskilt tillfälle.</p>
        </div>
        """;

    /// <summary>
    /// Updates the Predikan (Sermons) teaching resource page with a videos + recordings
    /// structure and seeds 22 YouTube sermon blocks. The page uses the REVERSED block
    /// marker, so storage stays chronological (oldest = index 0) while the rendered
    /// page shows newest first.
    /// </summary>
    private void SeedPredikanVideos(IContent home)
    {
        if (_elementYoutubeVideo == null || _blockListContent == null) return;

        try
        {
            var teachingPage = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "teachingPage");
            if (teachingPage == null) return;

            var predikan = _contentService.GetPagedChildren(teachingPage.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "teachingResource"
                                  && (c.GetCultureName(I18n.Swedish) ?? c.Name) == "Predikan");
            if (predikan == null) return;

            // 1. Update Swedish mainContent (only if version marker missing).
            var currentContent = predikan.GetValue<string>("mainContent", I18n.Swedish) ?? "";
            var contentChanged = false;
            if (!currentContent.Contains("predikan-content\" data-v=\"1"))
            {
                predikan.SetValue("mainContent", PredikanHtmlSv, I18n.Swedish);
                contentChanged = true;
            }

            // 2. Seed contentBlocks if empty.
            var blocksChanged = false;
            var currentBlocks = predikan.GetValue<string>("contentBlocks");
            if (string.IsNullOrWhiteSpace(currentBlocks) || !currentBlocks.Contains("contentTypeKey"))
            {
                _logger.LogInformation(
                    "Seeding Predikan page with {Count} sermon video block(s)",
                    PredikanSermonVideos.Length);
                var json = BuildBlockListValue(_elementYoutubeVideo.Key, PredikanSermonVideos);
                predikan.SetValue("contentBlocks", json);
                blocksChanged = true;
            }

            if (contentChanged || blocksChanged)
            {
                _contentService.Save(predikan);
                _contentService.Publish(predikan, new[] { "*" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeedPredikanVideos failed (non-fatal)");
        }
    }

    /// <summary>
    /// Migrates the Undervisning → Hemgrupper teaching page to the v3 layout: framing
    /// text only in mainContent + resource cards stored as Block List items. Guards both
    /// independently (mainContent by data-v marker, blocks by empty-check) so admin edits
    /// to either survive subsequent restarts.
    /// </summary>
    private void MigrateHemgrupperTeachingContent(IContent home)
    {
        if (_elementResourceCard == null || _blockListContent == null) return;

        try
        {
            var teachingPage = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "teachingPage");
            if (teachingPage == null) return;

            var hemgrupper = _contentService.GetPagedChildren(teachingPage.Id, 0, 100, out _, null)
                .FirstOrDefault(c => c.ContentType.Alias == "teachingResource"
                                  && (c.GetCultureName(I18n.Swedish) ?? c.Name) == "Hemgrupper");
            if (hemgrupper == null) return;

            // 1. Update Swedish mainContent (only if v3 marker missing).
            var current = hemgrupper.GetValue<string>("mainContent", I18n.Swedish) ?? "";
            var contentChanged = false;
            if (!current.Contains("hemgrupper-teaching-content\" data-v=\"3"))
            {
                _logger.LogInformation("Migrating Undervisning → Hemgrupper mainContent to v3");
                hemgrupper.SetValue("mainContent", HemgrupperTeachingContentSv, I18n.Swedish);
                contentChanged = true;
            }

            // 2. Seed contentBlocks if empty.
            var blocksChanged = false;
            var currentBlocks = hemgrupper.GetValue<string>("contentBlocks");
            if (string.IsNullOrWhiteSpace(currentBlocks) || !currentBlocks.Contains("contentTypeKey"))
            {
                _logger.LogInformation(
                    "Seeding Hemgrupper teaching page with {Count} resource card block(s)",
                    HemgrupperResourceCardSeeds.Length);
                var json = BuildBlockListValue(_elementResourceCard.Key, HemgrupperResourceCardSeeds);
                hemgrupper.SetValue("contentBlocks", json);
                blocksChanged = true;
            }

            if (contentChanged || blocksChanged)
            {
                _contentService.Save(hemgrupper);
                _contentService.Publish(hemgrupper, new[] { "*" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MigrateHemgrupperTeachingContent failed (non-fatal)");
        }
    }

    /// <summary>
    /// Builds an Umbraco 17 Block List stored value (the JSON written into the property's
    /// varcharValue/textValue column). Same element type key used for every block in
    /// the sequence — caller groups blocks per element type and calls multiple times if
    /// needed.
    /// </summary>
    private static string BuildBlockListValue(
        Guid elementTypeKey,
        IEnumerable<(string alias, string value)[]> blocks)
    {
        var layout = new List<object>();
        var contentData = new List<object>();

        foreach (var blockProps in blocks)
        {
            var contentKey = Guid.NewGuid().ToString();
            layout.Add(new Dictionary<string, object?> { ["contentKey"] = contentKey });
            contentData.Add(new Dictionary<string, object?>
            {
                ["key"] = contentKey,
                ["contentTypeKey"] = elementTypeKey.ToString(),
                ["values"] = blockProps.Select(p => new Dictionary<string, object?>
                {
                    ["alias"] = p.alias,
                    ["value"] = p.value,
                    ["culture"] = null,
                    ["segment"] = null,
                }).ToArray(),
            });
        }

        var rootValue = new Dictionary<string, object>
        {
            ["layout"] = new Dictionary<string, object> { ["Umbraco.BlockList"] = layout },
            ["contentData"] = contentData,
            ["settingsData"] = Array.Empty<object>(),
            ["expose"] = Array.Empty<object>(),
        };

        return System.Text.Json.JsonSerializer.Serialize(rootValue);
    }

    private record TemplateSet(ITemplate Home, ITemplate AboutPage, ITemplate ActivitiesPage, ITemplate Activity, ITemplate Calendar, ITemplate TeachingPage, ITemplate TeachingResource, ITemplate ContactPage);

    private TemplateSet EnsureTemplates() => new(
        EnsureTemplate("home", "Home"),
        EnsureTemplate("aboutPage", "About Page"),
        EnsureTemplate("activitiesPage", "Activities Page"),
        EnsureTemplate("activity", "Activity"),
        EnsureTemplate("calendar", "Calendar"),
        EnsureTemplate("teachingPage", "Teaching Page"),
        EnsureTemplate("teachingResource", "Teaching Resource"),
        EnsureTemplate("contactPage", "Contact Page")
    );

    private ITemplate EnsureTemplate(string alias, string name)
    {
        var existing = _fileService.GetTemplate(alias);
        if (existing != null) return existing;

        _logger.LogInformation("Creating template {Alias}", alias);

        var viewsPath = Path.Combine(_env.ContentRootPath, "Views", $"{alias}.cshtml");
        var content = System.IO.File.Exists(viewsPath)
            ? System.IO.File.ReadAllText(viewsPath)
            : "@inherits Umbraco.Cms.Web.Common.Views.UmbracoViewPage\n";

        var template = new Template(_shortStringHelper, name, alias) { Content = content };
        _fileService.SaveTemplate(template);
        return _fileService.GetTemplate(alias)!;
    }

    private record ContentTypes(IContentType Home, IContentType AboutPage, IContentType ActivitiesPage, IContentType Activity, IContentType CalendarPage, IContentType TeachingPage, IContentType TeachingResource, IContentType ContactPage, IContentType SiteSettings, IContentType ScheduleItem, IContentType EventItem, IContentType? Event);

    private ContentTypes EnsureContentTypes(TemplateSet templates)
    {
        var home = EnsureHomeContentType(templates.Home);
        var about = EnsureSimpleContentType("aboutPage", "About Page", "icon-info", templates.AboutPage, includeMainContent: true);
        var activities = EnsureSimpleContentType("activitiesPage", "Activities Page", "icon-grid", templates.ActivitiesPage, includeMainContent: false);
        var activity = EnsureActivityContentType(templates.Activity);
        var calendar = EnsureSimpleContentType("calendarPage", "Calendar Page", "icon-calendar", templates.Calendar, includeMainContent: false);
        var teaching = EnsureSimpleContentType("teachingPage", "Teaching Page", "icon-book", templates.TeachingPage, includeMainContent: false);
        var teachingResource = EnsureSimpleContentType("teachingResource", "Teaching Resource", "icon-document", templates.TeachingResource, includeMainContent: true);
        var contact = EnsureContactContentType(templates.ContactPage);
        EnsureContactExtraProperties(contact);
        var siteSettings = EnsureSiteSettingsContentType();
        var scheduleItem = EnsureScheduleItemContentType();
        var eventItem = EnsureEventItemContentType();
        var eventType = EnsureEventContentType();

        // Events now live under Calendar only (single source of truth).
        // Legacy scheduleItem / eventItem types stay registered but are no longer allowed under Home.
        SetAllowedChildren(home, new[] { about, activities, calendar, teaching, contact });
        SetAllowedChildren(activities, new[] { activity });
        SetAllowedChildren(teaching, new[] { teachingResource });
        if (eventType != null) SetAllowedChildren(calendar, new[] { eventType });

        EnsureHomeExtraProperties(home);

        // Add heroImage Media Picker to every page type (idempotent — only adds if missing)
        foreach (var ct in new[] { home, about, activities, activity, calendar, teaching, teachingResource, contact })
        {
            EnsureHeroImageProperty(ct);
        }

        return new ContentTypes(home, about, activities, activity, calendar, teaching, teachingResource, contact, siteSettings, scheduleItem, eventItem, eventType);
    }

    private IContentType? EnsureEventContentType()
    {
        if (_datePicker == null || _trueFalse == null) return null;

        var existing = _contentTypeService.Get("event");
        if (existing != null)
        {
            MigrateEventTypePropertyToDropdown(existing);
            MigrateDayOfWeekPropertyToDropdown(existing);
            EnsureLinkUrlProperty(existing);
            RemoveRedundantRecurrenceProperty(existing);
            return existing;
        }

        _logger.LogInformation("Creating Event content type");
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "event",
            Name = "Event",
            Icon = "icon-calendar-alt",
            AllowedAsRoot = false,
            Variations = ContentVariation.Culture,
            AllowedTemplates = Array.Empty<ITemplate>(),
        };

        var basics = new PropertyGroup(true) { Name = "Event", Alias = "event", SortOrder = 0 };
        basics.PropertyTypes!.Add(MakeProp(_textstring!, "title", "Title", 0));
        basics.PropertyTypes!.Add(MakeProp(_textarea!, "description", "Description", 1));
        basics.PropertyTypes!.Add(MakeProp(_textstring!, "icon", "Icon (emoji like 🎵)", 2, variant: false));
        basics.PropertyTypes!.Add(MakeProp(_textstring!, "time", "Time (e.g. 10:00 – 11:30)", 3, variant: false));
        basics.PropertyTypes!.Add(MakeProp(_eventTypeDropdown ?? _textstring!, "eventType", "Event Type", 4, variant: false));
        basics.PropertyTypes!.Add(MakeProp(_dayOfWeekDropdown ?? _textstring!, "dayOfWeek", "Day of Week (only used for static weekly slots without a date)", 5, variant: false));
        basics.PropertyTypes!.Add(MakeProp(_datePicker!, "eventDate", "Event Date (specific occurrence)", 6, variant: false));
        basics.PropertyTypes!.Add(MakeProp(_textstring!, "linkUrl", "Link URL (optional — internal like /verksamheter/next-generation, or external https://...)", 7, variant: false));
        ct.PropertyGroups.Add(basics);

        var recurrence = new PropertyGroup(true) { Name = "Recurrence", Alias = "recurrence", SortOrder = 1 };
        recurrence.PropertyTypes!.Add(MakeProp(_datePicker!, "seriesEndDate", "Series End Date (when does the recurrence stop?)", 0, variant: false));
        recurrence.PropertyTypes!.Add(MakeProp(_textstring!, "seriesId", "Series ID (auto-generated; do not edit)", 1, variant: false));
        recurrence.PropertyTypes!.Add(MakeProp(_trueFalse!, "applyToSeries", "Apply edits to all events in this series", 2, variant: false));
        ct.PropertyGroups.Add(recurrence);

        EnsureHeroImageProperty(ct);

        _contentTypeService.Save(ct);
        return _contentTypeService.Get("event")!;
    }

    private IContentType EnsureScheduleItemContentType()
    {
        var existing = _contentTypeService.Get("scheduleItem");
        if (existing != null) return existing;

        _logger.LogInformation("Creating scheduleItem content type");
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "scheduleItem",
            Name = "Schedule Item",
            Icon = "icon-time",
            AllowedAsRoot = false,
            Variations = ContentVariation.Culture,
            AllowedTemplates = Array.Empty<ITemplate>(),
        };

        var group = new PropertyGroup(true) { Name = "Schedule", Alias = "schedule", SortOrder = 0 };
        group.PropertyTypes!.Add(MakeProp(_textstring!, "day", "Day (e.g. Söndag)", 0));
        group.PropertyTypes!.Add(MakeProp(_textstring!, "title", "Title", 1));
        group.PropertyTypes!.Add(MakeProp(_textstring!, "time", "Time (e.g. 10:00 – 11:30)", 2, variant: false));
        group.PropertyTypes!.Add(MakeProp(_textarea!, "description", "Description", 3));
        ct.PropertyGroups.Add(group);

        _contentTypeService.Save(ct);
        return _contentTypeService.Get("scheduleItem")!;
    }

    private IContentType EnsureEventItemContentType()
    {
        var existing = _contentTypeService.Get("eventItem");
        if (existing != null)
        {
            EnsureHeroImageProperty(existing); // adds image property if missing
            return existing;
        }

        _logger.LogInformation("Creating eventItem content type");
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "eventItem",
            Name = "Event Item",
            Icon = "icon-calendar-alt",
            AllowedAsRoot = false,
            Variations = ContentVariation.Culture,
            AllowedTemplates = Array.Empty<ITemplate>(),
        };

        var group = new PropertyGroup(true) { Name = "Event", Alias = "event", SortOrder = 0 };
        group.PropertyTypes!.Add(MakeProp(_textstring!, "date", "Date (display string)", 0));
        group.PropertyTypes!.Add(MakeProp(_textstring!, "icon", "Icon (emoji)", 1, variant: false));
        group.PropertyTypes!.Add(MakeProp(_textstring!, "title", "Title", 2));
        group.PropertyTypes!.Add(MakeProp(_textarea!, "description", "Description", 3));
        ct.PropertyGroups.Add(group);

        _contentTypeService.Save(ct);
        var saved = _contentTypeService.Get("eventItem")!;
        EnsureHeroImageProperty(saved);
        return saved;
    }

    private void EnsureContactExtraProperties(IContentType contact)
    {
        EnsureTextProperty(contact, "Contact Info", "contactInfo", "pastorName", "Pastor Name", _textstring!, variant: false);
    }

    private void EnsureHomeExtraProperties(IContentType home)
    {
        EnsureTextProperty(home, "Home Sections", "homeSections", "scheduleHeading", "Schedule Heading", _textstring!, variant: true);
        EnsureTextProperty(home, "Home Sections", "homeSections", "scheduleSubheading", "Schedule Subheading", _textarea!, variant: true);
        EnsureTextProperty(home, "Home Sections", "homeSections", "eventsHeading", "Events Heading", _textstring!, variant: true);
        EnsureTextProperty(home, "Home Sections", "homeSections", "eventsSubheading", "Events Subheading", _textarea!, variant: true);
        EnsureTextProperty(home, "Home Sections", "homeSections", "heroButtonText", "Hero Button Text", _textstring!, variant: true);
        EnsureTextProperty(home, "Home Sections", "homeSections", "ctaHeading", "CTA Heading", _textstring!, variant: true);
        EnsureTextProperty(home, "Home Sections", "homeSections", "ctaText", "CTA Text", _textarea!, variant: true);
        EnsureTextProperty(home, "Home Sections", "homeSections", "ctaButtonText", "CTA Button Text", _textstring!, variant: true);
    }

    private void EnsureTextProperty(IContentType ct, string groupName, string groupAlias, string alias, string name, IDataType dataType, bool variant)
    {
        if (ct.PropertyTypes.Any(p => p.Alias == alias)) return;
        var group = ct.PropertyGroups.FirstOrDefault(g => g.Alias == groupAlias);
        if (group == null)
        {
            group = new PropertyGroup(true) { Name = groupName, Alias = groupAlias, SortOrder = ct.PropertyGroups.Count };
            ct.PropertyGroups.Add(group);
        }
        var sortOrder = group.PropertyTypes?.Count ?? 0;
        group.PropertyTypes!.Add(MakeProp(dataType, alias, name, sortOrder, variant));
        _contentTypeService.Save(ct);
    }

    private void EnsureHeroImageProperty(IContentType ct)
    {
        if (_mediaPicker == null) return;
        if (ct.PropertyTypes.Any(p => p.Alias == "heroImage")) return;

        _logger.LogInformation("Adding heroImage property to {Alias}", ct.Alias);

        var group = ct.PropertyGroups.FirstOrDefault(g => g.Alias == "hero");
        if (group == null)
        {
            group = new PropertyGroup(true) { Name = "Hero", Alias = "hero", SortOrder = 0 };
            ct.PropertyGroups.Add(group);
        }

        var prop = new PropertyType(_shortStringHelper, _mediaPicker)
        {
            Alias = "heroImage",
            Name = "Hero Image",
            SortOrder = 99,
            Variations = ContentVariation.Nothing,
        };
        group.PropertyTypes!.Add(prop);
        _contentTypeService.Save(ct);
    }

    private IContentType EnsureSiteSettingsContentType()
    {
        var existing = _contentTypeService.Get("siteSettings");
        if (existing != null) return existing;

        _logger.LogInformation("Creating Site Settings content type");
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "siteSettings",
            Name = "Site Settings",
            Icon = "icon-settings",
            AllowedAsRoot = true,
            Variations = ContentVariation.Culture,
            AllowedTemplates = Array.Empty<ITemplate>(), // not a renderable page
        };

        var logo = new PropertyGroup(true) { Name = "Logo", Alias = "logo", SortOrder = 0 };
        logo.PropertyTypes!.Add(MakeProp(_textstring!, "logoTopText", "Logo Top Text", 0, variant: false));
        logo.PropertyTypes!.Add(MakeProp(_textstring!, "logoBottomText", "Logo Bottom Text", 1, variant: false));
        ct.PropertyGroups.Add(logo);

        var footer = new PropertyGroup(true) { Name = "Footer", Alias = "footer", SortOrder = 1 };
        footer.PropertyTypes!.Add(MakeProp(_textarea!, "footerTagline", "Footer Tagline", 0));
        footer.PropertyTypes!.Add(MakeProp(_textarea!, "footerAddress", "Footer Address", 1, variant: false));
        footer.PropertyTypes!.Add(MakeProp(_textstring!, "footerPhone", "Footer Phone", 2, variant: false));
        footer.PropertyTypes!.Add(MakeProp(_textstring!, "footerEmail", "Footer Email", 3, variant: false));
        footer.PropertyTypes!.Add(MakeProp(_textstring!, "copyrightSuffix", "Copyright Suffix", 4));
        ct.PropertyGroups.Add(footer);

        _contentTypeService.Save(ct);
        return _contentTypeService.Get("siteSettings")!;
    }

    private PropertyType MakeProp(IDataType dataType, string alias, string name, int sortOrder, bool variant = true)
    {
        var p = new PropertyType(_shortStringHelper, dataType)
        {
            Alias = alias,
            Name = name,
            SortOrder = sortOrder,
        };
        if (variant) p.Variations = ContentVariation.Culture;
        return p;
    }

    private IContentType EnsureHomeContentType(ITemplate template)
    {
        var existing = _contentTypeService.Get("home");
        if (existing != null)
        {
            EnsureTemplateAssigned(existing, template);
            return existing;
        }

        _logger.LogInformation("Creating Home content type");
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "home",
            Name = "Home",
            Icon = "icon-home",
            AllowedAsRoot = true,
            Variations = ContentVariation.Culture,
        };

        var group = new PropertyGroup(true) { Name = "Hero", Alias = "hero", SortOrder = 0 };
        group.PropertyTypes!.Add(MakeProp(_textstring!, "heroTitle", "Hero Title", 0));
        group.PropertyTypes!.Add(MakeProp(_textarea!, "heroSubtitle", "Hero Subtitle", 1));
        ct.PropertyGroups.Add(group);

        ct.AllowedTemplates = new[] { template };
        ct.SetDefaultTemplate(template);
        _contentTypeService.Save(ct);
        return _contentTypeService.Get("home")!;
    }

    private IContentType EnsureSimpleContentType(string alias, string name, string icon, ITemplate template, bool includeMainContent)
    {
        var existing = _contentTypeService.Get(alias);
        if (existing != null)
        {
            EnsureTemplateAssigned(existing, template);
            return existing;
        }

        _logger.LogInformation("Creating {Alias} content type", alias);
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = alias,
            Name = name,
            Icon = icon,
            AllowedAsRoot = false,
            Variations = ContentVariation.Culture,
        };

        var hero = new PropertyGroup(true) { Name = "Hero", Alias = "hero", SortOrder = 0 };
        hero.PropertyTypes!.Add(MakeProp(_textstring!, "heroTitle", "Hero Title", 0));
        hero.PropertyTypes!.Add(MakeProp(_textarea!, "heroSubtitle", "Hero Subtitle", 1));
        ct.PropertyGroups.Add(hero);

        if (includeMainContent)
        {
            var body = new PropertyGroup(true) { Name = "Content", Alias = "content", SortOrder = 1 };
            body.PropertyTypes!.Add(MakeProp(_richtext!, "mainContent", "Main Content", 0));
            ct.PropertyGroups.Add(body);
        }

        ct.AllowedTemplates = new[] { template };
        ct.SetDefaultTemplate(template);
        _contentTypeService.Save(ct);
        return _contentTypeService.Get(alias)!;
    }

    private IContentType EnsureActivityContentType(ITemplate template)
    {
        var existing = _contentTypeService.Get("activity");
        if (existing != null)
        {
            EnsureTemplateAssigned(existing, template);
            return existing;
        }

        _logger.LogInformation("Creating activity content type");
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "activity",
            Name = "Activity",
            Icon = "icon-tag",
            AllowedAsRoot = false,
            Variations = ContentVariation.Culture,
        };

        var hero = new PropertyGroup(true) { Name = "Hero", Alias = "hero", SortOrder = 0 };
        hero.PropertyTypes!.Add(MakeProp(_textarea!, "heroSubtitle", "Hero Subtitle", 0));
        hero.PropertyTypes!.Add(MakeProp(_textstring!, "icon", "Icon (emoji)", 1, variant: false));
        hero.PropertyTypes!.Add(MakeProp(_textarea!, "shortDescription", "Short Description (card)", 2));
        ct.PropertyGroups.Add(hero);

        var body = new PropertyGroup(true) { Name = "Content", Alias = "content", SortOrder = 1 };
        body.PropertyTypes!.Add(MakeProp(_richtext!, "mainContent", "Main Content", 0));
        ct.PropertyGroups.Add(body);

        ct.AllowedTemplates = new[] { template };
        ct.SetDefaultTemplate(template);
        _contentTypeService.Save(ct);
        return _contentTypeService.Get("activity")!;
    }

    private IContentType EnsureContactContentType(ITemplate template)
    {
        var existing = _contentTypeService.Get("contactPage");
        if (existing != null)
        {
            EnsureTemplateAssigned(existing, template);
            return existing;
        }

        _logger.LogInformation("Creating contactPage content type");
        var ct = new ContentType(_shortStringHelper, Constants.System.Root)
        {
            Alias = "contactPage",
            Name = "Contact Page",
            Icon = "icon-message",
            AllowedAsRoot = false,
            Variations = ContentVariation.Culture,
        };

        var hero = new PropertyGroup(true) { Name = "Hero", Alias = "hero", SortOrder = 0 };
        hero.PropertyTypes!.Add(MakeProp(_textstring!, "heroTitle", "Hero Title", 0));
        hero.PropertyTypes!.Add(MakeProp(_textarea!, "heroSubtitle", "Hero Subtitle", 1));
        ct.PropertyGroups.Add(hero);

        var info = new PropertyGroup(true) { Name = "Contact Info", Alias = "contactInfo", SortOrder = 1 };
        info.PropertyTypes!.Add(MakeProp(_textarea!, "address", "Address", 0, variant: false));
        info.PropertyTypes!.Add(MakeProp(_textstring!, "phone", "Phone", 1, variant: false));
        info.PropertyTypes!.Add(MakeProp(_textstring!, "email", "Email", 2, variant: false));
        ct.PropertyGroups.Add(info);

        ct.AllowedTemplates = new[] { template };
        ct.SetDefaultTemplate(template);
        _contentTypeService.Save(ct);
        return _contentTypeService.Get("contactPage")!;
    }

    private void EnsureTemplateAssigned(IContentType ct, ITemplate template)
    {
        var allowed = ct.AllowedTemplates?.ToList() ?? new List<ITemplate>();
        if (allowed.Any(t => t.Id == template.Id)) return;
        allowed.Add(template);
        ct.AllowedTemplates = allowed;
        if (ct.DefaultTemplate == null) ct.SetDefaultTemplate(template);
        _contentTypeService.Save(ct);
    }

    private void SetAllowedChildren(IContentType parent, IContentType[] children)
    {
        var current = parent.AllowedContentTypes?.Select(a => a.Key).ToHashSet() ?? new HashSet<Guid>();
        var desired = children.Select(c => c.Key).ToHashSet();
        if (current.SetEquals(desired) && (parent.AllowedContentTypes?.Count() == desired.Count)) return;

        parent.AllowedContentTypes = children
            .Select((c, i) => new ContentTypeSort(c.Key, i, c.Alias))
            .ToList();
        _contentTypeService.Save(parent);
    }

    private IContent EnsureContentTree(ContentTypes types)
    {
        var home = _contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "home");
        if (home == null)
        {
            _logger.LogInformation("Creating Home content node");
            home = _contentService.Create("Hem", Constants.System.Root, types.Home);
            SetCultureNames(home, NameHome);
            SetVariantValue(home, "heroTitle", HomeHeroTitle);
            SetVariantValue(home, "heroSubtitle", HomeHeroSubtitle);
            _contentService.Save(home);
            _contentService.Publish(home, new[] { "*" });
        }

        EnsureUniqueChild(home, NameAbout.Sv, types.AboutPage, n =>
        {
            SetCultureNames(n, NameAbout);
            SetVariantValue(n, "heroTitle", NameAbout);
            SetVariantValue(n, "heroSubtitle", AboutHeroSubtitle);
            n.SetValue("mainContent", AboutHtmlSv, I18n.Swedish);
        });

        var activities = EnsureUniqueChild(home, NameActivities.Sv, types.ActivitiesPage, n =>
        {
            SetCultureNames(n, NameActivities);
            SetVariantValue(n, "heroTitle", NameActivities);
            SetVariantValue(n, "heroSubtitle", ActivitiesHeroSubtitle);
        });

        SeedActivities(activities, types.Activity);

        EnsureUniqueChild(home, NameCalendar.Sv, types.CalendarPage, n =>
        {
            SetCultureNames(n, NameCalendar);
            SetVariantValue(n, "heroTitle", NameCalendar);
            SetVariantValue(n, "heroSubtitle", CalendarHeroSubtitle);
        });

        var teaching = EnsureUniqueChild(home, NameTeaching.Sv, types.TeachingPage, n =>
        {
            SetCultureNames(n, NameTeaching);
            SetVariantValue(n, "heroTitle", NameTeaching);
            SetVariantValue(n, "heroSubtitle", TeachingHeroSubtitle);
        });

        SeedTeachingResources(teaching, types.TeachingResource);

        EnsureUniqueChild(home, NameContact.Sv, types.ContactPage, n =>
        {
            SetCultureNames(n, NameContact);
            SetVariantValue(n, "heroTitle", NameContact);
            SetVariantValue(n, "heroSubtitle", ContactHeroSubtitle);
            n.SetValue("address", "Kyrkogatan 1\n595 97 Mantorp");
            n.SetValue("phone", "073-064 01 71");
            n.SetValue("email", "info@elimmantorp.se");
            n.SetValue("pastorName", "Holger Schmidt");
        });

        EnforceHomeChildOrder(home);

        EnsureSiteSettings(types.SiteSettings);

        SeedHomeSectionDefaults(home);
        SeedScheduleItems(home, types.ScheduleItem);
        SeedEventItems(home, types.EventItem);
        MigrateLegacyEventTypes(home, types.Event);
        MoveEventsToCalendar(home);

        return home;
    }

    private void MoveEventsToCalendar(IContent home)
    {
        var calendar = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
            .FirstOrDefault(c => c.ContentType.Alias == "calendarPage");
        if (calendar == null) return;

        var eventsUnderHome = _contentService.GetPagedChildren(home.Id, 0, 1000, out _, null)
            .Where(c => c.ContentType.Alias == "event")
            .ToList();
        if (eventsUnderHome.Count > 0)
        {
            _logger.LogInformation("Moving {Count} events from Home to Calendar", eventsUnderHome.Count);
            foreach (var ev in eventsUnderHome)
            {
                _contentService.Move(ev, calendar.Id);
            }
        }

        DeduplicateEventsUnderCalendar(calendar);
        SeedDefaultEventLinks(calendar);
    }

    private static readonly System.Text.RegularExpressions.Regex TrailingCopySuffix =
        new(@"\s*\(\d+\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripCopySuffixes(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        while (TrailingCopySuffix.IsMatch(name))
        {
            name = TrailingCopySuffix.Replace(name, "");
        }
        return name.Trim();
    }

    private void DeduplicateEventsUnderCalendar(IContent calendar)
    {
        var events = _contentService.GetPagedChildren(calendar.Id, 0, 1000, out _, null)
            .Where(c => c.ContentType.Alias == "event")
            .ToList();

        // Group by base name (Umbraco-appended " (n)" suffixes stripped). Within each group,
        // keep the oldest (lowest Id) and delete the rest.
        var groups = events.GroupBy(c => StripCopySuffixes(c.GetCultureName(I18n.Swedish) ?? c.Name ?? ""));
        var deleted = 0;
        foreach (var group in groups.Where(g => g.Count() > 1))
        {
            var sorted = group.OrderBy(c => c.Id).ToList();
            foreach (var dup in sorted.Skip(1))
            {
                _logger.LogInformation("Deleting duplicate event '{Name}' (id={Id})", dup.Name, dup.Id);
                _contentService.Delete(dup);
                deleted++;
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Deduplicated {Count} event node(s) under Calendar", deleted);
        }
    }

    private void MigrateLegacyEventTypes(IContent home, IContentType? eventType)
    {
        if (eventType == null) return;

        var children = _contentService.GetPagedChildren(home.Id, 0, 1000, out _, null).ToList();
        var oldSchedule = children.Where(c => c.ContentType.Alias == "scheduleItem").ToList();
        var oldEvents = children.Where(c => c.ContentType.Alias == "eventItem").ToList();

        if (oldSchedule.Count == 0 && oldEvents.Count == 0) return;

        _logger.LogInformation("Migrating {S} scheduleItem + {E} eventItem nodes to unified Event content type",
            oldSchedule.Count, oldEvents.Count);

        foreach (var old in oldSchedule)
        {
            var ev = _contentService.Create(old.Name ?? "Event", home.Id, eventType);
            ev.SetValue("eventType", "Weekly");
            ev.SetValue("time", old.GetValue<string>("time"));
            ev.SetValue("dayOfWeek", old.GetValue<string>("day", I18n.Swedish) ?? old.GetValue<string>("day"));

            foreach (var (iso, _, _) in I18n.Languages)
            {
                var title = old.GetValue<string>("title", iso);
                var desc = old.GetValue<string>("description", iso);
                if (!string.IsNullOrWhiteSpace(title)) ev.SetValue("title", title, iso);
                if (!string.IsNullOrWhiteSpace(desc)) ev.SetValue("description", desc, iso);
                var name = old.GetCultureName(iso) ?? title ?? old.Name ?? "Event";
                ev.SetCultureName(name, iso);
            }

            _contentService.Save(ev);
            _contentService.Publish(ev, new[] { "*" });
            _contentService.Delete(old);
        }

        foreach (var old in oldEvents)
        {
            var ev = _contentService.Create(old.Name ?? "Event", home.Id, eventType);
            ev.SetValue("eventType", "Monthly");
            ev.SetValue("icon", old.GetValue<string>("icon"));

            // Try to parse "23 mars 2026" etc using Swedish culture
            var dateStr = old.GetValue<string>("date", I18n.Swedish) ?? old.GetValue<string>("date") ?? "";
            if (DateTime.TryParse(dateStr, new System.Globalization.CultureInfo("sv-SE"),
                System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                ev.SetValue("eventDate", parsedDate);
            }
            // If parsing failed (e.g. "Varje tisdag"), eventDate stays null and admin can set it manually.

            foreach (var (iso, _, _) in I18n.Languages)
            {
                var title = old.GetValue<string>("title", iso);
                var desc = old.GetValue<string>("description", iso);
                if (!string.IsNullOrWhiteSpace(title)) ev.SetValue("title", title, iso);
                if (!string.IsNullOrWhiteSpace(desc)) ev.SetValue("description", desc, iso);
                var name = old.GetCultureName(iso) ?? title ?? old.Name ?? "Event";
                ev.SetCultureName(name, iso);
            }

            _contentService.Save(ev);
            _contentService.Publish(ev, new[] { "*" });
            _contentService.Delete(old);
        }
    }

    private void SeedHomeSectionDefaults(IContent home)
    {
        // Only set if currently empty so we don't overwrite editor changes
        if (string.IsNullOrWhiteSpace(home.GetValue<string>("scheduleHeading", I18n.Swedish)))
        {
            SetVariantValue(home, "heroButtonText", HeroButtonText);
            SetVariantValue(home, "scheduleHeading", ScheduleHeading);
            SetVariantValue(home, "scheduleSubheading", ScheduleSubheading);
            SetVariantValue(home, "eventsHeading", EventsHeading);
            SetVariantValue(home, "eventsSubheading", EventsSubheading);
            SetVariantValue(home, "ctaHeading", CtaHeading);
            SetVariantValue(home, "ctaText", CtaText);
            SetVariantValue(home, "ctaButtonText", CtaButtonText);
            _contentService.Save(home);
            _contentService.Publish(home, new[] { "*" });
        }
    }

    private void SeedScheduleItems(IContent home, IContentType scheduleItemType)
    {
        var existing = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
            .Any(c => c.ContentType.Alias == "scheduleItem");
        if (existing) return;

        foreach (var row in ScheduleSeed)
        {
            var node = _contentService.Create(row.Title.Sv, home.Id, scheduleItemType);
            SetCultureNames(node, row.Title);
            SetVariantValue(node, "day", row.Day);
            SetVariantValue(node, "title", row.Title);
            SetVariantValue(node, "description", row.Description);
            node.SetValue("time", row.Time);
            _contentService.Save(node);
            _contentService.Publish(node, new[] { "*" });
        }
    }

    private void SeedEventItems(IContent home, IContentType eventItemType)
    {
        var existing = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null)
            .Any(c => c.ContentType.Alias == "eventItem");
        if (existing) return;

        foreach (var row in EventSeed)
        {
            var node = _contentService.Create(row.Title.Sv, home.Id, eventItemType);
            SetCultureNames(node, row.Title);
            SetVariantValue(node, "date", row.Date);
            SetVariantValue(node, "title", row.Title);
            SetVariantValue(node, "description", row.Description);
            node.SetValue("icon", row.Icon);
            _contentService.Save(node);
            _contentService.Publish(node, new[] { "*" });
        }
    }

    private record ScheduleRow(Trans Day, Trans Title, string Time, Trans Description);
    private record EventRow(Trans Date, string Icon, Trans Title, Trans Description);

    private static readonly ScheduleRow[] ScheduleSeed = new[]
    {
        new ScheduleRow(
            new Trans("Söndag", "Sunday", "Неділя", "Domingo", "วันอาทิตย์"),
            new Trans("Gudstjänst", "Service", "Богослужіння", "Servicio", "พิธีนมัสการ"),
            "10:00 – 11:30",
            new Trans(
                "Gudstjänst med lovsång, predikan och gemenskap. Söndagsskola för barnen.",
                "Service with worship, sermon and community. Sunday school for the children.",
                "Богослужіння з прославленням, проповіддю та спілкуванням. Недільна школа для дітей.",
                "Servicio con adoración, predicación y comunidad. Escuela dominical para los niños.",
                "พิธีนมัสการพร้อมเพลงสรรเสริญ คำเทศนา และการรวมตัว มีโรงเรียนวันอาทิตย์สำหรับเด็ก")),
        new ScheduleRow(
            new Trans("Tisdag", "Tuesday", "Вівторок", "Martes", "วันอังคาร"),
            new Trans("Tisdagscafé", "Tuesday Café", "Вівторкове кафе", "Café del martes", "คาเฟ่วันอังคาร"),
            "19:00 – 21:00",
            new Trans(
                "Öppet café med fika och gemenskap. Alla är välkomna!",
                "Open café with coffee and community. All are welcome!",
                "Відкрите кафе з кавою та спілкуванням. Усі бажані!",
                "Café abierto con café y comunidad. ¡Todos son bienvenidos!",
                "คาเฟ่เปิดพร้อมกาแฟและการพบปะ ทุกคนยินดีต้อนรับ!")),
        new ScheduleRow(
            new Trans("Onsdag", "Wednesday", "Середа", "Miércoles", "วันพุธ"),
            new Trans("Hemgrupper", "Home Groups", "Домашні групи", "Grupos en casa", "กลุ่มในบ้าน"),
            "19:00 – 21:00",
            new Trans(
                "Bibelstudium och gemenskap i mindre grupper runt om i Mantorp.",
                "Bible study and community in smaller groups around Mantorp.",
                "Вивчення Біблії та спілкування у менших групах по всьому Манторпу.",
                "Estudio bíblico y comunidad en grupos pequeños por Mantorp.",
                "การศึกษาพระคัมภีร์และการพบปะในกลุ่มเล็ก ๆ ทั่วเมืองมันทอร์ป")),
        new ScheduleRow(
            new Trans("Lördag", "Saturday", "Субота", "Sábado", "วันเสาร์"),
            new Trans("Mansfrukost", "Men's Breakfast", "Чоловічий сніданок", "Desayuno de hombres", "อาหารเช้าผู้ชาย"),
            "07:00 – 09:00",
            new Trans(
                "Frukost och samtal för män. En stund att ladda under veckan.",
                "Breakfast and conversation for men. A moment to recharge during the week.",
                "Сніданок і розмова для чоловіків. Момент перезарядитися посеред тижня.",
                "Desayuno y conversación para hombres. Un momento para recargar durante la semana.",
                "อาหารเช้าและการพูดคุยสำหรับผู้ชาย ช่วงเวลาเติมพลังในช่วงสัปดาห์")),
        new ScheduleRow(
            new Trans("Fredag", "Friday", "П'ятниця", "Viernes", "วันศุกร์"),
            new Trans("Next Generation", "Next Generation", "Next Generation", "Next Generation", "Next Generation"),
            "20:00 – 00:00",
            new Trans(
                "Ungdomskväll med aktiviteter, samtal och gemenskap.",
                "Youth evening with activities, conversation and community.",
                "Молодіжний вечір з заходами, розмовами та спілкуванням.",
                "Noche juvenil con actividades, conversación y comunidad.",
                "ค่ำคืนเยาวชนพร้อมกิจกรรม การพูดคุย และการพบปะ")),
        new ScheduleRow(
            new Trans("Lördag", "Saturday", "Субота", "Sábado", "วันเสาร์"),
            new Trans("Loppis Axplocket", "Loppis Axplocket", "Loppis Axplocket", "Loppis Axplocket", "Loppis Axplocket"),
            "10:00 – 14:00",
            new Trans(
                "Second hand och fynd. Välkommen att fika och fynda!",
                "Second hand and bargains. Come and grab a coffee and a find!",
                "Секонд-хенд і знахідки. Ласкаво просимо на каву та покупки!",
                "Segunda mano y gangas. ¡Bienvenido a tomar un café y encontrar tesoros!",
                "ของมือสองและสินค้าราคาดี ยินดีต้อนรับมาดื่มกาแฟและช้อปปิ้ง!")),
    };

    private static readonly EventRow[] EventSeed = new[]
    {
        new EventRow(
            new Trans("23 mars 2026", "23 March 2026", "23 березня 2026", "23 de marzo de 2026", "23 มีนาคม 2026"),
            "🎵",
            new Trans("Lovsångskväll", "Worship Evening", "Вечір прославлення", "Noche de adoración", "ค่ำคืนแห่งการนมัสการ"),
            new Trans(
                "En kväll av tillbedjan och lovsång. Alla är välkomna att delta i en stund av gemenskap och sång.",
                "An evening of worship and song. All are welcome to a time of community and singing.",
                "Вечір поклоніння та прославлення. Усі бажані на час спілкування та співу.",
                "Una noche de adoración y alabanza. Todos son bienvenidos a un tiempo de comunidad y canto.",
                "ค่ำคืนแห่งการนมัสการและบทเพลง ทุกคนยินดีต้อนรับเข้าสู่ช่วงเวลาแห่งการพบปะและการร้องเพลง")),
        new EventRow(
            new Trans("5 april 2026", "5 April 2026", "5 квітня 2026", "5 de abril de 2026", "5 เมษายน 2026"),
            "🌿",
            new Trans("Påskgudstjänst", "Easter Service", "Великодне богослужіння", "Servicio de Pascua", "พิธีอีสเตอร์"),
            new Trans(
                "Fira påskens budskap tillsammans. Särskilt program för barnen under gudstjänsten.",
                "Celebrate the Easter message together. Special program for the children during the service.",
                "Святкуйте великодне послання разом. Особлива програма для дітей під час богослужіння.",
                "Celebra el mensaje de Pascua juntos. Programa especial para los niños durante el servicio.",
                "ฉลองข่าวสารอีสเตอร์ด้วยกัน มีโปรแกรมพิเศษสำหรับเด็กระหว่างพิธี")),
        new EventRow(
            new Trans("Varje tisdag", "Every Tuesday", "Щовівторка", "Cada martes", "ทุกวันอังคาร"),
            "☕",
            new Trans("Öppet café", "Open Café", "Відкрите кафе", "Café abierto", "คาเฟ่เปิด"),
            new Trans(
                "Drop-in fika och gemenskap varje tisdag. Ta med en vän och njut av en lugn stund.",
                "Drop-in coffee and community every Tuesday. Bring a friend and enjoy a quiet moment.",
                "Кава та спілкування щовівторка. Візьміть друга і насолоджуйтесь спокійним моментом.",
                "Café y comunidad sin cita previa cada martes. Trae a un amigo y disfruta un momento tranquilo.",
                "กาแฟและการพบปะแบบไม่ต้องนัดหมายทุกวันอังคาร พาเพื่อนมาและเพลิดเพลินกับช่วงเวลาเงียบ ๆ")),
    };

    private static readonly Trans HeroButtonText = new(
        "Lär känna oss", "Get to know us", "Знайомтесь з нами", "Conócenos", "ทำความรู้จักกับเรา");

    private static readonly Trans ScheduleHeading = new(
        "Veckoschema", "Weekly Schedule", "Тижневий розклад", "Horario semanal", "ตารางประจำสัปดาห์");

    private static readonly Trans ScheduleSubheading = new(
        "Här hittar du veckans aktiviteter och sammankomster",
        "Here you'll find this week's activities and gatherings",
        "Ось активності та зустрічі цього тижня",
        "Aquí encontrarás las actividades y encuentros de la semana",
        "นี่คือกิจกรรมและการรวมตัวประจำสัปดาห์");

    private static readonly Trans EventsHeading = new(
        "Vad som händer", "What's happening", "Що відбувається", "Qué está pasando", "กิจกรรมที่จะเกิดขึ้น");

    private static readonly Trans EventsSubheading = new(
        "Kommande händelser och nyheter i församlingen",
        "Upcoming events and news from the congregation",
        "Майбутні події та новини громади",
        "Próximos eventos y noticias de la congregación",
        "กิจกรรมและข่าวสารที่กำลังจะมาถึงจากคริสตจักร");

    private static readonly Trans CtaHeading = new(
        "Välkommen att besöka oss", "Welcome to visit us", "Ласкаво просимо до нас", "Bienvenido a visitarnos", "ยินดีต้อนรับมาเยี่ยมเรา");

    private static readonly Trans CtaText = new(
        "Vi finns i centrala Mantorp och våra dörrar är öppna för alla. Kom som du är.",
        "We're in central Mantorp and our doors are open to all. Come as you are.",
        "Ми знаходимось у центрі Манторпа і наші двері відкриті för alla. Приходьте такими, як ви є.",
        "Estamos en el centro de Mantorp y nuestras puertas están abiertas a todos. Ven como eres.",
        "เราอยู่ใจกลางเมืองมันทอร์ปและประตูของเราเปิดต้อนรับทุกคน มาในแบบที่คุณเป็น");

    private static readonly Trans CtaButtonText = new(
        "Hitta hit →", "Find your way →", "Знайти дорогу →", "Cómo llegar →", "เส้นทางมาที่นี่ →");

    private void EnsureSiteSettings(IContentType siteSettingsType)
    {
        var existing = _contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "siteSettings");
        if (existing != null) return;

        _logger.LogInformation("Creating Site Settings content node");
        var settings = _contentService.Create("Site Settings", Constants.System.Root, siteSettingsType);
        SetCultureNames(settings, NameSiteSettings);
        SetVariantValue(settings, "footerTagline", FooterTagline);
        SetVariantValue(settings, "copyrightSuffix", CopyrightSuffix);
        settings.SetValue("logoTopText", "Elimkyrkan");
        settings.SetValue("logoBottomText", "Mantorp");
        settings.SetValue("footerAddress", "Kyrkogatan 1\n595 97 Mantorp");
        settings.SetValue("footerPhone", "0142-XXX XX");
        settings.SetValue("footerEmail", "info@elimmantorp.se");
        _contentService.Save(settings);
        _contentService.Publish(settings, new[] { "*" });
    }

    /// <summary>
    /// For content types where only one instance should exist under the parent.
    /// Deletes any duplicates beyond the first (oldest) match, preserving the original.
    /// </summary>
    private IContent EnsureUniqueChild(IContent parent, string swedishName, IContentType contentType, Action<IContent> setProps)
    {
        var matches = _contentService.GetPagedChildren(parent.Id, 0, 100, out _, null)
            .Where(c => c.ContentType.Alias == contentType.Alias)
            .OrderBy(c => c.Id)
            .ToList();

        if (matches.Count > 1)
        {
            foreach (var dup in matches.Skip(1))
            {
                _logger.LogWarning("Removing duplicate {Alias} node '{Name}' (id {Id})", dup.ContentType.Alias, dup.Name, dup.Id);
                _contentService.Delete(dup);
            }
        }

        if (matches.Count >= 1) return matches[0];

        _logger.LogInformation("Creating content node {Name}", swedishName);
        var node = _contentService.Create(swedishName, parent.Id, contentType);
        setProps(node);
        _contentService.Save(node);
        _contentService.Publish(node, new[] { "*" });
        return node;
    }

    private static readonly string[] HomeChildOrder = new[]
    {
        "aboutPage", "activitiesPage", "calendarPage", "teachingPage", "contactPage"
    };

    private void EnforceHomeChildOrder(IContent home)
    {
        var children = _contentService.GetPagedChildren(home.Id, 0, 100, out _, null).ToList();
        for (var i = 0; i < HomeChildOrder.Length; i++)
        {
            var alias = HomeChildOrder[i];
            var child = children.FirstOrDefault(c => c.ContentType.Alias == alias);
            if (child == null || child.SortOrder == i) continue;
            child.SortOrder = i;
            _contentService.Save(child);
            _contentService.Publish(child, new[] { "*" });
        }
    }

    private void SetCultureNames(IContent node, Trans names)
    {
        node.SetCultureName(names.Sv, I18n.Swedish);
        node.SetCultureName(names.En, I18n.English);
        node.SetCultureName(names.Uk, I18n.Ukrainian);
        node.SetCultureName(names.Es, I18n.Spanish);
        node.SetCultureName(names.Th, I18n.Thai);
    }

    private void SetVariantValue(IContent node, string alias, Trans values)
    {
        node.SetValue(alias, values.Sv, I18n.Swedish);
        node.SetValue(alias, values.En, I18n.English);
        node.SetValue(alias, values.Uk, I18n.Ukrainian);
        node.SetValue(alias, values.Es, I18n.Spanish);
        node.SetValue(alias, values.Th, I18n.Thai);
    }

    private IContent EnsureChild(IContent parent, string urlSlug, IContentType contentType, Action<IContent> setProps)
    {
        var existing = _contentService.GetPagedChildren(parent.Id, 0, 100, out _, null)
            .FirstOrDefault(c => c.ContentType.Alias == contentType.Alias && (c.Name == urlSlug || c.GetCultureName(I18n.Swedish)?.Equals(urlSlug, StringComparison.OrdinalIgnoreCase) == true));
        if (existing != null) return existing;

        _logger.LogInformation("Creating content node {Slug}", urlSlug);
        var node = _contentService.Create(urlSlug, parent.Id, contentType);
        setProps(node);
        _contentService.Save(node);
        _contentService.Publish(node, new[] { "*" });
        return node;
    }

    private void SeedTeachingResources(IContent teaching, IContentType teachingResourceType)
    {
        EnsureChild(teaching, NamePredikan.Sv, teachingResourceType, n =>
        {
            SetCultureNames(n, NamePredikan);
            SetVariantValue(n, "heroTitle", NamePredikan);
            SetVariantValue(n, "heroSubtitle", PredikanHeroSub);
            n.SetValue("mainContent", PredikanContentSv, I18n.Swedish);
        });

        EnsureChild(teaching, NameForaldrar.Sv, teachingResourceType, n =>
        {
            SetCultureNames(n, NameForaldrar);
            SetVariantValue(n, "heroTitle", NameForaldrar);
            SetVariantValue(n, "heroSubtitle", ForaldrarHeroSub);
            n.SetValue("mainContent", ForaldrarContentSv, I18n.Swedish);
        });

        EnsureChild(teaching, NameHemgrupperTeaching.Sv, teachingResourceType, n =>
        {
            SetCultureNames(n, NameHemgrupperTeaching);
            SetVariantValue(n, "heroTitle", NameHemgrupperTeaching);
            SetVariantValue(n, "heroSubtitle", HemgrupperTeachingHeroSub);
            n.SetValue("mainContent", HemgrupperTeachingContentSv, I18n.Swedish);
        });
    }

    private void SeedActivities(IContent activities, IContentType activityType)
    {
        foreach (var a in ActivitySeed)
        {
            EnsureChild(activities, a.Names.Sv, activityType, n =>
            {
                SetCultureNames(n, a.Names);
                SetVariantValue(n, "heroSubtitle", a.HeroSubtitle);
                SetVariantValue(n, "shortDescription", a.ShortDescription);
                n.SetValue("icon", a.Icon);
                n.SetValue("mainContent", a.MainContentSv, I18n.Swedish);
            });
        }
    }

    private void EnsureDomains(IContent home)
    {
        AssignDomain(home, "/", I18n.Swedish);
        AssignDomain(home, "/en/", I18n.English);
        AssignDomain(home, "/uk/", I18n.Ukrainian);
        AssignDomain(home, "/es/", I18n.Spanish);
        AssignDomain(home, "/th/", I18n.Thai);
    }

    private void AssignDomain(IContent home, string path, string iso)
    {
        var existing = _domainService.GetAll(false).FirstOrDefault(d => d.DomainName == path && d.RootContentId == home.Id);
        if (existing != null) return;

        var lang = _localizationService.GetLanguageByIsoCode(iso);
        if (lang == null)
        {
            _logger.LogWarning("Language {Iso} not found, skipping domain {Path}", iso, path);
            return;
        }

        _logger.LogInformation("Assigning domain {Path} -> {Iso}", path, iso);
        var domain = new UmbracoDomain(path)
        {
            LanguageId = lang.Id,
            RootContentId = home.Id,
        };
        _domainService.Save(domain);
    }

    // === Translations ===

    private static readonly Trans NameHome = new(
        "Hem", "Home", "Головна", "Inicio", "หน้าแรก");

    private static readonly Trans NameAbout = new(
        "Om oss", "About us", "Про нас", "Sobre nosotros", "เกี่ยวกับเรา");

    private static readonly Trans NameActivities = new(
        "Verksamheter", "Activities", "Заходи", "Actividades", "กิจกรรม");

    private static readonly Trans NameCalendar = new(
        "Kalender", "Calendar", "Календар", "Calendario", "ปฏิทิน");

    private static readonly Trans CalendarHeroSubtitle = new(
        "Kommande samlingar och händelser i Elimkyrkan",
        "Upcoming gatherings and events at Elimkyrkan",
        "Майбутні зустрічі та події в Елімській церкві",
        "Próximas reuniones y eventos en Elimkyrkan",
        "การรวมตัวและกิจกรรมที่กำลังจะมาถึงที่คริสตจักรเอลิม");

    private static readonly Trans NameTeaching = new(
        "Undervisning", "Teaching", "Навчання", "Enseñanza", "คำสอน");

    private static readonly Trans NamePredikan = new(
        "Predikan", "Sermons", "Проповіді", "Sermones", "คำเทศนา");

    private static readonly Trans NameForaldrar = new(
        "Föräldrar", "Parents", "Батьки", "Padres", "พ่อแม่");

    private static readonly Trans NameHemgrupperTeaching = new(
        "Hemgrupper", "Home Groups", "Домашні групи", "Grupos en casa", "กลุ่มในบ้าน");

    private static readonly Trans PredikanHeroSub = new(
        "Tidigare och aktuella predikningar",
        "Past and current sermons",
        "Минулі та поточні проповіді",
        "Sermones anteriores y actuales",
        "คำเทศนาในอดีตและปัจจุบัน");

    private static readonly Trans ForaldrarHeroSub = new(
        "Resurser för dig som förälder",
        "Resources for parents",
        "Ресурси для батьків",
        "Recursos para padres",
        "ทรัพยากรสำหรับพ่อแม่");

    private static readonly Trans HemgrupperTeachingHeroSub = new(
        "Studiematerial och resurser",
        "Study material and resources",
        "Навчальні матеріали та ресурси",
        "Material de estudio y recursos",
        "เนื้อหาการศึกษาและทรัพยากร");

    private static readonly Trans NameContact = new(
        "Kontakt", "Contact", "Контакти", "Contacto", "ติดต่อ");

    private static readonly Trans NameSiteSettings = new(
        "Inställningar", "Site Settings", "Налаштування", "Configuración", "ตั้งค่า");

    private static readonly Trans FooterTagline = new(
        "En del av Evangeliska Frikyrkan.\nMitt i Mantorp – för alla generationer.",
        "Part of the Evangelical Free Church.\nIn the heart of Mantorp – for all generations.",
        "Частина Євангельської Вільної Церкви.\nУ серці Манторпа – для всіх поколінь.",
        "Parte de la Iglesia Evangélica Libre.\nEn el corazón de Mantorp – para todas las generaciones.",
        "ส่วนหนึ่งของคริสตจักรเสรีอีแวนเจลิคัล\nใจกลางเมืองมันทอร์ป – สำหรับทุกวัย");

    private static readonly Trans CopyrightSuffix = new(
        "En del av Evangeliska Frikyrkan (EFK).",
        "Part of the Evangelical Free Church (EFK).",
        "Частина Євангельської Вільної Церкви (EFK).",
        "Parte de la Iglesia Evangélica Libre (EFK).",
        "ส่วนหนึ่งของคริสตจักรเสรีอีแวนเจลิคัล (EFK)");

    private static readonly Trans HomeHeroTitle = new(
        "Välkommen till Elimkyrkan Mantorp",
        "Welcome to Elimkyrkan Mantorp",
        "Ласкаво просимо до Елімської церкви в Манторпі",
        "Bienvenidos a la Iglesia Elim de Mantorp",
        "ยินดีต้อนรับสู่คริสตจักรเอลิม มันทอร์ป");

    private static readonly Trans HomeHeroSubtitle = new(
        "En levande församling för alla generationer – mitt i Mantorp, med rötter i evangelisk frikyrkotradition.",
        "A living congregation for all generations – in the heart of Mantorp, with roots in the evangelical free church tradition.",
        "Жива громада для всіх поколінь – у серці Манторпа, з корінням в євангельській вільноцерковній традиції.",
        "Una congregación viva para todas las generaciones – en el corazón de Mantorp, con raíces en la tradición evangélica de iglesia libre.",
        "คริสตจักรที่มีชีวิตชีวาสำหรับทุกวัย – ใจกลางเมืองมันทอร์ป มีรากฐานจากประเพณีคริสตจักรเสรีอีแวนเจลิคัล");

    private static readonly Trans AboutHeroSubtitle = new(
        "Elimkyrkan Mantorp – en del av Evangeliska Frikyrkan",
        "Elimkyrkan Mantorp – part of the Evangelical Free Church",
        "Елімська церква в Манторпі – частина Євангельської Вільної Церкви",
        "Iglesia Elim de Mantorp – parte de la Iglesia Evangélica Libre",
        "คริสตจักรเอลิม มันทอร์ป – ส่วนหนึ่งของคริสตจักรเสรีอีแวนเจลิคัล");

    private static readonly Trans ActivitiesHeroSubtitle = new(
        "Det finns plats för dig – oavsett ålder och livssituation",
        "There's a place for you – whatever your age or life situation",
        "Тут є місце для кожного – незалежно від віку чи життєвої ситуації",
        "Hay un lugar para ti – sin importar tu edad o situación de vida",
        "มีที่สำหรับคุณ – ไม่ว่าจะอายุเท่าใดหรือสถานการณ์ชีวิตแบบไหน");

    private static readonly Trans TeachingHeroSubtitle = new(
        "Väx i tro och kunskap – tre vägar att fördjupa dig",
        "Grow in faith and knowledge – three paths to go deeper",
        "Зростайте у вірі та знаннях – три шляхи для поглиблення",
        "Crece en fe y conocimiento – tres caminos para profundizar",
        "เติบโตในความเชื่อและความรู้ – สามเส้นทางสู่ความเข้าใจที่ลึกซึ้ง");

    private static readonly Trans ContactHeroSubtitle = new(
        "Vi vill gärna höra från dig",
        "We'd love to hear from you",
        "Ми будемо раді почути від вас",
        "Nos encantaría saber de ti",
        "เรายินดีที่จะได้ยินจากคุณ");

    private record ActivityRow(Trans Names, string Icon, Trans HeroSubtitle, Trans ShortDescription, string MainContentSv);

    private static readonly ActivityRow[] ActivitySeed = new[]
    {
        new ActivityRow(
            new Trans("Barn", "Children", "Діти", "Niños", "เด็ก"),
            "👶",
            new Trans(
                "En trygg plats för de yngsta",
                "A safe place for the youngest",
                "Безпечне місце для наймолодших",
                "Un lugar seguro para los más pequeños",
                "สถานที่ปลอดภัยสำหรับเด็กเล็ก"),
            new Trans(
                "Verksamhet för de yngsta med lek, sång och bibelberättelser i en trygg miljö.",
                "Activities for the youngest with play, songs and Bible stories in a safe environment.",
                "Заняття для наймолодших з іграми, піснями та біблійними історіями в безпечному середовищі.",
                "Actividades para los más pequeños con juegos, canciones e historias bíblicas en un entorno seguro.",
                "กิจกรรมสำหรับเด็กเล็กพร้อมการเล่น เพลง และเรื่องราวจากพระคัมภีร์ในสภาพแวดล้อมที่ปลอดภัย"),
            BarnHtmlSv),

        new ActivityRow(
            new Trans("Next Generation", "Next Generation", "Next Generation", "Next Generation", "Next Generation"),
            "🔥",
            new Trans(
                "Ungdomsverksamhet med energi och gemenskap",
                "Youth activities with energy and community",
                "Молодіжна діяльність з енергією та спільнотою",
                "Actividades juveniles con energía y comunidad",
                "กิจกรรมเยาวชนที่เต็มไปด้วยพลังและความเป็นน้ำหนึ่งใจเดียวกัน"),
            new Trans(
                "Ungdomsverksamhet med aktiviteter, samtal om tro och gemenskap för unga.",
                "Youth ministry with activities, faith conversations and community for young people.",
                "Молодіжна служба з заходами, бесідами про віру та спільнотою для молоді.",
                "Ministerio juvenil con actividades, conversaciones de fe y comunidad para jóvenes.",
                "พันธกิจเยาวชนที่มีกิจกรรม การพูดคุยเรื่องความเชื่อ และการรวมตัวสำหรับคนหนุ่มสาว"),
            "<h2>Ungdomskväll varje fredag</h2><p>Next Generation är vår ungdomsverksamhet där vi samlas varje fredag för en kväll fylld med gemenskap, aktiviteter, samtal och snacks.</p><p>Vi tror på nästa generation och vill skapa en plats där unga kan växa, både som människor och i sin tro.</p>"),

        new ActivityRow(
            new Trans("Hemgrupper", "Home Groups", "Домашні групи", "Grupos en casa", "กลุ่มในบ้าน"),
            "🏡",
            new Trans(
                "Gemenskap i vardagen",
                "Community in everyday life",
                "Спільнота у повсякденному житті",
                "Comunidad en la vida cotidiana",
                "ชุมชนในชีวิตประจำวัน"),
            new Trans(
                "Mindre grupper som träffas i hemmen för bibelstudium, bön och gemenskap.",
                "Smaller groups meeting in homes for Bible study, prayer and fellowship.",
                "Менші групи, які зустрічаються вдома для вивчення Біблії, молитви та спілкування.",
                "Grupos pequeños que se reúnen en hogares para estudio bíblico, oración y comunión.",
                "กลุ่มเล็ก ๆ ที่พบกันที่บ้านเพื่อศึกษาพระคัมภีร์ อธิษฐาน และมีสามัคคีธรรม"),
            HemgrupperHtmlSv),

        new ActivityRow(
            new Trans("Mission", "Mission", "Місія", "Misión", "พันธกิจ"),
            "🌍",
            new Trans(
                "Lokalt och globalt engagemang",
                "Local and global engagement",
                "Локальна та глобальна участь",
                "Compromiso local y global",
                "การมีส่วนร่วมทั้งในท้องถิ่นและทั่วโลก"),
            new Trans(
                "Vårt missionsengagemang lokalt och globalt – att dela tro och omsorg i världen.",
                "Our mission work locally and globally – sharing faith and care in the world.",
                "Наша місіонерська праця локально та глобально – ділитися вірою і турботою у світі.",
                "Nuestro trabajo misionero local y global – compartir fe y cuidado en el mundo.",
                "งานพันธกิจของเราทั้งในท้องถิ่นและทั่วโลก – แบ่งปันความเชื่อและการดูแลในโลก"),
            MissionHtmlSv),

        new ActivityRow(
            new Trans("Mansfrukost", "Men's Breakfast", "Чоловічий сніданок", "Desayuno de hombres", "อาหารเช้าผู้ชาย"),
            "🍳",
            new Trans(
                "Frukost och gemenskap för män",
                "Breakfast and community for men",
                "Сніданок і спілкування для чоловіків",
                "Desayuno y comunidad para hombres",
                "อาหารเช้าและการรวมตัวสำหรับผู้ชาย"),
            new Trans(
                "Frukost och samtal för män. En stund att ladda under veckan.",
                "Breakfast and conversation for men. A moment to recharge during the week.",
                "Сніданок і розмова для чоловіків. Момент перезарядитися посеред тижня.",
                "Desayuno y conversación para hombres. Un momento para recargar durante la semana.",
                "อาหารเช้าและการพูดคุยสำหรับผู้ชาย ช่วงเวลาเติมพลังในช่วงสัปดาห์"),
            MansfrukostHtmlSv),

        new ActivityRow(
            new Trans("Tisdagscafé", "Tuesday Café", "Вівторкове кафе", "Café del martes", "คาเฟ่วันอังคาร"),
            "☕",
            new Trans(
                "Öppet café – alla välkomna",
                "Open café – all welcome",
                "Відкрите кафе – всі бажані",
                "Café abierto – todos bienvenidos",
                "คาเฟ่เปิด – ทุกคนยินดีต้อนรับ"),
            new Trans(
                "Drop-in fika och gemenskap varje tisdag kväll.",
                "Drop-in coffee and community every Tuesday evening.",
                "Чай і спілкування щовівторка увечері.",
                "Café y comunidad sin cita previa cada martes por la noche.",
                "กาแฟและการพบปะแบบไม่ต้องนัดหมายทุกเย็นวันอังคาร"),
            TisdagscafeHtmlSv),

        new ActivityRow(
            new Trans("Loppis Axplocket", "Flea Market Axplocket", "Барахолка Axplocket", "Mercadillo Axplocket", "ตลาดมือสอง Axplocket"),
            "🛍️",
            new Trans(
                "250 m² med fina fynd mitt i Mantorp",
                "250 m² of great finds in the heart of Mantorp",
                "250 м² чудових знахідок у центрі Манторпа",
                "250 m² de buenos hallazgos en el centro de Mantorp",
                "250 ตร.ม. ของสินค้ามือสองคุณภาพดีกลางเมืองมันทอร์ป"),
            new Trans(
                "Second hand-butik. Intäkterna går till församling och mission.",
                "Second hand store. Proceeds go to the congregation and mission.",
                "Магазин секонд-хенд. Виручка йде на громаду та місію.",
                "Tienda de segunda mano. Las ganancias van a la congregación y la misión.",
                "ร้านขายของมือสอง รายได้สนับสนุนคริสตจักรและงานพันธกิจ"),
            AxplocketHtmlSv),

        new ActivityRow(
            new Trans("Familjerådgivning", "Family Counseling", "Сімейне консультування", "Consejería familiar", "การให้คำปรึกษาครอบครัว"),
            "👨‍👩‍👧‍👦",
            new Trans(
                "Stöd för hela familjen",
                "Support for the whole family",
                "Підтримка для всієї родини",
                "Apoyo para toda la familia",
                "การสนับสนุนสำหรับทั้งครอบครัว"),
            new Trans(
                "Professionell rådgivning för par och familjer i en trygg miljö.",
                "Professional counseling for couples and families in a safe environment.",
                "Професійне консультування для пар та родин у безпечному середовищі.",
                "Asesoramiento profesional para parejas y familias en un entorno seguro.",
                "การให้คำปรึกษาเชิงวิชาชีพสำหรับคู่รักและครอบครัวในสภาพแวดล้อมที่ปลอดภัย"),
            FamiljeradgivningHtmlSv),

        new ActivityRow(
            new Trans("Förbön", "Prayer", "Молитва", "Oración", "การอธิษฐาน"),
            "🙏",
            new Trans(
                "Bön för varandra och vår värld",
                "Prayer for one another and our world",
                "Молитва один за одного та за наш світ",
                "Oración por los demás y por nuestro mundo",
                "การอธิษฐานเพื่อกันและกันและเพื่อโลกของเรา"),
            new Trans(
                "Bön och förbön för varandra och vår omvärld.",
                "Prayer and intercession for one another and the world around us.",
                "Молитва і заступництво один за одного та за світ навколо нас.",
                "Oración e intercesión por los demás y por el mundo que nos rodea.",
                "การอธิษฐานและการวิงวอนเพื่อกันและกันและเพื่อโลกรอบตัวเรา"),
            ForboenHtmlSv),
    };

    private const string PredikanContentSv = """
        <div class="teaching-resource-content" data-v="1">
            <h2>Predikningar</h2>
            <p>Här kommer du snart att kunna lyssna på och titta på tidigare predikningar samt följa med i aktuella predikoserier.</p>
            <div class="video-list">
                <!-- Lägg in YouTube-videor här. Mall:
                <div class="video-embed"><iframe src="https://www.youtube.com/embed/VIDEO_ID" title="Predikan" loading="lazy" allowfullscreen></iframe></div>
                -->
            </div>
            <p><em>Innehållet uppdateras löpande.</em></p>
        </div>
        """;

    private const string ForaldrarContentSv = """
        <div class="teaching-resource-content" data-v="1">
            <h2>Resurser för föräldrar</h2>
            <p>Här hittar du snart undervisning, kurser och samtalsgrupper för dig som förälder.</p>
            <div class="video-list">
                <!-- Lägg in YouTube-videor här -->
            </div>
            <p><em>Innehållet uppdateras löpande.</em></p>
        </div>
        """;

    // v3: inline cards moved to Block List (`contentBlocks`) so admin can add/edit/reorder
    // resources via the friendly block editor in backoffice instead of touching HTML.
    private const string HemgrupperTeachingContentSv = """
        <div class="hemgrupper-teaching-content" data-v="3">
            <h2>Undervisning</h2>
            <p>I våra hemgrupper brukar vi ha någon form av undervisning. Det kan vara bibelläsning eller någon typ av bok som man studerar tillsammans. Här är några idéer till hemgruppsledare.</p>

            <!-- ELIM-BLOCKS -->

            <p style="margin-top:24px;"><em>Har du tips på fler resurser som passar för hemgrupperna? Hör av dig så lägger vi till dem här.</em></p>
        </div>
        """;

    // Initial seed for Undervisning → Hemgrupper. Each entry = one resourceCard block.
    // Admin can add more via backoffice ("+ Add" on the Content Blocks field, pick
    // "Resource card", fill in icon/title/description/CTA URL+label).
    private static readonly (string alias, string value)[][] HemgrupperResourceCardSeeds = new[]
    {
        new (string alias, string value)[]
        {
            ("icon", "📖"),
            ("title", "Upptäckande bibelläsning"),
            ("description", "En enkel metod för att läsa Bibeln tillsammans i hemgruppen. Passar både för nya och vana läsare – inga förkunskaper krävs."),
            ("ctaUrl", "/docs/upptäckande_bibelläsning.pdf"),
            ("ctaLabel", "Ladda ner PDF"),
        },
        new (string alias, string value)[]
        {
            ("icon", "📘"),
            ("title", "Ge det vidare"),
            ("description", "En bok om generationsförsamling av Egil Svartdal. En bra utgångspunkt för samtal om tro, gemenskap och att lämna vidare det vi själva fått."),
            ("ctaUrl", "https://www.adlibris.com/se/sok?q=Ge+det+vidare+Egil+Svartdal"),
            ("ctaLabel", "Köp boken"),
        },
    };

    // v2: original paragraphs preserved; added format / audience / practical / accessibility info.
    private const string TisdagscafeHtmlSv = """
        <div class="tisdagscafe-content" data-v="2">
            <h2>En öppen mötesplats</h2>
            <p>På Tisdagscafé kan alla komma som vill ha gemenskap. Kyrkan är öppen för alla.</p>
            <p>Ibland händer speciella saker – som grillning, våfflor eller besök av olika gäster – men prioritet är alltid att umgås.</p>

            <h2>Så ser en tisdag ut</h2>
            <ul>
                <li><strong>Kaffe och hembakat</strong> – det viktigaste.</li>
                <li><strong>Snack över borden</strong> – om allt mellan himmel och jord.</li>
                <li><strong>Något extra ibland</strong> – musik, en gäst med spännande historia, våfflor en gång i månaden, grillning på sommaren.</li>
                <li><strong>Stillsam stund</strong> – för den som vill be eller bara sitta tyst en stund.</li>
            </ul>

            <h2>Vem kommer hit?</h2>
            <p>Många av våra gäster är pensionärer, men alla är varmt välkomna – ingen åldersgräns åt något håll. Tisdagscafé är en bra plats om du vill träffa folk i en lugn och vänlig miljö, eller om du är ny i Mantorp och vill lära känna grannskapet.</p>

            <aside class="contact-card">
                <h3>När och var?</h3>
                <p><strong>Tisdagar kl. 14:00–16:00</strong> (varje vecka, om inget annat anges).<br>Elimkyrkan, Klockvägen 1, Mantorp.</p>
                <p>Specialdagar dyker upp i <a href="/kalender">kalendern</a>.</p>
            </aside>

            <aside class="contact-card">
                <h3>Praktiskt</h3>
                <p>Ingen anmälan, ingen kostnad. Kaffet och fikabrödet är gratis – en frivillig kollekt går till kyrkans verksamhet om du vill bidra.</p>
                <p>Lokalen är tillgänglig med rullator och rullstol – välkomna in!</p>
            </aside>

            <p style="text-align:center; font-size:18px; margin-top:24px;"><strong>Välkommen!</strong></p>
        </div>
        """;

    // v3: image gallery figures moved into Block List property `contentBlocks`.
    private const string AxplocketHtmlSv = """
        <div class="axplocket-content" data-v="3">
            <p>Vår loppis heter <strong>Axplocket</strong> och finns i den röda längan precis bredvid Mantorps tågstation.</p>
            <p><strong>Magasinvägen 3, 595 57 Mantorp</strong></p>

            <h2>Öppettider</h2>
            <ul class="hours-list">
                <li><strong>Onsdagar</strong> 13:00 – 16:00</li>
                <li><strong>Lördagar</strong> 10:00 – 13:00</li>
            </ul>
            <p>Öppet i princip året runt.</p>

            <h2>Här finner du allt möjligt</h2>
            <p>Glas, porslin, möbler, leksaker, lampor, verktyg, böcker – ja, allt du kan tänka dig! Självklart har vi också en ”gubbhörna” med allt från golfklubbor till tekniska prylar.</p>

            <!-- ELIM-BLOCKS -->

            <h2>Vårt syfte – välgörenhet</h2>
            <p>Axplocket drivs ideellt och vårt syfte är att samla in pengar till välgörenhet och skicka hjälpsändningar. Exempelvis:</p>
            <ul>
                <li><strong>Child Safe</strong> – arbetar i Thailand och Laos. De står på barnets sida och arbetar förebyggande mot trafficking.</li>
                <li><strong>Village of Hope</strong> – Estland. Drogrehabilitering på kristen grund.</li>
            </ul>
            <p>Därför är vi tacksamma för allt som lämnas in. Det du lämnar in hjälper någon som är i behov. Din gåva kan hjälpa ett barn som annars kanske blir ett offer för sexhandel, eller en vuxen människa som har hamnat fel i livet.</p>

            <h2>Klädinsamling</h2>
            <p>Varje år har vi glädjen att samla in kläder, skor m.m. som vi sedan har kört till bland annat Estland och Ukraina. Tider och situationer förändras, så det vi med säkerhet kan säga är att allt som samlas in kommer gå till välgörenhet på något sätt – men inte alltid till just Estland eller Ukraina.</p>
            <p>Vill du vara med och hjälpa oss att hjälpa? Har du kläder, skor, sängkläder m.m. så är du välkommen att lämna in det på vår loppis vid Mantorps pendelstation.</p>
            <p><strong>Observera:</strong> vi tar bara emot det som är helt, rent och funktionsdugligt. Vi tar inte emot kläder från och med oktober till och med april.</p>
            <p>Vid behov gör vi också speciella insamlingsdagar. Information om detta hittar du genom Loppis hemsida, Facebook-gruppen ”Loppis - Axplocket”, annonser i lokalpressen och affischering på stan.</p>
            <p><em>Tack för att du hjälper oss att hjälpa!</em></p>

            <h2>Lämna in saker</h2>
            <p>Har du saker som du vill skänka går det alldeles utmärkt att ta med dem vid ordinarie öppettider. Du kan även höra av dig om du behöver hjälp med transporten till oss.</p>

            <h2>Följ oss på Facebook</h2>
            <p>Information om vad som händer och nya grejer hittar du bäst i vår Facebook-grupp.</p>
            <a class="facebook-btn" href="https://www.facebook.com/profile.php?id=100064814883935" target="_blank" rel="noopener">
                <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor" xmlns="http://www.w3.org/2000/svg" aria-hidden="true"><path d="M22 12c0-5.523-4.477-10-10-10S2 6.477 2 12c0 4.991 3.657 9.128 8.438 9.878v-6.987h-2.54V12h2.54V9.797c0-2.506 1.492-3.89 3.777-3.89 1.094 0 2.238.195 2.238.195v2.46h-1.26c-1.243 0-1.63.771-1.63 1.562V12h2.773l-.443 2.89h-2.33v6.988C18.343 21.128 22 16.991 22 12z"/></svg>
                <span>Loppis - Axplocket på Facebook</span>
            </a>

            <aside class="contact-card">
                <h3>Ge en gåva</h3>
                <p><strong>Plusgiro:</strong> 33 93 04 - 8<br><strong>Swish:</strong> 123 343 10 46</p>
            </aside>

            <aside class="contact-card">
                <h3>Kontakt</h3>
                <p>
                    Hans Gustafsson · <a href="tel:+46708498789">070 - 849 87 89</a><br>
                    Gunvor Gransö · <a href="tel:+46705151227">070 - 515 12 27</a>
                </p>
            </aside>

            <h2>Hitta hit</h2>
            <iframe class="map-embed"
                    src="https://maps.google.com/maps?q=58.3486529,15.2897804&hl=sv&z=17&t=m&output=embed"
                    title="Loppis Axplocket Mantorp"
                    loading="lazy"
                    referrerpolicy="no-referrer-when-downgrade"></iframe>

            <p style="text-align:center; font-size:18px; margin-top:24px;"><strong>Välkommen att fynda hos oss!</strong></p>
        </div>
        """;

    // v2: bible verses moved into Block List property `contentBlocks`. The marker
    // lives inside the second column where the verses used to be.
    private const string ForboenHtmlSv = """
        <div class="foerboen-content" data-v="2">
            <div class="two-col">
                <div>
                    <h2>Vi ber gärna för dig!</h2>
                    <p>Vi har en stor och underbar Gud som vill hjälpa oss i våra olika situationer.</p>
                    <p>Många undrar vem Gud är och vad Han egentligen vill. Är det så att Gud verkligen vill hela mig? Är det verkligen så att Han vill hjälpa mig även om jag inte har levt som jag borde?</p>
                    <p>Gud älskar dig och vill möta dig och dina behov. Vi ser det som en stor förmån om vi får vara med och be för dig, så välkommen att maila ditt böneämne till:</p>
                    <p class="email-cta">
                        <a href="mailto:info@elimmantorp.se">info@elimmantorp.se</a>
                    </p>
                </div>
                <div>
                    <h2>Bibelord</h2>
                    <!-- ELIM-BLOCKS -->
                </div>
            </div>
        </div>
        """;

    // v2: counseling principles moved into Block List property `contentBlocks`.
    private const string FamiljeradgivningHtmlSv = """
        <div class="familjeradgivning-content" data-v="2">
            <p>Att leva tillsammans är inte alltid så lätt. Ibland strular det till sig och det blir problem i relationen. Det kan då vara skönt att prata med någon.</p>
            <p>Vi har en familjerådgivning eftersom vi tror att familjen är viktig – en av samhällets grundstenar.</p>

            <h2>Vi arbetar utifrån</h2>
            <!-- ELIM-BLOCKS -->

            <p>Elimkyrkans Familjerådgivning har ambitionen att lyssna, samtala och ge råd.</p>

            <aside class="contact-card">
                <h3>Boka tid för samtal</h3>
                <p>Vi finns i Mjölby centrum. Välkommen att ringa <a href="tel:+4614280070">0142 – 800 70</a> för att boka tid för samtal.</p>
                <p><strong>Besöksadress</strong><br>Kyrkogatan 27<br>595 30 Mjölby</p>
            </aside>
        </div>
        """;

    // v6: country cards moved into Block List property `contentBlocks`. The ELIM-BLOCKS marker
    // signals where the rendered blocks should be inserted in the template.
    private const string MissionHtmlSv = """
        <div class="mission-intro" data-v="6">
            <p>Mission är en viktig del av Elimförsamlingens liv och arbete. Sedan början av 1990-talet har vi på olika sätt fått vara med och stötta människor och församlingar i flera delar av världen.</p>
            <p>Många i församlingen har även själva rest ut på missionsresor genom åren. Man kan säga att mission verkligen finns i Elimförsamlingens hjärta. Vi tror på Jesu uppdrag att gå ut och göra alla folk till lärjungar, och vi vill vara med och sprida hopp, tro och praktisk hjälp där det behövs.</p>
        </div>

        <!-- ELIM-BLOCKS -->

        <aside class="mission-donation">
            <h3>Stöd vårt missionsarbete</h3>
            <p>Vill du stödja vårt missionsarbete går det bra att sätta in pengar på plusgiro eller Swisha. Märk gärna gåvan med landets namn så hamnar den rätt.</p>
            <p><strong>Missionskassan</strong><br>Plusgiro: 33 93 04 - 8<br>Swish: 123 343 10 46</p>
        </aside>
        """;

    private const string HemgrupperHtmlSv = """
        <div class="two-col">
            <div>
                <h2>Hemgrupper</h2>
                <p>Vår församling är uppdelad i mindre grupper som kallas för hemgrupper. Vi samlas varannan vecka jämna veckor hemma hos någon, oftast i närheten av där man själv bor, för att umgås, fika, be, lovsjunga, prata om livet och läsa bibeln tillsammans.</p>
                <p>I hemgruppen har vi möjlighet att, på ett annat sätt än i gudstjänsten, lära känna varandra på djupet och hjälpa varandra genom samtal, bön och andliga gåvor. Här finns möjligheten att hjälpa och stödja varandra i vardagen och här kan vi växa gemensamt i tron och i vår relation med Gud.</p>
                <p>Hemgrupperna är öppna även för dig som inte är kristen, men som är intresserad av att få veta mer om vad livet som kristen innebär. Det är helt okej att besöka någon hemgrupp ett par gånger bara för att testa och se hur det är.</p>
            </div>
            <div>
                <h2>Varför?</h2>
                <p>Vi tror att Gud har kallat oss att vara tillsammans med honom, men också att vara tillsammans med dem som tillhör honom. I den första kristna församlingen, i Nya Testamentets tid, samlades de troende ”i templet och i hemmen” (Apostlagärningarna 2:46) och där fungerade de som Guds familj och delade livet med varandra.</p>
                <p>I templet hade de sina stora sammankomster med hela församlingen och i hemmen träffades de i mindre grupper i en mera personlig gemenskap. Vi önskar att vår församling skall fungera på ett liknande sätt!</p>
                <p>Vi vill därför uppmuntra dig som vill vara med i församlingen att komma på gudstjänsterna, men också att överlåta dig till en djupare gemenskap genom att vara med i en av våra hemgrupper.</p>
            </div>
        </div>
        """;

    // v2: original paragraphs preserved; added sections targeting parents
    // (what happens, who's it for, practical info, first-time guidance).
    private const string BarnHtmlSv = """
        <div class="barn-content" data-v="2">
            <h2>Barnverksamhet i Elimkyrkan</h2>
            <p>Hoppet är vår söndagsskola för barn under gudstjänsten. Här får barnen en egen stund med gemenskap, lek, glädje och aktiviteter anpassade för dem.</p>
            <p>Gudstjänsten börjar klockan 16:00, och varannan söndag, under jämna veckor, går barnen upp till vår härliga övervåning. Där väntar en rolig och trygg miljö med mycket energi och glädje.</p>
            <p>Barn mellan 0–3 år behöver ha en vuxen med sig, men alla åldrar är varmt välkomna.</p>
            <p>Varmt välkommen till Hoppet!</p>

            <h2>Vad händer på Hoppet?</h2>
            <p>Vi vill att barnen ska känna sig sedda, trygga och inkluderade från första stunden. På en typisk söndag varvar vi:</p>
            <ul>
                <li><strong>Bibelberättelser</strong> – berättade på ett sätt som passar barn, ofta med bilder, sång eller rörelse.</li>
                <li><strong>Pyssel och skapande</strong> – något barnen kan ta med sig hem.</li>
                <li><strong>Lek</strong> – både fria lekar och styrda aktiviteter.</li>
                <li><strong>Fika</strong> – en stunds paus med frukt eller saft.</li>
            </ul>

            <h2>Vem är det för?</h2>
            <p>Hoppet är för barn upp till och med lågstadieåldern. När gruppen är stor delar vi ofta in efter ålder så att aktiviteterna passar. Föräldrar och syskon är alltid välkomna att vara med, särskilt med de yngsta.</p>

            <aside class="contact-card">
                <h3>När och var?</h3>
                <p><strong>Söndagar kl. 16:00</strong> – varannan vecka, jämna veckor.<br>Elimkyrkan, Klockvägen 1, Mantorp.</p>
                <p>Titta gärna i <a href="/kalender">kalendern</a> så du ser nästa tillfälle.</p>
            </aside>

            <aside class="contact-card">
                <h3>Första gången?</h3>
                <p>Ingen anmälan behövs – kom när du kan, och det kostar ingenting. Säg gärna till vid entrén att det är första gången så visar vi er till rätta.</p>
            </aside>

            <p style="text-align:center; font-size:18px; margin-top:24px;"><strong>Vi ses på Hoppet!</strong></p>
        </div>
        """;

    // v2: original paragraphs preserved; added format / who / practical info sections.
    private const string MansfrukostHtmlSv = """
        <div class="mansfrukost-content" data-v="2">
            <h2>Starta veckan rätt</h2>
            <p>Mansfrukost är en samling för män i alla åldrar där vi möts över en god frukost, får tid för samtal och delar gemenskap med varandra.</p>
            <p>Varje gång får vi också lyssna till en intressant talare. Ämnena varierar, men målet är att det ska vara både givande, uppmuntrande och relevant för livet.</p>
            <p>Mansfrukosten präglas av en varm gemenskap och en positiv anda. Oavsett om du brukar gå i kyrkan eller bara är nyfiken är du varmt välkommen att vara med.</p>
            <p>Vi samlas vanligtvis i Elimkyrkan på Klockvägen 1 i Mantorp, men ibland även i Equmeniakyrkan Mantorp.</p>
            <p>Håll gärna koll i programmet så att du inte missar nästa mansfrukost.</p>
            <p>Varmt välkommen – ung som gammal!</p>

            <h2>Så ser en mansfrukost ut</h2>
            <ul>
                <li><strong>God frukost</strong> – fralla, ost, ägg, juice och kaffe.</li>
                <li><strong>Talare</strong> – ofta en gäst som delar erfarenheter, livsfrågor eller något ur Bibeln. Cirka 20–30 minuter.</li>
                <li><strong>Samtal kring borden</strong> – utan tvång, du väljer själv hur mycket du deltar.</li>
                <li><strong>Kort andakt</strong> – enkel, ingen behöver svara eller säga något.</li>
            </ul>

            <h2>För vem?</h2>
            <p>För dig som är man – ung eller gammal, troende eller nyfiken. Ta gärna med en vän, kollega eller granne. Mansfrukosten är en bra tillställning att komma till för första gången, för stämningen är öppen och avslappnad.</p>

            <aside class="contact-card">
                <h3>När och var?</h3>
                <p>Vanligtvis <strong>en lördagsmorgon i månaden</strong>. Exakta tider och plats annonseras i <a href="/kalender">kalendern</a>.</p>
                <p>Oftast: Elimkyrkan, Klockvägen 1, Mantorp.<br>Ibland: Equmeniakyrkan Mantorp.</p>
            </aside>

            <aside class="contact-card">
                <h3>Praktiskt</h3>
                <p>Ingen anmälan, ingen kostnad. Vi börjar oftast kring kl. 08:30 och avslutar runt 10:00 så att hela lördagen ligger framför dig.</p>
            </aside>
        </div>
        """;

    // v1: dedicated expanded HTML for the Next Generation page. The original two-paragraph
    // version (still inline in ActivitySeed for first-install seeding) is preserved at the
    // top of this constant — the surrounding sections are additive and target teens/youth.
    private const string NextGenerationHtmlSv = """
        <div class="nextgen-content" data-v="1">
            <h2>Ungdomskväll varje fredag</h2>
            <p>Next Generation är vår ungdomsverksamhet där vi samlas varje fredag för en kväll fylld med gemenskap, aktiviteter, samtal och snacks.</p>
            <p>Vi tror på nästa generation och vill skapa en plats där unga kan växa, både som människor och i sin tro.</p>

            <h2>Vad gör vi tillsammans?</h2>
            <ul>
                <li><strong>Hänga och prata</strong> – soffhäng, spel, fika.</li>
                <li><strong>Aktiviteter</strong> – från pingisturneringar till filmkvällar och utflykter.</li>
                <li><strong>Samtal om livet och tron</strong> – på en nivå där alla kan vara med, oavsett bakgrund.</li>
                <li><strong>Mat eller snacks</strong> – ingen ska gå hungrig hem.</li>
            </ul>

            <h2>Vem är det för?</h2>
            <p>Next Generation är för dig som är i högstadie- eller gymnasieåldern (ungefär 13–19 år). Du behöver inte vara kristen eller ha varit i kyrkan förut – kom precis som du är. Ta gärna med en kompis.</p>

            <aside class="contact-card">
                <h3>När och var?</h3>
                <p><strong>Fredagar kl. 19:00</strong> – varje vecka om inget annat anges.<br>Elimkyrkan, Klockvägen 1, Mantorp.</p>
                <p>Specialkvällar och utflykter dyker upp i <a href="/kalender">kalendern</a>.</p>
            </aside>

            <aside class="contact-card">
                <h3>Första gången?</h3>
                <p>Det är helt okej att komma utan att känna någon – ledarna möter dig vid dörren och presenterar dig för gänget. Det kostar inget och du måste inte stanna hela kvällen.</p>
            </aside>

            <p style="text-align:center; font-size:18px; margin-top:24px;"><strong>Vi ses på fredag!</strong></p>
        </div>
        """;

    private const string AboutHtmlSv = """
        <div class="about-section">
            <div class="about-img">⛪</div>
            <div class="about-text">
                <h2>Vår församling</h2>
                <p>Elimkyrkan Mantorp är en levande församling som välkomnar alla generationer. Vi är en del av Evangeliska Frikyrkan (EFK) och delar deras vision om att sprida evangelium och vara en positiv kraft i samhället.</p>
                <p>Hos oss möts barn, ungdomar, unga vuxna, familjer och seniorer i en gemenskap som präglas av värme, öppenhet och tro.</p>
            </div>
        </div>
        <div class="about-section">
            <div class="about-img">📖</div>
            <div class="about-text">
                <h2>Vår tro</h2>
                <p>Vi tror på en Gud – Fadern, Sonen och den Helige Ande. Bibeln är vår grund och vägledning. Vi tror att Jesus Kristus är Guds son, sänd för att rädda världen, och att den Helige Ande verkar i och genom oss idag.</p>
                <p>Som en del av EFK delar vi den evangeliska frikyrkans trosbekännelse och står på en klassisk kristen trosgrund.</p>
            </div>
        </div>
        <div class="about-section">
            <div class="about-img">🏠</div>
            <div class="about-text">
                <h2>Vår historia</h2>
                <p>Elimkyrkan har en lång och rik historia i Mantorp. Genom åren har församlingen vuxit och förändrats, men kärnan har alltid varit densamma – en plats för gemenskap, tro och hopp.</p>
            </div>
        </div>
        <div class="about-section">
            <div class="about-img">🌍</div>
            <div class="about-text">
                <h2>EFK – Evangeliska Frikyrkan</h2>
                <p>Evangeliska Frikyrkan är ett samfund med rötter i den svenska väckelserörelsen. EFK samlar församlingar över hela Sverige och bedriver missionsarbete i ett flertal länder.</p>
            </div>
        </div>
        """;
}
