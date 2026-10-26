package arkive.tweaks;

import flash.events.Event;
import flash.events.TimerEvent;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.utils.Timer;

/**
	The market prices the player has seen, shared by every window (all efui movies share one ApplicationDomain, so these
	statics are the same wherever they're read). Filled by Market Price on Tooltips as the player browses the market
	and the auction house; never by a search of its own.

	An entry is {name, lowest, avg, last, bundle, seenAt, source}: prices are per unit in gold (-1 when the row didn't
	show one), bundle is how many units the market sells together, seenAt is when it was seen (Unix milliseconds) and
	source "market" or "auction". Items are keyed by their name as the game shows it, without tags.

	Kept between sessions (Market Price's "keep" option): new prices go to Arkive on the Data Bus a few seconds after
	the player stops browsing, and Arkive's <home>/prices/prices.txt is read back once per session (see Arkive's
	MarketPriceStore). Without Arkive running they last until the game closes.
**/
class Prices {
	static inline var TWEAK = "marketprice";
	static inline var CHANNEL = "prices";
	/** Auction rows are single listings: the cheapest seen within this long counts as the lowest. */
	static inline var AUCTION_WINDOW_MS = 600000;
	/** An unchanged price is sent again only after this long, so the kept one stays fresh. */
	static inline var RESEND_MS = 1800000;
	/** How long after the last new price the batch goes to Arkive. */
	static inline var SEND_DELAY_MS = 5000;
	static inline var MESSAGE_LENGTH = 1500;

	static var entries:Dynamic = {};
	static var ids:Dynamic = {};
	static var pending:Array<String> = [];
	static var sender:Timer;
	static var loaded = false;

	/** The key an item name is stored under: the name without tags and spaces at the ends. */
	public static function key(name:String):String {
		return name == null ? "" : Text.trim(Text.stripTags(name));
	}

	/**
		Records one sighting. Prices are per bundle, as the market shows them ("-1" or below for one it didn't show);
		bundle is the bundle size (1 when sold singly); id is the item's bindID when the row has one.
	**/
	public static function note(name:String, lowest:Float, avg:Float, last:Float, bundle:Int, source:String, id:String):Void {
		load();
		var at = key(name);
		if (at == "" || at.indexOf("|") >= 0 || at.indexOf("\n") >= 0 || (lowest <= 0 && avg <= 0 && last <= 0))
			return;
		var size:Float = bundle > 1 ? bundle : 1;
		var time = now();
		var entry:Dynamic = {name: at, lowest: unitOf(lowest, size), avg: unitOf(avg, size), last: unitOf(last, size),
			bundle: bundle > 1 ? bundle : 1, seenAt: time, source: source, sentAt: 0};
		var old:Dynamic = untyped entries[at];
		if (!Text.has(entries, at))
			old = null;
		if (source == "auction" && old != null && old.source == "auction" && time - old.seenAt < AUCTION_WINDOW_MS) {
			var before:Float = old.lowest;
			var seen:Float = entry.lowest;
			if (before > 0 && before <= seen)
				entry.lowest = before;
		}
		if (old != null && same(old, entry) && time - old.sentAt < RESEND_MS)
			entry.sentAt = old.sentAt;
		untyped entries[at] = entry;
		if (id != null && id != "" && id != "0")
			untyped ids[id] = at;
		if (entry.sentAt == 0)
			queue(at);
	}

	static function same(a:Dynamic, b:Dynamic):Bool {
		return a.lowest == b.lowest && a.avg == b.avg && a.last == b.last && a.bundle == b.bundle;
	}

	static function unitOf(price:Float, bundle:Float):Float {
		return price > 0 ? price / bundle : -1;
	}

	/** The entry for an item name (tags allowed), or null when it hasn't been seen. */
	public static function get(name:String):Dynamic {
		load();
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

	/** How long ago the entry was seen: "just now", "14 min ago", "3 h ago", "2 days ago". */
	public static function ageText(entry:Dynamic):String {
		var seen:Float = entry.seenAt;
		var minutes = Math.floor((now() - seen) / 60000);
		if (minutes < 1)
			return "just now";
		if (minutes < 60)
			return whole(minutes) + " min ago";
		var hours = Math.floor(minutes / 60);
		if (hours < 48)
			return whole(hours) + " h ago";
		return whole(Math.floor(hours / 24)) + " days ago";
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

	/** Now, in Unix milliseconds (the player's Date; Haxe's own Date needs Boot patches). */
	static function now():Float {
		var date:Dynamic = untyped __new__(__global__["Date"]);
		var time:Float = date.getTime();
		return time;
	}

	static function keep():Bool {
		return Options.home() != "" && Options.get(TWEAK, "keep", true) == true;
	}

	/** Reads Arkive's kept prices once per session; a price seen since stays. */
	static function load():Void {
		if (loaded)
			return;
		loaded = true;
		if (!keep())
			return;
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) {
			try {
				var text:String = loader.data;
				if (text != null)
					read(text);
			} catch (error:Dynamic) {}
		});
		loader.addEventListener("ioError", function(e:Dynamic) {});
		loader.addEventListener("securityError", function(e:Dynamic) {});
		loader.load(new URLRequest(Options.home() + "/prices/prices.txt"));
	}

	/** prices.txt lines: name|lowest|avg|last|bundle|source|seenAt, prices in hundredths of a gold per bundle. */
	static function read(text:String):Void {
		for (line in text.split("\n")) {
			var fields = line.split("|");
			if (fields.length != 7)
				continue;
			var name = Text.trim(fields[0]);
			var bundle = Math.floor(number(fields[4]));
			var seenAt = number(fields[6]);
			if (name == "" || bundle < 1 || seenAt <= 0)
				continue;
			if (Text.has(entries, name)) {
				var old:Dynamic = untyped entries[name];
				var newer:Float = old.seenAt;
				if (newer >= seenAt)
					continue;
			}
			untyped entries[name] = {name: name, lowest: kept(fields[1], bundle), avg: kept(fields[2], bundle),
				last: kept(fields[3], bundle), bundle: bundle, seenAt: seenAt, source: fields[5], sentAt: seenAt};
		}
	}

	static function kept(cents:String, bundle:Int):Float {
		var value = number(cents);
		return value > 0 ? value / 100 / bundle : -1;
	}

	/** A whole number written by Arkive ("-1" counts as -1). */
	static function number(text:String):Float {
		var value = parse(text);
		return text.charAt(0) == "-" ? -value : value;
	}

	static function queue(name:String):Void {
		if (!keep())
			return;
		if (pending.indexOf(name) < 0)
			pending.push(name);
		if (sender == null) {
			sender = new Timer(SEND_DELAY_MS, 1);
			sender.addEventListener(TimerEvent.TIMER_COMPLETE, send);
		}
		sender.reset();
		sender.start();
	}

	/** Sends the waiting prices in messages of up to MESSAGE_LENGTH characters. */
	static function send(event:Dynamic):Void {
		var lines:Array<String> = [];
		var length = 0;
		var names = pending;
		pending = [];
		var time = now();
		for (name in names) {
			if (!Text.has(entries, name))
				continue;
			var entry:Dynamic = untyped entries[name];
			var line = lineOf(entry);
			if (length + line.length + 1 > MESSAGE_LENGTH && lines.length > 0) {
				Bus.send(CHANNEL, lines.join("\n"));
				lines = [];
				length = 0;
			}
			lines.push(line);
			length += line.length + 1;
			entry.sentAt = time;
		}
		if (lines.length > 0)
			Bus.send(CHANNEL, lines.join("\n"));
	}

	static function lineOf(entry:Dynamic):String {
		var bundle:Int = entry.bundle;
		var name:String = entry.name;
		var source:String = entry.source;
		return [name, cents(entry.lowest, bundle), cents(entry.avg, bundle), cents(entry.last, bundle), whole(bundle), source].join("|");
	}

	/** A unit price as hundredths of a gold per bundle, "-1" when there is none. */
	static function cents(unit:Dynamic, bundle:Int):String {
		var value:Float = unit;
		if (value <= 0)
			return "-1";
		var rounded:Float = untyped __global__["Math"].round(value * bundle * 100);
		var text:String = untyped __global__["String"](rounded);
		return text;
	}
}
