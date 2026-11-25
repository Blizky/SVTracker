using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;
using StardewValley.GameData.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;

namespace StardewValleyItemTracker;

/// <summary>Tracks items through SMAPI console commands and notifies when one is acquired.</summary>
internal sealed class ModEntry : Mod
{
    private const string SaveDataKey = "tracking-v1";
    private TrackerSaveData data = new();
    private List<CatalogItem> catalog = new();
    private readonly Dictionary<string, int> inventoryBaselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> lastNotificationByItem = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BirthdayMatch>> birthdayWatch = new(StringComparer.Ordinal);
    private readonly List<CommunityBundleInfo> communityBundles = new();
    private readonly Queue<HudNotice> pendingHudNotices = new();
    private readonly List<NotificationHistoryEntry> notificationHistory = new();
    private HudNotice? currentHudNotice;
    private float currentHudNoticeTimeLeft;

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.Saving += this.OnSaving;
        helper.Events.Player.InventoryChanged += this.OnInventoryChanged;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.RenderedHud += this.OnRenderedHud;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            this.data = new();
            this.catalog.Clear();
            this.inventoryBaselines.Clear();
            this.lastNotificationByItem.Clear();
            this.birthdayWatch.Clear();
            this.communityBundles.Clear();
            this.notificationHistory.Clear();
            this.pendingHudNotices.Clear();
            this.currentHudNotice = null;
            this.currentHudNoticeTimeLeft = 0;
        };

        this.RegisterChatCommand("track", "Track an item: /track <item name>[, note]", this.TrackCommand);
        this.RegisterChatCommand("untrack", "Stop tracking an item or villager: /untrack <item name|NPC name>", this.UntrackCommand);
        this.RegisterChatCommand("tracking", "List items currently on your wishlist: /tracking", this.ListTrackedCommand);
        this.RegisterChatCommand("tracked", "List items currently on your wishlist: /tracked", this.ListTrackedCommand);
        this.RegisterChatCommand("check", "Check items loved by an NPC or active quest items: /check <NPC name|quests>", this.CheckCommand);
    }

    private void RegisterChatCommand(string name, string help, StardewValley.Delegates.ChatCommandHandlerDelegate handler)
    {
        try
        {
            if (!ChatCommands.Exists(name))
            {
                ChatCommands.Register(name, handler, _ => help, Array.Empty<string>(), false, false, false);
                return;
            }

            var namespacedName = $"{this.ModManifest.UniqueID}_{name}";
            if (!ChatCommands.Exists(namespacedName))
            {
                ChatCommands.Register(namespacedName, handler, _ => $"{help} (use /{namespacedName})", Array.Empty<string>(), false, false, false);
                this.Monitor.Log($"/{name} is already registered; use /{namespacedName} for this mod's command.", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't register chat command /{name}: {ex.Message}", LogLevel.Warn);
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.lastNotificationByItem.Clear();
        this.notificationHistory.Clear();
        this.pendingHudNotices.Clear();
        this.currentHudNotice = null;
        this.currentHudNoticeTimeLeft = 0;
        this.data = this.Helper.Data.ReadSaveData<TrackerSaveData>(SaveDataKey) ?? new();
        this.BuildCatalog();
        this.RefreshCommunityCenterBundles();
        this.RefreshBirthdayWatch();
        this.ResetInventoryBaselines();
        this.Monitor.Log($"Loaded {this.catalog.Count} item definitions. Use /track, /untrack, or /tracking in the in-game chat.", LogLevel.Info);
    }

    private void OnSaving(object? sender, SavingEventArgs e) => this.Persist();

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady) return;
        this.RefreshCommunityCenterBundles();
        this.RefreshBirthdayWatch();
        this.ResetInventoryBaselines();
        this.NotifyBirthdayGiftsToday();
    }

    private void RefreshCommunityCenterBundles()
    {
        this.communityBundles.Clear();
        var activeData = Game1.netWorldState.Value.BundleData;
        var bundleData = activeData.Count > 0
            ? activeData
            : this.Helper.GameContent.Load<Dictionary<string, string>>("Data/Bundles");
        foreach (var pair in bundleData)
        {
            var keyParts = pair.Key.Split('/');
            if (keyParts.Length < 2 || keyParts[0].Equals("Abandoned Joja Mart", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(keyParts[^1], out var bundleId)) continue;

            var fields = pair.Value.Split('/');
            if (fields.Length < 3 || string.IsNullOrWhiteSpace(fields[2])) continue;
            var tokens = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var requirements = new List<CommunityBundleRequirement>();
            for (var i = 0; i + 2 < tokens.Length; i += 3)
            {
                var itemId = QualifyBundleItemId(tokens[i]);
                if (string.IsNullOrWhiteSpace(itemId) || !int.TryParse(tokens[i + 2], out var quality)) continue;
                requirements.Add(new CommunityBundleRequirement(itemId, quality));
            }
            if (requirements.Count == 0) continue;

            var name = fields.Length > 6 && !string.IsNullOrWhiteSpace(fields[6]) ? fields[6] : fields[0];
            var requiredSlots = requirements.Count;
            if (fields.Length > 4 && int.TryParse(fields[4], out var configuredSlots) && configuredSlots > 0)
                requiredSlots = Math.Min(configuredSlots, requirements.Count);
            this.communityBundles.Add(new CommunityBundleInfo(bundleId, keyParts[0], name, requiredSlots, requirements));
        }
        this.Monitor.Log($"Loaded {this.communityBundles.Count} active Community Center bundles.", LogLevel.Trace);
    }

    private static string QualifyBundleItemId(string rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId) || rawId == "-1") return "";
        try { return ItemRegistry.QualifyItemId(rawId); }
        catch { return ""; }
    }

    private List<string> GetNeededBundleNames(Item item)
    {
        var names = new List<string>();
        var depositedBundles = Game1.netWorldState.Value.Bundles;
        foreach (var bundle in this.communityBundles)
        {
            var deposited = depositedBundles.GetValueOrDefault(bundle.Id) ?? Array.Empty<bool>();
            if (deposited.Count(isDeposited => isDeposited) >= bundle.RequiredSlots) continue;
            for (var i = 0; i < bundle.Requirements.Count; i++)
            {
                var requirement = bundle.Requirements[i];
                if (requirement.ItemId != item.QualifiedItemId || item.Quality < requirement.MinimumQuality
                    || (i < deposited.Length && deposited[i])) continue;
                names.Add($"{bundle.Room}: {bundle.Name}");
                break;
            }
        }
        return names;
    }

    private void RefreshBirthdayWatch()
    {
        this.birthdayWatch.Clear();
        var seasons = new[] { "spring", "summer", "fall", "winter" };
        var season = Array.IndexOf(seasons, Game1.currentSeason);
        if (season < 0) return;
        var today = season * 28 + Game1.dayOfMonth;
        var characterData = this.Helper.GameContent.Load<Dictionary<string, CharacterData>>("Data/Characters");
        var birthdays = new List<(NPC Npc, int Days)>();
        foreach (var npc in Utility.getAllVillagers())
        {
            if (!characterData.TryGetValue(npc.Name, out var info) || info is null) continue;
            var birthSeason = info.BirthSeason?.ToString();
            if (string.IsNullOrWhiteSpace(birthSeason) || info.BirthDay < 1) continue;
            var birthdaySeason = Array.IndexOf(seasons, birthSeason.ToLowerInvariant());
            if (birthdaySeason < 0) continue;
            var delta = (birthdaySeason * 28 + info.BirthDay - today + 112) % 112;
            if (delta <= 5 && this.HasRoomForFriendshipGrowth(npc, info)) birthdays.Add((npc, delta));
        }

        foreach (var entry in this.catalog)
        {
            Item item;
            try { item = ItemRegistry.Create(entry.Id); }
            catch { continue; }
            var matches = birthdays
                .Where(birthday => birthday.Npc.getGiftTasteForThisItem(item) == NPC.gift_taste_love)
                .OrderBy(birthday => birthday.Days)
                .ThenBy(birthday => birthday.Npc.displayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(birthday => new BirthdayMatch(birthday.Npc.Name, birthday.Npc.displayName, birthday.Days))
                .ToList();
            if (matches.Count > 0) this.birthdayWatch[entry.Id] = matches;
        }
        this.Monitor.Log($"Birthday reminders prepared for {birthdays.Count} upcoming birthdays and {this.birthdayWatch.Count} loved gifts.", LogLevel.Trace);
    }

    private bool HasRoomForFriendshipGrowth(NPC npc, CharacterData info)
    {
        var maxHearts = info.CanBeRomanced ? 8 : 10;
        if (Game1.player.friendshipData.TryGetValue(npc.Name, out var friendship))
        {
            if (friendship.Status != FriendshipStatus.Friendly) maxHearts = 10;
            if (friendship.Status == FriendshipStatus.Married) maxHearts = 14;
        }
        return Game1.player.getFriendshipHeartLevelForNPC(npc.Name) < maxHearts;
    }

    private void NotifyBirthdayGiftsToday()
    {
        var today = this.birthdayWatch
            .Where(pair => pair.Value.Any(match => match.Days == 0))
            .ToList();
        if (today.Count == 0) return;

        var candidates = today.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var available = this.FindAvailableItemCounts(candidates);
        var itemNames = this.catalog.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        var reminders = today
            .SelectMany(pair => pair.Value.Where(match => match.Days == 0).Select(match => (ItemId: pair.Key, match.Name)))
            .GroupBy(match => match.Name, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var birthday in reminders)
        {
            var gifts = birthday
                .Where(match => available.GetValueOrDefault(match.ItemId) > 0 && itemNames.ContainsKey(match.ItemId))
                .Select(match => itemNames[match.ItemId])
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (gifts.Count == 0) continue;

            var giftList = gifts.Count switch
            {
                1 => gifts[0],
                2 => $"{gifts[0]} and {gifts[1]}",
                _ => $"{string.Join(", ", gifts.Take(gifts.Count - 1))} and {gifts[^1]}"
            };
            var pronoun = gifts.Count == 1 ? "that" : "those";
            this.QueueHudNotice(new HudNotice(
                $"{birthday.Key}'s birthday is today. You have {giftList}. {birthday.Key} loves {pronoun}.",
                10000f,
                HighlightTerms: gifts.Append(birthday.Key).ToArray()));
        }
    }

    private Dictionary<string, int> FindAvailableItemCounts(HashSet<string> candidates)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in Game1.player.Items)
            if (item is not null && candidates.Contains(item.QualifiedItemId))
                counts[item.QualifiedItemId] = counts.GetValueOrDefault(item.QualifiedItemId) + item.Stack;

        var visited = new HashSet<GameLocation>();
        var pending = new Queue<GameLocation>(Game1.locations);
        while (pending.Count > 0)
        {
            var location = pending.Dequeue();
            if (location is null || !visited.Add(location)) continue;

            foreach (var obj in location.objects.Values)
            {
                if (obj is Chest chest)
                {
                    foreach (var item in chest.Items)
                        if (item is not null && candidates.Contains(item.QualifiedItemId))
                            counts[item.QualifiedItemId] = counts.GetValueOrDefault(item.QualifiedItemId) + item.Stack;
                }
            }

            if (location is Farm farm)
            {
                foreach (var building in farm.buildings)
                {
                    var indoors = building.GetIndoors();
                    if (indoors is not null && !visited.Contains(indoors)) pending.Enqueue(indoors);
                }
            }
        }
        return counts;
    }

    private void Persist() => this.Helper.Data.WriteSaveData(SaveDataKey, this.data);

    private void BuildCatalog()
    {
        var found = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        foreach (var type in ItemRegistry.ItemTypes)
        {
            foreach (var localId in type.GetAllIds())
            {
                var qualifiedId = $"{type.Identifier}{localId}";
                if (found.ContainsKey(qualifiedId)) continue;
                try
                {
                    var parsed = ItemRegistry.GetData(qualifiedId);
                    if (parsed is null) continue;
                    found.Add(qualifiedId, new CatalogItem(qualifiedId, parsed.DisplayName));
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Skipped item {qualifiedId}: {ex.Message}", LogLevel.Trace);
                }
            }
        }

        this.catalog = found.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void TrackCommand(string[] command, ChatBox chat)
    {
        if (!this.RequireLoadedSave(chat)) return;
        var input = string.Join(" ", command.Skip(1)).Trim();
        if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            this.ShowCommandHelp(chat);
            return;
        }
        if (input.Equals("history", StringComparison.OrdinalIgnoreCase) || input.Equals("h", StringComparison.OrdinalIgnoreCase))
        {
            this.ListNotificationHistory(chat);
            return;
        }
        if (input.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            var clearedCount = this.data.Entries.Count;
            this.data.Entries.Clear();
            this.ResetInventoryBaselines();
            this.Persist();
            this.Reply(chat, $"Cleared {clearedCount} saved tracked item(s) and notes.");
            this.QueueHudNotice(new HudNotice("All saved item tracking was cleared.", 4000f));
            return;
        }
        if (this.IsVillagerName(input)) return;
        if (!TrySplitItemAndNote(input, out var itemName, out var note, out var hasNote))
        {
            this.ShowError("Usage: /track [item], [note]. Type /track help for commands.");
            return;
        }
        if (!this.TryFindItem(itemName, out var item, chat)) return;
        if (!this.data.Entries.TryGetValue(item.Id, out var entry)) this.data.Entries[item.Id] = entry = new TrackedEntry();
        entry.Active = true;
        if (hasNote) entry.Note = note.Length > 64 ? note[..64] : note;
        this.inventoryBaselines[item.Id] = this.CountInventoryItem(item.Id);
        this.Persist();
        this.QueueHudNotice(new HudNotice(
            string.IsNullOrWhiteSpace(entry.Note) ? $"Now tracking {item.Name}." : $"Now tracking {item.Name} for {entry.Note}.",
            5000f,
            FadeDurationMs: 1000f,
            HighlightTerms: new[] { item.Name }));
    }

    private void ShowCommandHelp(ChatBox chat)
    {
        this.Reply(chat, "Item Tracker commands:");
        this.Reply(chat, "/track [item], [note] - starts tracking item.");
        this.Reply(chat, "Example: /track Acorn, Needed for a tree");
        this.Reply(chat, "/untrack [item] - stop tracking item.");
        this.Reply(chat, "Use /track [item], [note] again to add or replace its note.");
        this.Reply(chat, "/tracking - show the list of tracked items and notes.");
        this.Reply(chat, "/tracked - same as /tracking.");
        this.Reply(chat, "/track history (or /track h) - show notifications from this save session.");
        this.Reply(chat, "/track reset - clear all saved tracked items and notes.");
        this.Reply(chat, "/check quests (or /check q) - list active delivery quest items you have.");
        this.Reply(chat, "/check [NPC name] - list their loved items and your available count.");
    }

    private void CheckCommand(string[] command, ChatBox chat)
    {
        if (!this.RequireLoadedSave(chat)) return;
        var input = string.Join(" ", command.Skip(1)).Trim();
        if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            this.ShowCommandHelp(chat);
            return;
        }
        if (input.Equals("quests", StringComparison.OrdinalIgnoreCase) || input.Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            this.ListAvailableQuestItems(chat);
            return;
        }
        if (!this.TryFindVillager(input, out var villager, chat))
        {
            if (!string.IsNullOrWhiteSpace(input)) this.Reply(chat, $"Use /check [NPC name] or /check quests. Type /track help for commands.");
            else this.Reply(chat, "Usage: /check [NPC name] or /check quests.");
            return;
        }
        this.ListVillagerLovedItems(villager, chat);
    }

    private void UntrackCommand(string[] command, ChatBox chat)
    {
        if (!this.RequireLoadedSave(chat)) return;
        var itemName = string.Join(" ", command.Skip(1)).Trim();
        if (!this.TryFindItem(itemName, out var item, chat)) return;
        if (!this.data.Entries.TryGetValue(item.Id, out var entry) || !entry.Active)
        {
            this.ShowError($"{item.Name} is not currently tracked.");
            return;
        }
        entry.Active = false;
        entry.Note = "";
        this.inventoryBaselines.Remove(item.Id);
        this.Persist();
        this.QueueHudNotice(new HudNotice($"Stopped tracking {item.Name}; its note was deleted.", 3000f, HighlightTerms: new[] { item.Name }));
    }

    private void ListTrackedCommand(string[] command, ChatBox chat)
    {
        if (!this.RequireLoadedSave(chat)) return;
        var tracked = this.catalog.Where(item => this.data.Entries.TryGetValue(item.Id, out var entry) && entry.Active).ToList();
        this.Reply(chat, tracked.Count == 0 ? "Your wishlist is empty." : $"Currently tracking {tracked.Count} item(s):");
        foreach (var item in tracked)
        {
            var note = this.data.Entries[item.Id].Note;
            this.Reply(chat, $"- {item.Name}" + (string.IsNullOrWhiteSpace(note) ? "" : $" — {note}"));
        }
    }

    private void ListAvailableQuestItems(ChatBox chat)
    {
        var requirements = this.GetActiveDeliveryQuestRequirements();
        if (requirements.Count == 0)
        {
            this.Reply(chat, "You have no active item-delivery quests.");
            return;
        }

        var candidates = requirements.Select(quest => quest.ItemId).ToHashSet(StringComparer.Ordinal);
        var available = this.FindAvailableItemCounts(candidates);
        this.Reply(chat, $"Active quest items ({requirements.Count} quest(s); counts include inventory and chests):");
        foreach (var quest in requirements)
        {
            var itemName = this.catalog.FirstOrDefault(item => item.Id == quest.ItemId)?.Name ?? quest.ItemId;
            var count = available.GetValueOrDefault(quest.ItemId);
            this.Reply(chat, $"- {itemName} — {quest.QuestName} — {count} owned / {quest.RequiredCount} needed");
        }
    }

    private bool TryFindVillager(string name, out NPC villager, ChatBox chat)
    {
        var matches = Utility.getAllVillagers()
            .Where(npc => npc.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || npc.displayName.Equals(name, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (matches.Count == 1)
        {
            villager = matches[0];
            return true;
        }
        if (matches.Count > 1)
        {
            this.Reply(chat, $"'{name}' matches multiple villagers. Use the NPC's internal name:");
            foreach (var match in matches) this.Reply(chat, $"- {match.displayName}: {match.Name}");
        }
        villager = null!;
        return false;
    }

    private bool IsVillagerName(string name)
    {
        return Utility.getAllVillagers().Any(npc => npc.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || npc.displayName.Equals(name, StringComparison.CurrentCultureIgnoreCase));
    }

    private List<CatalogItem> GetVillagerLovedItems(NPC villager)
    {
        var lovedItems = new List<CatalogItem>();
        foreach (var item in this.catalog)
        {
            try
            {
                if (villager.getGiftTasteForThisItem(ItemRegistry.Create(item.Id)) == NPC.gift_taste_love)
                    lovedItems.Add(item);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't check {item.Id} as a gift for {villager.Name}: {ex.Message}", LogLevel.Trace);
            }
        }

        return lovedItems;
    }

    private void ListVillagerLovedItems(NPC villager, ChatBox chat)
    {
        var lovedItems = this.GetVillagerLovedItems(villager);
        if (lovedItems.Count == 0)
        {
            this.Reply(chat, $"No loved items were found for {villager.displayName}.");
            return;
        }

        var available = this.FindAvailableItemCounts(lovedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal));
        var availableTotal = lovedItems.Count(item => available.GetValueOrDefault(item.Id) > 0);
        this.Reply(chat, $"Items {villager.displayName} loves ({availableTotal} available of {lovedItems.Count}):");
        foreach (var item in lovedItems)
            this.Reply(chat, $"- {item.Name} — {available.GetValueOrDefault(item.Id)} available");
    }

    private void ListNotificationHistory(ChatBox chat)
    {
        if (!this.RequireLoadedSave(chat)) return;
        if (this.notificationHistory.Count == 0)
        {
            this.Reply(chat, "No tracker notifications have appeared this session.");
            return;
        }

        this.Reply(chat, $"Tracker notifications this session ({this.notificationHistory.Count}):");
        foreach (var notification in this.notificationHistory)
            this.Reply(chat, $"[{notification.GameTime}] {notification.Message}");
    }

    private bool RequireLoadedSave(ChatBox chat)
    {
        if (Context.IsWorldReady) return true;
        this.Reply(chat, "Load a save before using item tracking commands.");
        return false;
    }

    private bool TryFindItem(string name, out CatalogItem item, ChatBox chat)
    {
        var matches = this.catalog.Where(candidate => candidate.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || candidate.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            item = matches[0];
            return true;
        }
        if (matches.Count > 1)
        {
            this.ShowError($"'{name}' matches multiple items; see chat for qualified IDs.");
            this.Reply(chat, $"'{name}' matches multiple items. Use a qualified item ID:");
            foreach (var match in matches) this.Reply(chat, $"- {match.Name}: {match.Id}");
        }
        else this.ShowError($"Item '{name}' not found.", "Check spelling or use its exact name.");
        item = null!;
        return false;
    }

    private void Reply(ChatBox chat, string message) => chat.addMessage(message, Color.White);

    private void ShowError(string message, string? detail = null) => this.QueueHudNotice(new HudNotice(message, 3000f, IsError: true, Subtitle: detail));

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (Context.IsWorldReady && e.Ticks % 4 == 0) this.CheckInventoryChanges();
        if (this.currentHudNotice is not null)
            this.currentHudNoticeTimeLeft = Math.Max(0, this.currentHudNoticeTimeLeft - (float)Game1.currentGameTime.ElapsedGameTime.TotalMilliseconds);
        if (this.currentHudNotice is not null && this.currentHudNoticeTimeLeft <= 0)
        {
            this.currentHudNotice = null;
            this.currentHudNoticeTimeLeft = 0;
        }
        if (this.currentHudNotice is null && this.pendingHudNotices.Count > 0)
            this.ActivateHudNotice(this.pendingHudNotices.Dequeue());
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (this.currentHudNotice is null || this.currentHudNoticeTimeLeft <= 0) return;

        var batch = e.SpriteBatch;
        var font = Game1.smallFont;
        var hasSubtitle = !string.IsNullOrWhiteSpace(this.currentHudNotice.Subtitle);
        var maxWidth = Math.Max(160, Game1.viewport.Width - 48);
        var title = Shorten(this.currentHudNotice.Title, font, maxWidth);
        var subtitle = hasSubtitle ? Shorten(this.currentHudNotice.Subtitle!, font, maxWidth) : null;
        var alpha = Math.Clamp(this.currentHudNoticeTimeLeft / this.currentHudNotice.FadeDurationMs, 0f, 1f);
        var color = (this.currentHudNotice.IsError ? new Color(255, 176, 0) : Color.LimeGreen) * alpha;
        var panelX = 18;
        var panelY = 74;
        var contentWidth = (int)font.MeasureString(title).X;
        if (subtitle is not null) contentWidth = Math.Max(contentWidth, (int)font.MeasureString(subtitle).X);
        var panelWidth = Math.Min(Game1.viewport.Width - 36, contentWidth + 24);
        var panel = new Rectangle(panelX, panelY, panelWidth, hasSubtitle ? 68 : 42);
        batch.Draw(Game1.staminaRect, panel, new Color(18, 47, 28) * (0.88f * alpha));
        batch.Draw(Game1.staminaRect, new Rectangle(panel.X, panel.Y, panel.Width, 2), Color.PaleGreen * alpha);
        batch.Draw(Game1.staminaRect, new Rectangle(panel.X, panel.Bottom - 2, panel.Width, 2), Color.PaleGreen * alpha);
        batch.Draw(Game1.staminaRect, new Rectangle(panel.X, panel.Y, 2, panel.Height), Color.PaleGreen * alpha);
        batch.Draw(Game1.staminaRect, new Rectangle(panel.Right - 2, panel.Y, 2, panel.Height), Color.PaleGreen * alpha);
        var position = new Vector2(panelX + 12, panelY + 8);
        batch.DrawString(font, title, position + new Vector2(2, 2), Color.Black * alpha);
        this.DrawHighlightedString(batch, font, title, position, color, alpha, this.currentHudNotice.HighlightTerms);
        if (subtitle is not null)
        {
            var subtitlePosition = new Vector2(panelX + 12, panelY + 34);
            batch.DrawString(font, subtitle, subtitlePosition + new Vector2(2, 2), Color.Black * alpha);
            this.DrawHighlightedString(batch, font, subtitle, subtitlePosition, color, alpha, this.currentHudNotice.HighlightTerms);
        }
    }

    private void DrawHighlightedString(SpriteBatch batch, SpriteFont font, string text, Vector2 position, Color baseColor, float alpha, string[]? highlightTerms)
    {
        if (highlightTerms is null || highlightTerms.Length == 0)
        {
            batch.DrawString(font, text, position, baseColor);
            return;
        }

        var x = position.X;
        var index = 0;
        var amber = new Color(255, 176, 0) * alpha;
        while (index < text.Length)
        {
            var nextIndex = -1;
            string? nextTerm = null;
            foreach (var term in highlightTerms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                var foundAt = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (foundAt < 0 || (nextIndex >= 0 && foundAt >= nextIndex)) continue;
                nextIndex = foundAt;
                nextTerm = term;
            }

            if (nextIndex < 0 || nextTerm is null)
            {
                var remaining = text[index..];
                batch.DrawString(font, remaining, new Vector2(x, position.Y), baseColor);
                break;
            }

            if (nextIndex > index)
            {
                var prefix = text[index..nextIndex];
                batch.DrawString(font, prefix, new Vector2(x, position.Y), baseColor);
                x += font.MeasureString(prefix).X;
            }

            var matchedText = text.Substring(nextIndex, nextTerm.Length);
            batch.DrawString(font, matchedText, new Vector2(x, position.Y), amber);
            x += font.MeasureString(matchedText).X;
            index = nextIndex + nextTerm.Length;
        }
    }

    private void QueueHudNotice(HudNotice notice)
    {
        if (this.currentHudNotice is null)
            this.ActivateHudNotice(notice);
        else this.pendingHudNotices.Enqueue(notice);
    }

    private void ActivateHudNotice(HudNotice notice)
    {
        this.currentHudNotice = notice;
        this.currentHudNoticeTimeLeft = notice.DurationMs;
        var hour = Game1.timeOfDay / 100;
        var minute = Game1.timeOfDay % 100;
        var clockHour = hour % 12;
        if (clockHour == 0) clockHour = 12;
        var gameTime = $"{Game1.currentSeason} {Game1.dayOfMonth}, {clockHour}:{minute:00} {(hour >= 12 ? "PM" : "AM")}";
        var message = string.IsNullOrWhiteSpace(notice.Subtitle) ? notice.Title : $"{notice.Title} — {notice.Subtitle}";
        this.notificationHistory.Add(new NotificationHistoryEntry(gameTime, message));
    }

    private static string Shorten(string text, SpriteFont font, float maxWidth)
    {
        var shown = text;
        while (shown.Length > 12 && font.MeasureString(shown).X > maxWidth)
            shown = shown[..^2] + "…";
        return shown;
    }

    private static bool TrySplitItemAndNote(string input, out string itemName, out string note, out bool hasNote)
    {
        var comma = input.IndexOf(',');
        hasNote = comma >= 0;
        itemName = (comma < 0 ? input : input[..comma]).Trim();
        note = comma < 0 ? "" : input[(comma + 1)..].Trim();
        return itemName.Length > 0;
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (!e.IsLocalPlayer || !Context.IsWorldReady) return;
        var updatedTrackingState = false;
        var processedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in e.Added.Concat(e.QuantityChanged.Where(change => change.NewSize > change.OldSize).Select(change => change.Item)))
        {
            var id = item.QualifiedItemId;
            if (!processedIds.Add(id)) continue;
            var currentCount = this.CountInventoryItem(id);
            if (this.inventoryBaselines.TryGetValue(id, out var previousCount) && currentCount <= previousCount)
            {
                this.inventoryBaselines[id] = currentCount;
                continue;
            }
            this.inventoryBaselines[id] = currentCount;
            updatedTrackingState |= this.HandleAcquiredItem(item);
        }
        if (updatedTrackingState) this.Persist();
    }

    private void ResetInventoryBaselines()
    {
        this.inventoryBaselines.Clear();
        foreach (var id in this.GetMonitoredItemIds())
            this.inventoryBaselines[id] = this.CountInventoryItem(id);
    }

    private int CountInventoryItem(string qualifiedId) => Game1.player.Items
        .Where(item => item is not null && item.QualifiedItemId == qualifiedId)
        .Sum(item => item!.Stack);

    private void CheckInventoryChanges()
    {
        var monitoredIds = this.GetMonitoredItemIds();
        foreach (var oldId in this.inventoryBaselines.Keys.Where(id => !monitoredIds.Contains(id)).ToList())
            this.inventoryBaselines.Remove(oldId);
        foreach (var id in monitoredIds)
            if (!this.inventoryBaselines.ContainsKey(id)) this.inventoryBaselines[id] = this.CountInventoryItem(id);
        if (monitoredIds.Count == 0) return;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var representatives = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var item in Game1.player.Items)
        {
            if (item is null || !monitoredIds.Contains(item.QualifiedItemId)) continue;
            counts[item.QualifiedItemId] = counts.GetValueOrDefault(item.QualifiedItemId) + item.Stack;
            representatives[item.QualifiedItemId] = item;
        }

        var updatedTrackingState = false;
        foreach (var id in monitoredIds)
        {
            var currentCount = counts.GetValueOrDefault(id);
            var previousCount = this.inventoryBaselines[id];
            if (currentCount < previousCount) this.inventoryBaselines[id] = currentCount;
            else if (currentCount > previousCount && representatives.TryGetValue(id, out var item))
            {
                this.inventoryBaselines[id] = currentCount;
                updatedTrackingState |= this.HandleAcquiredItem(item);
            }
        }
        if (updatedTrackingState) this.Persist();
    }

    private HashSet<string> GetMonitoredItemIds()
    {
        var ids = this.data.Entries.Where(pair => pair.Value.Active).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        ids.UnionWith(this.birthdayWatch.Keys);
        ids.UnionWith(this.communityBundles.SelectMany(bundle => bundle.Requirements).Select(requirement => requirement.ItemId));
        foreach (var quest in Game1.player.questLog.OfType<ItemDeliveryQuest>())
        {
            if (quest.completed.Value) continue;
            var id = QualifyQuestItemId(quest.ItemId.Value);
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        return ids;
    }

    private bool HandleAcquiredItem(Item item)
    {
        var id = item.QualifiedItemId;
        var hasActiveTracking = this.data.Entries.TryGetValue(id, out var entry) && entry.Active;
        var questNames = this.GetMatchingDeliveryQuestNames(id);
        var birthdayMatches = (this.birthdayWatch.GetValueOrDefault(id) ?? new List<BirthdayMatch>())
            .Where(match => !this.HasGiftedNpcToday(match.NpcName))
            .ToList();
        var bundleNames = this.GetNeededBundleNames(item);
        if (!hasActiveTracking && questNames.Count == 0 && birthdayMatches.Count == 0 && bundleNames.Count == 0) return false;

        var now = Game1.currentGameTime.TotalGameTime;
        if (this.lastNotificationByItem.TryGetValue(id, out var lastNotification)
            && now - lastNotification < TimeSpan.FromSeconds(1))
            return false;
        this.lastNotificationByItem[id] = now;

        if (hasActiveTracking)
        {
            entry!.Active = false;
        }
        var note = hasActiveTracking && !string.IsNullOrWhiteSpace(entry!.Note) ? $" — {entry.Note}" : "";
        var questText = questNames.Count > 0 ? $" Needed for quest {string.Join(", ", questNames)}." : "";
        var birthdayText = this.FormatBirthdayReminder(item.DisplayName, birthdayMatches);
        var bundleText = bundleNames.Count switch
        {
            0 => "",
            1 => $" Needed for {bundleNames[0]} bundle.",
            _ => $" Needed for bundles: {string.Join(", ", bundleNames)}."
        };
        var title = $"{item.DisplayName} found!{note}{questText}{bundleText}{birthdayText}";
        var duration = hasActiveTracking ? 10000f : questNames.Count > 0 ? 6000f : 4000f;
        var highlightTerms = new[] { item.DisplayName }
            .Concat(questNames)
            .Concat(bundleNames)
            .Concat(birthdayMatches.Select(match => match.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        this.QueueHudNotice(new HudNotice(title, duration, HighlightTerms: highlightTerms));
        Game1.playSound("newArtifact");
        return hasActiveTracking;
    }

    private bool HasGiftedNpcToday(string npcName)
    {
        return Game1.player.friendshipData.TryGetValue(npcName, out var friendship)
            && friendship.GiftsToday > 0;
    }

    private string FormatBirthdayReminder(string itemName, List<BirthdayMatch> matches)
    {
        if (matches.Count == 0) return "";
        var maxWidth = Math.Max(160, Game1.viewport.Width - 48);
        var text = "";
        foreach (var match in matches)
        {
            var when = match.Days == 0 ? "today" : match.Days == 1 ? "tomorrow" : $"in {match.Days} days";
            var person = text.Length == 0
                ? $" {match.Name} loves {itemName}; birthday {when}."
                : $" {match.Name} loves it; birthday {when}.";
            if (Game1.smallFont.MeasureString($"{itemName} found!{text}{person}").X > maxWidth)
            {
                if (text.Length == 0) return $" {match.Name} loves {itemName}; birthday {when}.";
                break;
            }
            text += person;
        }
        return text;
    }

    private List<QuestItemRequirement> GetActiveDeliveryQuestRequirements()
    {
        var requirements = new List<QuestItemRequirement>();
        foreach (var quest in Game1.player.questLog.OfType<ItemDeliveryQuest>())
        {
            if (quest.completed.Value) continue;
            var itemId = QualifyQuestItemId(quest.ItemId.Value);
            if (string.IsNullOrWhiteSpace(itemId)) continue;

            var title = quest.questTitle;
            if (string.IsNullOrWhiteSpace(title)) title = "Help Wanted";
            var targetName = quest.target.Value;
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                var npcName = Game1.getCharacterFromName(targetName, false, false)?.displayName;
                if (!string.IsNullOrWhiteSpace(npcName) && !title.Contains(npcName, StringComparison.CurrentCultureIgnoreCase))
                    title = $"{title} for {npcName}";
            }
            requirements.Add(new QuestItemRequirement(itemId, title, quest.number.Value));
        }
        return requirements;
    }

    private List<string> GetMatchingDeliveryQuestNames(string qualifiedId)
    {
        return this.GetActiveDeliveryQuestRequirements()
            .Where(quest => string.Equals(quest.ItemId, qualifiedId, StringComparison.Ordinal))
            .Select(quest => quest.QuestName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string QualifyQuestItemId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return "";
        try { return ItemRegistry.QualifyItemId(itemId); }
        catch { return itemId; }
    }
}

internal sealed class TrackerSaveData
{
    public Dictionary<string, TrackedEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class TrackedEntry
{
    public string Note { get; set; } = "";
    public bool Active { get; set; }
}

internal sealed record CatalogItem(string Id, string Name);
internal sealed record BirthdayMatch(string NpcName, string Name, int Days);
internal sealed record CommunityBundleInfo(int Id, string Room, string Name, int RequiredSlots, List<CommunityBundleRequirement> Requirements);
internal sealed record CommunityBundleRequirement(string ItemId, int MinimumQuality);
internal sealed record NotificationHistoryEntry(string GameTime, string Message);
internal sealed record QuestItemRequirement(string ItemId, string QuestName, int RequiredCount);
internal sealed record HudNotice(string Title, float DurationMs, bool IsError = false, string? Subtitle = null, float FadeDurationMs = 500f, string[]? HighlightTerms = null);
