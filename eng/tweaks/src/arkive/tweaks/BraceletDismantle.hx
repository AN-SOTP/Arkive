package arkive.tweaks;

import flash.text.TextField;
import flash.text.TextFormat;
import scaleform.clik.events.ButtonEvent;

/**
	Bracelet Dismantle by Stat. The bracelet tab of the auto-dismantle settings gets six stat tick boxes. Each time the
	Dismantle window opens with the bag beside it, every bag bracelet whose stats are all ticked goes into the dismantle
	list, the same way a right-click puts it there. The player checks the list and presses Dismantle.

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

	/** The current opening of the window: shown, frames left to wait, next bag slot, bindIDs added, count added. */
	static var open = false;
	static var wait = 0;
	static var next = 0;
	static var added:Dynamic = {};
	static var count = 0;

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
		var heading = label("Add to the dismantle list when a bracelet only has (Arkive)", fontOf(below), 0xFFFFFF, 420);
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
			status = label("", fontOf(self.info_lb), 0xD9B26B, 360);
			status.autoSize = flash.text.TextFieldAutoSize.CENTER;
			status.x = button.x + button.width / 2 - 180;
			status.y = button.y - 24;
			button.parent.addChild(status);
			Options.own(ID, status);
		}
		self.addEventListener("enterFrame", function(event:Dynamic) {
			// Runs outside the hook's guard: anything unexpected stops this opening, never the window.
			try {
				onFrame(self);
			} catch (error:Dynamic) {
				open = true;
				wait = 0;
				next = 1 << 30;
			}
		});
	}

	static function onFrame(content:Dynamic):Void {
		if (!Options.on(ID) || !shown(content) || bag == null || !shown(bag)) {
			open = false;
			return;
		}
		if (!open) {
			open = true;
			wait = SETTLE_FRAMES;
			next = 0;
			added = {};
			count = 0;
			say("");
			return;
		}
		if (wait > 0) {
			wait--;
			return;
		}
		var slots:Dynamic = bag.mainBag;
		if (slots == null || slots.visible != true)
			return;
		var total:Int = slots.numItems;
		if (next >= total)
			return;
		var end = next + SLOTS_PER_FRAME;
		while (next < total && next < end) {
			var slot:Dynamic = slots.getItemSlot(next);
			next++;
			if (wanted(slot)) {
				var id:String = slot.bindID;
				untyped added[id] = true;
				count++;
				flash.external.ExternalInterface.call("ARKSlotMouseRightClick", slot);
				break;
			}
		}
		if (next >= total) {
			// A typed String: "text" + an Int could go through Std.string, which needs Boot's patches.
			var number:String = untyped __global__["String"](count);
			say(count == 0 ? "Arkive: no bracelets matched your stat picks" : "Arkive added " + number + (count == 1 ? " bracelet" : " bracelets")
				+ " by stat. Check the list before you dismantle.");
		}
	}

	/** A bag bracelet that isn't greyed out, locked or already added, whose stats are all ticked. */
	static function wanted(slot:Dynamic):Bool {
		if (slot == null || slot.data == null || slot.enabled != true)
			return false;
		var id:Dynamic = slot.bindID;
		if (!Tooltips.isString(id) || id == "" || Text.has(added, id))
			return false;
		var lock:Dynamic = slot.lock;
		if (lock != null && lock > 0)
			return false;
		var stats = statsOf(slot);
		if (stats == null || stats.length == 0)
			return false;
		var chosen = picked();
		for (stat in stats)
			if (!contains(chosen, stat))
				return false;
		return true;
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
		return lines;
	}

	/** Every stat named at the start of a line ("Crit +90"); special effects and empty bonuses add nothing. */
	public static function statsIn(lines:String):Array<String> {
		var found:Array<String> = [];
		for (line in lines.split("<BR>")) {
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
