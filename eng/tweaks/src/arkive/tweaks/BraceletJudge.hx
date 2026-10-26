package arkive.tweaks;

import flash.text.TextField;
import flash.text.TextFieldAutoSize;
import flash.text.TextFormat;
import flash.text.TextFormatAlign;
import flash.utils.Dictionary;

/**
	Bracelet Reroll Judge. On the bracelet page that asks whether to keep the current effects or take the new ones,
	every effect line ends with what it adds to your Combat Power, the new side's title row shows how your Combat Power
	changes if you take the new lines instead of the current ones, and the side worth more says "Keep" with the
	difference. Advice only: the player still picks.

	The prices come from the Combat Power on Tooltips model (its options and your profile as it last read it): an effect
	is matched against the game's bracelet table by its text; Crit, Specialization, Swiftness and your main stat count
	through your totals. Lines the game's Combat Power ignores (Vitality, Expertise, speed, damage to lower-grade
	monsters) show "no CP". Only what differs is compared: your equipped bracelet (without the current lines when it's
	the one being rerolled) with the new lines against it with the current ones, so the rest of the bracelet cancels out.
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
	static inline var SIDE_WIDTH = 130;
	static inline var MARGIN = 10;
	/** Label colours, the same as Combat Power on Tooltips' lines. */
	static inline var GAIN = 0x7EE08F;
	static inline var DIM = 0xA9A9A9;
	static inline var LOSS = 0xFF8A80;
	/** A line value that needs your profile. */
	static inline var NEEDS_PROFILE = -1000.0;

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
		is redrawn), and writes on the new side's title row how your Combat Power changes if you take it instead of the
		current lines, with "Keep" on the better side.
	**/
	static function judge(page:Dynamic, currentRows:Array<Dynamic>, offeredRows:Array<Dynamic>):Void {
		var before:Dynamic = page.beforeGroup;
		var after:Dynamic = page.afterGroup;
		if (CombatPower.table() == null) {
			note(before, after, "No CP data");
			return;
		}
		if (CombatPower.role == 1) {
			note(before, after, "Support: no CP yet");
			return;
		}
		var old = price(current);
		var now = price(offered);
		var base = baseOf(old.total);
		if (currentRows != null)
			mark(currentRows, old, base);
		mark(offeredRows, now, base);
		if (base == null) {
			// Without your profile there's no gear to price the stat lines against.
			if (firstRoll)
				clear(before);
			else
				note(before, null, "Open your profile");
			note(null, after, "Open your profile");
			return;
		}
		// Only what differs counts: the same bracelet with the new lines against it with the current ones.
		var change = valueOf(base, [now.total]) / valueOf(base, [old.total]) - 1;
		show(before, after, change);
	}

	// ---- Pricing ----

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

	/** The stat your class uses ("Strength" until your armour has shown it). */
	static function mainKey():String {
		return CombatPower.mainStat == "" ? CombatPower.STATS[0] : CombatPower.mainStat;
	}

	/**
		The bracelet the judged lines go on: the one you wear, as Combat Power on Tooltips read it, without the current
		lines when they're on it (you're rerolling that bracelet). For another bracelet the one you wear stands in for its
		other lines. Null until your profile has been read.
	**/
	static function baseOf(old:Dynamic):Dynamic {
		var gear:Array<Dynamic> = CombatPower.gear;
		if (gear == null)
			return null;
		var worn:Dynamic = gear[BRACELET_SLOT];
		var effects:Array<Float> = [];
		var combat:Float = 0;
		var main:Float = 0;
		if (worn != null) {
			var wornEffects:Array<Float> = worn.effects;
			for (factor in wornEffects)
				effects.push(factor);
			combat = worn.combat;
			var stats:Dynamic = worn.stats;
			if (Text.has(stats, mainKey()))
				main = untyped stats[mainKey()];
		}
		var oldEffects:Array<Float> = old.all;
		var oldCombat:Float = old.combat;
		var oldMain:Float = old.main;
		var left:Array<Float> = [];
		for (factor in effects)
			left.push(factor);
		var wearing = oldCombat <= combat + 0.5 && oldMain <= main + 0.5;
		for (factor in oldEffects) {
			var at = left.indexOf(factor);
			if (at < 0) {
				wearing = false;
				break;
			}
			left.splice(at, 1);
		}
		if (wearing)
			return {worn: worn, effects: left, combat: combat - oldCombat, main: main - oldMain};
		return {worn: worn, effects: effects, combat: combat, main: main};
	}

	/** CombatPower.value of your gear with the base bracelet plus these parts. */
	static function valueOf(base:Dynamic, parts:Array<Dynamic>):Float {
		var items = CombatPower.copyOf(CombatPower.gear);
		var worn:Dynamic = base.worn;
		var effects:Array<Float> = [];
		var baseEffects:Array<Float> = base.effects;
		for (factor in baseEffects)
			effects.push(factor);
		var combat:Float = base.combat;
		var main:Float = base.main;
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
		if (main > 0)
			untyped stats[mainKey()] = main;
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
		var gems:Array<Dynamic> = CombatPower.equipped == null ? [] : CombatPower.equipped;
		// When the profile's stat list wasn't found, your stats are taken as your gear's alone (they're nearly all of it).
		var other:Float = CombatPower.otherCombat;
		if (other < 0)
			CombatPower.otherCombat = 0;
		var found = CombatPower.value(items, gems);
		CombatPower.otherCombat = other;
		return found;
	}

	/** What one line adds to the base bracelet; NEEDS_PROFILE for a stat line before your profile was read. */
	static function lineChange(part:Dynamic, base:Dynamic):Float {
		if (base == null) {
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
		return valueOf(base, [part]) / valueOf(base, []) - 1;
	}

	// ---- Drawing ----

	/** Adds each row's value after its text (the game lays the rows out from their text, so nothing overlaps). */
	static function mark(rows:Array<Dynamic>, side:Dynamic, base:Dynamic):Void {
		if (rows == null)
			return;
		var parts:Array<Dynamic> = side.lines;
		for (i in 0...rows.length) {
			var row:Dynamic = rows[i];
			if (row == null || i >= parts.length)
				continue;
			var text = original(row);
			untyped shown[row] = text;
			var value = lineChange(parts[i], base);
			var suffix:String;
			if (value == NEEDS_PROFILE)
				suffix = CombatPower.font(CombatPower.DIM, "(open your profile)");
			else if (Math.abs(value) < SAME)
				suffix = CombatPower.font(CombatPower.DIM, "· no CP");
			else
				suffix = CombatPower.font(value > 0 ? CombatPower.GAIN : CombatPower.LOSS, "· CP " + CombatPower.signed(value * 100, "%"));
			if (Options.get(TWEAK, "showText", false) == true)
				suffix += " " + CombatPower.font(CombatPower.DIM, CombatPower.plain(text));
			row.descStr = text + "  " + suffix;
		}
	}

	/**
		The title rows: "Current" on the current side; on the new side the Combat Power change of taking it ("-4.45 CP
		(-0.25%)"); "Keep" on the side worth more. On a first roll there's nothing current, so the new side shows what
		its lines add.
	**/
	static function show(before:Dynamic, after:Dynamic, change:Float):Void {
		var power:Float = CombatPower.power;
		var gap = power > 0 ? CombatPower.signed(power * Math.abs(change), "") + " CP"
			: CombatPower.signed(Math.abs(change) * 100, "%");
		if (firstRoll)
			clear(before);
		else {
			say(field(before, RIGHT_NAME, TextFormatAlign.RIGHT), "Current", DIM);
			say(field(before, LEFT_NAME, TextFormatAlign.LEFT), change < -SAME ? "Keep " + gap : "", GAIN);
		}
		var text = Math.abs(change) < SAME ? "Same CP" : amount(change, power);
		say(field(after, RIGHT_NAME, TextFormatAlign.RIGHT), text, change > SAME ? GAIN : (change < -SAME ? LOSS : DIM));
		say(field(after, LEFT_NAME, TextFormatAlign.LEFT), change > SAME && !firstRoll ? "Keep " + gap : "", GAIN);
	}

	/** "-4.45 CP (-0.25%)", or "-0.25%" before the profile has shown your Combat Power. */
	static function amount(change:Float, power:Float):String {
		return power > 0 ? CombatPower.signed(power * change, "") + " CP (" + CombatPower.signed(change * 100, "%") + ")"
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
		if (field == null)
			return;
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
		if (group == null)
			return null;
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
