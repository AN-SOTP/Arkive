using Arkive.Core.Interface.Abc;

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
        Setup = Setup,
    };

    /// <summary>
    /// The hook sets its tweak up when a window or control is built (configUI, addedToStage): while the tweak is off the
    /// call is kept and replayed when it's switched on (see AbcHook.Setup).
    /// </summary>
    public bool Setup => Kind == Interface.Abc.AbcHookKind.After && Method is "configUI" or "addedToStage";
}

/// <summary>
/// An option of a tweak: an on/off switch, or with <see cref="Choices"/> a set of things to pick (saved as the list of
/// chosen values, for example raid ids). With <see cref="Single"/> exactly one choice is picked, saved as its value.
/// </summary>
public sealed record TweakOption(string Key, string Label, bool Default, IReadOnlyList<TweakChoice>? Choices = null, bool Single = false)
{
    /// <summary>The value a fresh Tweaks tab saves: the switch, the default choice's value (single), or the default choices.</summary>
    public System.Text.Json.JsonElement DefaultValue => Choices is null
        ? System.Text.Json.JsonSerializer.SerializeToElement(Default)
        : Single
            ? System.Text.Json.JsonSerializer.SerializeToElement((Choices.FirstOrDefault(choice => choice.Default) ?? Choices[0]).Value)
            : System.Text.Json.JsonSerializer.SerializeToElement(Choices.Where(choice => choice.Default).Select(choice => choice.Value).ToArray());

    /// <summary>"switch", "single" (one choice) or "list" (several choices), as the in-game panel reads it.</summary>
    public string Kind => Choices is null ? "switch" : Single ? "single" : "list";
}

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

    /// <summary>A tweak that changes game files rather than interface code (Faster Launch): only Arkive can switch it, with the game closed.</summary>
    public bool RestartOnly => Hooks.Count == 0;
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
        new("logs", "Logs Tab", "Adds a \"Logs\" tab to the character profile and inspect windows with the player's recent lostark.bible logs, by raid. Keep Arkive running while you play: it fetches the logs for the tab.", TweakRisk.Display,
        [
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoWndContent", "configUI", "arkive.tweaks.LogsTab.onWndConfigUI"),
        ]),
        new(TweakModService.SettingsTweak, "In-game Tweak Settings", "An \"Arkive\" button in the escape menu opens a list of your tweaks: switch them on or off and change their options while you play. Keep Arkive running to save those changes; without it they last until you close the game.", TweakRisk.Display,
        [
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.MenuWndContent", "configUI", "arkive.tweaks.SettingsPanel.onMenuConfigUI"),
        ],
        [
            new("everything", "Install every tweak, so any of them can be switched on in game (the ones not ticked here start off)", true),
        ], Note: "Faster Launch changes game files, so it is installed and removed on its own in the Tweaks tab, not in game."),
        new("cpgems", "Combat Power on Tooltips", "A line on item tooltips (gems, gear, accessories, bracelets, ability stones, engraving books, paradise orbs and Ark Grid cores and astrogems; your own, inspected players', the market's) shows how much your Combat Power would go up or down if you equipped that item instead of yours, priced with the game's own table. The honing window shows what the next level adds. Support characters get gem and Ark Grid lines only for now.", TweakRisk.Display,
        [
            H("efui_tooltip", "tooltip", "ark.ui.tooltipDocument.TooltipDocument", "setMainTooltipData", "arkive.tweaks.CombatPower.onMainTooltip", AbcHookKind.Before),
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoEquipAbilityPage", "configUI", "arkive.tweaks.CombatPower.onAbilityConfigUI"),
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoEquipAbilityPage", "combatType", "arkive.tweaks.CombatPower.onCombatType"),
            // Item Inheritance and Set Transfer carry their own copy of this class; whichever movie loads first defines
            // it for every window, so all three copies get the hook.
            H("efui_itembuildup", "itembuilduplevel", "ark.ui.itemBuildUp.levelUp.ItemBuildUpDetailInfoBox", "draw", "arkive.tweaks.CombatPower.onHoningDetails",
                expose: ["_detailData"]),
            H("efui_iteminherit", "iteminherit", "ark.ui.itemBuildUp.levelUp.ItemBuildUpDetailInfoBox", "draw", "arkive.tweaks.CombatPower.onHoningDetails",
                expose: ["_detailData"]),
            H("efui_itemsettransfer", "itemsettransfer", "ark.ui.itemBuildUp.levelUp.ItemBuildUpDetailInfoBox", "draw", "arkive.tweaks.CombatPower.onHoningDetails",
                expose: ["_detailData"]),
        ],
        [
            new("showData", "Show the tooltip data Arkive reads (for testing)", false),
        ], Note: "Open your Character Profile once after logging in: the tweak reads your Combat Power, DPS or support, and your gems from it."),
        new("cpgold", "Combat Power per Gold", "Auction house results (accessories, bracelets, ability stones, gems) and market engraving books show the Combat Power the item would give you and the gold it costs per 1% at the listed price. The best value on screen turns gold. Priced with the same model as Combat Power on Tooltips; rows are never reordered and nothing is searched for you.", TweakRisk.Display,
        [
            H("efui_exchangeshop", "exchangeshop", "ark.ui.exchangeShop.AuctionSearchListRenderer", "setData", "arkive.tweaks.CpGold.onAuctionRow"),
            H("efui_exchangeshop", "exchangeshop", "ark.ui.exchangeShop.controls.ExchangeShopItemRenderer", "setData", "arkive.tweaks.CpGold.onMarketRow"),
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoEquipAbilityPage", "configUI", "arkive.tweaks.CombatPower.onAbilityConfigUI"),
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoEquipAbilityPage", "combatType", "arkive.tweaks.CombatPower.onCombatType"),
        ], Note: "Open your Character Profile once after logging in, as for Combat Power on Tooltips. Support characters get gems only for now."),
        new("marketprice", "Market Price on Tooltips", "Item tooltips show the market price you last saw for that item: the unit price, the bundle's, what your stack is worth, the average, and how long ago you saw it. Prices are remembered as you browse the market (and gems in the auction house), and kept for your next session while Arkive is running; Arkive never searches for you.", TweakRisk.Display,
        [
            H("efui_exchangeshop", "exchangeshop", "ark.ui.exchangeShop.controls.ExchangeShopItemRenderer", "setData", "arkive.tweaks.MarketPrice.onItemRow"),
            H("efui_exchangeshop", "exchangeshop", "ark.ui.exchangeShop.ExchangeShopLastSearchListRenderer", "setData", "arkive.tweaks.MarketPrice.onLastSearchRow"),
            H("efui_exchangeshop", "exchangeshop", "ark.ui.exchangeShop.AuctionSearchListRenderer", "setData", "arkive.tweaks.MarketPrice.onAuctionRow"),
            H("efui_tooltip", "tooltip", "ark.ui.tooltipDocument.TooltipDocument", "setMainTooltipData", "arkive.tweaks.MarketPrice.onMainTooltip", AbcHookKind.Before),
            H(Base, "main", "ark.controls.ARKNewSlot", "handleMouseRollOver", "arkive.tweaks.MarketPrice.onSlotRollOver"),
            H(Base, "main", "ark.controls.ARKNewSlot", "handleMouseRollOut", "arkive.tweaks.MarketPrice.onSlotRollOut"),
        ],
        [
            new("stack", "What the stack you point at is worth", true),
            new("keep", "Keep prices for the next session (Arkive must be running)", true),
            new("average", "The average price beside the lowest", true),
            new("showData", "Show the price data Arkive reads (for testing)", false),
        ], Note: "Only items you have looked up on the market have a price. Bundle sizes are read from the English market text."),
        new("braceletjudge", "Bracelet Reroll Judge", "On the bracelet page that asks whether to keep the current effects or take the new ones, every effect line shows what it adds to your Combat Power, each side shows its total, and the better side says \"Keep\". It only advises: you still pick. Priced with the same game table as Combat Power on Tooltips.", TweakRisk.Display,
        [
            H("efui_itembraceletenchant", "itembraceletenchant", "ark.ui.itemBraceletEnchant.ItemBEChoiceResultPage", "setData", "arkive.tweaks.BraceletJudge.onSetData", AbcHookKind.Before),
            H("efui_itembraceletenchant", "itembraceletenchant", "ark.ui.itemBraceletEnchant.ItemBEChoiceResultPage", "updateNewResultData", "arkive.tweaks.BraceletJudge.onUpdateNewResult", AbcHookKind.Before),
            // Your Combat Power, role and gear, read the same way as Combat Power on Tooltips (each gated by its own tweak).
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoEquipAbilityPage", "configUI", "arkive.tweaks.CombatPower.onAbilityConfigUI"),
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoEquipAbilityPage", "combatType", "arkive.tweaks.CombatPower.onCombatType"),
        ],
        [
            new("showText", "Show the effect text Arkive matches (for testing)", false),
        ], Note: "Open your Character Profile once after logging in: Crit, Specialization and Swiftness lines are priced from your totals there. DPS characters only for now."),
        new("astrogem", "Astrogem Processing Advisor", "In astrogem processing, a box beside the gem's levels shows your chance to reach your target and the gold it takes if you process the offered effects, reroll them or reset the gem, worked out from the game's own odds tables, and suggests the best move. Advice only: it never presses anything.", TweakRisk.Display,
        [
            H("efui_arkgridgem", "arkgridgem", "ark.ui.arkGridGem.ArkGridGemProgressPanel", "configUI", "arkive.tweaks.Astrogem.onConfigUI"),
        ],
        [
            new("target", "Target", false,
                [.. AstrogemOdds.Targets.Select((target, i) => new TweakChoice(target.Id, target.Label, Default: i == 0))], Single: true),
            new("showData", "Show what the tweak reads from the panel (for testing)", false),
        ], Note: "Arkive works the odds out for every target when it installs the tweak, which takes several seconds the first time after a game update."),
        new("boxtweaks", "Chest Opening Tweaks", "Various tweaks for the chest opening window.", TweakRisk.Display,
        [
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemRollingItem", "setData", "arkive.tweaks.BoxTweaks.onSetData"),
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemItemContent", "configUI", "arkive.tweaks.BoxTweaks.onItemConfigUI"),
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemRandomItem", "configUI", "arkive.tweaks.BoxTweaks.onRandomConfigUI"),
            H("efui_boxsystem", "boxsystem", "ark.ui.boxSystem.BoxSystemSelectItem", "configUI", "arkive.tweaks.BoxTweaks.onSelectConfigUI"),
            H(Base, "main", "ark.ui.dialogSystem.contents.MiddleContentGroup", "addComponent", "arkive.tweaks.BoxTweaks.onAddComponent", AbcHookKind.AfterValue),
        ],
        [
            new("openAllButton", "\"Open All\" button (sets the most you can open, then opens)", true),
            new("confirm", "Open confirmation (the check box some chests ask you to tick)", false,
            [
                new TweakChoice("normal", "Normal", Default: true),
                new TweakChoice("tick", "Auto-Tick"),
                new TweakChoice("accept", "Auto-Accept (tick and press OK)"),
            ], Single: true),
        ]),
        new("buffstack", "Buff Icon Stacking", "Places support buff icons in a separate row above other buffs.", TweakRisk.Display,
        [
            H(Components, "components", "ark.controls.BaseList", "updateAlign", "arkive.tweaks.BuffStack.onUpdateAlign", AbcHookKind.Before),
            H("efui_quickslot", "quickslot", "ark.ui.quickSlot.QuickSLotBuffSlotManager", "configUI", "arkive.tweaks.BuffStack.onBarConfigUI"),
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
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMenuChildContentsPage", "configUI", "arkive.tweaks.EscapeMenu.onPageConfigUI"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMenuChildContentsPage", "contentsStatusInfo", "arkive.tweaks.EscapeMenu.onPageUpdate"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMenuChildContentsPage", "updateTextPosition", "arkive.tweaks.EscapeMenu.onPageUpdate"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMEnuContentsGroup", "configUI", "arkive.tweaks.EscapeMenu.onGroupConfigUI"),
            H("efui_systemmenu", "exitmenu", "ark.ui.systemMenu_new.ExitMEnuContentsGroup", "setLoading", "arkive.tweaks.EscapeMenu.onGroupLoading"),
        ]),
        new("extracurrency", "Additional Top Currencies", "Shows how many of certain progression materials you have in the top bar's currency list.", TweakRisk.Display,
        [
            H("efui_tophud", "tophud", "ark.ui.topHUD.TopHUDContentsContainer", "configUI", "arkive.tweaks.ExtraCurrency.onConfigUI"),
            H("efui_tophud", "tophud", "ark.ui.topHUD.TopHUDContentsContainer", "getTotalW", "arkive.tweaks.ExtraCurrency.onGetTotalW", AbcHookKind.AfterValue),
            H("efui_tophud", "tophud", "ark.ui.topHUD.renderers.TopHUDCurrencyListItem", "setData", "arkive.tweaks.ExtraCurrency.onSetData"),
        ],
        [
            new("showLeapstones", "Leapstones", true),
            new("showDestructionStones", "Destruction stones", true),
            new("showGuardianStones", "Guardian stones", true),
            new("showFusionMaterials", "Fusion materials", true),
            new("showGracedShards", "Shards", true),
            new("showCoreEssence", "Core essence", true),
        ]),
        new("fastadvhone", "Fast Advanced Honing", "Skips the result animation of advanced honing, so it can be clicked again straight away.", TweakRisk.Speed,
        [
            H("efui_itemamp", "itemamp", "ark.ui.itemAmp.ItemAmpContent", "successEffectState", "arkive.tweaks.FastAdvHone.onSuccessEffectState",
                AbcHookKind.Before, expose: ["checkAmpComplete"]),
        ]),
        new("fastfacet", "Auto-Dismiss Stone Faceting Results", "Closes the result of automatic stone faceting by itself, so cutting stones in a row is a little faster. Stones you cut by hand still show their result.", TweakRisk.Automation,
        [
            H("efui_interaction", "interactionmarble", "ark.ui.interaction.marble.MarbleContent", "addedToStage", "arkive.tweaks.FastFacet.onAddedToStage"),
            H("efui_interaction", "interactionmarble", "ark.ui.interaction.marble.MarbleContent", "marbleStoneComplete", "arkive.tweaks.FastFacet.onStoneComplete"),
        ]),
        new("fastpp", "Fast Paradise", "Lets paradise enhancing and resonance be clicked again straight away.", TweakRisk.Speed,
        [
            H("efui_trinityorbmanagement", "trinityorbmanagement", "ark.ui.trinityOrbManagement.TrinityOrbManagement_Child_AlterationGrade", "configUI", "arkive.tweaks.FastPp.onConfigUI"),
            H("efui_trinityorbmanagement", "trinityorbmanagement", "ark.ui.trinityOrbManagement.TrinityOrbManagement_Child_Enhance", "resultData", "arkive.tweaks.FastPp.onResultData", AbcHookKind.Before),
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
        new("nextbutton", "Next Item Button", "Adds a \"Next Item\" button to accessory refining, ability stone faceting and bracelet refining that picks the next item in the list.", TweakRisk.Display,
        [
            H("efui_itemaccupgrade", "itemaccupgrade", "ark.ui.itemAccUpgrade.ItemAccUpgradeContent", "configUI", "arkive.tweaks.NextButton.onAccUpgradeConfigUI"),
            H("efui_itembraceletenchant", "itembraceletenchant", "ark.ui.itemBraceletEnchant.ItemBEWndContent", "configUI", "arkive.tweaks.NextButton.onBraceletConfigUI"),
            H("efui_interaction", "interactionmarble", "ark.ui.interaction.marble.MarbleContent", "configUI", "arkive.tweaks.NextButton.onMarbleConfigUI"),
        ]),
        new("readycheck", "Ready Check Tweaks", "Warns in the entrance prompt of raids and matchmade content when fewer than 11 gems are equipped or no feast is active.", TweakRisk.Display,
        [
            H("efui_dialog", "dialog", "ark.ui.dialogSystem.DialogSystemFactory", "addSource", "arkive.tweaks.ReadyCheck.onAddSource"),
            H("efui_quickslot", "quickslot", "ark.ui.quickSlot.QuickSlotFrame", "configUI", "arkive.tweaks.ReadyCheck.onQuickSlotConfigUI"),
        ],
        [
            new("autoAccept", "Accept ready checks by itself (acts without a click)", false),
        ], Note: "Gems and feasts are read from the English text of their tooltips, so this needs the English game client. Auto-accept skips prompts that mention the keyboard input limit."),
        new("slottweaks", "Slot Tweaks", "Marks item icons so their properties show at a glance, and shows full stack counts instead of \"9999+\".", TweakRisk.Display,
        [
            H(Base, "main", "ark.controls.ARKNewSlot", "draw", "arkive.tweaks.SlotTweaks.onDraw"),
            H(Base, "main", "ark.controls.ARKNewSlot", "clearContentData", "arkive.tweaks.SlotTweaks.onClearContentData"),
            H(Base, "main", "ark.controls.ARKIcon", "applyDisableFilter", "arkive.tweaks.SlotTweaks.onApplyDisableFilter"),
            H(Components, "components", "ark.controls.ARKSlotManager", "setSlotCount", "arkive.tweaks.SlotTweaks.onSetSlotCount"),
        ],
        [
            new("showQuality", "Quality glow on accessories and ability stones", true),
            new("showPips", "Bonus pips on bracelets", true),
            new("showPoints", "Engraving levels on ability stones", true),
            new("fullCounts", "Full stack counts instead of \"9999+\"", true),
        ]),
        new("storageclaim", "Universal Storage Claim", "Adds \"Claim Page\" and \"Claim All\" buttons to the universal storage window.", TweakRisk.Batch,
        [
            H("efui_currencystorage", "currencystorage", "ark.ui.currencyStorage.CurrencyStorageWndContents", "configUI", "arkive.tweaks.StorageClaim.onConfigUI"),
        ],
        [
            new("page", "\"Claim Page\" button", true),
            new("all", "\"Claim All\" button", true),
        ]),
        new("fastlaunch", "Faster Launch", "Makes the game start faster by skipping the publisher logos and the seizure warning before the login screen.", TweakRisk.Speed,
        [],
        [
            new("skipIntro", "Skip intro logos", true),
        ]),
    ];

    public static TweakDefinition? Find(string id) => All.FirstOrDefault(tweak => tweak.Id == id);
}
