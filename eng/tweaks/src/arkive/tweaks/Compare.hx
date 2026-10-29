package arkive.tweaks;

import flash.display.Shape;
import flash.display.Sprite;
import flash.events.Event;
import flash.events.MouseEvent;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Dictionary;
import scaleform.clik.events.ButtonEvent;

/**
	Compare With Me: a "Compare" tab in the inspect window that sets your build (left) beside the player's (right). The
	header shows the gap between the two Combat Power figures the game shows. Equipment, accessories (with their
	refinement grades) and gems are drawn as the profile draws its slots (the slot's background, grade background, icon
	and frame), with honing, advanced honing, quality and paradise power beside them; engravings, Ark Passive points,
	combat stats and the Ark Grid are listed beside each other. Every difference says how your part compares with
	theirs in your Combat Power, priced with Combat Power on Tooltips' model (+ when yours is better). Pointing at an
	item shows the game's own tooltip of the slot it came from.

	The page never makes item slots of its own (a new ARKNewSlot binds itself to the game engine, ARKDataBinding), and
	never makes symbols of the profile's own movie: the game froze and crashed (an access violation in the UI engine,
	EFEngine.dll+0x1138318) making the profile's title art, CharInfo_Equip_AbilityPage_BasicAbilityTitleBG, by its class.
	Only the shared component library's pieces are made (a copy of the shared tab button); title rules and
	refinement marks are drawn. Items are read only through the windows' own slots, as Combat Power reads them: theirs
	from the inspect window when the page draws, yours from your profile while it shows (kept, with its lists and
	labels, for later; Combat Power's model keeps your gear and totals from the same reading).

	The tab is a page of its own: the other tabs' pages hide while it shows, and any real tab (or a new inspect)
	closes it.
**/
@:keep
@:access(arkive.tweaks.CombatPower)
class Compare {
	static inline var TWEAK = "compare";
	static inline var TAB_NAME = "arkiveCompareTab";
	static inline var GAME_TABS = 5;
	static inline var SLOT_COUNT = 15;
	static inline var GEM_COUNT = 11;
	/** The accessories' profile slots (necklace, earrings, rings), in the order the gear page lists their refinements. */
	static inline var FIRST_ACCESSORY = 6;
	static inline var ACCESSORIES = 5;

	/** The rule under a section title, in the profile's gold. */
	static inline var RULE = 0x8A7346;
	/** The profile's slots are this wide unscaled (a 64 px icon in a 3 px frame). */
	static inline var SLOT_ART = 70;
	static inline var ICON = 64;

	static inline var TITLE = 0x919CA6;
	static inline var TEXT = 0xE6E6E6;
	static inline var DIM = 0xA9A9A9;
	static inline var GAIN = 0x7EE08F;
	static inline var LOSS = 0xFF8A80;
	static inline var NAME = 0xE3C27F;
	static inline var ADVANCED = 0xC9A86A;

	/** Page layout: the header, three columns under it, and the gems along the bottom of the first two. */
	static inline var PAGE_WIDTH = 1036;
	static inline var PAGE_HEIGHT = 600;
	static inline var TOP = 62;
	static inline var TITLE_BLOCK = 34;
	static inline var ROW = 42;
	static inline var SLOT = 38;
	static inline var LABEL_WIDTH = 72;
	static inline var GEM = 34;
	static inline var GEM_STEP = 40;
	static inline var GEM_LABEL = 76;
	static inline var TEXT_ROW = 18;
	static inline var COLUMN_WIDTH = 326;
	static var COLUMNS:Array<Int> = [16, 362, 708];
	static inline var GEMS_TOP = 440;
	/** The right column's cells: name, yours, theirs, and the change at its end. */
	static inline var NAME_WIDTH = 116;
	static inline var CELL_MINE = 120;
	static inline var CELL_THEIRS = 188;
	static inline var CELL_WIDTH = 66;
	static inline var DELTA_X = 254;
	static inline var ENGRAVING_ROWS = 7;
	/** The Ark Grid rows' cells: shorter names, wider cells ("Legendary 14P"). */
	static inline var GRID_NAME = 90;
	static inline var GRID_MINE = 92;
	static inline var GRID_THEIRS = 174;
	static inline var GRID_CELL = 80;
	static inline var CORE_ROWS = 6;

	/** Equipment rows by the profile's slot numbers (Combat Power's SLOTS): armour and weapon as the profile orders them. */
	static var LEFT_SLOTS:Array<Int> = [1, 5, 2, 3, 4, 0, 14, 13];
	static var RIGHT_SLOTS:Array<Int> = [6, 7, 8, 9, 10, 11, 12];
	static var PASSIVES:Array<String> = ["Evolution", "Enlightenment", "Leap"];

	/** Engraving grades by the profile list's numbers (1 rare … 4 relic): the words tooltips use, and the colours. */
	static var GRADE_WORDS:Array<String> = ["", "Rare", "Heroic", "Legendary", "Relic"];
	static var GRADE_COLORS:Array<Int> = [TEXT, 0x00B0FA, 0xCE43FC, 0xF99200, 0xFA5D00];
	/** Ark Grid core grades (Combat Power's GRADE_NAMES: epic, legendary, relic, ancient) in the game's grade colours. */
	static var CORE_COLORS:Array<Int> = [0xCE43FC, 0xF99200, 0xFA5D00, 0xDCC999];

	/** Your own profile window, and what it last showed. */
	static var mineWindow:Dynamic;
	static var frames = 0;
	/** Your slots' data and the items and gems read from them, by slot; your Ark Grid; your accessories' refinements. */
	static var myGear:Array<Dynamic>;
	static var myItems:Array<Dynamic>;
	static var myGems:Array<Dynamic>;
	static var myGemData:Array<Dynamic>;
	static var myGrid:Dynamic;
	static var myGridData:Array<Dynamic>;
	static var myRefines:Array<Dynamic>;
	static var myEngravings:Array<Dynamic>;
	static var myEngraveData:Array<Dynamic>;
	static var myPassive:Array<Float>;
	static var myCombat:Dynamic;
	static var myName:String = "";
	static var myClass:String = "";
	static var myLevel:String = "";
	static var myPower:String = "";

	static var inspect:Dynamic;
	static var window:Dynamic;
	static var tabs:Dynamic;
	static var tab:Dynamic;
	static var showing = false;
	static var shownName:String = "";
	static var shownSignature:String = "";
	static var listening = false;
	static var originals:Dictionary = new Dictionary(true);
	/** The slot whose tooltip a picture is showing. */
	static var hovered:Dynamic;

	/**
		Test steps (option "steps"): each click on the tab builds one more stage of the page, so the click that froze the
		game names the stage. 1 the empty page, 2 names, labels and lists, 3 items, gems, engravings and Ark Grid read
		through their tooltips, with prices, 4 the slot art and title rules, 5 item icons and hover tooltips (the whole page).
	**/
	static inline var STEPS = 5;
	static var STEP_NAMES:Array<String> = ["the empty page", "names, labels and lists",
		"reading items, gems, engravings and Ark Grid, with prices", "slot art and title rules", "item icons and tooltips"];
	static var step = 0;
	static var level = STEPS;

	static var page:Sprite;
	static var content:Sprite;
	/** Under the page's slots and text: title rules and row bands. */
	static var under:Sprite;
	static var font:String = "$YG760";
	static var bigFont:String = "$YoonGasiIIM";
	static var embed = false;

	/** CharInfoWndContent.configUI: your profile is watched for your side; the inspect window gets the Compare tab. */
	public static function onWndConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (!listening) {
			listening = true;
			Options.listen(onOptions);
		}
		if (mine(self)) {
			mineWindow = self;
			self.addEventListener(Event.ENTER_FRAME, watchMine);
			return;
		}
		var group:Dynamic = self.tabGroupManager;
		if (group == null || self.getChildByName(TAB_NAME) != null)
			return;
		var last:Dynamic = group.getButtonAt(GAME_TABS - 1);
		if (last == null)
			return;
		inspect = self;
		var button:Dynamic = GameUi.copy(last, "Compare");
		button.name = TAB_NAME;
		button.autoSize = "none";
		button.width = last.width;
		button.height = last.height;
		button.x = group.x + last.x + last.width;
		button.y = group.y + last.y;
		GameUi.addAbove(button, group);
		Options.own(TWEAK, button);
		button.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) show(self, group, button));
		group.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) closeFor(button, true));
		group.addEventListener("clikIndexChange", function(event:Dynamic) closeFor(button, true));
		// The tabs are laid out again whenever the game lays its own tabs out afresh.
		button.addEventListener(Event.ENTER_FRAME, function(event:Dynamic) keepTabs(self));
		layout(self);
	}

	static function mine(self:Dynamic):Bool {
		return self.parent != null && self.parent.name == "charInfoWnd";
	}

	static function onOptions(tweak:String, key:String):Void {
		if (inspect != null)
			layout(inspect);
		if (showing && !Options.on(TWEAK))
			closeFor(tab, true);
	}

	// ---- The tab row ----

	static function keepTabs(self:Dynamic):Void {
		frames++;
		if (frames % 15 == 0 && Options.on(TWEAK))
			layout(self);
	}

	/**
		Lays the tab row out: the game's tabs keep their places and the Compare tab follows.
	**/
	static function layout(self:Dynamic):Void {
		var group:Dynamic = self.tabGroupManager;
		var own:Dynamic = self.getChildByName(TAB_NAME);
		if (group == null || own == null)
			return;
		var game:Array<Dynamic> = [];
		for (i in 0...GAME_TABS) {
			var button:Dynamic = group.getButtonAt(i);
			if (button != null)
				game.push(button);
		}
		if (game.length == 0)
			return;
		var extras:Array<Dynamic> = [];
		if (Options.on(TWEAK))
			extras.push(own);
		var first:Dynamic = original(game[0]);
		var last:Dynamic = original(game[game.length - 1]);
		var lastX:Float = last.x;
		var step:Float = last.width;
		var firstX:Float = first.x;
		var even = game.length + extras.length > game.length + 1;
		var width:Float = even ? (lastX + 2 * step - firstX) / (game.length + extras.length) : step;
		var x:Float = firstX;
		for (button in game) {
			var kept:Dynamic = original(button);
			var keptX:Float = kept.x;
			var keptWidth:Float = kept.width;
			place(button, even ? x : keptX, even ? width : keptWidth);
			x = even ? x + width : keptX + keptWidth;
		}
		var top:Float = group.y + game[0].y;
		for (extra in extras) {
			place(extra, group.x + x, width);
			if (extra.y != top)
				extra.y = top;
			x += width;
		}
	}

	/** Where the game placed one of its tabs, before this tweak moved it. */
	static function original(button:Dynamic):Dynamic {
		var kept:Dynamic = untyped originals[button];
		if (kept == null) {
			kept = {x: button.x, width: button.width};
			untyped originals[button] = kept;
		}
		return kept;
	}

	static function place(button:Dynamic, x:Float, width:Float):Void {
		var at:Float = button.x;
		var wide:Float = button.width;
		if (Math.abs(at - x) > 0.5)
			button.x = x;
		if (Math.abs(wide - width) > 0.5) {
			button.autoSize = "none";
			button.width = width;
		}
	}

	// ---- Showing and closing ----

	static function show(self:Dynamic, group:Dynamic, button:Dynamic):Void {
		var current:Dynamic = group.getButtonAt(group.selectedIndex);
		if (current != null)
			current.selected = false;
		button.selected = true;
		window = self;
		tabs = group;
		tab = button;
		showing = true;
		if (Options.get(TWEAK, "steps", false) == true) {
			if (step < STEPS)
				step++;
			level = step;
		} else {
			level = STEPS;
		}
		pages(self, false);
		build(self);
		page.visible = true;
		safeDraw();
	}

	/** Closes the page; `restore` shows the window's own tab again. */
	static function closeFor(button:Dynamic, restore:Bool):Void {
		if (button == null)
			return;
		button.selected = false;
		if (!showing || tab != button)
			return;
		showing = false;
		hideTooltip();
		if (page != null)
			page.visible = false;
		if (!restore)
			return;
		pages(window, true);
		var current:Dynamic = tabs.getButtonAt(tabs.selectedIndex);
		if (current != null)
			current.selected = true;
	}

	/** Hides the tabs' pages, or shows the one the tab group has selected again (Gear also shows the character). */
	static function pages(self:Dynamic, restore:Bool):Void {
		var index:Int = restore ? tabs.selectedIndex : -1;
		visible(self.texturePanel, false);
		visible(self.otherTexturePanel, index == 0);
		visible(self.equipPanel, index == 0);
		visible(self.engravePanel, index == 1);
		visible(self.gemPanel, index == 2);
		visible(self.cardPanel, index == 3);
		visible(self.arkGridPanel, index == 4);
	}

	static function visible(item:Dynamic, on:Bool):Void {
		if (item != null && item.visible != on)
			item.visible = on;
	}

	/** While the page shows: a new inspect or the window closing closes it; data arriving late redraws it. */
	static function watch(event:Dynamic):Void {
		if (!showing || window == null)
			return;
		var shown:Bool = window.parent != null && window.parent.visible && window.visible;
		var name = labelText(nameLabel(window));
		if (!shown || (name.length > 0 && name != shownName)) {
			closeFor(tab, true);
			return;
		}
		frames++;
		if (frames % 20 == 0 && signature(window) != shownSignature)
			safeDraw();
	}

	/** What decides the page's content: the name, Combat Power and which slots are filled. */
	static function signature(self:Dynamic):String {
		var parts:Array<String> = [labelText(nameLabel(self)), labelText(powerLabel(self))];
		for (data in slotData(gearSlots(self), SLOT_COUNT))
			parts.push(data == null ? "-" : "+");
		for (data in slotData(gemSlots(self), GEM_COUNT))
			parts.push(data == null ? "-" : "+");
		return parts.join("");
	}

	// ---- Your side ----

	/**
		Your profile, while it shows: keeps its slots, lists and labels for the Compare page (twice a second or so). Items,
		gems and Ark Grid cores are read through tooltips only when their slots' data changed.
	**/
	static function watchMine(event:Dynamic):Void {
		frames++;
		if (frames % 20 != 0 || !Options.on(TWEAK))
			return;
		var self:Dynamic = mineWindow;
		if (self == null || self.parent == null || !self.parent.visible || !self.visible)
			return;
		var gear = slotData(gearSlots(self), SLOT_COUNT);
		if (filled(gear) && changed(gear, myGear)) {
			myGear = gear;
			myItems = readItems(self);
		}
		var gemData = slotData(gemSlots(self), GEM_COUNT);
		if (filled(gemData) && changed(gemData, myGemData)) {
			myGemData = gemData;
			myGems = readGems(self);
		}
		var gridData = gridRows(self);
		if (gridData.length > 0 && changed(gridData, myGridData)) {
			myGridData = gridData;
			myGrid = readGrid(self);
		}
		var engraveData = engraveRows(self);
		if (engraveData.length > 0 && changed(engraveData, myEngraveData)) {
			myEngraveData = engraveData;
			myEngravings = readEngravings(self);
		}
		var refines = readRefines(self);
		if (refines.length > 0)
			myRefines = refines;
		var passive = readPassive(self);
		if (passive != null)
			myPassive = passive;
		var combat = readCombat(self);
		if (combat != null)
			myCombat = combat;
		var name = labelText(nameLabel(self));
		if (name.length > 0) {
			myName = name;
			myClass = labelText(classLabel(self));
			myLevel = labelText(levelLabel(self));
		}
		var power = powerLabel(self);
		if (power != null && power.visible)
			myPower = labelText(power);
	}

	static function changed(now:Array<Dynamic>, before:Array<Dynamic>):Bool {
		if (before == null || before.length != now.length)
			return true;
		for (i in 0...now.length)
			if (now[i] != before[i])
				return true;
		return false;
	}

	static function filled(list:Array<Dynamic>):Bool {
		for (item in list)
			if (item != null)
				return true;
		return false;
	}

	// ---- Reading a profile window ----

	static function gearSlots(self:Dynamic):Dynamic {
		return self == null || self.equipPanel == null || self.equipPanel.itemPage == null ? null
			: self.equipPanel.itemPage.equipItemSlotManager;
	}

	static function gemSlots(self:Dynamic):Dynamic {
		return self == null || self.gemPanel == null ? null : self.gemPanel.gemSlotManager;
	}

	/** Each slot's data (null where empty), as the game gave it to the slot. */
	static function slotData(manager:Dynamic, count:Int):Array<Dynamic> {
		var found:Array<Dynamic> = [];
		for (i in 0...count) {
			var slot:Dynamic = manager == null ? null : manager.getSlotAtIndex(i);
			found.push(slot == null ? null : slot.data);
		}
		return found;
	}

	static function nameLabel(self:Dynamic):Dynamic {
		return self == null || self.equipPanel == null || self.equipPanel.infoPage == null ? null
			: self.equipPanel.infoPage.equipInfoCharacterNameLabel;
	}

	static function classLabel(self:Dynamic):Dynamic {
		return self == null || self.equipPanel == null || self.equipPanel.infoPage == null ? null
			: self.equipPanel.infoPage.equipInfoClassNameLabel;
	}

	static function abilityPage(self:Dynamic):Dynamic {
		return self == null || self.equipPanel == null ? null : self.equipPanel.abilityPage;
	}

	static function levelLabel(self:Dynamic):Dynamic {
		var page:Dynamic = abilityPage(self);
		return page == null ? null : page.abilityItemLevelLabel;
	}

	static function powerLabel(self:Dynamic):Dynamic {
		var page:Dynamic = abilityPage(self);
		return page == null ? null : page.abilityAchievementLabel;
	}

	/** 0 DPS, 1 support, -1 while the figure loads (the badge behind it shows frame 1 or 2). */
	static function roleOf(self:Dynamic):Int {
		var page:Dynamic = abilityPage(self);
		if (page == null || page.combatBgMc == null || page.abilityAchievementLabel == null || !page.abilityAchievementLabel.visible)
			return -1;
		var frame:Int = page.combatBgMc.currentFrame;
		return frame == 2 ? 1 : 0;
	}

	/**
		Engravings from the Engravings tab's list: {name, grade (the list's 1 rare … 4 relic), level (0-4)}. The grade
		and level come from each engraving's own tooltip ("Relic Lv. 4 Engraving"), read through its icon data as Combat
		Power reads Ark Grid slots; the list's own numbers only when the tooltip doesn't say (an inspected player's list
		gave levels 0 and 1 for maxed engravings).
	**/
	static function readEngravings(self:Dynamic):Array<Dynamic> {
		var list:Dynamic = self == null || self.engravePanel == null || self.engravePanel.engraveEquipList == null ? null
			: self.engravePanel.engraveEquipList.dataProvider;
		var found:Array<Dynamic> = [];
		if (list == null)
			return found;
		var count:Int = list.length;
		for (i in 0...count) {
			var row:Dynamic = list[i];
			if (row == null || row.label == null)
				continue;
			var name = Text.trim(Text.stripTags(row.label));
			if (name == "")
				continue;
			var grade:Int = row.engraveGrade == null ? 0 : row.engraveGrade;
			var at:Int = row.curActivity == null ? 0 : row.curActivity;
			var shown = row.slotData == null ? null : gradeAndLevel(Tooltips.boxes(row.slotData, row.slotData));
			if (shown != null) {
				grade = shown.grade;
				at = shown.level;
			}
			found.push({name: name, grade: grade, level: at});
		}
		return found;
	}

	/** The Engravings tab's list rows, as its list holds them. */
	static function engraveRows(self:Dynamic):Array<Dynamic> {
		var list:Dynamic = self == null || self.engravePanel == null || self.engravePanel.engraveEquipList == null ? null
			: self.engravePanel.engraveEquipList.dataProvider;
		var found:Array<Dynamic> = [];
		if (list == null)
			return found;
		var count:Int = list.length;
		for (i in 0...count)
			found.push(list[i]);
		return found;
	}

	/** The grade (list numbering) and level an engraving tooltip names ("Relic Lv. 4 Engraving"), or null. */
	static function gradeAndLevel(boxes:Array<Dynamic>):Dynamic {
		if (boxes == null)
			return null;
		for (text in CombatPower.strings(boxes, [], 0)) {
			var plain = Text.stripTags(text);
			for (i in 1...GRADE_WORDS.length) {
				var at = plain.indexOf(GRADE_WORDS[i] + " Lv.");
				if (at < 0)
					continue;
				var shown = Tooltips.leadingInt(plain.substr(at + GRADE_WORDS[i].length + 4), -1);
				if (shown >= 0)
					return {grade: i, level: shown};
			}
		}
		return null;
	}

	/** The accessories' refinement grades from the gear page (0 low, 1 middle, 2 high; -1 none), one list per accessory. */
	static function readRefines(self:Dynamic):Array<Dynamic> {
		var list:Dynamic = self == null || self.equipPanel == null || self.equipPanel.itemPage == null
			|| self.equipPanel.itemPage.equipItemACCInfoList == null ? null
			: self.equipPanel.itemPage.equipItemACCInfoList.dataProvider;
		var found:Array<Dynamic> = [];
		if (list == null)
			return found;
		var count:Int = list.length;
		for (i in 0...count) {
			var row:Dynamic = list[i];
			var grades:Array<Int> = [];
			for (key in ["gradeValue0", "gradeValue1", "gradeValue2"]) {
				var value:Int = row != null && Text.has(row, key) ? untyped row[key] : -1;
				grades.push(value);
			}
			found.push(grades);
		}
		return found;
	}

	/** Evolution, Enlightenment and Leap points from the gear page's Ark Passive list, or null. */
	static function readPassive(self:Dynamic):Array<Float> {
		var list:Dynamic = self == null || self.equipPanel == null || self.equipPanel.infoPage == null
			|| self.equipPanel.infoPage.arkPassiveGroup == null ? null
			: self.equipPanel.infoPage.arkPassiveGroup.equipInfoArkPassiveList.dataProvider;
		if (list == null || list.length < 3)
			return null;
		var points:Array<Float> = [];
		for (i in 0...3)
			points.push(CombatPower.number(Text.stripTags(list[i].pointStr)));
		return points;
	}

	/** Crit, Specialization and Swiftness from the gear page's combat stat list ({Crit: 75, …}), or null. */
	static function readCombat(self:Dynamic):Dynamic {
		var list:Dynamic = CombatPower.combatList(self);
		if (list == null)
			return null;
		var found:Dynamic = {};
		var seen = 0;
		var count:Int = list.length;
		for (i in 0...count) {
			var row:Dynamic = list[i];
			if (row == null || row.titleStr == null || row.valueStr == null)
				continue;
			var title = Text.trim(Text.stripTags(row.titleStr));
			var shown:String = untyped __global__["String"](row.valueStr);
			if (CombatPower.COMBAT_LABELS.indexOf(title) >= 0) {
				untyped found[title] = CombatPower.number(Text.stripTags(shown));
				seen++;
			}
		}
		return seen == CombatPower.COMBAT_LABELS.length ? found : null;
	}

	/**
		A window's equipment by slot, each read through its own slot's tooltip (Combat Power's reading), null where empty;
		with the slot (for its tooltip), its data (for its picture), the quality shown and the advanced honing level.
	**/
	static function readItems(self:Dynamic):Array<Dynamic> {
		var manager:Dynamic = gearSlots(self);
		var items:Array<Dynamic> = [];
		for (i in 0...SLOT_COUNT) {
			var slot:Dynamic = manager == null ? null : manager.getSlotAtIndex(i);
			var boxes = slot == null || slot.data == null ? null : Tooltips.boxes(slot.binding, slot);
			var item:Dynamic = boxes == null ? null : CombatPower.itemOf(boxes);
			if (item != null) {
				item.slot = CombatPower.SLOTS[i];
				item.shownQuality = CombatPower.qualityOf(boxes);
				if (i == 0)
					item.quality = item.shownQuality;
				item.data = slot.data;
				item.source = slot;
				item.advanced = advancedHoning(boxes);
			}
			items.push(item);
		}
		return items;
	}

	/** The advanced honing level an item tooltip names ("Advanced Honing … 20"), or 0. */
	static function advancedHoning(boxes:Array<Dynamic>):Int {
		for (text in CombatPower.strings(boxes, [], 0)) {
			var plain = Text.stripTags(text);
			var at = plain.indexOf("Advanced Honing");
			if (at < 0)
				continue;
			var shown = CombatPower.number(plain.substr(at + 15));
			if (shown > 0 && shown <= 40)
				return Math.floor(shown);
		}
		return 0;
	}

	/** A window's equipped gems ({tier, level, kind, base, name, data, source}), read through its gem slots' tooltips. */
	static function readGems(self:Dynamic):Array<Dynamic> {
		var manager:Dynamic = gemSlots(self);
		var gems:Array<Dynamic> = [];
		for (i in 0...GEM_COUNT) {
			var slot:Dynamic = manager == null ? null : manager.getSlotAtIndex(i);
			var boxes = slot == null || slot.data == null ? null : Tooltips.boxes(slot.binding, slot);
			var gem:Dynamic = boxes == null ? null : CombatPower.gemOf(boxes);
			if (gem != null) {
				gem.data = slot.data;
				gem.source = slot;
				gems.push(gem);
			}
		}
		return gems;
	}

	/** The Ark Grid tab's rows (one per core), as its list holds them. */
	static function gridRows(self:Dynamic):Array<Dynamic> {
		var grid:Dynamic = self == null ? null : self.arkGridPanel;
		var rows:Dynamic = grid == null || grid.arkGridList == null ? null : grid.arkGridList.dataProvider;
		var found:Array<Dynamic> = [];
		if (rows == null)
			return found;
		var count:Int = rows.length;
		for (i in 0...count)
			found.push(rows[i]);
		return found;
	}

	/**
		A window's Ark Grid as Combat Power reads yours: each core ({family, name, grade, points}, points summed from its
		astrogems) and the grid's astrogem option levels ({"Atk. Power": 12, …}); null when the tab's lists are empty.
	**/
	static function readGrid(self:Dynamic):Dynamic {
		var cores:Array<Dynamic> = [];
		for (row in gridRows(self)) {
			if (row == null || row.slotData == null)
				continue;
			var boxes = Tooltips.boxes(row.slotData, row.slotData);
			var core:Dynamic = boxes == null ? null : CombatPower.gridItemOf(boxes);
			if (core == null || core.kind != "core")
				continue;
			var points:Float = 0;
			var gems:Array<Dynamic> = row.gemSlotData == null ? [] : row.gemSlotData;
			for (data in gems) {
				if (data == null)
					continue;
				var gemBoxes = Tooltips.boxes(data, data);
				var gem:Dynamic = gemBoxes == null ? null : CombatPower.gridItemOf(gemBoxes);
				if (gem != null) {
					var gemPoints:Float = gem.points;
					points += gemPoints;
				}
			}
			core.points = points;
			cores.push(core);
		}
		var grid:Dynamic = self == null ? null : self.arkGridPanel;
		var info:Dynamic = grid == null || grid.imageView == null ? null : grid.imageView.preResource;
		var list:Dynamic = info == null || info.arkGridAbilityList == null ? null : info.arkGridAbilityList.dataProvider;
		var levels:Dynamic = {};
		if (list != null) {
			var count:Int = list.length;
			for (i in 0...count) {
				var row:Dynamic = list[i];
				if (row != null && row.label != null)
					CombatPower.addLevel(levels, Text.trim(Text.stripTags(row.label)));
			}
		}
		if (cores.length == 0 && (list == null || list.length == 0))
			return null;
		return {cores: cores, levels: levels};
	}

	// ---- The page ----

	static function build(self:Dynamic):Void {
		if (page != null && page.parent == self)
			return;
		if (page != null && page.parent != null) {
			page.removeEventListener(Event.ENTER_FRAME, watch);
			page.parent.removeChild(page);
		}
		gameFont(self);
		page = new Sprite();
		var panel:Dynamic = self.equipPanel;
		page.x = 0;
		page.y = panel == null ? 76 : panel.y;
		// Nearly clear: it keeps clicks off the hidden pages underneath.
		var g = page.graphics;
		g.beginFill(0x000000, 0.01);
		g.drawRect(COLUMNS[0], 0, PAGE_WIDTH - COLUMNS[0], PAGE_HEIGHT);
		g.endFill();
		content = new Sprite();
		page.addChild(content);
		page.addEventListener(Event.ENTER_FRAME, watch);
		// Under the tab row, so the tabs (and the window's pop-ups) stay on top.
		var group:Dynamic = self.tabGroupManager;
		self.addChildAt(page, group == null ? self.numChildren : self.getChildIndex(group));
	}

	/** The fonts of the window's own name label and Combat Power figure. */
	static function gameFont(self:Dynamic):Void {
		var field:Dynamic = fieldOf(nameLabel(self));
		if (field != null) {
			var format:Dynamic = field.getTextFormat();
			if (format != null && format.font != null) {
				font = format.font;
				embed = field.embedFonts;
			}
		}
		var power:Dynamic = fieldOf(powerLabel(self));
		if (power != null) {
			var format:Dynamic = power.getTextFormat();
			if (format != null && format.font != null)
				bigFont = format.font;
		}
	}

	static function fieldOf(label:Dynamic):Dynamic {
		return label == null ? null : label.textField;
	}

	/** Draws the page; an error leaves whatever was drawn (the page's listeners aren't inside the hooks' guard). */
	static function safeDraw():Void {
		try {
			draw();
		} catch (error:Dynamic) {}
	}

	/**
		Draws the page afresh: you on the left (what your profile last showed), them on the right (the inspect window now),
		and between them how your part compares with theirs in Combat Power.
	**/
	static function draw():Void {
		hideTooltip();
		while (content.numChildren > 0)
			content.removeChildAt(0);
		under = new Sprite();
		content.addChild(under);
		shownName = labelText(nameLabel(window));
		shownSignature = signature(window);
		if (level < STEPS)
			stepNote();
		if (level < 2)
			return;
		var ready = level >= 3 && CombatPower.gear != null && CombatPower.equipped != null && CombatPower.role >= 0
			&& CombatPower.table() != null;
		var dps = ready && CombatPower.role == 0;
		var theirName = labelText(nameLabel(window));
		var you = myName.length > 0 ? myName : "You";
		var sameClass = myClass.length > 0 && myClass == labelText(classLabel(window));

		var theirs:Array<Dynamic> = level >= 3 ? readItems(window) : [];
		while (theirs.length < SLOT_COUNT)
			theirs.push(null);
		var mine:Array<Dynamic> = myItems == null || level < 3 ? [] : CombatPower.copyOf(myItems);
		while (mine.length < SLOT_COUNT)
			mine.push(null);
		var theirGems:Array<Dynamic> = level >= 3 ? readGems(window) : [];
		var mineGems:Array<Dynamic> = myGems == null || level < 3 ? [] : CombatPower.copyOf(myGems);
		var theirRefines = readRefines(window);
		var mineRefines:Array<Dynamic> = myRefines == null ? [] : myRefines;
		var theirGrid:Dynamic = level >= 3 ? readGrid(window) : null;
		var mineGrid:Dynamic = level >= 3 ? myGrid : null;

		header(you, theirName);
		var model:Dynamic = slotModel();
		gearColumn(COLUMNS[0], "Equipment", LEFT_SLOTS, you, theirName, mine, theirs, [], [], model, dps);
		gearColumn(COLUMNS[1], "Accessories", RIGHT_SLOTS, you, theirName, mine, theirs, mineRefines, theirRefines, model, dps);
		gemStrip(model, you, theirName, mineGems, theirGems, ready);
		var y = engravingRows(you, theirName, mine, theirs, level >= 3 ? readEngravings(window) : [], ready, sameClass);
		y = statRows(y, readPassive(window), readCombat(window), dps);
		gridSection(y, mineGrid, theirGrid, ready);
	}

	/** The test step's line at the bottom of the page: what this click built and what the next one adds. */
	static function stepNote():Void {
		var shown:String = untyped __global__["String"](level);
		var next:String = untyped __global__["String"](level + 1);
		var steps:String = untyped __global__["String"](STEPS);
		var done:String = STEP_NAMES[level - 1];
		var coming:String = STEP_NAMES[level];
		var line = "Compare test, step " + shown + " of " + steps + " (" + done + ") worked. Click Compare again for step "
			+ next + " (" + coming + ").";
		content.addChild(text(COLUMNS[0], PAGE_HEIGHT - 20, COLUMNS[2] + COLUMN_WIDTH - COLUMNS[0], 13, 0xFFC861, line, "center"));
	}

	// ---- Header ----

	/** Your name and figures on the left, theirs on the right, and the gap between the game's own Combat Power figures. */
	static function header(you:String, theirName:String):Void {
		var left = COLUMNS[0];
		var middle = COLUMNS[1];
		var right = COLUMNS[2];
		content.addChild(text(left, 2, COLUMN_WIDTH, 18, NAME, you, "left"));
		content.addChild(text(left, 28, COLUMN_WIDTH, 12, DIM, details(myClass, myLevel, myPower), "left"));
		content.addChild(text(right, 2, COLUMN_WIDTH, 18, NAME, theirName, "right"));
		var theirPower = roleOf(window) < 0 ? "" : labelText(powerLabel(window));
		content.addChild(text(right, 28, COLUMN_WIDTH, 12, DIM, details(labelText(classLabel(window)),
			labelText(levelLabel(window)), theirPower), "right"));
		var mineValue = CombatPower.power > 0 ? CombatPower.power : CombatPower.number(myPower);
		var theirValue = CombatPower.number(theirPower);
		if (mineValue <= 0 || theirValue <= 0) {
			content.addChild(text(middle, 0, COLUMN_WIDTH, 22, DIM, "—", "center", bigFont));
			content.addChild(text(middle - 20, 30, COLUMN_WIDTH + 40, 12, DIM,
				mineValue <= 0 ? "Open your Character Profile once to compare" : "Waiting for their Combat Power", "center"));
			return;
		}
		var gap = mineValue - theirValue;
		var change = mineValue / theirValue - 1;
		var shown = CombatPower.signed(gap, "") + "  (" + percent(change) + ")";
		content.addChild(text(middle, 0, COLUMN_WIDTH, 22, colorOf(change), shown, "center", bigFont));
		content.addChild(text(middle - 20, 30, COLUMN_WIDTH + 40, 12, DIM, "your Combat Power against theirs", "center"));
	}

	static function details(className:String, level:String, power:String):String {
		var parts:Array<String> = [];
		if (className.length > 0)
			parts.push(className);
		if (level.length > 0)
			parts.push("Item Lv. " + level);
		if (power.length > 0)
			parts.push("Combat Power " + power);
		return parts.join(" · ");
	}

	// ---- Equipment columns ----

	static function gearColumn(x:Int, heading:String, slots:Array<Int>, you:String, theirName:String, mine:Array<Dynamic>,
			theirs:Array<Dynamic>, mineRefines:Array<Dynamic>, theirRefines:Array<Dynamic>, model:Dynamic, dps:Bool):Void {
		var y = sectionTitle(x, TOP, COLUMN_WIDTH, heading, you, theirName);
		var shade = false;
		for (index in slots) {
			if (shade)
				band(x, y, COLUMN_WIDTH, ROW);
			shade = !shade;
			var mineItem:Dynamic = mine[index];
			var theirItem:Dynamic = theirs[index];
			var accessory = index >= FIRST_ACCESSORY && index < FIRST_ACCESSORY + ACCESSORIES;
			var inset = accessory ? 16 : 6;
			itemPicture(model, mineItem, x, y + 2, SLOT);
			itemPicture(model, theirItem, x + COLUMN_WIDTH - SLOT, y + 2, SLOT);
			if (accessory) {
				refineMarks(refinesAt(mineRefines, index), x + SLOT + 3, y);
				refineMarks(refinesAt(theirRefines, index), x + COLUMN_WIDTH - SLOT - 13, y);
			}
			itemLabel(mineItem, x + SLOT + inset, y, "left");
			itemLabel(theirItem, x + COLUMN_WIDTH - SLOT - inset - LABEL_WIDTH, y, "right");
			var change:Float = dps && (theirItem != null || CombatPower.gear[index] != null) ? versus(index, theirItem) : nothing();
			var same = theirItem != null && mineItem != null && sameItem(theirItem, mineItem);
			delta(x + SLOT + LABEL_WIDTH + 6, y, COLUMN_WIDTH - 2 * (SLOT + LABEL_WIDTH + 6), same ? 0 : change, same ? "same" : null);
			y += ROW;
		}
	}

	/** Your Combat Power with your item in this slot against with theirs in its place, as a change (+ when yours is better). */
	static function versus(index:Int, item:Dynamic):Float {
		var withTheirs = CombatPower.copyOf(CombatPower.gear);
		withTheirs[index] = item;
		return CombatPower.value(CombatPower.gear, CombatPower.equipped) / CombatPower.value(withTheirs, CombatPower.equipped) - 1;
	}

	static function sameItem(a:Dynamic, b:Dynamic):Bool {
		var first:String = a.text;
		var second:String = b.text;
		return first == second;
	}

	static function refinesAt(list:Array<Dynamic>, index:Int):Array<Int> {
		var at = index - FIRST_ACCESSORY;
		return at >= 0 && at < list.length ? list[at] : null;
	}

	/**
		An accessory's refinement grades as the gear page marks them, top to bottom: a blue star (low), a purple diamond
		(middle) or a gold diamond (high). Drawn, not the profile's own icons.
	**/
	static function refineMarks(grades:Array<Int>, x:Float, y:Float):Void {
		if (grades == null)
			return;
		var shown = 0;
		for (grade in grades)
			if (grade >= 0)
				shown++;
		var top = y + (ROW - shown * 12) / 2;
		for (grade in grades) {
			if (grade < 0)
				continue;
			var mark = new Shape();
			var g = mark.graphics;
			var color = grade >= 2 ? 0xF5A623 : (grade == 1 ? 0xB04BEF : 0x3FA0FF);
			var cx = x + 5;
			var cy = top + 6;
			if (grade == 0) {
				// A four-pointed star.
				g.beginFill(color, 1);
				g.moveTo(cx, cy - 5);
				g.lineTo(cx + 1.3, cy - 1.3);
				g.lineTo(cx + 5, cy);
				g.lineTo(cx + 1.3, cy + 1.3);
				g.lineTo(cx, cy + 5);
				g.lineTo(cx - 1.3, cy + 1.3);
				g.lineTo(cx - 5, cy);
				g.lineTo(cx - 1.3, cy - 1.3);
				g.lineTo(cx, cy - 5);
				g.endFill();
			} else {
				g.beginFill(color, 1);
				g.moveTo(cx, cy - 5);
				g.lineTo(cx + 5, cy);
				g.lineTo(cx, cy + 5);
				g.lineTo(cx - 5, cy);
				g.lineTo(cx, cy - 5);
				g.endFill();
				g.beginFill(0xFFFFFF, 0.45);
				g.moveTo(cx, cy - 2);
				g.lineTo(cx + 2, cy);
				g.lineTo(cx, cy + 2);
				g.lineTo(cx - 2, cy);
				g.lineTo(cx, cy - 2);
				g.endFill();
			}
			content.addChild(mark);
			top += 12;
		}
	}

	/**
		Two short lines beside a slot: the honing level from the name ("+18"), or a paradise orb's paradise power; then the
		quality in the game's colours and the advanced honing level ("Q 70  Adv 20"), mirrored on their side.
	**/
	static function itemLabel(item:Dynamic, x:Float, y:Float, align:String):Void {
		if (item == null)
			return;
		var name:String = item.name;
		var slot:String = item.slot;
		var top = "";
		if (StringTools.startsWith(name, "+")) {
			var honing = Tooltips.leadingInt(name.substr(1), -1);
			if (honing >= 0) {
				var shown:String = untyped __global__["String"](honing);
				top = "+" + shown;
			}
		}
		if (slot == "orb") {
			var power:Float = item.paradise;
			if (power > 0)
				top = "PP " + CombatPower.money(power / 1000000) + "M";
		}
		content.addChild(text(x, y + 2, LABEL_WIDTH, 13, TEXT, top, align));
		var quality:Int = item.shownQuality;
		var used:Float = 0;
		if (quality >= 0) {
			var shown:String = untyped __global__["String"](quality);
			var field = text(x, y + 20, LABEL_WIDTH, 12, qualityColor(quality), "Q " + shown, align);
			content.addChild(field);
			used = field.textWidth + 8;
		}
		var advanced:Int = item.advanced;
		if (advanced > 0) {
			var shown:String = untyped __global__["String"](advanced);
			var width = LABEL_WIDTH - used;
			content.addChild(text(align == "left" ? x + used : x, y + 20, width, 12, ADVANCED, "Adv " + shown, align));
		}
	}

	/** The game's quality colours: 100 orange, 90+ purple, 70+ blue, 30+ green, 10+ yellow, else red. */
	static function qualityColor(quality:Int):Int {
		if (quality >= 100)
			return 0xFE9600;
		if (quality >= 90)
			return 0xCE43FC;
		if (quality >= 70)
			return 0x00B5FF;
		if (quality >= 30)
			return 0x91FE02;
		if (quality >= 10)
			return 0xFFD200;
		return 0xFF4040;
	}

	// ---- Gems ----

	static function gemStrip(model:Dynamic, you:String, theirName:String, mineGems:Array<Dynamic>, theirGems:Array<Dynamic>,
			ready:Bool):Void {
		var left = COLUMNS[0];
		var y = sectionTitle(left, GEMS_TOP, COLUMNS[1] + COLUMN_WIDTH - left, "Gems", "", "");
		mineGems.sort(gemOrder);
		theirGems.sort(gemOrder);
		gemRow(model, left, y, you, mineGems);
		gemRow(model, left, y + GEM + 12, theirName, theirGems);
		var change:Float = nothing();
		if (ready && theirGems.length > 0)
			change = CombatPower.value(CombatPower.gear, CombatPower.equipped) / CombatPower.value(CombatPower.gear, theirGems) - 1;
		var x = left + GEM_LABEL + GEM_COUNT * GEM_STEP;
		delta(x, y + 14, COLUMNS[1] + COLUMN_WIDTH - x, change, theirGems.length == 0 ? "open their Gems tab" : null);
	}

	static function gemRow(model:Dynamic, x:Float, y:Float, label:String, gems:Array<Dynamic>):Void {
		content.addChild(text(x, y + 9, GEM_LABEL - 2, 12, DIM, label, "left"));
		for (i in 0...gems.length) {
			if (i >= GEM_COUNT)
				break;
			var gem:Dynamic = gems[i];
			var at = x + GEM_LABEL + i * GEM_STEP;
			itemPicture(model, gem, at, y, GEM);
			var level:Int = gem.level;
			var shown:String = untyped __global__["String"](level);
			content.addChild(text(at - 3, y + GEM - 4, GEM + 6, 11, TEXT, "Lv." + shown, "center"));
		}
	}

	/** Damage gems first, then cooldown, each by level (highest first). */
	static function gemOrder(a:Dynamic, b:Dynamic):Int {
		var kindA:String = a.kind;
		var kindB:String = b.kind;
		if (kindA != kindB)
			return kindA == "damage" ? -1 : (kindB == "damage" ? 1 : (kindA < kindB ? -1 : 1));
		var levelA:Int = a.level;
		var levelB:Int = b.level;
		return levelB - levelA;
	}

	// ---- Engravings, Ark Passive and stats, Ark Grid ----

	/**
		Your engravings and theirs, by name: the level in the grade's colour with the stone's levels for it. The total under
		them prices yours against theirs (with each side's stone) for players of the same class; other classes' engravings
		don't compare.
	**/
	static function engravingRows(you:String, theirName:String, mine:Array<Dynamic>, theirs:Array<Dynamic>,
			theirEngravings:Array<Dynamic>, ready:Bool, sameClass:Bool):Float {
		var x = COLUMNS[2];
		var y = sectionTitle(x, TOP, COLUMN_WIDTH, "Engravings", you, theirName);
		var mineList:Array<Dynamic> = myEngravings == null || level < 3 ? [] : myEngravings;
		var names:Array<String> = [];
		for (engraving in mineList)
			names.push(engraving.name);
		for (engraving in theirEngravings) {
			var name:String = engraving.name;
			if (names.indexOf(name) < 0)
				names.push(name);
		}
		var mineStone:Dynamic = mine[CombatPower.STONE_SLOT];
		var theirStone:Dynamic = theirs[CombatPower.STONE_SLOT];
		var shade = false;
		var shownRows = 0;
		for (name in names) {
			if (shownRows >= ENGRAVING_ROWS)
				break;
			shownRows++;
			if (shade)
				band(x, y, COLUMN_WIDTH, TEXT_ROW);
			shade = !shade;
			content.addChild(text(x + 4, y + 1, NAME_WIDTH, 13, TEXT, name, "left"));
			engravingCell(find(mineList, name), mineStone, name, x + CELL_MINE, y);
			engravingCell(find(theirEngravings, name), theirStone, name, x + CELL_THEIRS, y);
			y += TEXT_ROW;
		}
		var known = mineList.length > 0 && theirEngravings.length > 0;
		var change:Float = nothing();
		var word:String = null;
		if (!known)
			word = mineList.length == 0 ? "open your Engravings tab" : "open their Engravings tab";
		else if (!sameClass)
			word = "other class";
		else if (ready)
			change = engravingFactor(mineList, mineStone) / engravingFactor(theirEngravings, theirStone) - 1;
		content.addChild(text(x + 4, y + 1, NAME_WIDTH, 13, DIM, "Total", "left"));
		inlineDelta(x + CELL_THEIRS, y, change, word);
		return y + TEXT_ROW + 6;
	}

	static function find(list:Array<Dynamic>, name:String):Dynamic {
		for (entry in list)
			if (entry.name == name)
				return entry;
		return null;
	}

	/** "Lv. 4" in the grade's colour (as the profile colours grades), with the stone's levels for it ("+2"). */
	static function engravingCell(engraving:Dynamic, stone:Dynamic, name:String, x:Float, y:Float):Void {
		if (engraving == null) {
			content.addChild(text(x, y + 1, CELL_WIDTH, 13, DIM, "—", "center"));
			return;
		}
		var grade:Int = engraving.grade;
		var level:Int = engraving.level;
		var shown:String = untyped __global__["String"](level);
		var stoneLevel = CombatPower.stoneLevel(stone, name);
		var extra:String = untyped __global__["String"](stoneLevel);
		content.addChild(text(x, y + 1, CELL_WIDTH, 13, grade >= 0 && grade < GRADE_COLORS.length ? GRADE_COLORS[grade] : TEXT,
			"Lv. " + shown + (stoneLevel > 0 ? "  +" + extra : ""), "center"));
	}

	/** A side's engravings in the model, with its stone's levels: the product of (1 + value). */
	static function engravingFactor(list:Array<Dynamic>, stone:Dynamic):Float {
		var factor:Float = 1;
		for (engraving in list) {
			var name:String = engraving.name;
			var grade:Int = engraving.grade;
			grade = grade - 1;
			if (grade > 3)
				grade = 3;
			var level:Int = engraving.level;
			factor *= 1 + CombatPower.engravingValue(name, 20 * CombatPower.stoneLevel(stone, name) + CombatPower.index(grade, level)) / 10000;
		}
		return factor;
	}

	/** Ark Passive points and combat stats, yours and theirs, each priced yours against theirs. */
	static function statRows(top:Float, theirPassive:Array<Float>, theirCombat:Dynamic, dps:Bool):Float {
		var x = COLUMNS[2];
		var y = sectionTitle(x, top, COLUMN_WIDTH, "Ark Passive and Stats", "", "");
		var shade = false;
		for (i in 0...PASSIVES.length) {
			var mine:Float = myPassive == null ? -1 : myPassive[i];
			var their:Float = theirPassive == null ? -1 : theirPassive[i];
			var type = i == 1 ? CombatPower.ENLIGHTENMENT : (i == 2 ? CombatPower.LEAP : -1);
			var change:Float = nothing();
			if (dps && type > 0 && their >= 0 && mine >= 0) {
				var each = CombatPower.perPoint(type);
				change = (1 + mine * each) / (1 + their * each) - 1;
			}
			numberRow(x, y, PASSIVES[i], mine, their, change, shade);
			shade = !shade;
			y += TEXT_ROW;
		}
		// The game prices combat stats together, one factor on their sum: each row swaps just that stat into your sum.
		var each = CombatPower.combatPerPoint();
		var priced = dps && CombatPower.otherCombat >= 0;
		var mineTotal = combatSum(myCombat);
		for (label in CombatPower.COMBAT_LABELS) {
			var mine:Float = myCombat != null && Text.has(myCombat, label) ? untyped myCombat[label] : -1;
			var their:Float = theirCombat != null && Text.has(theirCombat, label) ? untyped theirCombat[label] : -1;
			var change:Float = priced && their >= 0 && mine >= 0
				? (1 + mineTotal * each) / (1 + (mineTotal - mine + their) * each) - 1
				: nothing();
			numberRow(x, y, label, mine, their, change, shade);
			shade = !shade;
			y += TEXT_ROW;
		}
		return y + 6;
	}

	/**
		Ark Grid: each core kind (Order Sun, Chaos Moon …) with its grade and points on each side, priced yours against
		theirs with the steps each side's points light; then the astrogems' option levels, priced the same way.
	**/
	static function gridSection(top:Float, mineGrid:Dynamic, theirGrid:Dynamic, ready:Bool):Void {
		var x = COLUMNS[2];
		var y = sectionTitle(x, top, COLUMN_WIDTH, "Ark Grid", "", "");
		if (mineGrid == null || theirGrid == null) {
			content.addChild(text(x + 4, y + 1, COLUMN_WIDTH - 8, 12, DIM, mineGrid == null
				? "Open your profile's Ark Grid tab once to compare" : "Open their Ark Grid tab once to compare", "left"));
			return;
		}
		var mineCores:Array<Dynamic> = mineGrid.cores;
		var theirCores:Array<Dynamic> = theirGrid.cores;
		var families:Array<String> = [];
		for (core in mineCores)
			families.push(core.family);
		for (core in theirCores) {
			var family:String = core.family;
			if (families.indexOf(family) < 0)
				families.push(family);
		}
		var shade = false;
		var shownRows = 0;
		for (family in families) {
			if (shownRows >= CORE_ROWS)
				break;
			shownRows++;
			if (shade)
				band(x, y, COLUMN_WIDTH, TEXT_ROW);
			shade = !shade;
			var mine = findCore(mineCores, family);
			var their = findCore(theirCores, family);
			content.addChild(text(x + 4, y + 1, GRID_NAME, 13, TEXT, family.split(" Core").join(""), "left"));
			coreCell(mine, x + GRID_MINE, y);
			coreCell(their, x + GRID_THEIRS, y);
			var change:Float = nothing();
			if (ready)
				change = coreVersus(mine, their);
			inlineDelta(x + DELTA_X, y, change, null);
			y += TEXT_ROW;
		}
		// Astrogem options by the grid's total level each: the three that count for your role (DPS: Atk. Power,
		// Additional Damage, Boss Damage; support: the ally options), each priced at both totals.
		var mineLevels:Dynamic = mineGrid.levels;
		var theirLevels:Dynamic = theirGrid.levels;
		var first = CombatPower.role == 1 ? 3 : 0;
		for (i in first...first + 3) {
			if (shade)
				band(x, y, COLUMN_WIDTH, TEXT_ROW);
			shade = !shade;
			var option = CombatPower.ASTRO_NAMES[i];
			var mine = optionLevel(mineLevels, option);
			var their = optionLevel(theirLevels, option);
			content.addChild(text(x + 4, y + 1, GRID_NAME, 13, TEXT, option.split("Additional").join("Add."), "left"));
			content.addChild(text(x + GRID_MINE, y + 1, GRID_CELL, 13, TEXT, "Lv. " + whole(mine), "center"));
			content.addChild(text(x + GRID_THEIRS, y + 1, GRID_CELL, 13, TEXT, "Lv. " + whole(their), "center"));
			var id = CombatPower.ASTRO_IDS[i];
			// A support's Combat Power adds Buff and Shield & Heal: the ally options count on the Buff share.
			var share = CombatPower.role == 1 ? CombatPower.buffShare() : 1;
			var change:Float = ready
				? share * ((1 + CombatPower.astroValue(id, Math.floor(mine)) / 10000)
					/ (1 + CombatPower.astroValue(id, Math.floor(their)) / 10000) - 1)
				: nothing();
			inlineDelta(x + DELTA_X, y, change, null);
			y += TEXT_ROW;
		}
	}

	static function optionLevel(levels:Dynamic, option:String):Float {
		var value:Float = Text.has(levels, option) ? untyped levels[option] : 0;
		return value;
	}

	static function findCore(list:Array<Dynamic>, family:String):Dynamic {
		for (core in list)
			if (core.family == family)
				return core;
		return null;
	}

	/** "Relic 17P": the core's grade and its astrogems' points. */
	static function coreCell(core:Dynamic, x:Float, y:Float):Void {
		if (core == null) {
			content.addChild(text(x, y + 1, GRID_CELL, 13, DIM, "—", "center"));
			return;
		}
		var grade:Int = core.grade;
		var at = CombatPower.GRADE_VALUES.indexOf(grade);
		var word = at >= 0 ? CombatPower.GRADE_NAMES[at] : "";
		var points:Float = core.points;
		var shown:String = untyped __global__["String"](Math.floor(points));
		content.addChild(text(x, y + 2, GRID_CELL, 12, at >= 0 ? CORE_COLORS[at] : TEXT, word + " " + shown + "P", "center"));
	}

	/**
		Your core against theirs, each at the steps its own points light, as a change. A support's Combat Power adds its
		Buff and Shield & Heal sides, so each side's core rows count on that side's share (as CombatPower.coreSwap).
	**/
	static function coreVersus(mine:Dynamic, their:Dynamic):Float {
		var keys = CombatPower.role == 1 ? ["2", CombatPower.DEFENCE] : ["1"];
		var share = CombatPower.role == 1 ? CombatPower.buffShare() : 1;
		var change:Float = 0;
		for (key in keys) {
			var after = coreWorth(mine, key);
			var before = coreWorth(their, key);
			change += (key == CombatPower.DEFENCE ? 1 - share : share) * ((1 + after / 10000) / (1 + before / 10000) - 1);
		}
		return change;
	}

	/** A core's value in the model for a role key at the steps its own points light (hundredths of a percent), 0 for none. */
	static function coreWorth(core:Dynamic, key:String):Float {
		if (core == null)
			return 0;
		var name:String = core.name;
		var family:String = core.family;
		var grade:Int = core.grade;
		var points:Float = core.points;
		return CombatPower.coreValueFor(name, family, grade, CombatPower.coreSteps(points), key);
	}

	/** Crit + Specialization + Swiftness, or 0 when unknown. */
	static function combatSum(combat:Dynamic):Float {
		var sum:Float = 0;
		if (combat == null)
			return sum;
		for (label in CombatPower.COMBAT_LABELS) {
			var points:Float = Text.has(combat, label) ? untyped combat[label] : 0;
			if (points > 0)
				sum += points;
		}
		return sum;
	}

	static function numberRow(x:Float, y:Float, label:String, mine:Float, their:Float, change:Float, shade:Bool):Void {
		if (shade)
			band(x, y, COLUMN_WIDTH, TEXT_ROW);
		content.addChild(text(x + 4, y + 1, NAME_WIDTH, 13, TEXT, label, "left"));
		content.addChild(text(x + CELL_MINE, y + 1, CELL_WIDTH, 13, TEXT, mine < 0 ? "—" : whole(mine), "center"));
		content.addChild(text(x + CELL_THEIRS, y + 1, CELL_WIDTH, 13, TEXT, their < 0 ? "—" : whole(their), "center"));
		inlineDelta(x + DELTA_X, y, change, null);
	}

	// ---- Pieces ----

	/**
		A section title in the title font over a drawn gold rule with a diamond at its middle (the profile's own title art
		can't be made: see the class comment), with the two players' names at its ends when given. Returns where the
		section's rows start.
	**/
	static function sectionTitle(x:Float, y:Float, width:Float, title:String, left:String, right:String):Float {
		if (level >= 4) {
			var rule = new Shape();
			var g = rule.graphics;
			g.lineStyle(1, RULE, 0.9);
			g.moveTo(x + 2, y + 27);
			g.lineTo(x + width - 2, y + 27);
			g.moveTo(x + width / 2 - 4, y + 27);
			g.lineTo(x + width / 2, y + 23);
			g.lineTo(x + width / 2 + 4, y + 27);
			g.lineTo(x + width / 2, y + 31);
			g.lineTo(x + width / 2 - 4, y + 27);
			under.addChild(rule);
		}
		content.addChild(text(x, y + 2, width, 16, TITLE, title, "center", bigFont));
		if (left.length > 0)
			content.addChild(text(x, y + 8, width / 2 - 50, 12, DIM, left, "left"));
		if (right.length > 0)
			content.addChild(text(x + width / 2 + 50, y + 8, width / 2 - 50, 12, DIM, right, "right"));
		return y + TITLE_BLOCK;
	}

	/**
		The art a profile slot is made of, taken from one of the window's own slots: its background, grade background and
		frame (the shared component library's classes, ARKSlotBackgroundV2, GradeIcon_00V2, arkSlot_renew_basic_Frame_V2),
		and where its 64 px icon sits. Null when the window has no slot to copy.
	**/
	static function slotModel():Dynamic {
		var manager:Dynamic = gearSlots(window);
		var slot:Dynamic = manager == null ? null : manager.getSlotAtIndex(1);
		if (slot == null)
			return null;
		var background:Dynamic = slot.background;
		var grade:Dynamic = slot.gradeBG;
		var border:Dynamic = slot.border;
		var canvas:Dynamic = slot.contentCanvas;
		return {
			background: background == null ? null : background.constructor,
			grade: grade == null ? null : grade.constructor,
			border: border == null ? null : border.constructor,
			iconX: canvas == null ? 3 : canvas.x,
			iconY: canvas == null ? 3 : canvas.y
		};
	}

	/**
		An item (or gem) drawn as the profile draws its slot: background, the grade background for its grade, the icon (the
		game's own icon loader, ExtendIcon) and the frame. Only pictures: nothing in it is a slot the game knows. Pointing
		at it shows the game's tooltip of the slot it came from (see hover).
	**/
	static function itemPicture(model:Dynamic, item:Dynamic, x:Float, y:Float, size:Float):Void {
		var data:Dynamic = item == null ? null : item.data;
		if (model == null || data == null || level < 4)
			return;
		var picture = new Sprite();
		picture.mouseChildren = false;
		picture.x = x;
		picture.y = y;
		var scale = size / SLOT_ART;
		picture.scaleX = scale;
		picture.scaleY = scale;
		content.addChild(picture);
		var background:Dynamic = model.background == null ? null : untyped __new__(model.background);
		if (background != null)
			picture.addChild(background);
		var iconX:Float = model.iconX;
		var iconY:Float = model.iconY;
		if (model.grade != null && data.iconGrade != null) {
			var grade:Dynamic = untyped __new__(model.grade);
			grade.x = iconX;
			grade.y = iconY;
			picture.addChild(grade);
			grade.icon = data.iconGrade;
		}
		var iconClass:Dynamic = GameUi.definition("ark.controls.ExtendIcon");
		var path:String = data.iconPath;
		if (iconClass != null && path != null && path != "" && level >= 5) {
			var icon:Dynamic = untyped __new__(iconClass);
			icon.iconType = "Type_NORMAL";
			icon.canvasWidth = ICON;
			icon.canvasHeight = ICON;
			icon.x = iconX;
			icon.y = iconY;
			picture.addChild(icon);
			icon.loadImageEx(path, data.iconU, data.iconV, data.iconWidth, data.iconHeight, false);
		}
		var border:Dynamic = model.border == null ? null : untyped __new__(model.border);
		if (border != null)
			picture.addChild(border);
		var source:Dynamic = item.source;
		if (source != null && level >= STEPS && Options.get(TWEAK, "tooltips", true) != false) {
			picture.addEventListener(MouseEvent.ROLL_OVER, function(event:Dynamic) hover(source));
			picture.addEventListener(MouseEvent.ROLL_OUT, function(event:Dynamic) hideTooltip());
		}
	}

	/**
		Shows the game's tooltip of the slot a picture came from: the slot's own hover event (SlotEvent.ROLL_OVER), which
		the game's TooltipManager listens for; the tooltip sits where that slot is on its page. Only for slots still on
		the stage (your profile's slots go when it closes).
	**/
	static function hover(source:Dynamic):Void {
		hideTooltip();
		if (source == null || source.stage == null)
			return;
		var bindID:String = source.bindID;
		var slotID:Int = source.slotID;
		hovered = source;
		source.dispatchEvent(new ark.events.SlotEvent(ark.events.SlotEvent.ROLL_OVER, true, false, 0, 0, bindID, slotID));
	}

	static function hideTooltip():Void {
		var source:Dynamic = hovered;
		hovered = null;
		if (source == null)
			return;
		var bindID:String = source.bindID;
		var slotID:Int = source.slotID;
		source.dispatchEvent(new ark.events.SlotEvent(ark.events.SlotEvent.ROLL_OUT, true, false, 0, 0, bindID, slotID));
	}

	/** A faint band behind every other row, as the profile's lists shade theirs. */
	static function band(x:Float, y:Float, width:Float, height:Float):Void {
		var shape = new Shape();
		shape.graphics.beginFill(0xFFFFFF, 0.035);
		shape.graphics.drawRect(x, y, width, height);
		shape.graphics.endFill();
		under.addChild(shape);
	}

	/** The change in the middle of an equipment row: the percentage, and the Combat Power under it. */
	static function delta(x:Float, y:Float, width:Float, change:Float, word:String):Void {
		if (word != null) {
			content.addChild(text(x, y + 11, width, 12, DIM, word, "center"));
			return;
		}
		if (isNaN(change)) {
			content.addChild(text(x, y + 11, width, 13, DIM, "—", "center"));
			return;
		}
		content.addChild(text(x, y + 2, width, 15, colorOf(change), percent(change), "center"));
		var shown = amount(change);
		if (shown.length > 0)
			content.addChild(text(x, y + 21, width, 11, DIM, shown, "center"));
	}

	/** The change at the end of a text row (from `x` to the column's end), or a grey word. */
	static function inlineDelta(x:Float, y:Float, change:Float, word:String):Void {
		var width = COLUMN_WIDTH - (x - COLUMNS[2]) - 2;
		if (word != null) {
			content.addChild(text(x, y + 1, width, 12, DIM, word, "right"));
			return;
		}
		content.addChild(text(x, y + 1, width, 13, isNaN(change) ? DIM : colorOf(change), isNaN(change) ? "—" : percent(change),
			"right"));
	}

	static function colorOf(change:Float):Int {
		if (isNaN(change) || Math.abs(change) < 0.00005)
			return DIM;
		return change > 0 ? GAIN : LOSS;
	}

	/** "+1.23%", or "0%" when it rounds to nothing. */
	static function percent(change:Float):String {
		if (Math.abs(change) < 0.00005)
			return "0%";
		return CombatPower.signed(change * 100, "%");
	}

	/** "+234.56" of your Combat Power; "" when your figure isn't known. */
	static function amount(change:Float):String {
		if (CombatPower.power <= 0 || Math.abs(change) < 0.00005)
			return "";
		return CombatPower.signed(CombatPower.power * change, "");
	}

	static function whole(value:Float):String {
		return Tooltips.grouped(Math.round(value));
	}

	static function nothing():Float {
		// Number.NaN: Haxe's Math.NaN needs its Boot, which Arkive leaves empty.
		return untyped __global__["Number"].NaN;
	}

	static function isNaN(value:Float):Bool {
		return untyped __global__["isNaN"](value);
	}

	/** A text field in the window's own font (or `face`), set the way game fonts draw: embedded, .text and textColor. */
	static function text(x:Float, y:Float, width:Float, size:Int, color:Int, value:String, align:String, ?face:String):TextField {
		var format = new TextFormat();
		format.font = face == null ? font : face;
		format.size = size;
		format.align = untyped align;
		var field = new TextField();
		field.embedFonts = embed;
		field.defaultTextFormat = format;
		field.textColor = color;
		field.multiline = false;
		field.wordWrap = false;
		field.selectable = false;
		field.mouseEnabled = false;
		field.x = x;
		field.y = y;
		field.width = width;
		field.height = size + 10;
		field.text = value;
		return field;
	}

	static function labelText(label:Dynamic):String {
		if (label == null)
			return "";
		var field:Dynamic = label.textField;
		var value:String = field == null ? label.text : field.text;
		return value == null ? "" : Text.trim(value);
	}
}
