package arkive.tweaks;

import flash.text.TextField;
import flash.text.TextFormat;
import scaleform.clik.events.ButtonEvent;

/**
	Bracelet Dismantle by Stat. The bracelet tab of the auto-dismantle settings gets six stat tick boxes. Each time the
	Dismantle window opens with the bag beside it, every bag bracelet with a ticked stat goes into the dismantle list,
	the same way a right-click puts it there. The player checks the list and presses Dismantle.

	The game's own auto-dismantle runs in the engine and only knows its own settings, so these boxes are the tweak's.
	A bracelet's stats are read from its tooltip's "Bracelet Bonus" lines ("Crit +90"), English client only.
**/
@:keep
class BraceletDismantle {
	static inline var ID = "braceletdismantle";
	static inline var KEY = "stats";
	static inline var FONT = "$YG760";
	static inline var BONUS_TITLE = "Bracelet Bonus";

	/** Frames to wait after the window opens before reading the bag, so the game has marked what it lists. */
	static inline var SETTLE_FRAMES = 12;
	static inline var SLOTS_PER_FRAME = 8;

	/**
		After each right-click, how long to wait for the bracelet to show up in the list before clicking it again, how
		many clicks it gets in all, and the frames to leave between one bracelet going in and the next click. Clicks sent
		one a frame, without waiting, put only the first bracelet in the list (GitHub issue #4).
	**/
	static inline var CONFIRM_MS = 1200;
	static inline var TRIES = 3;
	static inline var GAP_FRAMES = 3;

	/** Passes over the bag in one opening: another pass after one that added something catches bracelets that moved. */
	static inline var PASSES = 3;

	/** In the settings' order: left column Crit, Specialization, Domination; right column the rest. */
	static var STATS:Array<Dynamic> = [
		{value: "crit", label: "Crit"},
		{value: "spec", label: "Specialization"},
		{value: "dom", label: "Domination"},
		{value: "swift", label: "Swiftness"},
		{value: "endur", label: "Endurance"},
		{value: "exp", label: "Expertise"},
	];

	static inline var LABEL_X = 23;
	static inline var RIGHT_X = 250;
	static inline var BOX_OFFSET = 165;
	static inline var ROW = 32;

	/** The inventory's bag (MainBag), the bracelet tab's added rows, and the window's status line. */
	static var bag:Dynamic;
	static var rows:Array<Dynamic> = [];
	static var status:TextField;

	/**
		The current opening of the window: shown, frames left to wait, next bag slot, passes so far, bracelets added in
		this pass, finished, bindIDs that went in, bindIDs given up on, and the counts of both.
	**/
	static var open = false;
	static var wait = 0;
	static var next = 0;
	static var pass = 0;
	static var addedThisPass = 0;
	static var done = false;
	static var added:Dynamic = {};
	static var failed:Dynamic = {};
	static var count = 0;
	static var gaveUp = 0;

	/** The click waiting to show up: the bag slot, its bindID, the list's item count before, when, clicks so far. */
	static var pending:Dynamic = null;
	static var pendingId = "";
	static var listBefore = 0;
	static var clickedAt = 0;
	static var tries = 0;

	/** What the last scan saw, shown when nothing matched: bracelets read, greyed out, locked, stats and a first line. */
	static var bracelets = 0;
	static var greyed = 0;
	static var locked = 0;
	static var seen:Array<Dynamic> = [];
	static var sample = "";

	/** For Arkive's log: how each bracelet was seen to go in ("list", "bag", "same"), retries, and the first error. */
	static var how:Array<String> = [];
	static var retries = 0;
	static var problem = "";

	/** MainBag.configUI: keep the bag, whose slots are the ones a right-click puts in the dismantle list. */
	public static function onBagConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		bag = self;
	}

	/** ItemDisassembleBraceletPage.configUI: the heading and six stat tick boxes under the bonus slot setting. */
	public static function onBraceletConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var like:Dynamic = self.brAcquiredBoxCheck_mc;
		var below:Dynamic = self.brOptionContent2_lb;
		if (like == null || below == null)
			return;
		rows = [];
		var heading = label("Add to the dismantle list when a bracelet has (Arkive)", fontOf(below), 0xFFFFFF, 420);
		self.addChild(heading);
		Options.own(ID, heading);
		rows.push(heading);
		var chosen = picked();
		for (i in 0...STATS.length) {
			var stat:Dynamic = STATS[i];
			var value:String = stat.value;
			var name:String = stat.label;
			var text = label(name, fontOf(below), 0xD9B26B, BOX_OFFSET - 10);
			var box:Dynamic = untyped __new__(like.constructor);
			box.selected = contains(chosen, value);
			box.name = value;
			box.addEventListener(ButtonEvent.CLICK, ticked);
			self.addChild(text);
			self.addChild(box);
			Options.own(ID, text);
			Options.own(ID, box);
			rows.push(text);
			rows.push(box);
		}
		layout(self);
		// The game moves the bonus slot rows when the tier drop-down shows or hides; follow them afterwards.
		var tier:Dynamic = self.brApplyAutoDisassembleTier_mc;
		if (tier != null) {
			tier.addEventListener("show", function(event:Dynamic) layout(self), false, -1);
			tier.addEventListener("hide", function(event:Dynamic) layout(self), false, -1);
		}
	}

	/** Heading under the "fewer than N bonus slots" line, then three rows of two: label, then its box. */
	static function layout(page:Dynamic):Void {
		var below:Dynamic = page.brOptionContent2_lb;
		if (below == null || rows.length != 1 + 2 * STATS.length)
			return;
		var top:Float = below.y + 34;
		rows[0].x = LABEL_X;
		rows[0].y = top;
		for (i in 0...STATS.length) {
			var column = i < 3 ? 0 : 1;
			var row = i % 3;
			var x:Float = column == 0 ? LABEL_X : RIGHT_X;
			var y:Float = top + ROW * (row + 1);
			var text:Dynamic = rows[1 + 2 * i];
			var box:Dynamic = rows[2 + 2 * i];
			text.x = x;
			text.y = y + 2;
			box.x = x + BOX_OFFSET;
			box.y = y;
		}
	}

	/** A stat box was clicked: keep the choice for this session and send the change to Arkive to save. */
	static function ticked(event:Dynamic):Void {
		var box:Dynamic = event.currentTarget;
		var value:String = box.name;
		var on = box.selected == true;
		var list:Array<Dynamic> = [];
		for (item in picked())
			if (item != value)
				list.push(item);
		if (on)
			list.push(value);
		Options.set(ID, KEY, list);
		var code:Int = untyped Math.floor(Math.random() * 65536);
		var request:String = StringTools.hex(code, 4).toLowerCase();
		var change:String = (on ? "+" : "-") + value;
		Bus.send("settings", request + "\t" + ID + "\t" + KEY + "\t" + change);
	}

	/** ItemDisassembleWndContent.configUI: watch for the window opening, then fill the list a slot at a time. */
	public static function onWindowConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var button:Dynamic = self.disassemble_btn;
		if (button != null && button.parent != null) {
			// Under the window's bottom buttons, so it covers none of the window's own controls.
			var list:Dynamic = self.disassembleSelectList;
			var left:Float = list != null ? list.x : 14;
			status = label("", fontOf(self.info_lb), 0xD9B26B, button.x + button.width - left);
			status.autoSize = flash.text.TextFieldAutoSize.LEFT;
			status.multiline = true;
			status.wordWrap = true;
			status.x = left;
			status.y = button.y + button.height + 8;
			button.parent.addChild(status);
			Options.own(ID, status);
		}
		self.addEventListener("enterFrame", function(event:Dynamic) {
			// Runs outside the hook's guard: anything unexpected ends this opening, never the window.
			try {
				onFrame(self);
			} catch (error:Dynamic) {
				if (!done) {
					note(error);
					finish();
				}
			}
		});
	}

	static function onFrame(content:Dynamic):Void {
		if (!showText())
			say("");
		if (!Options.on(ID) || !shown(content) || bag == null || !shown(bag)) {
			open = false;
			return;
		}
		if (!open) {
			start();
			return;
		}
		if (wait > 0) {
			wait--;
			return;
		}
		if (done)
			return;
		var slots:Dynamic = bag.mainBag;
		if (slots == null || slots.visible != true)
			return;
		if (pending != null) {
			confirm(content);
			return;
		}
		var total:Int = slots.numItems;
		var end = next + SLOTS_PER_FRAME;
		while (next < total && next < end) {
			var slot:Dynamic = slots.getItemSlot(next);
			next++;
			var take = false;
			try {
				take = wanted(slot, content);
			} catch (error:Dynamic) {
				// One odd slot is skipped; the rest of the bag is still read.
				note(error);
			}
			if (take) {
				var id:String = slot.bindID;
				pending = slot;
				pendingId = id;
				tries = 0;
				click(content);
				return;
			}
		}
		if (next < total)
			return;
		if (addedThisPass > 0 && pass + 1 < PASSES) {
			pass++;
			next = 0;
			addedThisPass = 0;
			return;
		}
		finish();
	}

	/** A new opening of the window: forget the last one and wait for the game to mark the bag. */
	static function start():Void {
		open = true;
		wait = SETTLE_FRAMES;
		next = 0;
		pass = 0;
		addedThisPass = 0;
		done = false;
		added = {};
		failed = {};
		count = 0;
		gaveUp = 0;
		pending = null;
		pendingId = "";
		bracelets = 0;
		greyed = 0;
		locked = 0;
		seen = [];
		sample = "";
		how = [];
		retries = 0;
		problem = "";
		say("");
	}

	/** Right-clicks the pending bracelet, the same engine call a player's right-click makes. */
	static function click(content:Dynamic):Void {
		listBefore = listCount(content);
		clickedAt = flash.Lib.getTimer();
		tries++;
		flash.external.ExternalInterface.call("ARKSlotMouseRightClick", pending);
	}

	/** Waits for the pending bracelet to show up in the list (or greyed out in the bag); clicks again if it doesn't. */
	static function confirm(content:Dynamic):Void {
		var sign = wentIn(content);
		if (sign != "") {
			untyped added[pendingId] = true;
			count++;
			addedThisPass++;
			how.push(sign);
			pending = null;
			pendingId = "";
			wait = GAP_FRAMES;
			return;
		}
		if (flash.Lib.getTimer() - clickedAt < CONFIRM_MS)
			return;
		var slot:Dynamic = pending;
		var id:String = slot.bindID;
		if (tries < TRIES && id == pendingId && slot.disabled != true) {
			retries++;
			click(content);
			return;
		}
		untyped failed[pendingId] = true;
		gaveUp++;
		pending = null;
		pendingId = "";
	}

	/** How the pending bracelet is seen in the list: by its bindID, by the list growing, or greyed in the bag; "" if not. */
	static function wentIn(content:Dynamic):String {
		if (inList(content, pendingId))
			return "same";
		if (listBefore >= 0 && listCount(content) > listBefore)
			return "list";
		var slot:Dynamic = pending;
		var id:Dynamic = slot.bindID;
		if (id == pendingId && slot.disabled == true)
			return "bag";
		return "";
	}

	/** The items in the dismantle list, or -1 when it can't be read. */
	static function listCount(content:Dynamic):Int {
		var list:Dynamic = content.disassembleSlotManager;
		if (list == null)
			return -1;
		var total:Int = list.numItems;
		var items = 0;
		for (i in 0...total) {
			var slot:Dynamic = list.getItemSlot(i);
			if (slot != null && slot.data != null)
				items++;
		}
		return items;
	}

	/** True when a dismantle list slot holds the item with this bindID. */
	static function inList(content:Dynamic, id:String):Bool {
		var list:Dynamic = content.disassembleSlotManager;
		if (list == null)
			return false;
		var total:Int = list.numItems;
		for (i in 0...total) {
			var slot:Dynamic = list.getItemSlot(i);
			if (slot != null && slot.data != null && slot.bindID == id)
				return true;
		}
		return false;
	}

	/** The bag is read: show what happened and tell Arkive, which writes it to its log. */
	static function finish():Void {
		done = true;
		pending = null;
		pendingId = "";
		var text = count == 0 ? "Arkive: no bracelets matched. " : "Arkive added " + num(count) + (count == 1 ? " bracelet. " : " bracelets. ");
		if (gaveUp > 0)
			text += num(gaveUp) + " didn't go in. ";
		if (problem != "")
			text += "Error: " + problem + ". ";
		say(text + scanned());
		if (count > 0 || gaveUp > 0 || problem != "")
			Bus.send(ID, report());
	}

	/** "added 3 (list, list, same), gave up on 0, retries 1, bracelets read 5, greyed 0, locked 1" and any error. */
	static function report():String {
		return "added " + num(count) + " (" + how.join(", ") + "), gave up on " + num(gaveUp) + ", retries " + num(retries)
			+ ", bracelets read " + num(bracelets) + ", greyed " + num(greyed) + ", locked " + num(locked)
			+ (problem == "" ? "" : ", error: " + problem);
	}

	/** A typed String: "text" + an Int could go through Std.string, which needs Boot's patches. */
	static function num(value:Int):String {
		var text:String = untyped __global__["String"](value);
		return text;
	}

	/** Keeps the first error caught, short enough for the status line and the bus. */
	static function note(error:Dynamic):Void {
		if (problem != "")
			return;
		var text:String = untyped __global__["String"](error);
		problem = text.length > 120 ? text.substr(0, 120) : text;
	}

	/**
		A bag bracelet that isn't greyed out, locked, already in the list or already tried, with at least one ticked
		stat. A greyed-out slot is one the game marks disabled: a player's right-click on it does nothing.
	**/
	static function wanted(slot:Dynamic, content:Dynamic):Bool {
		if (slot == null || slot.data == null)
			return false;
		var id:Dynamic = slot.bindID;
		if (!Tooltips.isString(id) || id == "" || Text.has(added, id) || Text.has(failed, id))
			return false;
		var stats = statsOf(slot);
		if (stats == null)
			return false;
		// Counted once per opening: later passes read the same bracelets again.
		var first = pass == 0;
		if (first)
			bracelets++;
		for (stat in stats)
			if (!contains(seen, stat))
				seen.push(stat);
		if (slot.enabled != true || slot.disabled == true) {
			if (first)
				greyed++;
			return false;
		}
		var lock:Dynamic = slot.lock;
		if (lock != null && lock > 0) {
			if (first)
				locked++;
			return false;
		}
		if (inList(content, id))
			return false;
		var chosen = picked();
		for (stat in stats)
			if (contains(chosen, stat))
				return true;
		return false;
	}

	/** The stat values ("crit", …) of a bracelet's bonus lines; null when the item isn't a bracelet. */
	public static function statsOf(slot:Dynamic):Array<String> {
		var list = Tooltips.boxes(slot.binding, slot);
		if (list == null)
			return null;
		for (box in list) {
			if (box == null || box.type != "ItemPartBox")
				continue;
			var lines = bonusLines(box.value);
			if (lines != null)
				return statsIn(lines);
		}
		return null;
	}

	/** ["Bracelet Bonus …", "line<BR>line…"] -> the lines; null for any other part box. */
	static function bonusLines(value:Dynamic):String {
		if (!untyped __is__(value, Array) || value.length < 2 || !Tooltips.isString(value[0]) || !Tooltips.isString(value[1]))
			return null;
		var title:String = value[0];
		if (title.indexOf(BONUS_TITLE) < 0)
			return null;
		var lines:String = value[1];
		if (sample == "")
			sample = Text.trim(Text.stripTags(lines.split("<br>").join("<BR>").split("<BR>")[0]));
		return lines;
	}

	/**
		"Ticked: Domination, Expertise. Bag bracelets read: 3, skipped 0 greyed out and 2 locked. Stats on them: Crit,
		Swiftness. First bonus line read: Swiftness +102"
	**/
	static function scanned():String {
		var read:String = untyped __global__["String"](bracelets);
		var grey:String = untyped __global__["String"](greyed);
		var lock:String = untyped __global__["String"](locked);
		var ticked = names(picked());
		var found = names(seen);
		return "Ticked: " + (ticked == "" ? "nothing" : ticked) + ". Bag bracelets read: " + read + ", skipped " + grey
			+ " greyed out and " + lock + " locked. Stats on them: " + (found == "" ? "none" : found)
			+ (sample == "" ? "" : ". First bonus line read: " + sample);
	}

	/** The stat labels of these values, in the settings' order ("Crit, Swiftness"). */
	static function names(values:Array<Dynamic>):String {
		var found:Array<String> = [];
		for (stat in STATS) {
			var value:String = stat.value;
			var name:String = stat.label;
			if (contains(values, value))
				found.push(name);
		}
		return found.join(", ");
	}

	/** The status line under the window is for testing: shown only while its option is ticked. */
	static function showText():Bool {
		return Options.get(ID, "showText", false) == true;
	}

	/** Every stat named at the start of a line ("Crit +90"); special effects and empty bonuses add nothing. */
	public static function statsIn(lines:String):Array<String> {
		var found:Array<String> = [];
		for (line in lines.split("<br>").join("<BR>").split("<BR>")) {
			var text = Text.trim(Text.stripTags(line));
			for (stat in STATS) {
				var name:String = stat.label;
				var value:String = stat.value;
				if (text.indexOf(name + " +") == 0 && !contains(found, value))
					found.push(value);
			}
		}
		return found;
	}

	/** True while it and every parent up to the stage are shown. */
	static function shown(item:Dynamic):Bool {
		if (item.stage == null)
			return false;
		var at:Dynamic = item;
		while (at != null) {
			if (at.visible != true)
				return false;
			at = at.parent;
		}
		return true;
	}

	static function say(text:String):Void {
		if (!showText())
			text = "";
		if (status != null && status.text != text)
			status.text = text;
	}

	/** The ticked stats as a new array; Domination, Endurance and Expertise until changed. */
	static function picked():Array<Dynamic> {
		var saved:Dynamic = Options.get(ID, KEY, null);
		var values:Array<Dynamic> = [];
		if (saved == null)
			return ["dom", "endur", "exp"];
		var length:Int = saved.length;
		for (i in 0...length)
			values.push(saved[i]);
		return values;
	}

	static function contains(values:Array<Dynamic>, value:String):Bool {
		for (item in values)
			if (item == value)
				return true;
		return false;
	}

	/** A plain label in a game font (a code-made field needs embedFonts and a game font, or it draws boxes). */
	static function label(text:String, font:String, color:Int, width:Float):TextField {
		var format = new TextFormat();
		format.font = font;
		format.size = 14;
		format.color = color;
		var field = new TextField();
		field.embedFonts = true;
		field.defaultTextFormat = format;
		field.selectable = false;
		field.mouseEnabled = false;
		field.width = width;
		field.height = 22;
		field.text = text;
		field.textColor = color;
		return field;
	}

	/** The font of a game label that shows text, else the font the game's labels are made with. */
	static function fontOf(gameLabel:Dynamic):String {
		var field:Dynamic = gameLabel == null ? null : gameLabel.textField;
		if (field != null && field.length > 0) {
			var format:Dynamic = field.getTextFormat(0, 1);
			if (format != null && format.font != null) {
				var font:String = format.font;
				return font;
			}
		}
		return FONT;
	}
}
