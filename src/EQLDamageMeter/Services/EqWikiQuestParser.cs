using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static partial class EqWikiQuestParser
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly HashSet<string> NoiseLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bard", "Cleric", "Druid", "Enchanter", "Magician", "Monk", "Necromancer",
        "Paladin", "Ranger", "Rogue", "Shadow Knight", "Shaman", "Warrior", "Wizard",
        "Quests", "Category", "File", "Image", "Faction", "All",
        "Arcane Scientists", "Knights of Truth", "Opal Darkbriar", "Freeport Militia",
        "Priests of Life", "Knights of Thunder", "Guards of Qeynos", "Bloodsabers",
        "Antonius Bayle", "Merchants of Qeynos", "Coalition of Tradefolk",
        "Classic Era", "Velious Era", "Kunark Era", "CheckboxList", "End", "exp",
        "Screenshot Needed", "YouGainExperience", "Item Lore", "Itempage",
        "Keepers of the Art", "Coldain Ring War"
    };

    /// <summary>
    /// Wiki era templates that Template:PageEra currently marks as Out of Era.
    /// </summary>
    private static readonly string[] OutOfEraTemplateMarkers =
    [
        "{{Epics Era",
        "{{EpicQuests Era",
        "{{Epic Quests Era",
        "{{Kunark Era",
        "{{Velious Era",
        "{{Luclin Era",
        "{{Chardok Era",
        "{{Chardok Revamp Era",
        "{{FearHateRevamp Era",
        "{{Unknown Era",
        "{{PageEra|epics",
        "{{PageEra|epicquests",
        "{{PageEra|kunark",
        "{{PageEra|velious",
        "{{PageEra|luclin",
        "{{PageEra|chardok",
        "{{PageEra|chardokrevamp",
        "{{PageEra|unknown"
    ];

    public static async Task<(QuestDetails? Details, string? Error)> FetchAsync(string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (null, "Choose a quest first.");

        if (EqWikiQuestCatalog.IsExcludedTitle(title))
            return (null, "That quest is Out of Era on eqlwiki and is not listed in EQDM.");

        try
        {
            var url =
                "https://eqlwiki.com/api.php?action=parse&prop=wikitext&format=json&page=" +
                Uri.EscapeDataString(title.Trim());
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<ParseResponse>(stream,
                cancellationToken: cancellationToken);
            var wikitext = payload?.Parse?.Wikitext?.Text;
            if (string.IsNullOrWhiteSpace(wikitext))
                return (null, "That quest page could not be loaded from the wiki.");

            if (IsOutOfEraWikitext(wikitext))
                return (null, "That quest is marked Out of Era on eqlwiki and cannot be tracked.");

            var pageTitle = payload?.Parse?.Title?.Trim() is { Length: > 0 } parsedTitle
                ? parsedTitle
                : title.Trim();
            return (Parse(pageTitle, wikitext), null);
        }
        catch (HttpRequestException)
        {
            return (null, "Could not reach eqlwiki.com. Check your network connection.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The wiki request timed out.");
        }
        catch (JsonException)
        {
            return (null, "The wiki response could not be read.");
        }
    }

    public static bool IsOutOfEraWikitext(string? wikitext)
    {
        if (string.IsNullOrWhiteSpace(wikitext)) return false;
        foreach (var marker in OutOfEraTemplateMarkers)
        {
            if (wikitext.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static QuestDetails Parse(string title, string wikitext)
    {
        var checklistSection = ExtractSectionBody(wikitext, "Checklist");
        var walkthroughSection = FirstNonEmpty(
            ExtractSectionBody(wikitext, "Walkthrough"),
            ExtractSectionBody(wikitext, "Short Walkthrough"));
        // Some long quests (e.g. Coldain Ring 10) are prose-only with no section headers.
        var proseFallback = string.IsNullOrWhiteSpace(checklistSection) &&
                            string.IsNullOrWhiteSpace(walkthroughSection)
            ? StripCategories(wikitext)
            : string.Empty;

        var sourceForSteps = !string.IsNullOrWhiteSpace(checklistSection)
            ? checklistSection
            : FirstNonEmpty(walkthroughSection, proseFallback);
        var checklist = ExtractStepLines(sourceForSteps);
        var itemSource = string.Join('\n',
            checklistSection,
            walkthroughSection,
            proseFallback,
            ExtractSectionBody(wikitext, "Related Items"),
            ExtractSectionBody(wikitext, "Reward"),
            ExtractSectionBody(wikitext, "Rewards"));
        var suggested = ExtractSuggestedItems(itemSource);

        var startZone = ReadTableField(wikitext, "Start Zone");
        if (string.IsNullOrWhiteSpace(startZone))
            startZone = InferZoneFromCategories(wikitext);

        return new QuestDetails
        {
            Title = title,
            WikiUrl = EqWikiLinks.BaseUrl + title.Replace(' ', '_'),
            StartZone = CleanField(startZone),
            QuestGiver = CleanField(ReadTableField(wikitext, "Quest Giver")),
            RecommendedLevel = CleanField(
                FirstNonEmpty(
                    ReadTableField(wikitext, "Recommended Level"),
                    ReadTableField(wikitext, "Minimum Level"))),
            Classes = CleanField(ReadTableField(wikitext, "Classes")),
            RelatedZones = CleanField(ReadTableField(wikitext, "Related Zones")),
            RelatedNpcs = CleanField(ReadTableField(wikitext, "Related NPCs")),
            ChecklistLines = checklist,
            SuggestedItems = suggested
        };
    }

    private static string ReadTableField(string wikitext, string label)
    {
        foreach (Match row in TableRowRegex().Matches(wikitext))
        {
            var header = row.Groups["header"].Value;
            if (!header.Contains(label, StringComparison.OrdinalIgnoreCase)) continue;
            return row.Groups["value"].Value.Trim();
        }

        var compact = new Regex(
            @"!\s*'''\s*" + Regex.Escape(label) + @"\s*:?\s*'''\s*\|\s*(?<value>.+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = compact.Match(wikitext);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string ExtractSectionBody(string wikitext, string heading)
    {
        var match = SectionRegex(heading).Match(wikitext);
        return match.Success ? match.Groups["body"].Value : string.Empty;
    }

    private static IReadOnlyList<string> ExtractStepLines(string sectionBody)
    {
        if (string.IsNullOrWhiteSpace(sectionBody)) return [];

        var lines = new List<string>();
        foreach (var raw in sectionBody.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith('*'))
            {
                var bullet = CleanWikiMarkup(trimmed.TrimStart('*').Trim());
                if (bullet.Length == 0 || IsFactionStandingLine(bullet) || IsNoiseStep(bullet))
                    continue;
                // Skip bare mob-name bullets from wave lists (e.g. Coldain ring war).
                if (bullet.Length < 40 && !ItemCueRegex().IsMatch(bullet) && LooksLikeNpc(bullet))
                    continue;
                lines.Add(bullet);
                continue;
            }

            // Prose walkthroughs often put key steps in bold lines (full or leading).
            if (trimmed.StartsWith("'''"))
            {
                var step = CleanWikiMarkup(trimmed);
                if (step.Length > 0 && !IsFactionStandingLine(step) && !IsNoiseStep(step))
                    lines.Add(step);
                continue;
            }

            // Or actionable sentences that mention give/loot/receive/bring.
            if (!trimmed.StartsWith(':') &&
                !trimmed.StartsWith("You say", StringComparison.OrdinalIgnoreCase) &&
                ItemCueRegex().IsMatch(trimmed) &&
                (WikiLinkRegex().IsMatch(trimmed) || ItemTransclusionRegex().IsMatch(trimmed)))
            {
                var step = CleanWikiMarkup(trimmed);
                if (step.Length > 12 && !IsFactionStandingLine(step) && !IsNoiseStep(step))
                    lines.Add(step);
            }
        }

        return DeduplicatePreserveOrder(lines);
    }

    private static IReadOnlyList<string> ExtractSuggestedItems(string rawWikitext)
    {
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Read links from original wikitext before display cleaning strips [[...]].
        foreach (var link in ExtractLinks(rawWikitext))
        {
            if (!IsTrackableItemCandidate(link)) continue;
            items.Add(link);
        }

        // {{:Item Name}} / {{Item Name}} transclusions used for rewards/stats.
        foreach (Match match in ItemTransclusionRegex().Matches(rawWikitext))
        {
            var name = match.Groups["item"].Value.Replace('_', ' ').Trim();
            if (!IsTrackableItemCandidate(name)) continue;
            items.Add(name);
        }

        return items.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsTrackableItemCandidate(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return false;
        if (NoiseLinks.Contains(link)) return false;
        if (link.StartsWith("Category:", StringComparison.OrdinalIgnoreCase)) return false;
        if (link.StartsWith("Skill ", StringComparison.OrdinalIgnoreCase)) return false;
        if (link.EndsWith(" Quest", StringComparison.OrdinalIgnoreCase)) return false;
        if (link.EndsWith(" Quests", StringComparison.OrdinalIgnoreCase)) return false;
        if (link.Equals("Minor Items", StringComparison.OrdinalIgnoreCase)) return false;
        if (FactionNameRegex().IsMatch(link)) return false;
        // Item names can contain zone words (Kedge Backbone, Solusek Mining Company Invoice).
        if (LooksLikeItem(link)) return true;
        if (LooksLikeZone(link)) return false;
        if (LooksLikeNpc(link)) return false;
        return false;
    }

    private static bool IsFactionStandingLine(string text) =>
        text.Contains("faction standing", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("faction has been adjusted", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoiseStep(string text)
    {
        var normalized = text.Trim().TrimStart('\'').Trim();
        return normalized.Equals("{{exp}}", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Your faction standing", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Notes on faction", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Overview:", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("At apprehensive faction", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("At a higher faction", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("You say,", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractLinks(string text)
    {
        foreach (Match match in WikiLinkRegex().Matches(text))
        {
            var target = match.Groups["target"].Value.Trim();
            if (target.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("Image:", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
                continue;
            var pipe = target.IndexOf('|');
            if (pipe >= 0) target = target[..pipe].Trim();
            // Section links like Skill Baking#Guide -> Skill Baking
            var hash = target.IndexOf('#');
            if (hash >= 0) target = target[..hash].Trim();
            target = target.Replace('_', ' ').Trim();
            if (target.Length > 0) yield return target;
        }
    }

    private static string CleanField(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var cleaned = CleanWikiMarkup(value);
        return string.IsNullOrWhiteSpace(cleaned) ? "—" : cleaned;
    }

    private static string CleanWikiMarkup(string value)
    {
        var text = value;
        text = WikiLinkRegex().Replace(text, match =>
        {
            var target = match.Groups["target"].Value;
            var pipe = target.IndexOf('|');
            var display = (pipe >= 0 ? target[(pipe + 1)..] : target).Replace('_', ' ').Trim();
            var hash = display.IndexOf('#');
            if (hash >= 0) display = display[..hash].Trim();
            return display;
        });
        text = ItemTransclusionRegex().Replace(text, match =>
        {
            var name = match.Groups["item"].Value.Replace('_', ' ').Trim();
            if (NoiseLinks.Contains(name) || name.Length < 3) return string.Empty;
            return name;
        });
        text = TemplateRegex().Replace(text, string.Empty);
        text = BoldItalicRegex().Replace(text, "$1");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }

    private static string StripCategories(string wikitext) =>
        CategoryLinkRegex().Replace(wikitext, string.Empty);

    private static string InferZoneFromCategories(string wikitext)
    {
        foreach (Match match in CategoryLinkRegex().Matches(wikitext))
        {
            var category = match.Groups["cat"].Value.Replace('_', ' ').Trim();
            if (category.EndsWith(" Quests", StringComparison.OrdinalIgnoreCase)) continue;
            if (category.Equals("Quests", StringComparison.OrdinalIgnoreCase)) continue;
            if (category.Contains("Equipment", StringComparison.OrdinalIgnoreCase)) continue;
            if (category.Contains("Items", StringComparison.OrdinalIgnoreCase)) continue;
            if (LooksLikeZone(category) || category.Length > 2)
                return category;
        }

        return string.Empty;
    }

    private static bool LooksLikeItem(string name)
    {
        if (ItemNameHintRegex().IsMatch(name) ||
            name.EndsWith(" Ears", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Ear", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Pie", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Scales", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Strings", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Ring", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Head", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Card", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Book", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Orders", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Recipe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Testimony", StringComparison.OrdinalIgnoreCase))
            return true;

        // "Paw of Opolla" / "Cloak of the Undead Eye" — but not zones/mobs like "Ocean of Tears".
        if (!name.Contains(" of ", StringComparison.OrdinalIgnoreCase)) return false;
        if (LooksLikeZone(name)) return false;
        if (MobTitleRegex().IsMatch(name)) return false;
        if (FactionNameRegex().IsMatch(name)) return false;
        return true;
    }

    private static bool LooksLikeNpc(string name)
    {
        if (LooksLikeItem(name) || LooksLikeZone(name)) return false;
        if (name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
            return true;
        return NpcNameHintRegex().IsMatch(name);
    }

    private static bool LooksLikeZone(string name) =>
        ZoneHintRegex().IsMatch(name);

    private static IReadOnlyList<string> DeduplicatePreserveOrder(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (!seen.Add(line)) continue;
            result.Add(line);
        }

        return result;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EQDM/1.2.10 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

    [GeneratedRegex(@"\[\[(?<target>[^\]]+)\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"\{\{[^{}]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateRegex();

    [GeneratedRegex(@"'{2,3}(.+?)'{2,3}", RegexOptions.CultureInvariant)]
    private static partial Regex BoldItalicRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"!\s*'''\s*(?<header>[^']+?)\s*'''\s*\|\s*(?<value>.+?)(?=\r?\n\s*\|-|\r?\n\s*!|\r?\n\s*\|\})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    private static Regex SectionRegex(string heading) => new(
        @"==\s*" + Regex.Escape(heading) + @"\s*==(?<body>.*?)(?=\r?\n==[^=]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [GeneratedRegex(
        @"\b(loot|looted|receive|received|give|bring|hand in|turn in|need|requires?|drop(?:s|ped)?|camp|combine|recipe)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemCueRegex();

    [GeneratedRegex(
        @"\b(Ring|Scythe|Heart|Sword|Shield|Amulet|Cloak|Boots|Helm|Cap|Tome|Page|Note|Torch|Gut|Scales?|Skull|Bone|Blood|Gem|Crystal|Idol|Totem|Key|Badge|Sash|Shackle|Headband|Horn|Lute|Doll|Invoice|Tendril|Backbone|Bits|Ears?|Pie|Paw|Flour|Spirits|Recipe|Head|Strings|Bongos|Dirk|Declaration|Insignia|Crown|Choker|Earring|Faceguard|Protection|Testimony|Kiss)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemNameHintRegex();

    [GeneratedRegex(
        @"^(?:a |an |the )?(?:High )?((?:Priest|Cleric|Warrior|Warlord|Captain|General|Recruit|Veteran|Spearman|Archer|Guard)(?: of .+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MobTitleRegex();

    [GeneratedRegex(@"\{\{:?(?<item>[^{}|]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ItemTransclusionRegex();

    [GeneratedRegex(@"\[\[Category:(?<cat>[^\]]+)\]\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CategoryLinkRegex();

    [GeneratedRegex(
        @"\b(Mountains?|Hills?|Plains?|Desert|Swamp|Forest|Woods?|Keep|Castle|Temple|City|Town|Ruins?|Tunnel|Caverns?|Isle|Island|Ocean|Lake|River|Thicket|Aqueducts|Karana|Qeynos|Freeport|Felwit|Cabilis|Halas|Oggok|Grobb|Rivervale|Ak'Anon|Erudin|Neriak|Paineel|Kunark|Velious|Fear|Hate|Sky|Sebilis|Kedge|Permafrost|Solusek|Mistmoore|Unrest|Najena|Crushbone|Blackburrow|Everfrost|Toxxulia|Steamfont|Butcherblock|Dreadlands|Burning Woods|Skyfire|Karnor's|Kaladim|Cauldron|Plane of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZoneHintRegex();

    [GeneratedRegex(@"^[A-Z][A-Za-z'`\-]+(?:\s+[A-Z][A-Za-z'`\-]+){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex NpcNameHintRegex();

    [GeneratedRegex(
        @"\b(Guards|Knights|Priests|Merchants|Militia|Scientists|Coalition|Bloodsabers|Bayle)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FactionNameRegex();

    private sealed class ParseResponse
    {
        [JsonPropertyName("parse")]
        public ParseBlock? Parse { get; set; }
    }

    private sealed class ParseBlock
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("wikitext")]
        public WikitextBlock? Wikitext { get; set; }
    }

    private sealed class WikitextBlock
    {
        [JsonPropertyName("*")]
        public string? Text { get; set; }
    }
}
