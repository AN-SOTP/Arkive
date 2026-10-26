package arkive.tweaks;

import flash.events.Event;
import flash.filters.GlowFilter;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.text.TextField;
import flash.text.TextFormat;

/**
	Astrogem Processing Advisor. In the astrogem processing panel, a box beside the gem's four levels shows the chance
	to reach your target and the gold it takes from here if you process the four offered effects, reroll them or reset
	the gem, and suggests the best of these (or to stop when the target is reached or out of reach). Advice only.

	Arkive works the odds out from the game's tables when it installs the tweak (AstrogemOdds.cs) and writes one table
	file per target; "tables" maps target ids to those files, "target" is the one picked. The panel reads the gem's
	levels (Willpower Efficiency, Points, first and second option, in the order the panel lists them), the attempts,
	rerolls and resets left from its buttons, the cost from its cost label, and works out what each offered effect does
	from its text: an effect naming one of the four rows changes that row (by its number; no number means the option
	changes), a percentage is a cost change, another number extra rerolls, and anything else keeps the gem as it is.
	Plus and minus come from the sign or, failing that, the game's green and red.
**/
@:keep
class Astrogem {
	static inline var TWEAK = "astrogem";
	static inline var GOOD = "#7EE08F";
	static inline var BAD = "#FF8A80";
	static inline var DIM = "#A9A9A9";
	static inline var PLUS_COLOR = "b7fb00";
	static inline var MINUS_COLOR = "e73517";
	static inline var LEVELS = 5;
	static inline var STATES = 625;
	static inline var DIGITS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_";
	static inline var CHANCE_SCALE = 4095.0;
	static inline var GOLD_SCALE = 200.0;
	/** Where the box sits in the panel: right of the gem's four level rows (which end at x 558). */
	static inline var BOX_X = 566;
	static inline var BOX_Y = 200;
	static inline var BOX_WIDTH = 150;
	static inline var BOX_HEIGHT = 250;
	static inline var EVERY = 6;
	static inline var BR = "<br>";

	static var panel:Dynamic;
	static var box:TextField;
	static var fontSet = false;
	static var frames = 0;
	static var shown = "";
	static var listening = false;

	/** The table being shown and the one loading. */
	static var tableFile = "";
	static var loadingFile = "";
	static var table:Dynamic;

	/** ArkGridGemProgressPanel.configUI (after): the box, and a check a few times a second while the panel shows. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		panel = self;
		box = field(BOX_WIDTH, BOX_HEIGHT);
		fontSet = false;
		box.x = BOX_X;
		box.y = BOX_Y;
		self.addChild(box);
		Options.own(TWEAK, box);
		self.addEventListener("enterFrame", tick);
		if (!listening) {
			listening = true;
			Options.listen(function(tweak:String, key:String) {
				shown = "";
			});
		}
	}

	static function tick(event:Dynamic):Void {
		try {
			frames++;
			if (frames % EVERY != 0 || panel == null || box == null)
				return;
			if (!Options.on(TWEAK) || !panel.visible)
				return;
			var file = tableFor(Options.get(TWEAK, "target", ""));
			if (file == "") {
				show("<font color='" + DIM + "'>Arkive: no odds tables. Install the tweak again from Arkive.</font>", null);
				return;
			}
			if (file != tableFile) {
				load(file);
				if (table == null || tableFile != file) {
					show("<font color='" + DIM + "'>Arkive: reading the odds…</font>", null);
					return;
				}
			}
			var state = read();
			var key:String = state == null ? "" : state.key;
			var data = Options.get(TWEAK, "showData", false) == true;
			if (key == shown && !data)
				return;
			shown = key;
			show(advice(state), data ? describe(state) : null);
		} catch (error:Dynamic) {}
	}

	// ---- The table ----

	static function tableFor(target:Dynamic):String {
		var tables:Dynamic = Options.get(TWEAK, "tables", null);
		if (tables == null)
			return "";
		var id:String = target == null ? "" : target;
		if (!Text.has(tables, id)) {
			var ids:Array<Dynamic> = untyped __keys__(tables);
			if (ids.length == 0)
				return "";
			id = ids[0];
		}
		var file:String = untyped tables[id];
		return file;
	}

	static function load(file:String):Void {
		if (file == "" || file == loadingFile)
			return;
		loadingFile = file;
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) {
			try {
				var text:String = loader.data;
				var parsed = parse(text);
				if (parsed != null && loadingFile == file) {
					table = parsed;
					tableFile = file;
					shown = "";
				}
			} catch (error:Dynamic) {}
		});
		loader.addEventListener("ioError", function(e:Dynamic) {});
		loader.addEventListener("securityError", function(e:Dynamic) {});
		loader.load(new URLRequest(file));
	}

	/** The table file (AstrogemOdds.Encode): its header lines and the text of each state table. */
	static function parse(text:String):Dynamic {
		if (text == null || text.indexOf("arkive-astrogem 1") != 0)
			return null;
		var result:Dynamic = {grades: [], none: ""};
		var lines = text.split("\n");
		for (line in lines) {
			var parts = line.split(" ");
			var kind = parts[0];
			if (kind == "target") {
				result.name = parts[1];
				result.willpower = whole(parts[2]);
				result.points = whole(parts[3]);
				result.total = whole(parts[4]);
			} else if (kind == "rules") {
				result.cap = whole(parts[2]);
				result.costStates = whole(parts[3]);
				result.costLow = whole(parts[4]);
				result.costStep = whole(parts[5]);
			} else if (kind == "grade") {
				var grade:Dynamic = {
					attempts: whole(parts[1]),
					rerolls: whole(parts[2]),
					resets: whole(parts[3]),
					cost: whole(parts[4]),
					freshChance: decimal(parts[5]),
					freshGold: decimal(parts[6]),
					data: ""
				};
				var grades:Array<Dynamic> = result.grades;
				grades.push(grade);
			} else if (kind == "table") {
				var data:String = parts.length > 2 ? parts[2] : "";
				if (parts[1] == "none")
					result.none = data;
				else {
					var grades:Array<Dynamic> = result.grades;
					var attempts = whole(parts[1]);
					for (grade in grades)
						if (grade.attempts == attempts)
							grade.data = data;
				}
			}
		}
		return result.cap == null ? null : result;
	}

	/** Chance and gold (in attempts at the base cost) of a state before its offer is seen. */
	static function lookup(data:String, attempts:Int, rerolls:Int, cost:Int, levels:Array<Int>):Array<Float> {
		var cap:Int = table.cap;
		var costStates:Int = table.costStates;
		if (rerolls > cap)
			rerolls = cap;
		var index = ((attempts * (cap + 1) + rerolls) * costStates + cost) * STATES + (levels[0] - 1) + LEVELS * (levels[1] - 1)
			+ 25 * (levels[2] - 1) + 125 * (levels[3] - 1);
		var at = index * 4;
		if (data == null || at + 4 > data.length)
			return [0.0, 0.0];
		return [pair(data, at) / CHANCE_SCALE, pair(data, at + 2) / GOLD_SCALE];
	}

	static function pair(data:String, at:Int):Int {
		return DIGITS.indexOf(data.charAt(at)) * 64 + DIGITS.indexOf(data.charAt(at + 1));
	}

	static function reached(levels:Array<Int>):Bool {
		var willpower:Int = table.willpower;
		var points:Int = table.points;
		var total:Int = table.total;
		return levels[0] >= willpower && levels[1] >= points && levels[0] + levels[1] + levels[2] + levels[3] >= total;
	}

	// ---- The panel ----

	/**
		The gem as the panel shows it: levels, attempts left and in all, rerolls and resets left, cost now, the four
		offered effects, and a key that changes whenever any of them does.
	**/
	static function read():Dynamic {
		var rows = items(panel.progressGemOptionList);
		var offers = items(panel.progressGemChoiceOptionList);
		if (rows.length < 4)
			return null;
		var levels:Array<Int> = [];
		var names:Array<String> = [];
		var key = "";
		for (i in ordered(rows)) {
			var html:String = text(rows[i], "optionStr");
			key += html + "|";
			var level = lastNumber(Text.stripTags(html));
			levels.push(level < 1 ? 1 : (level > LEVELS ? LEVELS : level));
			names.push(letters(firstLine(Text.stripTags(html))));
		}
		var process = numbers(label(panel.progressGemOkButton));
		var rerolls = numbers(label(panel.progressGemRetryButton));
		var resets = numbers(label(panel.progressGemResetButton));
		var costText = labelText(panel.progressGemCostLabel);
		key += label(panel.progressGemOkButton) + "|" + label(panel.progressGemRetryButton) + "|" + label(panel.progressGemResetButton) + "|" + costText;
		var effects:Array<Dynamic> = [];
		for (offer in offers) {
			var html:String = text(offer, "optionStr");
			key += "|" + html;
			effects.push(effect(html, names));
		}
		return {
			key: key,
			levels: levels,
			left: process.length > 0 ? process[0] : 0,
			all: process.length > 1 ? process[1] : 0,
			rerolls: rerolls.length > 0 ? rerolls[0] : 0,
			resets: resets.length > 0 ? resets[0] : 0,
			cost: lastNumber(Text.stripTags(costText)),
			effects: effects
		};
	}

	/**
		The rows in the order Willpower Efficiency, Points, first option, second option. The panel lists them in that
		order; as a check, the option rows are the two whose value line has a word ("Lv. 2") before the number.
	**/
	static function ordered(rows:Array<Dynamic>):Array<Int> {
		var plain:Array<Int> = [];
		var options:Array<Int> = [];
		for (i in 0...4) {
			var value = Text.stripTags(text(rows[i], "optionStr"));
			var cut = value.indexOf("\n");
			if (cut >= 0)
				value = value.substr(cut + 1);
			if (letters(value).length > 0)
				options.push(i);
			else
				plain.push(i);
		}
		return plain.length == 2 && options.length == 2 ? plain.concat(options) : [0, 1, 2, 3];
	}

	/** What an offered effect does: {stat (0-3, or -1), delta, cost (in percent), rerolls, change}. */
	static function effect(html:String, names:Array<String>):Dynamic {
		var plain = Text.stripTags(html);
		var head = letters(firstLine(plain));
		var number = lastNumber(plain);
		var lower = html.toLowerCase();
		var sign = signOf(plain);
		if (sign == 0)
			sign = lower.indexOf(MINUS_COLOR) >= 0 ? -1 : 1;
		for (i in 0...4) {
			var name = names[i];
			if (name.length > 0 && (head.indexOf(name) == 0 || name.indexOf(head) == 0) && head.length > 0)
				return number < 0 ? {stat: -1, delta: 0, cost: 0, rerolls: 0, change: true} : {stat: i, delta: sign * number, cost: 0, rerolls: 0};
		}
		if (number >= 0 && plain.indexOf("%") >= 0)
			return {stat: -1, delta: 0, cost: sign * number, rerolls: 0};
		if (number > 0)
			return {stat: -1, delta: 0, cost: 0, rerolls: number};
		return {stat: -1, delta: 0, cost: 0, rerolls: 0};
	}

	// ---- The advice ----

	static function advice(state:Dynamic):String {
		if (state == null)
			return "";
		var levels:Array<Int> = state.levels;
		var heading = "<font color='" + DIM + "'>Arkive · " + targetName() + "</font>";
		if (reached(levels))
			return heading + BR + "<font color='" + GOOD + "'>Target reached." + BR + "Suggestion: Processing Complete</font>";
		var all:Int = state.all;
		var left:Int = state.left;
		var grade:Dynamic = gradeFor(all);
		if (grade == null || left < 1)
			return heading + BR + "<font color='" + DIM + "'>No odds for this gem.</font>";
		var fresh = left >= all;
		var resets:Int = state.resets;
		var withReset = resets > 0 && grade.data != "";
		var data:String = withReset ? grade.data : table.none;
		var base:Float = grade.cost;
		var costState = costIndex(state.cost, base);
		var rerolls:Int = state.rerolls;

		// Process: the four offered effects, 25% each.
		var effects:Array<Dynamic> = state.effects;
		var processChance = 0.0;
		var processGold = 0.0;
		var counted = 0;
		for (e in effects) {
			var next = after(e, levels, left, rerolls, costState, data);
			processChance += next[0];
			processGold += next[1];
			counted++;
		}
		var price = 1 + (table.costLow + costState) * table.costStep / 10000.0;
		if (counted > 0) {
			processChance /= counted;
			processGold = price + processGold / counted;
		}
		var lines = heading;
		var best = "Process";
		var bestChance = counted > 0 ? processChance : -1.0;
		var bestGold = processGold;
		if (counted > 0)
			lines += BR + row("Process", processChance, processGold * base);
		if (!fresh && rerolls > 0) {
			var reroll = lookup(data, left, rerolls - 1, costState, levels);
			lines += BR + row("Reroll", reroll[0], reroll[1] * base);
			if (better(reroll[0], reroll[1], bestChance, bestGold)) {
				best = "Reroll";
				bestChance = reroll[0];
				bestGold = reroll[1];
			}
		}
		if (!fresh && withReset) {
			var freshChance:Float = grade.freshChance;
			var freshGold:Float = grade.freshGold;
			lines += BR + row("Reset", freshChance, freshGold * base);
			if (better(freshChance, freshGold, bestChance, bestGold)) {
				best = "Reset";
				bestChance = freshChance;
			}
		}
		if (bestChance <= 0.0005)
			return lines + BR + "<font color='" + BAD + "'>Target out of reach." + BR + "Suggestion: Processing Complete</font>";
		return lines + BR + "<font color='" + GOOD + "'>Suggestion: " + best + "</font>";
	}

	/** Chance and gold after an effect applies (an attempt used), from the table one attempt down. */
	static function after(e:Dynamic, levels:Array<Int>, left:Int, rerolls:Int, cost:Int, data:String):Array<Float> {
		var next = levels.concat([]);
		var stat:Int = e.stat;
		if (stat >= 0) {
			var delta:Int = e.delta;
			var level = next[stat] + delta;
			next[stat] = level < 1 ? 1 : (level > LEVELS ? LEVELS : level);
		}
		if (reached(next))
			return [1.0, 0.0];
		if (left <= 1)
			return [0.0, 0.0];
		var steps:Int = 0;
		var percent:Int = e.cost;
		if (percent != 0) {
			var step:Int = table.costStep;
			steps = Std.int(percent * 100 / step);
		}
		var costStates:Int = table.costStates;
		var nextCost = cost + steps;
		if (nextCost < 0)
			nextCost = 0;
		if (nextCost >= costStates)
			nextCost = costStates - 1;
		var extra:Int = e.rerolls;
		return lookup(data, left - 1, rerolls + extra, nextCost, next);
	}

	static function better(chance:Float, gold:Float, bestChance:Float, bestGold:Float):Bool {
		return chance > bestChance + 0.0005 || (chance > bestChance - 0.0005 && gold < bestGold);
	}

	/** The cost state from the cost shown and the grade's base cost (900 gold shown as 0, 900 or 1,800). */
	static function costIndex(shown:Int, base:Float):Int {
		var low:Int = table.costLow;
		var step:Int = table.costStep;
		var states:Int = table.costStates;
		if (shown < 0 || base <= 0)
			return -low;
		var addend = (shown / base - 1) * 10000;
		var index = Std.int(Math.round(addend / step)) - low;
		return index < 0 ? 0 : (index >= states ? states - 1 : index);
	}

	static function gradeFor(attempts:Int):Dynamic {
		var grades:Array<Dynamic> = table.grades;
		for (grade in grades)
			if (grade.attempts == attempts)
				return grade;
		return null;
	}

	static function targetName():String {
		var willpower:Int = table.willpower;
		var points:Int = table.points;
		var total:Int = table.total;
		var parts:Array<String> = [];
		if (willpower > 1)
			parts.push("Willpower " + willpower + (willpower < LEVELS ? "+" : ""));
		if (points > 1)
			parts.push("Points " + points + (points < LEVELS ? "+" : ""));
		if (total > 4)
			parts.push("total " + total + "+");
		return parts.join(", ");
	}

	static function row(name:String, chance:Float, gold:Float):String {
		return name + ": " + percent(chance) + " <font color='" + DIM + "'>~" + money(gold) + " g</font>";
	}

	static function show(html:String, data:String):Void {
		if (!fontSet)
			fontSet = fontFrom(panel, box);
		box.htmlText = data == null ? html : html + BR + "<font color='" + DIM + "'>" + data + "</font>";
	}

	/**
		showData: what the tweak made of the panel, in a few short lines under the advice: levels (Willpower, Points,
		first and second option), attempts left/in all, rerolls and resets left, cost, and each offer as the stat it
		changes (W, P, O1, O2; "chg" an option change, "$" a cost change, "rr" extra rerolls, "=" no change).
	**/
	static function describe(state:Dynamic):String {
		if (state == null)
			return "read: no rows";
		var levels:Array<Int> = state.levels;
		var left:Int = state.left;
		var all:Int = state.all;
		var rerolls:Int = state.rerolls;
		var resets:Int = state.resets;
		var cost:Int = state.cost;
		var out = "read: " + levels.join(" ") + BR + "left " + left + "/" + all + ", rr " + rerolls + ", reset " + resets + ", cost " + cost;
		var stats = ["W", "P", "O1", "O2"];
		var effects:Array<Dynamic> = state.effects;
		var offers:Array<String> = [];
		for (e in effects) {
			var stat:Int = e.stat;
			var delta:Int = e.delta;
			var percent:Int = e.cost;
			var extra:Int = e.rerolls;
			if (stat >= 0)
				offers.push(stats[stat] + (delta > 0 ? "+" : "") + delta);
			else if (percent != 0)
				offers.push("$" + (percent > 0 ? "+" : "") + percent + "%");
			else if (extra != 0)
				offers.push("rr+" + extra);
			else
				offers.push(e.change == true ? "chg" : "=");
		}
		return out + BR + offers.join(" · ");
	}

	// ---- Reading game objects ----

	static function items(list:Dynamic):Array<Dynamic> {
		var found:Array<Dynamic> = [];
		if (list == null)
			return found;
		var data:Dynamic = list.dataProvider;
		if (data == null)
			return found;
		var count:Int = data.length;
		for (i in 0...count) {
			var item:Dynamic = data[i];
			if (item != null)
				found.push(item);
		}
		return found;
	}

	static function text(item:Dynamic, key:String):String {
		if (!Text.has(item, key))
			return "";
		var value:Dynamic = untyped item[key];
		if (value == null)
			return "";
		var result:String = value;
		return result;
	}

	static function label(button:Dynamic):String {
		if (button == null)
			return "";
		var html:Dynamic = button.labelHtml;
		if (html != null && html != "") {
			var result:String = html;
			return result;
		}
		var plain:Dynamic = button.label;
		if (plain != null && plain != "") {
			var result:String = plain;
			return result;
		}
		var field:Dynamic = button.textField;
		if (field != null) {
			var result:String = field.text;
			return result == null ? "" : result;
		}
		return "";
	}

	static function labelText(label:Dynamic):String {
		if (label == null)
			return "";
		var html:Dynamic = label.htmlText;
		if (html != null) {
			var result:String = html;
			return result;
		}
		var field:Dynamic = label.textField;
		if (field != null) {
			var result:String = field.text;
			return result == null ? "" : result;
		}
		return "";
	}

	/** The box; its font is set from the panel's own text once that shows (see fontFrom). */
	static function field(width:Int, height:Int):TextField {
		var format = new TextFormat();
		format.font = "$YG760";
		format.size = 13;
		var made = new TextField();
		made.embedFonts = true;
		made.defaultTextFormat = format;
		made.filters = [new GlowFilter(0, 1, 2, 2, 4, 1)];
		made.textColor = 0xE6E6E6;
		made.selectable = false;
		made.mouseEnabled = false;
		made.multiline = true;
		made.wordWrap = true;
		made.width = width;
		made.height = height;
		return made;
	}

	/**
		Gives the box the font of a text field in the panel that shows text ("Processing Cost", the gem's name), with
		its embedFonts: a field in a font the movie doesn't carry draws boxes, and an empty field reports no font.
		False until one is found.
	**/
	static function fontFrom(owner:Dynamic, target:TextField):Bool {
		var fields:Array<Dynamic> = [];
		var count:Int = owner.numChildren;
		for (i in 0...count) {
			var child:Dynamic = owner.getChildAt(i);
			if (child != target && untyped __is__(child, TextField))
				fields.push(child);
		}
		for (label in [owner.progressGemNameLabel, owner.progressGemTryCountLabel, owner.progressGemAbilityLabel])
			if (label != null && label.textField != null)
				fields.push(label.textField);
		for (source in fields) {
			var text:String = source.text;
			if (text == null || text.length == 0)
				continue;
			var own:Dynamic = source.getTextFormat(0, 1);
			var name:String = own == null ? null : own.font;
			if (name == null || name == "")
				continue;
			var format = new TextFormat();
			format.font = name;
			format.size = 13;
			target.embedFonts = source.embedFonts;
			target.defaultTextFormat = format;
			return true;
		}
		return false;
	}

	// ---- Text ----

	/** All whole numbers in the text, in order ("Process (7/9)" gives 7 and 9). */
	static function numbers(text:String):Array<Int> {
		var found:Array<Int> = [];
		var value = -1;
		var plain = Text.stripTags(text);
		for (i in 0...plain.length) {
			var c = StringTools.fastCodeAt(plain, i);
			if (c >= "0".code && c <= "9".code)
				value = (value < 0 ? 0 : value * 10) + (c - "0".code);
			else if (c == ",".code && value >= 0) {} else if (value >= 0) {
				found.push(value);
				value = -1;
			}
		}
		if (value >= 0)
			found.push(value);
		return found;
	}

	/** The last whole number in the text, -1 when none. */
	static function lastNumber(text:String):Int {
		var found = numbers(text);
		return found.length == 0 ? -1 : found[found.length - 1];
	}

	/** +1 or -1 for the sign just before the last number, 0 when there is none. */
	static function signOf(plain:String):Int {
		var i = plain.length - 1;
		while (i >= 0 && !digit(StringTools.fastCodeAt(plain, i)))
			i--;
		while (i >= 0 && (digit(StringTools.fastCodeAt(plain, i)) || StringTools.fastCodeAt(plain, i) == ",".code))
			i--;
		while (i >= 0 && StringTools.fastCodeAt(plain, i) == " ".code)
			i--;
		if (i < 0)
			return 0;
		var c = StringTools.fastCodeAt(plain, i);
		return c == "+".code ? 1 : (c == "-".code || c == 0x2212 ? -1 : 0);
	}

	static function digit(c:Int):Bool {
		return c >= "0".code && c <= "9".code;
	}

	static function firstLine(plain:String):String {
		var cut = plain.length;
		for (separator in ["\n", "\r"]) {
			var at = plain.indexOf(separator);
			if (at >= 0 && at < cut)
				cut = at;
		}
		return plain.substr(0, cut);
	}

	/** Letters only, lower-cased, so "Willpower Efficiency:" and "Willpower Efficiency" match. */
	static function letters(text:String):String {
		var pieces:Array<String> = [];
		var lower = text.toLowerCase();
		for (i in 0...lower.length) {
			var c = StringTools.fastCodeAt(lower, i);
			if (c > 127 || (c >= "a".code && c <= "z".code))
				pieces.push(lower.charAt(i));
		}
		return pieces.join("");
	}

	static function whole(text:String):Int {
		var found = numbers(text == null ? "" : text);
		var value = found.length == 0 ? 0 : found[0];
		return text != null && text.charAt(0) == "-" ? -value : value;
	}

	static function decimal(text:String):Float {
		if (text == null)
			return 0;
		var value = 0.0;
		var scale = 0.0;
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (digit(c)) {
				if (scale > 0) {
					value += (c - "0".code) * scale;
					scale /= 10;
				} else
					value = value * 10 + (c - "0".code);
			} else if (c == ".".code)
				scale = 0.1;
		}
		return value;
	}

	static function percent(chance:Float):String {
		var tenths = Std.int(chance * 1000 + 0.5);
		var whole = Std.int(tenths / 10);
		return whole + "." + (tenths - whole * 10) + "%";
	}

	static function money(value:Float):String {
		var rounded = Std.int(value / 10 + 0.5) * 10;
		var text = "" + rounded;
		var out = "";
		var count = 0;
		var i = text.length - 1;
		while (i >= 0) {
			out = text.charAt(i) + out;
			count++;
			if (count % 3 == 0 && i > 0)
				out = "," + out;
			i--;
		}
		return out;
	}
}
