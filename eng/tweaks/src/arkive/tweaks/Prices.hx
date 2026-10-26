package arkive.tweaks;

/**
	The market prices seen this session, shared by every window (all efui movies share one ApplicationDomain, so these
	statics are the same wherever they're read). Filled by Market Price on Tooltips as the player browses the market
	and the auction house; never by a search of its own.

	An entry is {name, lowest, avg, last, bundle, seenAt, source}: prices are per unit in gold (-1 when the row didn't
	show one), bundle is how many units the market sells together, seenAt is flash's getTimer() when it was seen and
	source "market" or "auction". Items are keyed by their name as the game shows it, without tags.
**/
class Prices {
	/** Auction rows are single listings: the cheapest seen within this long counts as the lowest. */
	static inline var AUCTION_WINDOW_MS = 600000;

	static var entries:Dynamic = {};
	static var ids:Dynamic = {};

	/** The key an item name is stored under: the name without tags and spaces at the ends. */
	public static function key(name:String):String {
		return name == null ? "" : Text.trim(Text.stripTags(name));
	}

	/**
		Records one sighting. Prices are per bundle, as the market shows them ("-1" or below for one it didn't show);
		bundle is the bundle size (1 when sold singly); id is the item's bindID when the row has one.
	**/
	public static function note(name:String, lowest:Float, avg:Float, last:Float, bundle:Int, source:String, id:String):Void {
		var at = key(name);
		if (at == "" || (lowest <= 0 && avg <= 0 && last <= 0))
			return;
		var size:Float = bundle > 1 ? bundle : 1;
		var now:Int = flash.Lib.getTimer();
		var entry:Dynamic = {name: at, lowest: unitOf(lowest, size), avg: unitOf(avg, size), last: unitOf(last, size),
			bundle: bundle > 1 ? bundle : 1, seenAt: now, source: source};
		var old:Dynamic = get(at);
		if (source == "auction" && old != null && old.source == "auction" && now - old.seenAt < AUCTION_WINDOW_MS) {
			var before:Float = old.lowest;
			var seen:Float = entry.lowest;
			if (before > 0 && before <= seen)
				entry.lowest = before;
		}
		untyped entries[at] = entry;
		if (id != null && id != "" && id != "0")
			untyped ids[id] = at;
	}

	static function unitOf(price:Float, bundle:Float):Float {
		return price > 0 ? price / bundle : -1;
	}

	/** The entry for an item name (tags allowed), or null when it hasn't been seen this session. */
	public static function get(name:String):Dynamic {
		var at = key(name);
		return Text.has(entries, at) ? untyped entries[at] : null;
	}

	/** The entry for an item's bindID as a market row gave it, or null. */
	public static function byId(id:String):Dynamic {
		if (id == null || !Text.has(ids, id))
			return null;
		var at:String = untyped ids[id];
		return get(at);
	}

	/** The best unit price for an item: the lowest listing, else the last sale, else the average; -1 when none. */
	public static function unit(name:String):Float {
		return best(get(name));
	}

	public static function best(entry:Dynamic):Float {
		if (entry == null)
			return -1;
		var lowest:Float = entry.lowest;
		var last:Float = entry.last;
		var avg:Float = entry.avg;
		return lowest > 0 ? lowest : last > 0 ? last : avg;
	}

	/** How long ago the entry was seen: "just now", "14 min ago", "3 h ago". */
	public static function ageText(entry:Dynamic):String {
		var seen:Int = entry.seenAt;
		var minutes = Math.floor((flash.Lib.getTimer() - seen) / 60000);
		if (minutes < 1)
			return "just now";
		if (minutes < 60)
			return whole(minutes) + " min ago";
		return whole(Math.floor(minutes / 60)) + " h ago";
	}

	/** Gold the way the market shows it: whole numbers with commas from 100 up, else up to two decimals ("0.83 g"). */
	public static function gold(value:Float):String {
		if (value >= 100)
			return Tooltips.grouped(Math.round(value)) + " g";
		var cents:Int = Math.round(value * 100);
		var units:Int = Math.floor(cents / 100);
		var rest:Int = cents - units * 100;
		if (rest == 0)
			return whole(units) + " g";
		var two:String = whole(rest);
		var decimals = rest < 10 ? "0" + two : two;
		if (decimals.charAt(1) == "0")
			decimals = decimals.substr(0, 1);
		return whole(units) + "." + decimals + " g";
	}

	static function whole(value:Int):String {
		var text:String = untyped __global__["String"](value);
		return text;
	}

	/** A price as the market prints it ("1,234", "12.5", inside tags): the number, or -1 when it shows none ("-"). */
	public static function parse(html:Dynamic):Float {
		if (html == null || !Tooltips.isString(html))
			return -1;
		var text = Text.stripTags(html);
		var value:Float = 0;
		var scale:Float = 0;
		var digits = 0;
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c >= "0".code && c <= "9".code) {
				if (scale > 0) {
					value += (c - "0".code) * scale;
					scale = scale / 10;
				} else {
					value = value * 10 + (c - "0".code);
				}
				digits++;
			} else if (c == ".".code && scale == 0 && digits > 0) {
				scale = 0.1;
			} else if (c != ",".code && c != " ".code && digits > 0) {
				break;
			}
		}
		return digits == 0 ? -1 : value;
	}
}
