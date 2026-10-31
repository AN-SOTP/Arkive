package arkive.tweaks;

import flash.events.Event;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.utils.Dictionary;

/**
	Box Odds on Tooltips. A box's tooltip lists what it can give; each listed item gets its chance beside it ("5%"), and
	under the title a line says what opening it is worth at the market prices you've seen, next to what the box itself
	sells for: "Opening it: ~1,240 g · 31 of 43 items priced · the box sells for 900 g".

	The odds come from the game's drop tables (BoxOddsTable.cs), which Arkive writes to the "table" file when it
	installs the tweak: per box name, every item with the chance to get at least one and how many on average. Several
	boxes can share a name, so the one whose items the tooltip lists is picked. Items whose drop the server decides
	(daily limits) say "server". Boxes that only list their contents in the game files (shop gem chests, card packs) roll
	on the server: their tooltip says so in one grey line ("serverNote"). When the game's list has no rows Arkive can put
	the odds on, they go in a line under the title instead. Prices are Market Price on Tooltips' (only items you looked
	up); gold counts as is.
**/
@:keep
class BoxOdds {
	static inline var TWEAK = "boxodds";
	static inline var ODDS = "#FFD200";
	static inline var PRICE = "#FFD200";
	static inline var DIM = "#A9A9A9";
	static inline var GOLD = "Gold";

	/** The table: box name -> [[{name, chance, expected, server}]], one list per box with that name. */
	static var boxes:Dynamic;
	/** Names of the boxes the server rolls (the files only list their contents) -> true. */
	static var rolled:Dynamic;
	static var tableFile = "";
	static var loadingFile = "";

	/** Content rows given their odds: {before, after}, the text before and with them (the game can hand a list twice). */
	static var original:Dictionary = new Dictionary(true);

	/** How many of a box's items the summary line names when the game's list has no rows to put the odds on. */
	static inline var SUMMARY_ITEMS = 6;

	/** TooltipDocument.setMainTooltipData, before the game draws it: odds beside the box's contents, value under the title. */
	public static function onMainTooltip(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var list:Dynamic = args[0];
		if (list == null || !(untyped __is__(list, Array)))
			return null;
		load();
		var shown:Array<Dynamic> = list;
		var name:String = null;
		var title = false;
		var types:Array<String> = [];
		var rows:Array<Dynamic> = [];
		var bunches:Array<Dynamic> = [];
		for (box in shown) {
			if (box == null)
				continue;
			var type:String = box.type;
			types.push(type);
			if (type == "NameTagBox" && name == null)
				name = plain(Tooltips.htmlOf(box.value));
			else if (type == "ItemTitle")
				title = true;
			else if ((type == "ItemBunch" || type == "ItemSmallBunch") && box.value != null) {
				var data:Array<Dynamic> = box.value.itemData;
				if (data != null && untyped __is__(data, Array)) {
					bunches.push(box.value);
					for (row in data)
						if (row != null)
							rows.push(row);
				}
			}
		}
		if (!title || name == null || name == "")
			return null;
		var data = Options.get(TWEAK, "showData", false) == true;
		if (boxes == null) {
			if (data)
				dump(shown, name, loadingFile == "" ? "no odds table in the settings" : "the odds table isn't read yet (" + loadingFile + ")",
					types, rows);
			return null;
		}
		if (!Text.has(boxes, name)) {
			var server = Text.has(rolled, name);
			if (server && GameUi.on(TWEAK, "serverNote"))
				TooltipLines.add(list, TWEAK, font(DIM, "Box Odds: the server rolls this box. The game files have no odds for it."),
					TooltipLines.BOX_VALUE);
			if (data)
				dump(shown, name, server ? "rolled by the server" : "not a box with odds", types, rows);
			return null;
		}
		var choices:Array<Dynamic> = untyped boxes[name];
		var items = pick(choices, rows);
		var server = false;
		var placed = 0;
		for (row in rows) {
			var text = textOf(row);
			var item = itemIn(items, plain(text));
			if (item == null)
				continue;
			if (item.server == true)
				server = true;
			var after = withOdds(text, item);
			row.text = after;
			untyped original[row] = {before: text, after: after};
			placed++;
		}
		if (server && bunches.length > 0)
			note(bunches[bunches.length - 1]);
		// No list rows to put the odds on (a layout Arkive doesn't know): the odds go in a line under the title instead.
		if (placed == 0)
			TooltipLines.add(list, TWEAK + ".odds", summary(items), TooltipLines.BOX_VALUE - 1);
		if (GameUi.on(TWEAK, "value"))
			valueLine(list, name, items);
		if (data)
			dump(shown, name, choices.length + " table entries, " + placed + " rows given odds", types, rows);
		return null;
	}

	/** showData: a grey line at the end of the tooltip with what Arkive read. */
	static function dump(shown:Array<Dynamic>, name:String, status:String, types:Array<String>, rows:Array<Dynamic>):Void {
		shown.push({type: "SingleTextBox", dirty: true, value: font(DIM, escape("Arkive box data: \"" + name + "\": " + status + "; boxes "
			+ types.join(",") + "; rows " + describeRows(rows)))});
	}

	/** "Odds: Lucky Shard Mini Chest 100% · Lucky Shard Jackpot Chest 5% · …", likeliest first; "43 items, 2.3% each" when even. */
	static function summary(items:Array<Dynamic>):String {
		var left:Array<Dynamic> = [];
		for (item in items)
			if (item.server != true)
				left.push(item);
		if (left.length == 0)
			return font(DIM, "Odds: decided by the server");
		var first:Float = left[0].chance;
		var even = true;
		for (item in left) {
			var chance:Float = item.chance;
			if (Math.abs(chance - first) > 0.000001)
				even = false;
		}
		if (even && left.length > SUMMARY_ITEMS)
			return font(DIM, "Odds: " + whole(left.length) + " items, ") + font(ODDS, percent(first)) + font(DIM, " each");
		// Likeliest first: pick the highest left a few times (lists are short).
		var parts:Array<String> = [];
		while (left.length > 0 && parts.length < SUMMARY_ITEMS) {
			var best = 0;
			for (i in 1...left.length) {
				var a:Float = left[i].chance;
				var b:Float = left[best].chance;
				if (a > b)
					best = i;
			}
			var item:Dynamic = left[best];
			untyped left.splice(best, 1);
			var name:String = item.name;
			parts.push(font(DIM, name + " ") + font(ODDS, percent(item.chance)));
		}
		var more = left.length > 0 ? font(DIM, " · " + whole(left.length) + " more") : "";
		return font(DIM, "Odds: ") + parts.join(font(DIM, " · ")) + more;
	}

	/** Text without tags, the entities the game writes decoded, and no spaces at the ends: how names are compared. */
	static function plain(html:String):String {
		if (html == null)
			return "";
		var text = Text.stripTags(html);
		if (text.indexOf("&") >= 0)
			text = text.split("&apos;").join("'").split("&#39;").join("'").split("&quot;").join("\"").split("&lt;").join("<")
				.split("&gt;").join(">").split("&nbsp;").join(" ").split("&amp;").join("&");
		return Text.trim(text);
	}

	/** The row's text as the game sent it ("left|right"), before any odds were added. */
	static function textOf(row:Dynamic):String {
		var text:Dynamic = row.text;
		var value:String = text != null && Tooltips.isString(text) ? text : "";
		var kept:Dynamic = untyped original[row];
		if (kept != null && kept.after == value) {
			var before:String = kept.before;
			return before;
		}
		return value;
	}

	/** The item's chance at the end of the row: in the right column when the game uses one, else after the name. */
	static function withOdds(text:String, item:Dynamic):String {
		var odds = item.server == true ? font(DIM, "server") : font(ODDS, percent(item.chance));
		var bar = text.indexOf("|");
		if (bar < 0)
			return text + "  " + odds;
		var right = text.substr(bar + 1);
		return text.substr(0, bar) + "|" + (Text.trim(Text.stripTags(right)) == "" ? odds : right + "  " + odds);
	}

	/** One line under the list for items the server decides. */
	static function note(bunch:Dynamic):Void {
		var last:Dynamic = bunch.lastMsg;
		var text:String = last != null && Tooltips.isString(last) ? last : "";
		var line = font(DIM, "\"server\": the server decides when this drops.");
		if (text.indexOf(line) >= 0)
			return;
		bunch.lastMsg = text == "" ? line : text + "<BR>" + line;
	}

	/** Of the boxes with this name, the one whose items the tooltip lists most of. */
	static function pick(choices:Array<Dynamic>, rows:Array<Dynamic>):Array<Dynamic> {
		var best:Array<Dynamic> = choices[0];
		var bestScore = -1;
		for (choice in choices) {
			var items:Array<Dynamic> = choice;
			var score = 0;
			for (row in rows)
				if (itemIn(items, plain(textOf(row))) != null)
					score++;
			if (score > bestScore) {
				best = items;
				bestScore = score;
			}
		}
		return best;
	}

	/** The item whose name the row's text holds (the longest such name, so "X Jackpot Chest" isn't taken for "X Chest"). */
	static function itemIn(items:Array<Dynamic>, plain:String):Dynamic {
		var found:Dynamic = null;
		var length = 0;
		for (item in items) {
			var name:String = item.name;
			if (name.length > length && plain.indexOf(name) >= 0) {
				found = item;
				length = name.length;
			}
		}
		return found;
	}

	/** "Opening it: ~1,240 g · 31 of 43 items priced · the box sells for 900 g", when anything in it has a price. */
	static function valueLine(list:Dynamic, name:String, items:Array<Dynamic>):Void {
		var total:Float = 0;
		var priced = 0;
		var counted = 0;
		for (item in items) {
			if (item.server == true || item.name == "Silver")
				continue;
			counted++;
			var expected:Float = item.expected;
			if (item.name == GOLD) {
				total += expected;
				priced++;
				continue;
			}
			var unit = Prices.unit(item.name);
			if (unit > 0) {
				total += unit * expected;
				priced++;
			}
		}
		if (priced == 0)
			return;
		var parts = [font(DIM, "Opening it: ") + font(PRICE, "~" + Prices.gold(total))];
		if (priced < counted)
			parts.push(font(DIM, whole(priced) + " of " + whole(counted) + " items priced"));
		var own = Prices.unit(name);
		if (own > 0)
			parts.push(font(DIM, "the box sells for ") + font(PRICE, Prices.gold(own)));
		TooltipLines.add(list, TWEAK, parts.join(font(DIM, " · ")), TooltipLines.BOX_VALUE);
	}

	/** A chance as the game would print it: "100%", "45%", "8.7%", "0.83%", "<0.01%". */
	static function percent(chance:Float):String {
		var value = chance * 100;
		if (value >= 99.95)
			return "100%";
		if (value >= 10)
			return whole(Math.round(value)) + "%";
		if (value >= 1)
			return number(Math.round(value * 10) / 10) + "%";
		if (value >= 0.01)
			return number(Math.round(value * 100) / 100) + "%";
		return "<0.01%";
	}

	// ---- The table ----

	static function load():Void {
		var file:Dynamic = Options.get(TWEAK, "table", "");
		var path:String = file == null ? "" : file;
		if (path == "" || path == tableFile || path == loadingFile)
			return;
		loadingFile = path;
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) {
			try {
				var text:String = loader.data;
				if (loadingFile == path && parse(text))
					tableFile = path;
			} catch (error:Dynamic) {}
		});
		loader.addEventListener("ioError", function(e:Dynamic) {});
		loader.addEventListener("securityError", function(e:Dynamic) {});
		loader.load(new URLRequest(path));
	}

	/**
		BoxOddsTable.Encode: "B name" per box, then "I name|chance|expected" or "S name" per item; "N name" per box the
		server rolls. Sets boxes and rolled.
	**/
	static function parse(text:String):Bool {
		if (text == null || text.indexOf("arkive-boxodds 1") != 0)
			return false;
		var result:Dynamic = {};
		var server:Dynamic = {};
		var items:Array<Dynamic> = null;
		var lines = text.split("\n");
		for (line in lines) {
			if (line.length < 3)
				continue;
			var kind = line.charAt(0);
			var rest = line.substr(2);
			if (kind == "B") {
				items = [];
				if (!Text.has(result, rest))
					untyped result[rest] = [];
				var same:Array<Dynamic> = untyped result[rest];
				same.push(items);
			} else if (kind == "N") {
				untyped server[rest] = true;
			} else if (items != null && kind == "I") {
				var parts = rest.split("|");
				if (parts.length >= 3)
					items.push({name: parts[0], chance: decimal(parts[1]), expected: decimal(parts[2]), server: false});
			} else if (items != null && kind == "S") {
				items.push({name: rest, chance: 0, expected: 0, server: true});
			}
		}
		boxes = result;
		rolled = server;
		return true;
	}

	static function decimal(text:String):Float {
		var value:Float = untyped __global__["parseFloat"](text);
		return value;
	}

	// ---- Text ----

	static function describeRows(rows:Array<Dynamic>):String {
		var parts:Array<String> = [];
		for (row in rows) {
			var modal:Dynamic = row.modalMsg;
			var badge:String = modal == null ? "" : untyped __global__["String"](modal);
			parts.push("[" + textOf(row) + (badge == "" ? "" : " / " + badge) + "]");
		}
		return parts.join(" ");
	}

	static function font(color:String, text:String):String {
		return "<FONT COLOR='" + color + "'>" + text + "</FONT>";
	}

	static function whole(value:Int):String {
		var text:String = untyped __global__["String"](value);
		return text;
	}

	static function number(value:Float):String {
		var text:String = untyped __global__["String"](value);
		return text;
	}

	static function escape(text:String):String {
		return text.split("&").join("&amp;").split("<").join("&lt;").split(">").join("&gt;");
	}
}
