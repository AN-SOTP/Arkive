using static Arkive.Core.Interface.Abc.AbcHookKind;

namespace Arkive.Core.Tweaks;

/// <summary>How far a tweak goes beyond changing what the interface shows.</summary>
public enum TweakRisk
{
    /// <summary>Only changes what you see.</summary>
    Display,

    /// <summary>Skips waits or animations.</summary>
    Speed,

    /// <summary>Sends several game actions from one click.</summary>
    Batch,

    /// <summary>Acts without a click, or turns a key into clicks.</summary>
    Automation,
}

/// <summary>
/// A game method a tweak hooks: the interface package, the movie export, the class, the method, and the tweak code's
/// static handler ("arkive.tweaks.Class.method"; null while the tweak isn't written yet), and when the handler runs.
/// </summary>
public sealed record TweakHookTarget(string Package, string Movie, string ClassName, string Method, string? Handler = null,
    Interface.Abc.AbcHookKind Kind = Interface.Abc.AbcHookKind.After)
{
    /// <summary>Private or protected methods and fields of the class that the handler reaches by plain name (see AbcHook.Expose).</summary>
    public IReadOnlyList<string> Expose { get; init; } = [];

    public Interface.Abc.AbcHook Hook => new(ClassName, Method,
        Handler ?? throw new InvalidOperationException($"{ClassName}.{Method} has no handler yet."), Kind: Kind)
    {
        Expose = Expose,
    };
}

/// <summary>
/// An option of a tweak: an on/off switch, or with <see cref="Choices"/> a set of things to pick (saved as the list of
/// chosen values, for example raid ids).
/// </summary>
public sealed record TweakOption(string Key, string Label, bool Default, IReadOnlyList<TweakChoice>? Choices = null);

/// <summary>One thing to pick in a <see cref="TweakOption"/> with choices.</summary>
public sealed record TweakChoice(string Value, string Label, bool Default = false);

public sealed record TweakDefinition(
    string Id,
    string Name,
    string Description,
    TweakRisk Risk,
    IReadOnlyList<TweakHookTarget> Hooks,
    IReadOnlyList<TweakOption>? Options = null,
    string? Note = null)
{
    public IReadOnlyList<TweakOption> OptionList => Options ?? [];

    /// <summary>True once every hook has its handler written (Faster Launch needs none).</summary>
    public bool Available => Id == "fastlaunch" || (Hooks.Count > 0 && Hooks.All(hook => hook.Handler is not null));
}

/// <summary>Every tweak Arkive offers, and the game methods each one hooks.</summary>
public static class TweakCatalog
{
    private const string FindParty = "efui_findpartynew";
    private const string Components = "efui_components";
    private const string Base = "efui_base";

    private static TweakHookTarget H(string package, string movie, string className, string method, string? handler = null,
        Interface.Abc.AbcHookKind kind = Interface.Abc.AbcHookKind.After, string[]? expose = null) =>
        new(package, movie, className, method, handler, kind) { Expose = expose ?? [] };

    public static IReadOnlyList<TweakDefinition> All { get; } =
    [
        new("boxtweaks", "Chest Opening Tweaks", "Various tweaks for the chest opening window.", TweakRisk.Display,
        [
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemRollingItem", "setData"),
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemItemContent", "configUI"),
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemRandomItem", "configUI"),
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemSelectItem", "configUI"),
            H(Base, "main", "ark.ui.dialogSystem.contents.MiddleContentGroup", "addComponent"),
        ]),
        new("buffstack", "Buff Icon Stacking", "Places support buff icons in a separate row above other buffs.", TweakRisk.Display,
        [
            H(Components, "components", "ark.controls.ARKSlotManager", "setSlotCount"),
            H(Components, "components", "ark.controls.BaseList", "updateAlign"),
            H("efui_quickslot", "quickslot", "ark.ui.quickSlot.QuickSlotFrame", "configUI"),
            H("efui_quickslot", "quickslot", "ark.ui.quickSlot.QuickSLotBuffSlotManager", "configUI"),
        ], Note: "This doesn't raise how many buffs show at once. If icons get hidden, raise the number of shown buffs in the game's settings."),
        new("exchangemax", "Exchange Max Button", "Adds \"Exchange Max\" buttons to the event shop and other exchange windows.", TweakRisk.Batch,
        [
            H("efui_shoptradepopup", "shoptradepopup", "ark.ui.shopTradePopup.ShopTradePopupWndContent", "configUI", "arkive.tweaks.ExchangeMax.onConfigUI"),
            H("efui_shoptradepopup", "shoptradepopup", "ark.ui.shopTradePopup.ShopTradePopupWndContent", "invalidateSizeUpdate", "arkive.tweaks.ExchangeMax.onInvalidateSizeUpdate"),
        ],
        [
            new("bound", "\"Exchange Max (Bound)\" button, which uses bound items first", true),
        ]),
        new("extendedescapemenu", "Extended Escape Menu", "Adds separate rows for paradise power and hourglass status to the content status tables in the escape menu.", TweakRisk.Display,
        [
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMenuChildContentsPage", "configUI"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMenuChildContentsPage", "contentsStatusInfo"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMenuChildContentsPage", "updateTextPosition"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMEnuContentsGroup", "configUI"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMEnuContentsGroup", "setLoading"),
        ]),
        new("extracurrency", "Additional Top Currencies", "Shows how many of certain progression materials you have in the top bar's currency list.", TweakRisk.Display,
        [
            H("efui_tophud", "tophud", "ark.ui.topHUD.TopHUDContentsContainer", "configUI"),
            H("efui_tophud", "tophud", "ark.ui.topHUD.TopHUDContentsContainer", "getTotalW"),
            H("efui_tophud", "tophud", "ark.ui.topHUD.renderers.TopHUDCurrencyListItem", "setData"),
        ],
        [
            new("showLeapstones", "Leapstones", true),
            new("showDestructionStones", "Destruction stones", true),
            new("showGuardianStones", "Guardian stones", true),
            new("showFusionMaterials", "Fusion materials", true),
            new("showGracedShards", "Graced shards", true),
            new("showCoreEssence", "Core essence", true),
        ]),
        new("fastadvhone", "Fast Advanced Honing", "Lets advanced honing be clicked again straight away.", TweakRisk.Speed,
        [
            H("efui_itemamp", "itemamp", "ark.ui.itemAmp.ItemAmpContent", "successEffectState"),
        ]),
        new("fastfacet", "Auto-Dismiss Stone Faceting Results", "Closes the result of automatic stone faceting by itself, so cutting stones in a row is a little faster. Stones you cut by hand still show their result.", TweakRisk.Automation,
        []),
        new("fastpp", "Fast Paradise", "Lets paradise enhancing and resonance be clicked again straight away.", TweakRisk.Speed,
        [
            H("efui_trinityorbmanagement", "trinityorbmanagement", "ark.ui.trinityOrbManagement.TrinityOrbManagement_Child_AlterationGrade", "configUI"),
            H("efui_trinityorbmanagement", "trinityorbmanagement", "ark.ui.trinityOrbManagement.TrinityOrbManagement_Child_Enhance", "resultData"),
        ]),
        new("lobbycp", "Lobby CP", "Shows party members' combat power in the party finder, without hovering over them: green with the support mark for supports, red for damage dealers.", TweakRisk.Display,
        [
            H(FindParty, "findpartynew", "ark.ui.findPartyNew.FindPartyNewPreviewListRendererMember", "configUI", "arkive.tweaks.LobbyCp.onConfigUI"),
            H(FindParty, "findpartynew", "ark.ui.findPartyNew.FindPartyNewPreviewListRendererMember", "memberData", "arkive.tweaks.LobbyCp.onMemberData"),
        ]),
        new("markettweaks", "Market Tweaks", "Buttons on the market's listing page that set the quantity to everything you have and the price to the lowest listed price.", TweakRisk.Batch,
        [
            H("efui_exchangeshop", "exchangeshop", "ark.ui.exchangeShop.childPage.ExchangeShopItemRegistrationPage", "configUI", "arkive.tweaks.MarketTweaks.onConfigUI"),
        ],
        [
            new("maxCount", "\"Max\" button beside the quantity", true),
            new("prices", "\"Lowest\" and \"-1\" buttons beside the price", true),
        ]),
        new("nextbutton", "Next Item Button", "Adds a \"Next Item\" button to accessory refining and bracelet refining that picks the next item in the list.", TweakRisk.Display,
        [
            H("efui_itemaccupgrade", "itemaccupgrade", "ark.ui.itemAccUpgrade.ItemAccUpgradeContent", "configUI"),
            H("efui_itembraceletenchant", "itembraceletenchant", "ark.ui.itemBraceletEnchant.ItemBEWndContent", "configUI"),
        ]),
        new("readycheck", "Ready Check Tweaks", "Warns in the entrance prompt of raids and matchmade content when fewer than 11 gems are equipped or no feast is active.", TweakRisk.Display,
        [
            H("efui_dialog", "dialog", "ark.ui.dialogSystem.DialogSystemFactory", "addSource", "arkive.tweaks.ReadyCheck.onAddSource"),
            H("efui_quickslot", "quickslot", "ark.ui.quickSlot.QuickSlotFrame", "configUI", "arkive.tweaks.ReadyCheck.onQuickSlotConfigUI"),
        ],
        [
            new("autoAccept", "Accept ready checks by itself (acts without a click)", false),
        ], Note: "Gems and feasts are checked the way Mari does, by the English text of their tooltips, so this works with the English game client. Auto-accept skips prompts that mention the keyboard input limit."),
        new("slottweaks", "Slot Tweaks", "Marks item icons so their properties show at a glance, and shows full stack counts instead of \"9999+\".", TweakRisk.Display,
        [
            H(Base, "main", "ark.controls.ARKNewSlot", "draw"),
            H(Base, "main", "ark.controls.ARKNewSlot", "clearContentData"),
            H(Base, "main", "ark.controls.ARKNewSlot", "addedToStage"),
            H(Base, "main", "ark.controls.ARKIcon", "applyDisableFilter"),
        ]),
        new("storageclaim", "Universal Storage Claim", "Adds \"Claim Page\" and \"Claim All\" buttons to the universal storage window.", TweakRisk.Batch,
        [
            H("efui_currencystorage", "currencystorage", "ark.ui.currencyStorage.CurrencyStorageWndContents", "configUI", "arkive.tweaks.StorageClaim.onConfigUI"),
        ],
        [
            new("page", "\"Claim Page\" button", true),
            new("all", "\"Claim All\" button", true),
        ]),
        new("strongholdclaim", "Stronghold Crafting Claim All", "Adds a \"Claim EVERYTHING\" button to stronghold crafting that collects the finished items of every workshop.", TweakRisk.Batch,
        [
            H("efui_townproduction", "townproduction", "ark.ui.townProduction.TownPWndContent", "configUI", "arkive.tweaks.StrongholdClaim.onConfigUI"),
        ], Note: "If a result window opens after a claim, close it and the button moves on to the next workshop."),
        new("tabslot", "Hold Tab to Right-Click Slots", "Holding Tab while pointing at an item slot counts as right-clicking it, for quick equipping and unequipping of items and gems.", TweakRisk.Automation,
        [
            H(Base, "main", "ark.controls.ARKNewSlot", "addedToStage", "arkive.tweaks.TabSlot.onAddedToStage"),
            H(Base, "main", "ark.controls.ARKNewSlot", "handleMouseRollOver", "arkive.tweaks.TabSlot.onRollOver", expose: ["handleMouseUp"]),
            H(Base, "main", "ark.controls.ARKNewSlot", "handleMouseRollOut", "arkive.tweaks.TabSlot.onRollOut"),
        ]),
        new("unequipgems", "Unequip Gems Button", "Adds an \"Unequip All\" button to the gem page of the character profile, and an \"Unequip Gems\" button to roster storage.", TweakRisk.Batch,
        [
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoGemPanel", "configUI", "arkive.tweaks.UnequipGems.onGemPanelConfigUI"),
            H("efui_interaction", "windowgo", "ark.ui.windowGo.ExpeditionWindowGoContent", "configUI", "arkive.tweaks.UnequipGems.onStorageConfigUI"),
        ],
        [
            new("profile", "\"Unequip All\" on the character profile's gem page", true),
            new("storage", "\"Unequip Gems\" in roster storage (gems bound to the character stay)", true),
        ]),
        new("fastlaunch", "Faster Launch", "Makes the game start faster by skipping the publisher logos and the seizure warning before the login screen.", TweakRisk.Speed,
        [],
        [
            new("skipIntro", "Skip intro logos", true),
        ]),
        new("favoriteraids", "Favorite Raids", "Adds a Favorites section to the top of the party finder with the raids you mark as favorites.", TweakRisk.Display,
        [
            H(Components, "components", "ark.controls.RollingTreeList", "draw", "arkive.tweaks.FavoriteRaids.onDraw", Before),
            H(Components, "components", "ark.controls.RollingTreeList", "getFilteredParentData", "arkive.tweaks.FavoriteRaids.onGetFilteredParentData", Before),
            H(Components, "components", "ark.controls.RollingTreeList", "pushRootIndex", "arkive.tweaks.FavoriteRaids.onPushRootIndex",
                expose: ["_filteredIndexDataArray", "_nextIndex"]),
            H(FindParty, "findpartynew", "ark.ui.findPartyNew.FindPartyNewContent", "configUI", "arkive.tweaks.FavoriteRaids.onConfigUI"),
        ],
        [
            new("raids", "Favorite raids", false, Raids(
                "Raid Level 1", "Raid Level 2", "Raid Level 3", "Raid Level 4", "Raid Level 5", "Raid Level 6", "Raid Level 7",
                "Raid Level 8", "Shade", "Trial Guardian Raid", "Argos", "Behemoth, the Storm Commander",
                "Resurrected Demon Beast Lair", "Garden of Crimson Delight", "Midnight Circus", "Phantom Astalgia",
                "Corrupt Lord's Pandemonium", "Dark Baratron", "Overture: Spiral of the Crimson Midnight Sun",
                "Act 1: Earth-Shattering Hellfire", "Act 2: Requiem of the Floating Nightmare", "Act 3: Night of Storms and Darkness",
                "Act 4: Fortress of Destruction", "Denouement: Final Day", "Witch of Agony, Serca", "Belgardin, Arbiter of Death",
                "Ancient Elveria", "Phantom Palace", "Ark of Arrogance", "Gate of Paradise", "Oreha's Well", "Kayangel",
                "Ivory Tower of Chaos", "Horizon Cathedral", "Ebony Cube", "Haal's Hourglass", "Other")),
        ], Note: "Raids are matched by the name the party finder shows, so this works with the English game client."),
    ];

    /// <summary>
    /// The party finder's raid categories as Mari lists them (by the English titles the tree shows, which is what the
    /// tweak code matches), all picked to start with, as in Mari.
    /// </summary>
    private static IReadOnlyList<TweakChoice> Raids(params string[] titles) =>
        [.. titles.Select(title => new TweakChoice(title, title, Default: true))];

    public static TweakDefinition? Find(string id) => All.FirstOrDefault(tweak => tweak.Id == id);
}
