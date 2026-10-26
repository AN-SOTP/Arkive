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
	Compare With Me: a "Compare" tab in the inspect window that sets the player's build beside yours. Equipment,
	accessories and gems are drawn as the profile draws its slots (the slot's background, grade background, icon and
	frame), engravings, Ark Passive points and combat stats are listed beside yours, and every difference says what
	taking just that part of theirs would do to your Combat Power, priced with Combat Power on Tooltips' model. The top
	of the page prices their whole build on you.

	The page never makes item slots of its own: a new ARKNewSlot binds itself to the game engine (ARKDataBinding) and
	the engine froze and crashed on one. Items are read only through the windows' own slots, as Combat Power reads
	them: theirs from the inspect window when the page draws, yours from your profile while it shows (kept, with its
	lists and labels, for later; Combat Power's model keeps your gear and totals from the same reading).

	The tab sits before Logs Tab's. With both, the seven tabs share the row the game's five and Logs share alone. Like
	the Logs tab it is a page of its own: the other tabs' pages hide while it shows, and any real tab (or a new inspect)
	closes it.
**/
@:keep
@:access(arkive.tweaks.CombatPower)
@:access(arkive.tweaks.LogsTab)
class Compare {
	static inline var TWEAK = "compare";
	static inline var TAB_NAME = "arkiveCompareTab";
	static inline var GAME_TABS = 5;
	static inline var SLOT_COUNT = 15;
	static inline var GEM_COUNT = 11;

	/** The profile's section title (the art behind "Basic Stats"), and its native size. */
	static inline var TITLE_ART = "CharInfo_Equip_AbilityPage_BasicAbilityTitleBG";
	static inline var TITLE_ART_WIDTH = 194;
	/** The profile's slots are this wide unscaled (a 64 px icon in a 3 px frame). */
	static inline var SLOT_ART = 70;
	static inline var ICON = 64;

	static inline var TITLE = 0x919CA6;
	static inline var TEXT = 0xE6E6E6;
	static inline var DIM = 0xA9A9A9;
	static inline var GAIN = 0x7EE08F;
	static inline var LOSS = 0xFF8A80;
	static inline var NAME = 0xE3C27F;

	/** Page layout: the header, three columns under it, and the gems along the bottom of the first two. */
	static inline var PAGE_WIDTH = 1036;
	static inline var PAGE_HEIGHT = 600;
	static inline var TOP = 62;
	static inline var TITLE_BLOCK = 50;
	static inline var ROW = 42;
	static inline var SLOT = 38;
	static inline var GEM = 34;
	static inline var GEM_STEP = 40;
	static inline var TEXT_ROW = 22;
	static inline var COLUMN_WIDTH = 326;
	static var COLUMNS:Array<Int> = [16, 362, 708];
	static inline var GEMS_TOP = 452;

	/** Equipment rows by the profile's slot numbers (Combat Power's SLOTS): armour and weapon as the profile orders them. */
	static var LEFT_SLOTS:Array<Int> = [1, 5, 2, 3, 4, 0, 14, 13];
	static var RIGHT_SLOTS:Array<Int> = [6, 7, 8, 9, 10, 11, 12];
	static var PASSIVES:Array<String> = ["Evolution", "Enlightenment", "Leap"];

	/** Engraving grades as the profile's list numbers them (1 rare … 4 relic), with their names and colours. */
	static var GRADE_WORDS:Array<String> = ["", "Rare", "Heroic", "Legendary", "Relic"];
	static var GRADE_COLORS:Array<Int> = [TEXT, 0x00B0FA, 0xCE43FC, 0xF99200, 0xFA5D00];

	/** Your own profile window, and what it last showed. */
	static var mineWindow:Dynamic;
	static var frames = 0;
	/** Your slots' data (for their pictures) and the items and gems read from them, by slot. */
	static var myGear:Array<Dynamic>;
	static var myItems:Array<Dynamic>;
	static var myGems:Array<Dynamic>;
	static var myGemData:Array<Dynamic>;
	static var myEngravings:Array<Dynamic>;
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
	static var logsWatched:Dictionary = new Dictionary(true);

	static var page:Sprite;
	static var content:Sprite;
	/** Under the page's slots and text: section title art, empty-slot pictures and row bands. */
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
		// Logs Tab adds its tab in the same call, before or after this one; the tabs are laid out once both are there,
		// and again whenever the game lays its own tabs out afresh.
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
		Lays the tab row out: with one extra tab (Compare or Logs) the game's tabs keep their places and the extra one
		follows; with both, the game's five, Compare and Logs share that same row evenly.
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
		var logs = logsTab(self, own);
		if (logs != null && Options.on("logs"))
			extras.push(logs);
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

	/** Logs Tab's tab in this window (a copy of the same game tab, labelled "Logs"), or null. */
	static function logsTab(self:Dynamic, own:Dynamic):Dynamic {
		var count:Int = self.numChildren;
		for (i in 0...count) {
			var child:Dynamic = self.getChildAt(i);
			if (child == own || child.constructor != own.constructor)
				continue;
			var label:Dynamic = child.label;
			if (label != "Logs")
				continue;
			if (untyped logsWatched[child] != true) {
				untyped logsWatched[child] = true;
				// The Logs tab draws its own page over the hidden ones; this page just steps aside.
				child.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) closeFor(own, false));
			}
			return child;
		}
		return null;
	}

	// ---- Showing and closing ----

	static function show(self:Dynamic, group:Dynamic, button:Dynamic):Void {
		if (LogsTab.showing && LogsTab.window == self && LogsTab.tab != null)
			LogsTab.closeFor(LogsTab.tab);
		var current:Dynamic = group.getButtonAt(group.selectedIndex);
		if (current != null)
			current.selected = false;
		button.selected = true;
		window = self;
		tabs = group;
		tab = button;
		showing = true;
		pages(self, false);
		build(self);
		page.visible = true;
		safeDraw();
	}

	/** Closes the page; `restore` shows the window's own tab again (not when Logs Tab's page takes over). */
	static function closeFor(button:Dynamic, restore:Bool):Void {
		if (button == null)
			return;
		button.selected = false;
		if (!showing || tab != button)
			return;
		showing = false;
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

	/** Your profile, while it shows: keeps its slots, lists and labels for the Compare page (twice a second or so). */
	static function watchMine(event:Dynamic):Void {
		frames++;
		if (frames % 20 != 0 || !Options.on(TWEAK))
			return;
		var self:Dynamic = mineWindow;
		if (self == null || self.parent == null || !self.parent.visible || !self.visible)
			return;
		// Tooltips are read only when a slot's data changed, not every check.
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
		var engravings = readEngravings(self);
		if (engravings.length > 0)
			myEngravings = engravings;
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

	/** Engravings from the Engravings tab's list: {name, grade (the list's 1-4), level (0-4)}. */
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
			var level:Int = row.curActivity == null ? 0 : row.curActivity;
			found.push({name: name, grade: grade, level: level});
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

	/** A window's equipment by slot, each read through its own slot's tooltip (Combat Power's reading), null where empty. */
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
			}
			items.push(item);
		}
		return items;
	}

	/** A window's equipped gems ({tier, level, kind, base, name, data}), read through its gem slots' tooltips. */
	static function readGems(self:Dynamic):Array<Dynamic> {
		var manager:Dynamic = gemSlots(self);
		var gems:Array<Dynamic> = [];
		for (i in 0...GEM_COUNT) {
			var slot:Dynamic = manager == null ? null : manager.getSlotAtIndex(i);
			var boxes = slot == null || slot.data == null ? null : Tooltips.boxes(slot.binding, slot);
			var gem:Dynamic = boxes == null ? null : CombatPower.gemOf(boxes);
			if (gem != null) {
				gem.data = slot.data;
				gems.push(gem);
			}
		}
		return gems;
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

	/** Draws the page afresh from the inspect window (theirs) and what your profile last showed (yours). */
	static function draw():Void {
		while (content.numChildren > 0)
			content.removeChildAt(0);
		under = new Sprite();
		content.addChild(under);
		shownName = labelText(nameLabel(window));
		shownSignature = signature(window);
		var ready = CombatPower.gear != null && CombatPower.equipped != null && CombatPower.role >= 0 && CombatPower.table() != null;
		var dps = ready && CombatPower.role == 0;
		var theirRole = roleOf(window);

		// Theirs are read from the inspect window now; yours are what your profile last showed.
		var theirs = readItems(window);
		var mineItems:Array<Dynamic> = myItems == null ? [] : myItems;
		while (mineItems.length < SLOT_COUNT)
			mineItems.push(null);
		var model:Dynamic = slotModel();
		for (i in 0...SLOT_COUNT) {
			var at = rowOf(i);
			var x:Float = at.x;
			var y:Float = at.y;
			var theirItem:Dynamic = theirs[i];
			var mineItem:Dynamic = mineItems[i];
			emptyBack(i, x, y + 2, SLOT);
			emptyBack(i, x + COLUMN_WIDTH - SLOT, y + 2, SLOT);
			itemPicture(model, theirItem == null ? null : theirItem.data, x, y + 2, SLOT);
			itemPicture(model, mineItem == null ? null : mineItem.data, x + COLUMN_WIDTH - SLOT, y + 2, SLOT);
		}
		var theirGems = readGems(window);
		var mineGems:Array<Dynamic> = myGems == null ? [] : CombatPower.copyOf(myGems);
		var theirEngravings = readEngravings(window);
		var theirPassive = readPassive(window);
		var theirCombat = readCombat(window);

		header(ready, dps, theirRole, theirs, theirGems, theirEngravings, theirPassive, theirCombat);
		gearColumn(COLUMNS[0], "Equipment", LEFT_SLOTS, theirs, mineItems, dps);
		gearColumn(COLUMNS[1], "Accessories", RIGHT_SLOTS, theirs, mineItems, dps);
		gemStrip(model, theirGems, mineGems, ready);
		var y = engravingRows(theirs, mineItems, theirEngravings, ready);
		y = passiveRows(y, theirPassive, dps);
		y = combatRows(y, theirCombat, dps);
		if (y + 40 <= PAGE_HEIGHT) {
			var note = text(COLUMNS[2], y + 4, COLUMN_WIDTH, 11, DIM, "", "left");
			note.multiline = true;
			note.wordWrap = true;
			note.height = PAGE_HEIGHT - y - 4;
			note.text = "Each figure is your Combat Power with just that part of theirs in place of yours.";
			content.addChild(note);
		}
	}

	/** Where an equipment slot's row is: {x, y} of the row in its column. */
	static function rowOf(index:Int):Dynamic {
		var at = LEFT_SLOTS.indexOf(index);
		if (at >= 0)
			return {x: COLUMNS[0], y: TOP + TITLE_BLOCK + at * ROW};
		at = RIGHT_SLOTS.indexOf(index);
		return {x: COLUMNS[1], y: TOP + TITLE_BLOCK + at * ROW};
	}

	// ---- Header ----

	static function header(ready:Bool, dps:Bool, theirRole:Int, theirs:Array<Dynamic>, theirGems:Array<Dynamic>,
			theirEngravings:Array<Dynamic>, theirPassive:Array<Float>, theirCombat:Dynamic):Void {
		var left = COLUMNS[0];
		var right = COLUMNS[2];
		var name = labelText(nameLabel(window));
		content.addChild(text(left, 2, COLUMN_WIDTH, 18, NAME, name, "left"));
		content.addChild(text(left, 28, COLUMN_WIDTH, 13, DIM, details(labelText(classLabel(window)), labelText(levelLabel(window)),
			theirRole < 0 ? "" : labelText(powerLabel(window))), "left"));
		var you = myName.length > 0 ? "You  ·  " + myName : "You";
		content.addChild(text(right, 2, COLUMN_WIDTH, 18, NAME, you, "right"));
		content.addChild(text(right, 28, COLUMN_WIDTH, 13, DIM, details(myClass, myLevel, myPower), "right"));

		var middle = COLUMNS[1];
		var lead:TextField;
		var line:String;
		if (!ready) {
			lead = text(middle, 0, COLUMN_WIDTH, 20, DIM, "—", "center", bigFont);
			line = "Open your Character Profile once to price the differences";
		} else if (!dps) {
			lead = text(middle, 0, COLUMN_WIDTH, 20, DIM, "—", "center", bigFont);
			line = "Support: only gems and engravings are priced for now";
		} else {
			var change = wholeBuild(theirs, theirGems, theirEngravings, theirPassive, theirCombat);
			lead = text(middle, 0, COLUMN_WIDTH, 22, colorOf(change), percent(change) + amount(change, "  (", ")"), "center",
				bigFont);
			line = theirRole == 1 ? "your Combat Power with their whole build (they play support)"
				: "your Combat Power with their whole build";
			if (Options.get(TWEAK, "showCheck", false) == true && CombatPower.power > 0)
				line += "  ·  model " + CombatPower.money(CombatPower.power * (1 + change)) + " vs game "
					+ labelText(powerLabel(window));
		}
		content.addChild(lead);
		content.addChild(text(middle - 20, 30, COLUMN_WIDTH + 40, 12, DIM, line, "center"));
	}

	static function details(className:String, level:String, power:String):String {
		var parts:Array<String> = [];
		if (className.length > 0)
			parts.push(className);
		if (level.length > 0)
			parts.push("Item Lv. " + level);
		if (power.length > 0)
			parts.push("Combat Power " + power);
		return parts.join("  ·  ");
	}

	/**
		Your Combat Power with their whole build, as a change: their gear, gems and engravings, and their Ark Passive points
		and combat stats that don't come from gear (their totals minus their gear's, as Combat Power reads yours).
	**/
	static function wholeBuild(theirs:Array<Dynamic>, theirGems:Array<Dynamic>, theirEngravings:Array<Dynamic>,
			theirPassive:Array<Float>, theirCombat:Dynamic):Float {
		var before = CombatPower.value(CombatPower.gear, CombatPower.equipped);
		var keptEngravings = CombatPower.engravings;
		var keptEnlightenment = CombatPower.otherEnlightenment;
		var keptLeap = CombatPower.otherLeap;
		var keptCombat = CombatPower.otherCombat;
		var gearEnlightenment:Float = 0;
		var gearLeap:Float = 0;
		var gearCombat:Float = 0;
		for (item in theirs) {
			if (item == null)
				continue;
			var e:Float = item.enlightenment;
			var l:Float = item.leap;
			var c:Float = item.combat;
			gearEnlightenment += e;
			gearLeap += l;
			gearCombat += c;
		}
		if (theirPassive != null && theirPassive[1] >= 0 && theirPassive[2] >= 0) {
			CombatPower.otherEnlightenment = Math.max(0, theirPassive[1] - gearEnlightenment);
			CombatPower.otherLeap = Math.max(0, theirPassive[2] - gearLeap);
		}
		var total = combatSum(theirCombat);
		if (total >= 0 && keptCombat >= 0)
			CombatPower.otherCombat = Math.max(0, total - gearCombat);
		if (theirEngravings.length > 0)
			CombatPower.engravings = tableEngravings(theirEngravings);
		var after = CombatPower.value(theirs, theirGems.length > 0 ? theirGems : CombatPower.equipped);
		CombatPower.engravings = keptEngravings;
		CombatPower.otherEnlightenment = keptEnlightenment;
		CombatPower.otherLeap = keptLeap;
		CombatPower.otherCombat = keptCombat;
		return after / before - 1;
	}

	/** Engravings as Combat Power's model counts them: grade 1 heroic … 3 relic (the list counts from rare). */
	static function tableEngravings(list:Array<Dynamic>):Array<Dynamic> {
		var found:Array<Dynamic> = [];
		for (engraving in list) {
			var grade:Int = engraving.grade;
			grade = grade - 1;
			if (grade > 3)
				grade = 3;
			found.push({name: engraving.name, grade: grade, level: engraving.level});
		}
		return found;
	}

	static function combatSum(combat:Dynamic):Float {
		if (combat == null)
			return -1;
		var sum:Float = 0;
		for (label in CombatPower.COMBAT_LABELS) {
			var points:Float = Text.has(combat, label) ? untyped combat[label] : 0;
			if (points > 0)
				sum += points;
		}
		return sum;
	}

	// ---- Equipment columns ----

	static function gearColumn(x:Int, heading:String, slots:Array<Int>, theirs:Array<Dynamic>, mineItems:Array<Dynamic>,
			dps:Bool):Void {
		var y = sectionTitle(x, TOP, heading, true);
		var shade = false;
		for (index in slots) {
			if (shade)
				band(x, y, COLUMN_WIDTH, ROW);
			shade = !shade;
			var theirItem:Dynamic = theirs[index];
			var mineItem:Dynamic = mineItems[index];
			itemLabel(theirItem, x + SLOT + 6, y, "left");
			itemLabel(mineItem, x + COLUMN_WIDTH - SLOT - 76, y, "right");
			var change:Float = dps && (theirItem != null || CombatPower.gear[index] != null) ? swap(index, theirItem) : nothing();
			var same = theirItem != null && mineItem != null && sameItem(theirItem, mineItem);
			delta(x + SLOT + 70, y, COLUMN_WIDTH - 2 * SLOT - 140, same ? 0 : change, same ? "same" : null);
			y += ROW;
		}
	}

	/** Your Combat Power with their item (or nothing) in this slot instead of yours, as a change. */
	static function swap(index:Int, item:Dynamic):Float {
		var after = CombatPower.copyOf(CombatPower.gear);
		after[index] = item;
		return CombatPower.value(after, CombatPower.equipped) / CombatPower.value(CombatPower.gear, CombatPower.equipped) - 1;
	}

	static function sameItem(a:Dynamic, b:Dynamic):Bool {
		var first:String = a.text;
		var second:String = b.text;
		return first == second;
	}

	/** Two short lines beside a slot: the honing level from the name ("+25") and the quality, in the game's colours. */
	static function itemLabel(item:Dynamic, x:Float, y:Float, align:String):Void {
		if (item == null)
			return;
		var name:String = item.name;
		var top = "";
		if (StringTools.startsWith(name, "+")) {
			var level = Tooltips.leadingInt(name.substr(1), -1);
			if (level >= 0) {
				var shown:String = untyped __global__["String"](level);
				top = "+" + shown;
			}
		}
		var quality:Int = item.shownQuality;
		content.addChild(text(x, y + 2, 70, 13, TEXT, top, align));
		if (quality >= 0) {
			var shown:String = untyped __global__["String"](quality);
			content.addChild(text(x, y + 20, 70, 12, qualityColor(quality), "Q " + shown, align));
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

	static function gemStrip(model:Dynamic, theirGems:Array<Dynamic>, mineGems:Array<Dynamic>, ready:Bool):Void {
		var left = COLUMNS[0];
		var y = sectionTitle(left, GEMS_TOP, "Gems", false, COLUMNS[1] + COLUMN_WIDTH - left);
		theirGems.sort(gemOrder);
		mineGems.sort(gemOrder);
		gemRow(model, left, y, "Theirs", theirGems);
		gemRow(model, left, y + GEM + 12, "Yours", mineGems);
		var change:Float = nothing();
		if (ready && theirGems.length > 0)
			change = CombatPower.value(CombatPower.gear, theirGems) / CombatPower.value(CombatPower.gear, CombatPower.equipped) - 1;
		var x = left + 58 + GEM_COUNT * GEM_STEP;
		delta(x, y + 14, COLUMNS[1] + COLUMN_WIDTH - x, change, theirGems.length == 0 ? "open their Gems tab" : null);
	}

	static function gemRow(model:Dynamic, x:Float, y:Float, label:String, gems:Array<Dynamic>):Void {
		content.addChild(text(x, y + 9, 56, 13, DIM, label, "left"));
		for (i in 0...gems.length) {
			if (i >= GEM_COUNT)
				break;
			var gem:Dynamic = gems[i];
			var at = x + 58 + i * GEM_STEP;
			itemPicture(model, gem.data, at, y, GEM);
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

	// ---- Engravings, Ark Passive, combat stats ----

	static function engravingRows(theirs:Array<Dynamic>, mineItems:Array<Dynamic>, theirEngravings:Array<Dynamic>,
			ready:Bool):Float {
		var x = COLUMNS[2];
		var y = sectionTitle(x, TOP, "Engravings", true);
		var mineList:Array<Dynamic> = myEngravings == null ? [] : myEngravings;
		var names:Array<String> = [];
		for (engraving in theirEngravings)
			names.push(engraving.name);
		for (engraving in mineList) {
			var name:String = engraving.name;
			if (names.indexOf(name) < 0)
				names.push(name);
		}
		// The list fills when its tab has been open; until then that side is unknown rather than empty.
		var known = theirEngravings.length > 0 && mineList.length > 0;
		var theirStone:Dynamic = theirs[CombatPower.STONE_SLOT];
		var mineStone:Dynamic = CombatPower.gear == null ? null : CombatPower.gear[CombatPower.STONE_SLOT];
		var shade = false;
		for (name in names) {
			if (shade)
				band(x, y, COLUMN_WIDTH, TEXT_ROW);
			shade = !shade;
			var their = find(theirEngravings, name);
			var mine = find(mineList, name);
			content.addChild(text(x + 4, y + 2, 124, 13, TEXT, name, "left"));
			engravingCell(their, theirStone, name, x + 128, y);
			engravingCell(mine, mineItems[CombatPower.STONE_SLOT], name, x + 198, y);
			var change:Float = nothing();
			if (ready && known) {
				var before = engravingValue(mine, mineStone, name);
				var after = engravingValue(their, theirStone, name);
				change = (1 + after / 10000) / (1 + before / 10000) - 1;
			}
			inlineDelta(x + 268, y, change);
			y += TEXT_ROW;
		}
		if (!known) {
			content.addChild(text(x + 4, y + 2, COLUMN_WIDTH - 8, 12, DIM, theirEngravings.length == 0
				? "Open their Engravings tab once to compare" : "Open your profile's Engravings tab once to compare", "left"));
			y += TEXT_ROW;
		}
		return y + 8;
	}

	static function find(list:Array<Dynamic>, name:String):Dynamic {
		for (entry in list)
			if (entry.name == name)
				return entry;
		return null;
	}

	/** "Relic 4" in the grade's colour, with the stone's levels for it in grey ("+2"). */
	static function engravingCell(engraving:Dynamic, stone:Dynamic, name:String, x:Float, y:Float):Void {
		if (engraving == null) {
			content.addChild(text(x, y + 2, 70, 13, DIM, "—", "center"));
			return;
		}
		var grade:Int = engraving.grade;
		var level:Int = engraving.level;
		var word = grade >= 0 && grade < GRADE_WORDS.length ? GRADE_WORDS[grade] : "";
		var shown:String = untyped __global__["String"](level);
		var stoneLevel = CombatPower.stoneLevel(stone, name);
		var extra:String = untyped __global__["String"](stoneLevel);
		content.addChild(text(x, y + 2, 70, 13, grade >= 0 && grade < GRADE_COLORS.length ? GRADE_COLORS[grade] : TEXT,
			(word.length > 0 ? word + " " : "Lv. ") + shown + (stoneLevel > 0 ? " +" + extra : ""), "center"));
	}

	/** An engraving's value in the model (hundredths of a percent) at its grade, level and the stone's levels. */
	static function engravingValue(engraving:Dynamic, stone:Dynamic, name:String):Float {
		if (engraving == null)
			return 0;
		var grade:Int = engraving.grade;
		grade = grade - 1;
		if (grade > 3)
			grade = 3;
		var level:Int = engraving.level;
		return CombatPower.engravingValue(name, 20 * CombatPower.stoneLevel(stone, name) + CombatPower.index(grade, level));
	}

	static function passiveRows(top:Float, theirPassive:Array<Float>, dps:Bool):Float {
		var x = COLUMNS[2];
		var y = sectionTitle(x, top, "Ark Passive", false);
		for (i in 0...PASSIVES.length) {
			var their:Float = theirPassive == null ? -1 : theirPassive[i];
			var mine:Float = myPassive == null ? -1 : myPassive[i];
			var type = i == 1 ? CombatPower.ENLIGHTENMENT : (i == 2 ? CombatPower.LEAP : -1);
			var change:Float = nothing();
			if (dps && type > 0 && their >= 0 && mine >= 0) {
				var each = CombatPower.perPoint(type);
				change = (1 + their * each) / (1 + mine * each) - 1;
			}
			numberRow(x, y, PASSIVES[i], their, mine, change, i % 2 == 1);
			y += TEXT_ROW;
		}
		return y + 8;
	}

	static function combatRows(top:Float, theirCombat:Dynamic, dps:Bool):Float {
		var x = COLUMNS[2];
		var y = sectionTitle(x, top, "Combat Stats", false);
		var each = CombatPower.combatPerPoint();
		var priced = dps && CombatPower.otherCombat >= 0;
		for (i in 0...CombatPower.COMBAT_LABELS.length) {
			var label = CombatPower.COMBAT_LABELS[i];
			var their:Float = theirCombat != null && Text.has(theirCombat, label) ? untyped theirCombat[label] : -1;
			var mine:Float = myCombat != null && Text.has(myCombat, label) ? untyped myCombat[label] : -1;
			var change:Float = priced && their >= 0 && mine >= 0 ? (1 + their * each) / (1 + mine * each) - 1 : nothing();
			numberRow(x, y, label, their, mine, change, i % 2 == 1);
			y += TEXT_ROW;
		}
		return y + 8;
	}

	static function numberRow(x:Float, y:Float, label:String, their:Float, mine:Float, change:Float, shade:Bool):Void {
		if (shade)
			band(x, y, COLUMN_WIDTH, TEXT_ROW);
		content.addChild(text(x + 4, y + 2, 124, 13, TEXT, label, "left"));
		content.addChild(text(x + 128, y + 2, 70, 13, TEXT, their < 0 ? "—" : whole(their), "center"));
		content.addChild(text(x + 198, y + 2, 70, 13, TEXT, mine < 0 ? "—" : whole(mine), "center"));
		inlineDelta(x + 268, y, change);
	}

	// ---- Pieces ----

	/**
		A section title on the profile's own title art, centred over the column (or `width`), with "Theirs" and "Yours"
		over the two sides when `sides`. Returns where the section's rows start.
	**/
	static function sectionTitle(x:Float, y:Float, title:String, sides:Bool, ?width:Float):Float {
		var wide:Float = width == null ? COLUMN_WIDTH : width;
		var art:Dynamic = make(TITLE_ART);
		if (art != null) {
			art.x = x + (wide - TITLE_ART_WIDTH) / 2;
			art.y = y;
			art.mouseEnabled = false;
			under.addChild(art);
		}
		content.addChild(text(x, y + 20, wide, 16, TITLE, title, "center"));
		if (sides) {
			content.addChild(text(x + 2, y + 30, 60, 12, DIM, "Theirs", "left"));
			content.addChild(text(x + wide - 62, y + 30, 60, 12, DIM, "Yours", "right"));
		}
		return y + TITLE_BLOCK;
	}

	/** The profile's empty-slot picture for an equipment slot (helm, weapon …), as it draws one under each slot. */
	static function emptyBack(index:Int, x:Float, y:Float, size:Float):Void {
		var manager:Dynamic = gearSlots(window);
		var back:Dynamic = manager == null ? null : manager.getChildByName("slotBgMc" + index);
		if (back == null)
			return;
		var made:Dynamic = untyped __new__(back.constructor);
		made.mouseEnabled = false;
		made.mouseChildren = false;
		var natural:Float = made.width;
		var scale = natural > 0 ? size / natural : 1;
		made.scaleX = scale;
		made.scaleY = scale;
		made.x = x;
		made.y = y;
		under.addChild(made);
	}

	/**
		The art a profile slot is made of, taken from one of the window's own slots: its background, grade background and
		frame (their classes), and where its 64 px icon sits. Null when the window has no slot to copy.
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
		An item drawn as the profile draws its slot: background, the grade background for the item's grade, the icon
		(the game's own icon loader, ExtendIcon) and the frame. Only pictures: nothing in it is a slot the game knows.
	**/
	static function itemPicture(model:Dynamic, data:Dynamic, x:Float, y:Float, size:Float):Void {
		if (model == null || data == null)
			return;
		var picture = new Sprite();
		picture.mouseEnabled = false;
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
		if (iconClass != null && path != null && path != "") {
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
			content.addChild(text(x, y + 11, width, 13, DIM, word, "center"));
			return;
		}
		if (isNaN(change)) {
			content.addChild(text(x, y + 11, width, 13, DIM, "—", "center"));
			return;
		}
		content.addChild(text(x, y + 2, width, 15, colorOf(change), percent(change), "center"));
		var shown = amount(change, "", "");
		if (shown.length > 0)
			content.addChild(text(x, y + 21, width, 11, DIM, shown, "center"));
	}

	/** The change at the end of a text row. */
	static function inlineDelta(x:Float, y:Float, change:Float):Void {
		content.addChild(text(x, y + 2, COLUMN_WIDTH - (x - COLUMNS[2]) - 2, 13, isNaN(change) ? DIM : colorOf(change),
			isNaN(change) ? "—" : percent(change), "right"));
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

	/** "+234.56" of your Combat Power, wrapped in `before` and `after`; "" when your figure isn't known. */
	static function amount(change:Float, before:String, after:String):String {
		if (CombatPower.power <= 0 || Math.abs(change) < 0.00005)
			return "";
		return before + CombatPower.signed(CombatPower.power * change, "") + after;
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

	static function make(symbol:String):Dynamic {
		var type:Dynamic = GameUi.definition(symbol);
		return type == null ? null : untyped __new__(type);
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
