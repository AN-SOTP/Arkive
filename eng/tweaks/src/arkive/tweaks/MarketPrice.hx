package arkive.tweaks;

/**
	Market Price on Tooltips. Every market row the game draws (search results, wish list, recent searches) and every
	gem in auction search results is remembered in Prices as the player browses; item tooltips then show the price
	under the title: "Market: 128 g each · ×120 = 15,360 g · avg 131 g · seen 14 min ago". Only what the player looked
	at is known; nothing is searched for them. Prices keeps them for the next session while Arkive runs.

	Market prices are per bundle ("[Sold in bundles of 10 units]" under the name), so the line gives the unit price and
	the bundle's. Auction rows are single listings, so a gem's price is the cheapest buy-now seen in the last ten
	minutes. With the "showData" option the tooltip also lists what the tweak read, for testing.
**/
@:keep
class MarketPrice {
	static inline var TWEAK = "marketprice";
	static inline var PRICE = "#FFD200";
	static inline var DIM = "#A9A9A9";

	/** What the last market row held, for showData. */
	static var lastRow:String = "";

	/** ExchangeShopItemRenderer.setData: a market search, wish list or interest list row. */
	public static function onItemRow(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var row:Dynamic = args[0];
		if (row == null)
			return;
		Prices.note(row.itemName, Prices.parse(row.lowestPrice), Prices.parse(row.avgPrice), Prices.parse(row.lastPrice),
			bundleOf(row.packStr), "market", idOf(row.itemData));
		if (showData())
			lastRow = describe(row, 0);
	}

	/** ExchangeShopLastSearchListRenderer.setData: a recent search row (average and last price, no lowest). */
	public static function onLastSearchRow(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var row:Dynamic = args[0];
		if (row == null)
			return;
		var name = Prices.key(row.itemName);
		var old:Dynamic = Prices.get(name);
		// A search row seen just as recently knows the lowest price too; don't drop it.
		if (old != null && old.lowest > 0)
			return;
		Prices.note(name, -1, Prices.parse(row.avgPrice), Prices.parse(row.lastPrice), bundleOf(row.packStr), "market",
			idOf(row.itemData));
	}

	/** AuctionSearchListRenderer.setData: gems only, since other auction items each have their own stats. */
	public static function onAuctionRow(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var row:Dynamic = args[0];
		if (row == null)
			return;
		var name = Prices.key(row.itemName);
		if (!isGem(name))
			return;
		var price = Prices.parse(row.buyNowPrice);
		if (price > 0)
			Prices.note(name, price, -1, -1, 1, "auction", null);
	}

	/** TooltipDocument.setMainTooltipData, before the game draws it: the price line under an item's title. */
	public static function onMainTooltip(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var boxes:Dynamic = args[0];
		if (boxes == null || !(untyped __is__(boxes, Array)))
			return null;
		var list:Array<Dynamic> = boxes;
		var name:String = null;
		var slot:Dynamic = null;
		var title = false;
		for (box in list) {
			if (box == null)
				continue;
			if (box.type == "NameTagBox" && name == null)
				name = Prices.key(Tooltips.htmlOf(box.value));
			else if (box.type == "ItemTitle") {
				title = true;
				if (box.value != null && Text.has(box.value, "slotData"))
					slot = box.value.slotData;
			}
		}
		if (!title || name == null || name == "")
			return null;
		var entry:Dynamic = Prices.get(name);
		if (entry == null && slot != null)
			entry = Prices.byId(idOf(slot));
		var count = countOf(slot);
		if (entry != null)
			TooltipLines.add(boxes, TWEAK, lineFor(entry, count), TooltipLines.MARKET_PRICE);
		if (showData())
			TooltipLines.add(boxes, TWEAK + "Data", font(DIM, escape("Arkive price data: name \"" + name + "\", slot "
				+ describe(slot, 1) + ", entry " + describe(entry, 1) + ", last market row " + lastRow)), TooltipLines.MARKET_PRICE + 1);
		return null;
	}

	static function lineFor(entry:Dynamic, count:Int):String {
		var unit = Prices.best(entry);
		var lowest:Float = entry.lowest;
		var last:Float = entry.last;
		var avg:Float = entry.avg;
		var bundle:Int = entry.bundle;
		var kind = lowest > 0 ? "" : last > 0 ? " (last sale)" : " (average)";
		var parts:Array<String> = [];
		var head = font(DIM, "Market: ") + font(PRICE, Prices.gold(unit)) + font(DIM, (bundle > 1 ? " each" : "") + kind);
		if (bundle > 1)
			head += font(DIM, " (") + font(PRICE, Prices.gold(unit * bundle)) + font(DIM, " per " + whole(bundle) + ")");
		parts.push(head);
		if (count > 1 && GameUi.on(TWEAK, "stack"))
			parts.push(font(DIM, "×" + Tooltips.grouped(count) + " = ") + font(PRICE, Prices.gold(unit * count)));
		if (lowest > 0 && avg > 0 && GameUi.on(TWEAK, "average"))
			parts.push(font(DIM, "avg " + Prices.gold(avg)));
		parts.push(font(DIM, "seen " + Prices.ageText(entry)));
		return parts.join(font(DIM, " · "));
	}

	/** Gems are sold one by one with fixed stats: "Lv. 8 Doomfire Gem". */
	static function isGem(name:String):Bool {
		return StringTools.startsWith(name, "Lv.") && name.indexOf(" Gem") > 0;
	}

	/** The bundle size in the market's "[Sold in bundles of 10 units]" text; 1 for anything else. */
	static function bundleOf(text:Dynamic):Int {
		if (text == null || !Tooltips.isString(text))
			return 1;
		var plain = Text.stripTags(text);
		if (plain.toLowerCase().indexOf("bundle") < 0)
			return 1;
		var size = Prices.parse(plain);
		return size > 1 ? Math.floor(size) : 1;
	}

	static function idOf(slot:Dynamic):String {
		if (slot == null || !Text.has(slot, "bindID") || slot.bindID == null)
			return null;
		var id:String = untyped __global__["String"](slot.bindID);
		return id;
	}

	/** The stack size a tooltip's slot data says (0 when it doesn't). */
	static function countOf(slot:Dynamic):Int {
		if (slot == null || !Text.has(slot, "count"))
			return 0;
		var count:Float = Prices.parse(untyped __global__["String"](slot.count));
		return count > 1 ? Math.floor(count) : 0;
	}

	static function showData():Bool {
		return Options.get(TWEAK, "showData", false) == true;
	}

	static function font(color:String, text:String):String {
		return "<FONT COLOR='" + color + "'>" + text + "</FONT>";
	}

	static function whole(value:Int):String {
		var text:String = untyped __global__["String"](value);
		return text;
	}

	/** A value as short text for showData: strings without tags, objects as {key=value} a few levels deep. */
	static function describe(value:Dynamic, depth:Int):String {
		if (value == null)
			return "null";
		var type:String = untyped __typeof__(value);
		if (type == "string") {
			var plain = Text.stripTags(value);
			return plain.length > 60 ? plain.substr(0, 60) + "…" : plain;
		}
		if (type != "object" || depth > 2) {
			var shown:String = untyped __global__["String"](value);
			return shown;
		}
		var parts:Array<String> = [];
		var keys:Array<String> = untyped __keys__(value);
		for (key in keys)
			if (key != "iconPath" && key != "iconU" && key != "iconV" && key != "iconWidth" && key != "iconHeight")
				parts.push(key + "=" + describe(untyped value[key], depth + 1));
		return "{" + parts.join(", ") + "}";
	}

	static function escape(text:String):String {
		return text.split("&").join("&amp;").split("<").join("&lt;").split(">").join("&gt;");
	}
}
