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

/// <summary>A game method a tweak routes through its code: the interface package, the movie export, the class and the method.</summary>
public sealed record TweakHookTarget(string Package, string Movie, string ClassName, string Method)
{
    public Interface.Abc.AbcHook Hook => new(ClassName, Method);
}

public sealed record TweakOption(string Key, string Label, bool Default);

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
}

/// <summary>Every tweak Arkive offers, and the game methods each one hooks.</summary>
public static class TweakCatalog
{
    private const string FindParty = "efui_findpartynew";
    private const string Components = "efui_components";
    private const string Base = "efui_base";

    private static TweakHookTarget H(string package, string movie, string className, string method) =>
        new(package, movie, className, method);

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
            H("efui_shoptradepopup", "shoptradepopup", "ark.ui.shopTradePopup.ShopTradePopupWndContent", "configUI"),
            H("efui_shoptradepopup", "shoptradepopup", "ark.ui.shopTradePopup.ShopTradePopupWndContent", "invalidateSizeUpdate"),
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
        new("lobbycp", "Lobby CP", "Shows party members' combat power in the party finder, without hovering over them.", TweakRisk.Display,
        [
            H(FindParty, "findpartynew", "ark.ui.findPartyNew.FindPartyNewPreviewListRendererMember", "memberData"),
        ]),
        new("markettweaks", "Market Tweaks", "Small improvements to the market window, such as buttons that list every item and set prices quickly.", TweakRisk.Batch,
        [
            H("efui_exchangeshop", "exchangeshop", "ark.ui.exchangeShop.childPage.ExchangeShopItemRegistrationPage", "configUI"),
        ]),
        new("nextbutton", "Next Item Button", "Adds a \"Next Item\" button to accessory refining and bracelet refining that picks the next item in the list.", TweakRisk.Display,
        [
            H("efui_itemaccupgrade", "itemaccupgrade", "ark.ui.itemAccUpgrade.ItemAccUpgradeContent", "configUI"),
            H("efui_itembraceletenchant", "itembraceletenchant", "ark.ui.itemBraceletEnchant.ItemBEWndContent", "configUI"),
        ]),
        new("readycheck", "Ready Check Tweaks", "Warns in the entrance prompt of raids and matchmade content when fewer than 11 gems are equipped or no feast is active.", TweakRisk.Display,
        [
            H("efui_dialog", "dialog", "ark.ui.dialogSystem.DialogSystemFactory", "addSource"),
        ],
        [
            new("autoAccept", "Accept ready checks by itself (acts without a click)", false),
        ]),
        new("slottweaks", "Slot Tweaks", "Marks item icons so their properties show at a glance, and shows full stack counts instead of \"9999+\".", TweakRisk.Display,
        [
            H(Base, "main", "ark.controls.ARKNewSlot", "draw"),
            H(Base, "main", "ark.controls.ARKNewSlot", "clearContentData"),
            H(Base, "main", "ark.controls.ARKNewSlot", "addedToStage"),
            H(Base, "main", "ark.controls.ARKIcon", "applyDisableFilter"),
        ]),
        new("storageclaim", "Universal Storage Claim", "Adds \"Claim Page\" and \"Claim All\" buttons to the universal storage window.", TweakRisk.Batch,
        [
            H("efui_currencystorage", "currencystorage", "ark.ui.currencyStorage.CurrencyStorageWndContents", "configUI"),
        ]),
        new("strongholdclaim", "Stronghold Crafting Claim All", "Adds a \"Claim All\" button to stronghold crafting that collects the finished items of every workshop.", TweakRisk.Batch,
        [
            H("efui_townproduction", "townproduction", "ark.ui.townProduction.TownPWndContent", "configUI"),
        ]),
        new("tabslot", "Hold Tab to Right-Click Slots", "Holding Tab while pointing at an item slot counts as right-clicking it, for quick equipping and unequipping of items and gems.", TweakRisk.Automation,
        [
            H(Base, "main", "ark.controls.ARKNewSlot", "handleMouseRollOver"),
            H(Base, "main", "ark.controls.ARKNewSlot", "handleMouseRollOut"),
            H(Base, "main", "ark.ui.ARKMain_New", "structCanvas"),
        ]),
        new("unequipgems", "Unequip Gems Button", "Adds an \"Unequip All\" button to the gem page of the character profile, and an \"Unequip Gems\" button to roster storage.", TweakRisk.Batch,
        [
            H("efui_characterinfo", "characterinfo", "ark.ui.charInfo.CharInfoGemPanel", "configUI"),
            H("efui_interaction", "windowgo", "ark.ui.windowGo.ExpeditionWindowGoContent", "configUI"),
        ]),
        new("fastlaunch", "Faster Launch", "Makes the game start faster by skipping the publisher logos and the seizure warning before the login screen.", TweakRisk.Speed,
        [],
        [
            new("skipIntro", "Skip intro logos", true),
        ]),
        new("favoriteraids", "Favorite Raids", "Adds a Favorites section to the top of the party finder with the raids you mark as favorites.", TweakRisk.Display,
        [
            H(Components, "components", "ark.controls.RollingTreeList", "draw"),
            H(Components, "components", "ark.controls.RollingTreeList", "getFilteredParentData"),
            H(Components, "components", "ark.controls.RollingTreeList", "pushRootIndex"),
            H(FindParty, "findpartynew", "ark.ui.findPartyNew.FindPartyNewContent", "configUI"),
        ]),
    ];

    public static TweakDefinition? Find(string id) => All.FirstOrDefault(tweak => tweak.Id == id);
}
