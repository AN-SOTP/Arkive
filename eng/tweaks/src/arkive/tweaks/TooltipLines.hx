package arkive.tweaks;

/**
	One shared place for the lines tweaks add to an item tooltip, so they never overlap or stack apart. Every tweak's
	line goes into a single text box right after the item's title, one line each, in order (lower first), with the
	same size and spacing; a tweak that adds its line again (the game hands the same list twice) replaces it.

	Call it from a Before hook on TooltipDocument.setMainTooltipData, which gets the box list before the game draws it:
	`TooltipLines.add(args[0], "cp", html, TooltipLines.COMBAT_POWER)`.
**/
class TooltipLines {
	/** Orders of the tweaks' lines, top first. Leave gaps so new lines can go between. */
	public static inline var COMBAT_POWER = 10;
	public static inline var MARKET_PRICE = 20;
	public static inline var VALUE_PER_GOLD = 30;
	public static inline var JUDGE = 40;

	/** Adds or replaces the line `key` in the tweaks' box of this tooltip. */
	public static function add(boxes:Dynamic, key:String, html:String, order:Int):Void {
		if (boxes == null || !(untyped __is__(boxes, Array)) || html == null)
			return;
		var list:Array<Dynamic> = boxes;
		var box:Dynamic = null;
		var after = 0;
		for (i in 0...list.length) {
			var entry:Dynamic = list[i];
			if (entry == null)
				continue;
			if (entry.arkiveLines != null)
				box = entry;
			if (entry.type == "NameTagBox" || entry.type == "ItemTitle")
				after = i + 1;
		}
		if (box == null) {
			box = {type: "SingleTextBox", value: "", dirty: true, arkiveLines: []};
			untyped boxes.splice(after, 0, box);
		}
		var lines:Array<Dynamic> = box.arkiveLines;
		var kept:Array<Dynamic> = [];
		for (line in lines)
			if (line.key != key)
				kept.push(line);
		var placed = false;
		var ordered:Array<Dynamic> = [];
		for (line in kept) {
			var at:Int = line.order;
			if (!placed && order < at) {
				ordered.push({key: key, html: html, order: order});
				placed = true;
			}
			ordered.push(line);
		}
		if (!placed)
			ordered.push({key: key, html: html, order: order});
		box.arkiveLines = ordered;
		var shown:Array<String> = [];
		for (line in ordered) {
			var text:String = line.html;
			shown.push(text);
		}
		box.value = shown.join("<BR>");
	}
}
