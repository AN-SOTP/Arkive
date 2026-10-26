package arkive.tweaks;

/**
	Reads what the game's own tooltips say, through the TooltipManager the game fills in (nativeGetTooltip). A tooltip
	comes back as [[{type, value}, ...]], one entry per box: "ItemTitle" carries the item's slot data (its icon),
	"SingleTextBox" the "you have N" line.
**/
class Tooltips {
	/** Item tooltips: slot type 149 with the item's id as bindID shows the item as the game knows it, owned amount included. */
	static inline var ITEM_SLOT_TYPE = 149;

	static inline var ITEM_TABLE = 73;

	/** The game prints the owned amount in this colour, right after the tag. */
	static inline var AMOUNT_TAG = "#ffd200'>";

	/** The boxes of the tooltip for this binding (and the slot or query it belongs to), or null. */
	public static function boxes(binding:Dynamic, owner:Dynamic):Array<Dynamic> {
		var managerClass:Dynamic = GameUi.definition("ark.managers.TooltipManager");
		var manager:Dynamic = managerClass == null ? null : managerClass.getInstance();
		if (manager == null || manager.nativeGetTooltip == null)
			return null;
		var result:Dynamic = manager.nativeGetTooltip(binding, owner);
		if (result == null || result.length == 0 || result[0] == null)
			return null;
		return result[0];
	}

	/** {count, slotData} for an item id: how many you have (-1 when the tooltip doesn't say) and its icon data. */
	public static function item(id:String):Dynamic {
		var query:Dynamic = {slotType: ITEM_SLOT_TYPE, slotID: 0, tableID: ITEM_TABLE, bindID: id};
		var list = boxes(query, query);
		if (list == null)
			return null;
		var count = -1;
		var slot:Dynamic = null;
		for (box in list) {
			if (box == null)
				continue;
			var value:Dynamic = box.value;
			if (box.type == "ItemTitle" && value != null && value.slotData != null)
				slot = value.slotData;
			else if (box.type == "SingleTextBox" && count == -1)
				count = amount(htmlOf(value));
		}
		return {count: count, slotData: slot};
	}

	/** The amount printed after the game's amount colour ("1,234"), or -1. */
	public static function amount(html:String):Int {
		if (html == null)
			return -1;
		var at = html.toLowerCase().indexOf(AMOUNT_TAG);
		if (at < 0)
			return -1;
		var i = at + AMOUNT_TAG.length;
		var value = 0;
		var digits = 0;
		while (i < html.length) {
			var c = StringTools.fastCodeAt(html, i);
			if (c >= "0".code && c <= "9".code) {
				value = value * 10 + (c - "0".code);
				digits++;
			} else if (c == "<".code && digits == 0) {
				var close = html.indexOf(">", i);
				if (close < 0)
					break;
				i = close;
			} else if (c != ",".code && c != ".".code && c != " ".code) {
				break;
			}
			i++;
		}
		return digits == 0 ? -1 : value;
	}

	/** The text of a tooltip box value: the value itself, or the first text field the box has. */
	public static function htmlOf(value:Dynamic):String {
		if (value == null)
			return null;
		if (isString(value))
			return value;
		for (field in ["contentStr", "text", "htmlText", "string", "value", "leftStr", "title"]) {
			var text:Dynamic = untyped value[field];
			if (isString(text))
				return text;
		}
		return null;
	}

	public static function isString(value:Dynamic):Bool {
		return untyped __typeof__(value) == "string";
	}

	/** Digits of a whole number with commas every three ("12,345"). */
	public static function grouped(value:Int):String {
		var digits:String = untyped __global__["String"](value);
		var start = digits.charAt(0) == "-" ? 1 : 0;
		var pieces:Array<String> = [digits.substring(0, start)];
		var first = (digits.length - start) % 3;
		if (first == 0)
			first = 3;
		var at = start;
		var end = start + first;
		while (at < digits.length) {
			if (at > start)
				pieces.push(",");
			pieces.push(digits.substring(at, end));
			at = end;
			end = at + 3;
		}
		return pieces.join("");
	}

	/** The whole number a string starts with (after spaces), or `fallback` when it has none. */
	public static function leadingInt(text:String, fallback:Int):Int {
		if (text == null)
			return fallback;
		var i = 0;
		while (i < text.length && StringTools.fastCodeAt(text, i) == " ".code)
			i++;
		var value = 0;
		var digits = 0;
		while (i < text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c < "0".code || c > "9".code)
				break;
			value = value * 10 + (c - "0".code);
			digits++;
			i++;
		}
		return digits == 0 ? fallback : value;
	}
}
