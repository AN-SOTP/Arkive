package arkive.tweaks;

import flash.text.TextField;
import flash.text.TextFieldAutoSize;
import flash.text.TextFormat;
import flash.text.TextFormatAlign;
import flash.utils.Dictionary;

/**
	Bracelet Reroll Judge. On the bracelet page that asks whether to keep the current effects or take the new ones,
	every effect line ends with what it adds to your Combat Power, each side's title row shows its total, and the side
	worth more says "Keep" with the difference. Advice only: the player still picks.

	The prices come from the Combat Power on Tooltips model (its options and your profile as it last read it): an effect
	is matched against the game's bracelet table by its text; Crit, Specialization and Swiftness lines need your
	Character Profile opened once, since what a point is worth depends on your total. The comparison swaps the effects
	on your equipped bracelet (when the rerolled bracelet is the one you wear, its current effects are taken out first).
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
	static inline var SIDE_WIDTH = 150;
	static inline var MARGIN = 10;
	/** Label colours, the same as Combat Power on Tooltips' lines. */
	static inline var GAIN = 0x7EE08F;
	static inline var DIM = 0xA9A9A9;

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
		is redrawn) and writes each side's total and the verdict on its title row.
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
		var old = price(current);
		var rest = restOf(old);
		var now = price(offered);
		var oldTotal = total(old, rest);
		var newTotal = total(now, rest);
		if (currentRows != null)
			mark(currentRows, old, rest);
		mark(offeredRows, now, rest);
		var oldMain:Float = old.main;
		var newMain:Float = now.main;
		var oldCombat:Float = old.combat + oldMain;
		var newCombat:Float = now.combat + newMain;
		var needsProfile = rest == null && (oldCombat > 0 || newCombat > 0);
		if (firstRoll) {
			// Nothing to compare with yet: the new side's total only.
			clear(before);
			show(after, newTotal, -1, needsProfile, newCombat > 0);
			return;
		}
		show(before, oldTotal, newTotal, needsProfile, oldCombat > 0);
		show(after, newTotal, oldTotal, needsProfile, newCombat > 0);
	}

	// ---- Pricing ----

	/**
		The effects on one side: {factors: one per line (0 when the line adds nothing or is a stat line), points: the
		Crit, Specialization and Swiftness on each line, mains: your main stat on each line (Strength for a Strength
		class), all: their factors (non-zero), combat and main: the totals}.
	**/
	static function price(lines:Array<String>):Dynamic {
		var factors:Array<Float> = [];
		var points:Array<Float> = [];
		var mains:Array<Float> = [];
		var all:Array<Float> = [];
		var combat:Float = 0;
		var main:Float = 0;
		for (line in lines) {
			var text = Text.trim(Text.stripTags(line));
			var factor = CombatPower.braceletEffect(text);
			var stat:Float = 0;
			var own:Float = 0;
			if (factor == 0) {
				// An effect the game wrote over several lines is in the table as one text; a stat is on a line of its own.
				for (part in CombatPower.splitLines(line)) {
					var found = CombatPower.combatStat(part);
					var points = mainStat(part);
					if (found > 0)
						stat += found;
					else if (points > 0)
						own += points;
					else
						factor += CombatPower.braceletEffect(part);
				}
			}
			factors.push(factor);
			points.push(stat);
			mains.push(own);
			combat += stat;
			main += own;
			if (factor != 0)
				all.push(factor);
		}
		return {factors: factors, points: points, mains: mains, all: all, combat: combat, main: main};
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

	/**
		Your equipped bracelet without the effects being judged: when the current effects are all on it (you're
		rerolling the bracelet you wear) they're taken out; otherwise only its effects go and its stats stay, standing in
		for the rerolled bracelet's own stats. Null until your profile has been read.
	**/
	static function restOf(old:Dynamic):Dynamic {
		var gear:Array<Dynamic> = CombatPower.gear;
		if (gear == null)
			return null;
		var worn:Dynamic = gear[BRACELET_SLOT];
		var effects:Array<Float> = [];
		var combat:Float = 0;
		var mainOff:Float = 0;
		if (worn != null) {
			var wornEffects:Array<Float> = worn.effects;
			var wornCombat:Float = worn.combat;
			var left:Array<Float> = [];
			for (factor in wornEffects)
				left.push(factor);
			var oldEffects:Array<Float> = old.all;
			var oldCombat:Float = old.combat;
			var wearing = oldCombat <= wornCombat;
			for (factor in oldEffects) {
				var at = left.indexOf(factor);
				if (at < 0) {
					wearing = false;
					break;
				}
				left.splice(at, 1);
			}
			var oldMain:Float = old.main;
			if (wearing && (oldEffects.length > 0 || oldCombat > 0 || oldMain > 0)) {
				effects = left;
				combat = wornCombat - oldCombat;
				// Combat Power on Tooltips counts a bracelet's bonus main stat in its stats; take the judged one out.
				mainOff = oldMain;
			} else {
				combat = wornCombat;
			}
		}
		return {worn: worn, effects: effects, combat: combat, mainOff: mainOff};
	}

	/** How much the side adds to your Combat Power (0.021 for +2.1%), or -1 when it can't be priced. */
	static function total(side:Dynamic, rest:Dynamic):Float {
		var all:Array<Float> = side.all;
		var combat:Float = side.combat;
		var main:Float = side.main;
		return change(all, combat, main, rest);
	}

	/** The change from adding these effect factors, stat points and main stat to the bracelet; -1 when stats need the profile. */
	static function change(factors:Array<Float>, points:Float, main:Float, rest:Dynamic):Float {
		if (rest == null) {
			if (points > 0 || main > 0)
				return -1;
			var product:Float = 1;
			for (factor in factors)
				product *= 1 + factor;
			return product - 1;
		}
		var base = valueWith(rest, [], 0, 0);
		return base <= 0 ? -1 : valueWith(rest, factors, points, main) / base - 1;
	}

	/** CombatPower.value with your equipped bracelet replaced by the rest plus these effects, points and main stat. */
	static function valueWith(rest:Dynamic, factors:Array<Float>, points:Float, main:Float):Float {
		var items = CombatPower.copyOf(CombatPower.gear);
		var worn:Dynamic = rest.worn;
		var restEffects:Array<Float> = rest.effects;
		var restCombat:Float = rest.combat;
		var effects:Array<Float> = [];
		for (factor in restEffects)
			effects.push(factor);
		for (factor in factors)
			effects.push(factor);
		// The worn bracelet's main stat (with its bonus lines, less the judged ones when it's the rerolled bracelet) plus
		// the main stat on the judged lines.
		var stats:Dynamic = {};
		var wornStats:Dynamic = worn == null ? null : worn.stats;
		var key:String = CombatPower.mainStat;
		for (stat in CombatPower.STATS)
			if (Text.has(wornStats, stat)) {
				untyped stats[stat] = wornStats[stat];
				if (key == "")
					key = stat;
			}
		var mainOff:Float = rest.mainOff;
		var added = main - mainOff;
		if (added != 0) {
			if (key == "")
				key = CombatPower.STATS[0];
			var had:Float = 0;
			if (Text.has(stats, key))
				had = untyped stats[key];
			untyped stats[key] = Math.max(0, had + added);
		}
		items[BRACELET_SLOT] = {
			slot: "bracelet",
			stats: stats,
			weapon: worn == null ? 0.0 : worn.weapon,
			weaponPct: worn == null ? 0.0 : worn.weaponPct,
			flat: worn == null ? 0.0 : worn.flat,
			basicPct: worn == null ? 0.0 : worn.basicPct,
			quality: -1,
			refine: [],
			effects: effects,
			combat: restCombat + points,
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

	// ---- Drawing ----

	/** Adds each row's value after its text (the game lays the rows out from their text, so nothing overlaps). */
	static function mark(rows:Array<Dynamic>, side:Dynamic, rest:Dynamic):Void {
		if (rows == null)
			return;
		var factors:Array<Float> = side.factors;
		var points:Array<Float> = side.points;
		var mains:Array<Float> = side.mains;
		for (i in 0...rows.length) {
			var row:Dynamic = rows[i];
			if (row == null || i >= factors.length)
				continue;
			var text = original(row);
			untyped shown[row] = text;
			var value = change(factors[i] == 0 ? [] : [factors[i]], points[i], mains[i], rest);
			var suffix:String;
			if (value < 0 && (points[i] > 0 || mains[i] > 0))
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

	/** One side's title row: its total at the right and, on the better side, "Keep" and by how much at the left. */
	static function show(group:Dynamic, mine:Float, other:Float, needsProfile:Bool, hasStats:Bool):Void {
		if (group == null)
			return;
		var right = field(group, RIGHT_NAME, TextFormatAlign.RIGHT);
		var left = field(group, LEFT_NAME, TextFormatAlign.LEFT);
		if (right == null || left == null)
			return;
		if (mine < 0) {
			say(right, hasStats ? "Open your profile" : "Not priced", DIM);
			say(left, "", DIM);
			return;
		}
		var power:Float = CombatPower.power;
		var amount = power > 0 ? "CP " + CombatPower.signed(power * mine, "") + " (" + CombatPower.signed(mine * 100, "%") + ")"
			: "CP " + CombatPower.signed(mine * 100, "%");
		say(right, amount, mine > SAME ? GAIN : DIM);
		if (needsProfile || other < 0) {
			say(left, "", DIM);
		} else if (Math.abs(mine - other) < SAME) {
			say(left, "Same", DIM);
		} else if (mine > other) {
			// Both totals are changes against the same bracelet, so their ratio is the gain of keeping this side.
			var better = (1 + mine) / (1 + other) - 1;
			var by = power > 0 ? CombatPower.signed(power * (1 + other) * better, "") : CombatPower.signed(better * 100, "%");
			say(left, "Keep " + by, GAIN);
		} else {
			say(left, "", DIM);
		}
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
		var shown:Dynamic = like == null || like.length < 1 ? null : like.getTextFormat(0, 1);
		var format = new TextFormat();
		format.font = shown != null && shown.font != null ? shown.font : "$YG760";
		format.size = 13;
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
