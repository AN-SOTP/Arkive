package arkive.tweaks;

import flash.text.TextField;
import flash.text.TextFieldAutoSize;
import flash.text.TextFormat;
import flash.text.TextFormatAlign;
import flash.utils.Dictionary;

/**
	Bracelet Reroll Judge. On the bracelet page that asks whether to keep the current effects or take the new ones,
	every effect line ends with what it adds to your Combat Power, each side's title row shows how your Combat Power
	would change with the whole bracelet (its other lines plus that side's) instead of the bracelet you wear, and the
	side worth more says "Keep" with the difference. Advice only: the player still picks.

	The prices come from the Combat Power on Tooltips model (its options and your profile as it last read it): an effect
	is matched against the game's bracelet table by its text; Crit, Specialization, Swiftness and your main stat count
	through your totals. Lines the game's Combat Power ignores (Vitality, Expertise, speed, damage to lower-grade
	monsters) show "no CP". The bracelet's other lines are read from the window's fixed and changeable lists.
**/
@:keep
@:access(arkive.tweaks.CombatPower)
class BraceletJudge {
	static inline var TWEAK = "braceletjudge";
	static inline var BRACELET_SLOT = 11;
	/** Sides closer than this (0.005%) count as the same. */
	static inline var SAME = 0.00005;
	static inline var LEFT_NAME = "arkiveJudgeVerdict";
	static inline var RIGHT_NAME = "arkiveJudgeTotal";
	static inline var SIDE_WIDTH = 160;
	static inline var MARGIN = 10;
	/** Label colours, the same as Combat Power on Tooltips' lines. */
	static inline var GAIN = 0x7EE08F;
	static inline var DIM = 0xA9A9A9;
	static inline var LOSS = 0xFF8A80;
	/** A line value that needs your profile, and a side with nothing to compare with. */
	static inline var NEEDS_PROFILE = -1000.0;
	static inline var NOTHING = -1000.0;

	/** The current and new effect texts as the game sent them (before Arkive added its values). */
	static var current:Array<String> = [];
	static var offered:Array<String> = [];
	/** Effect rows already given a value, and their text before it. */
	static var shown:Dictionary = new Dictionary(true);
	/** True while the bracelet has no effects of its own yet: the current side only holds placeholder rows. */
	static var firstRoll = false;

	/** ItemBEChoiceResultPage.setData, before the game draws the page: both sides. */
	public static function onSetData(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var data:Dynamic = args[0];
		if (data == null)
			return null;
		firstRoll = Text.has(data, "isOnlyOne") && data.isOnlyOne == true;
		current = firstRoll ? [] : texts(listOf(data, "originEnchantList"));
		offered = texts(listOf(data, "newEnchantList"));
		judge(self, firstRoll ? null : listOf(data, "originEnchantList"), listOf(data, "newEnchantList"));
		return null;
	}

	/** ItemBEChoiceResultPage.updateNewResultData, before the game draws it: a new roll; the current side stays. */
	public static function onUpdateNewResult(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var data:Dynamic = args[0];
		if (data == null)
			return null;
		offered = texts(listOf(data, "newEnchantList"));
		judge(self, null, listOf(data, "newEnchantList"));
		return null;
	}

	static function listOf(data:Dynamic, field:String):Array<Dynamic> {
		if (!Text.has(data, field))
			return null;
		var list:Dynamic = untyped data[field];
		return list != null && (untyped __is__(list, Array)) ? list : null;
	}

	/** Each row's effect text, without a value Arkive added earlier. */
	static function texts(rows:Array<Dynamic>):Array<String> {
		var found:Array<String> = [];
		if (rows == null)
			return found;
		for (row in rows)
			found.push(original(row));
		return found;
	}

	static function original(row:Dynamic):String {
		if (row == null)
			return "";
		var kept:Dynamic = untyped shown[row];
		if (kept != null)
			return kept;
		var text:Dynamic = Text.has(row, "descStr") ? row.descStr : null;
		return text == null ? "" : text;
	}

	/**
		Prices both sides, adds a value to each row the game is about to draw (`currentRows` null when only the new side
		is redrawn) and writes on each side's title row how the whole bracelet with that side compares with the bracelet
		you wear, and "Keep" on the better side.
	**/
	static function judge(page:Dynamic, currentRows:Array<Dynamic>, offeredRows:Array<Dynamic>):Void {
		var before:Dynamic = page.beforeGroup;
		var after:Dynamic = page.afterGroup;
		if (CombatPower.table() == null) {
			note(before, after, "Combat Power: no game data");
			return;
		}
		if (CombatPower.role == 1) {
			note(before, after, "Support: not priced yet");
			return;
		}
		var others = otherLines(page, current);
		var rest = price(others);
		var old = price(current);
		var now = price(offered);
		if (currentRows != null)
			mark(currentRows, old, rest, others);
		mark(offeredRows, now, rest, others);
		if (CombatPower.gear == null) {
			// Without your profile there is no equipped bracelet to compare with.
			if (firstRoll)
				clear(before);
			else
				note(before, null, "Open your profile");
			note(null, after, "Open your profile");
			return;
		}
		var newWhole = whole([rest.total, now.total]);
		if (firstRoll) {
			// Nothing to keep yet: the new side only.
			clear(before);
			show(after, newWhole, NOTHING);
			return;
		}
		var oldWhole = whole([rest.total, old.total]);
		show(before, oldWhole, newWhole);
		show(after, newWhole, oldWhole);
	}

	// ---- Pricing ----

	/**
		The bracelet's lines that the reroll leaves alone: the window's fixed and changeable lists (locked lines stay)
		without the current side's lines.
	**/
	static function otherLines(page:Dynamic, judged:Array<String>):Array<String> {
		var window:Dynamic = GameUi.ancestor(page, "ark.ui.itemBraceletEnchant.ItemBEWndContent");
		var lines:Array<String> = [];
		if (window != null) {
			addRows(lines, window.fixAttributeList);
			addRows(lines, window.fluidAttributeList);
		}
		var plains:Array<String> = [];
		for (line in lines)
			plains.push(CombatPower.plain(line));
		for (line in judged) {
			var at = plains.indexOf(CombatPower.plain(line));
			if (at >= 0) {
				plains.splice(at, 1);
				lines.splice(at, 1);
			}
		}
		return lines;
	}

	static function addRows(into:Array<String>, list:Dynamic):Void {
		var data:Dynamic = list == null ? null : list.dataProvider;
		if (data == null)
			return;
		var count:Int = data.length;
		for (i in 0...count) {
			var text = original(data[i]);
			if (text != "")
				into.push(text);
		}
	}

	/**
		Each line's part ({all: its effect factors, combat: Crit + Specialization + Swiftness, main: your main stat}) and
		their total: {lines, total}.
	**/
	static function price(lines:Array<String>):Dynamic {
		var parts:Array<Dynamic> = [];
		var all:Array<Float> = [];
		var combat:Float = 0;
		var main:Float = 0;
		for (line in lines) {
			var part = partOf(line);
			parts.push(part);
			var factors:Array<Float> = part.all;
			for (factor in factors)
				all.push(factor);
			var points:Float = part.combat;
			var own:Float = part.main;
			combat += points;
			main += own;
		}
		return {lines: parts, total: {all: all, combat: combat, main: main}};
	}

	static function partOf(line:String):Dynamic {
		var text = Text.trim(Text.stripTags(line));
		var factor = CombatPower.braceletEffect(text);
		var stat:Float = 0;
		var own:Float = 0;
		if (factor == 0) {
			// An effect the game wrote over several lines is in the table as one text; a stat is on a line of its own.
			for (piece in CombatPower.splitLines(line)) {
				var found = CombatPower.combatStat(piece);
				var points = mainStat(piece);
				if (found > 0)
					stat += found;
				else if (points > 0)
					own += points;
				else
					factor += CombatPower.braceletEffect(piece);
			}
		}
		var factors:Array<Float> = [];
		if (factor != 0)
			factors.push(factor);
		return {all: factors, combat: stat, main: own};
	}

	/**
		Your main stat on a line ("Strength +12032" for a Strength class), or 0. Another class's stat adds nothing; until
		your armour has shown which stat is yours, any of the three counts.
	**/
	static function mainStat(line:String):Float {
		for (stat in CombatPower.STATS) {
			var start = stat + " +";
			if (StringTools.startsWith(line, start) && line.indexOf("%") < 0
				&& (CombatPower.mainStat == "" || CombatPower.mainStat == stat))
				return CombatPower.number(line.substr(start.length));
		}
		return 0;
	}

	/** How your Combat Power changes (0.012 for +1.2%) with a bracelet made of these parts instead of the one you wear. */
	static function whole(parts:Array<Dynamic>):Float {
		var base = measure(CombatPower.copyOf(CombatPower.gear));
		return base <= 0 ? 0 : measure(withBracelet(parts)) / base - 1;
	}

	/** What one line adds to a bracelet that has the other lines (`rest`); NEEDS_PROFILE for a stat line without it. */
	static function lineChange(part:Dynamic, rest:Dynamic):Float {
		if (CombatPower.gear == null) {
			var points:Float = part.combat;
			var main:Float = part.main;
			if (points > 0 || main > 0)
				return NEEDS_PROFILE;
			var product:Float = 1;
			var factors:Array<Float> = part.all;
			for (factor in factors)
				product *= 1 + factor;
			return product - 1;
		}
		var base = measure(withBracelet([rest]));
		return base <= 0 ? 0 : measure(withBracelet([rest, part])) / base - 1;
	}

	/**
		Your equipment with a bracelet made of these parts. What a bracelet has besides its lines (its Ark Passive points)
		is taken from the one you wear.
	**/
	static function withBracelet(parts:Array<Dynamic>):Array<Dynamic> {
		var items = CombatPower.copyOf(CombatPower.gear);
		var worn:Dynamic = items[BRACELET_SLOT];
		var effects:Array<Float> = [];
		var combat:Float = 0;
		var main:Float = 0;
		for (part in parts) {
			var factors:Array<Float> = part.all;
			for (factor in factors)
				effects.push(factor);
			var points:Float = part.combat;
			var own:Float = part.main;
			combat += points;
			main += own;
		}
		var stats:Dynamic = {};
		if (main > 0) {
			var key:String = CombatPower.mainStat == "" ? CombatPower.STATS[0] : CombatPower.mainStat;
			untyped stats[key] = main;
		}
		items[BRACELET_SLOT] = {
			slot: "bracelet",
			stats: stats,
			weapon: 0.0,
			weaponPct: 0.0,
			flat: 0.0,
			basicPct: 0.0,
			quality: -1,
			refine: [],
			effects: effects,
			combat: combat,
			enlightenment: worn == null ? 0 : worn.enlightenment,
			leap: worn == null ? 0 : worn.leap,
			stone: {}
		};
		return items;
	}

	/** CombatPower.value of this equipment with your gems. */
	static function measure(items:Array<Dynamic>):Float {
		var gems:Array<Dynamic> = CombatPower.equipped == null ? [] : CombatPower.equipped;
		// When the profile's stat list wasn't found, your stats are taken as your gear's alone (they're nearly all of it).
		var other:Float = CombatPower.otherCombat;
		if (other < 0)
			CombatPower.otherCombat = 0;
		var found = CombatPower.value(items, gems);
		CombatPower.otherCombat = other;
		return found;
	}

	// ---- Drawing ----

	/** Adds each row's value after its text (the game lays the rows out from their text, so nothing overlaps). */
	static function mark(rows:Array<Dynamic>, side:Dynamic, rest:Dynamic, others:Array<String>):Void {
		if (rows == null)
			return;
		var parts:Array<Dynamic> = side.lines;
		var restTotal:Dynamic = rest.total;
		for (i in 0...rows.length) {
			var row:Dynamic = rows[i];
			if (row == null || i >= parts.length)
				continue;
			var text = original(row);
			untyped shown[row] = text;
			var part:Dynamic = parts[i];
			var value = lineChange(part, restTotal);
			var suffix:String;
			if (value == NEEDS_PROFILE)
				suffix = CombatPower.font(CombatPower.DIM, "(open your profile)");
			else if (Math.abs(value) < SAME)
				suffix = CombatPower.font(CombatPower.DIM, "· no CP");
			else
				suffix = CombatPower.font(value > 0 ? CombatPower.GAIN : CombatPower.LOSS, "· CP " + CombatPower.signed(value * 100, "%"));
			if (Options.get(TWEAK, "showText", false) == true) {
				suffix += " " + CombatPower.font(CombatPower.DIM, CombatPower.plain(text));
				if (i == 0)
					suffix += " " + CombatPower.font(CombatPower.DIM, "[other lines: " + others.join(" / ") + "]");
			}
			row.descStr = text + "  " + suffix;
		}
	}

	/**
		One side's title row: at the right, your Combat Power change with this bracelet instead of the one you wear; at the
		left of the better side, "Keep" and by how much (`other` NOTHING when there's nothing to compare with).
	**/
	static function show(group:Dynamic, mine:Float, other:Float):Void {
		if (group == null)
			return;
		var right = field(group, RIGHT_NAME, TextFormatAlign.RIGHT);
		var left = field(group, LEFT_NAME, TextFormatAlign.LEFT);
		if (right == null || left == null)
			return;
		var power:Float = CombatPower.power;
		if (Math.abs(mine) < SAME)
			say(right, "Same as equipped", DIM);
		else
			say(right, amount(mine, power) + " vs equipped", mine > 0 ? GAIN : LOSS);
		if (other == NOTHING) {
			say(left, "", DIM);
		} else if (Math.abs(mine - other) < SAME) {
			say(left, "Same", DIM);
		} else if (mine > other) {
			// Both are changes from the same equipment, so their difference is what keeping this side is worth.
			say(left, "Keep " + (power > 0 ? CombatPower.signed(power * (mine - other), "") + " CP"
				: CombatPower.signed((mine - other) * 100, "%")), GAIN);
		} else {
			say(left, "", DIM);
		}
	}

	/** "+12.34 (+0.53%)", or "+0.53%" before the profile has shown your Combat Power. */
	static function amount(change:Float, power:Float):String {
		return power > 0 ? CombatPower.signed(power * change, "") + " (" + CombatPower.signed(change * 100, "%") + ")"
			: CombatPower.signed(change * 100, "%");
	}

	static function note(before:Dynamic, after:Dynamic, text:String):Void {
		for (group in [before, after]) {
			if (group == null)
				continue;
			var right = field(group, RIGHT_NAME, TextFormatAlign.RIGHT);
			var left = field(group, LEFT_NAME, TextFormatAlign.LEFT);
			if (right != null)
				say(right, text, DIM);
			if (left != null)
				say(left, "", DIM);
		}
	}

	/**
		Plain text in one colour. htmlText with a FONT tag drops a code-made field's font in the game's player (the colour
		shows, the letters are boxes), so labels use text and textColor.
	**/
	static function say(field:TextField, text:String, color:Int):Void {
		field.text = text;
		field.textColor = color;
	}

	static function clear(group:Dynamic):Void {
		if (group == null)
			return;
		for (name in [RIGHT_NAME, LEFT_NAME]) {
			var made:Dynamic = group.getChildByName(name);
			if (made != null)
				made.text = "";
		}
	}

	/** Arkive's own text on the group's title row (the game's title stays as it is), made the first time. */
	static function field(group:Dynamic, name:String, align:TextFormatAlign):TextField {
		var title:Dynamic = group.titleTextField;
		if (title == null)
			return null;
		var made:Dynamic = group.getChildByName(name);
		var text:TextField = made;
		if (text == null) {
			text = new TextField();
			text.name = name;
			text.selectable = false;
			text.mouseEnabled = false;
			text.multiline = false;
			text.wordWrap = false;
			text.autoSize = TextFieldAutoSize.NONE;
			text.width = SIDE_WIDTH;
			text.height = 20;
			var titleX:Float = title.x;
			var titleY:Float = title.y;
			var titleWidth:Float = title.width;
			var titleHeight:Float = title.height;
			text.x = align == TextFormatAlign.RIGHT ? titleX + titleWidth - MARGIN - SIDE_WIDTH : titleX + MARGIN;
			text.y = titleY + (titleHeight - 20) / 2;
			group.addChild(text);
			Options.own(TWEAK, text);
		}
		// The font of a field the game filled: the page's cost title ($YG760, there from the start), else an effect row.
		var page:Dynamic = group.parent;
		var like:Dynamic = page != null && (untyped __in__("costTitleTextField", page)) ? page.costTitleTextField : null;
		if (like == null)
			like = rowField(group);
		var sample:Dynamic = like == null || like.length < 1 ? null : like.getTextFormat(0, 1);
		var format = new TextFormat();
		format.font = sample != null && sample.font != null ? sample.font : "$YG760";
		format.size = 12;
		format.align = align;
		text.embedFonts = true;
		text.defaultTextFormat = format;
		return text;
	}

	/** The text field of the group's first effect row, or null. */
	static function rowField(group:Dynamic):Dynamic {
		var view:Dynamic = group.resultImageView;
		var content:Dynamic = view == null ? null : view.content;
		if (content == null || !(untyped __is__(content, flash.display.DisplayObjectContainer)))
			return null;
		var count:Int = content.numChildren;
		for (i in 0...count) {
			var child:Dynamic = content.getChildAt(i);
			if (child != null && (untyped __in__("textField", child)) && child.textField != null)
				return child.textField;
		}
		return null;
	}
}
