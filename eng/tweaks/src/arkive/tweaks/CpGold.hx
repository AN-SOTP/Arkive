package arkive.tweaks;

import flash.events.Event;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Dictionary;

/**
	Combat Power per Gold. Auction house results (accessories, bracelets, ability stones, gems) and market rows of
	engraving books get a short line under the item's name: the Combat Power the item would give you and the gold it
	costs per 1% of it at the price on the row, "+0.84% CP · 9,400 g per 1%". The best value among the rows on screen
	turns gold. A loss shows in red without a gold figure; an item Combat Power can't compare shows the reason in grey.

	The item's stats come from its tooltip, fetched for the row's own slot the way a hover does (no search, nothing
	sent), and are priced by CombatPower.priced, so the numbers are the ones the item's tooltip shows. Books count as
	the five a level takes. A row with no buy-now price is priced at its lowest bid, marked "(bid)" and never best.

	The line sits where the game puts its own second line (the trade count): under the name, which moves up the way the
	game moves it for a trade count. When the row already has that line, ours follows it on the same line in a shorter
	form. Rows are never reordered: the game ties Buy and the wish list to a row's position.
**/
@:keep
class CpGold {
	static inline var TWEAK = "cpgold";
	static inline var GAIN = "#7EE08F";
	static inline var LOSS = "#FF8A80";
	static inline var DIM = "#A9A9A9";
	static inline var BEST = "#FFD200";
	/** Books needed for one engraving level. */
	static inline var BOOKS = 5;
	static inline var SIZE = 12;
	static inline var LINE_HEIGHT = 18;
	static inline var GAP = 8;
	/** A change smaller than this (0.005%) shows as none. */
	static inline var NONE = 0.00005;
	static inline var AUCTION_ROW = "ark.ui.exchangeShop.AuctionSearchListRenderer";
	static inline var BOOK = " Engraving Recipe";

	/** Per row (weak keys): {change, price, bid, note} from the last time it was priced, or none when it shows nothing. */
	static var states = new Dictionary(true);
	static var labels = new Dictionary(true);
	/** Per row: the name's own y, to put it back when the row shows nothing. */
	static var nameYs = new Dictionary(true);
	static var pending:Array<Dynamic> = [];
	static var ticker:Dynamic;
	static var listening = false;

	/** AuctionSearchListRenderer.setData, after the game fills the row. */
	public static function onAuctionRow(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		queue(self);
	}

	/** ExchangeShopItemRenderer.setData (market rows): only engraving books get a line. */
	public static function onMarketRow(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		queue(self);
	}

	/** Prices rows a frame later, once the whole page is filled, so the best row is picked among all of them. */
	static function queue(row:Dynamic):Void {
		if (!listening) {
			listening = true;
			Options.listen(onSwitch);
		}
		if (row == null || pending.indexOf(row) >= 0)
			return;
		pending.push(row);
		if (ticker == null) {
			ticker = row;
			row.addEventListener(Event.ENTER_FRAME, onFrame);
		}
	}

	static function onFrame(event:Dynamic):Void {
		var rows = pending;
		pending = [];
		if (ticker != null)
			ticker.removeEventListener(Event.ENTER_FRAME, onFrame);
		ticker = null;
		var lists:Array<Dynamic> = [];
		for (row in rows) {
			try {
				price(row);
				var list:Dynamic = row.parent;
				if (list != null && lists.indexOf(list) < 0)
					lists.push(list);
			} catch (error:Dynamic) {
				clear(row);
			}
		}
		for (list in lists)
			try {
				showList(list);
			} catch (error:Dynamic) {}
	}

	/**
		Switched on or off in game: every row goes back to the game's own look (names in place, lines empty), so a line
		never shows over a name the game hasn't moved. Rows get their line again when the game next fills them.
	**/
	static function onSwitch(tweak:String, key:String):Void {
		if (tweak != TWEAK || key != "")
			return;
		var rows:Array<Dynamic> = untyped __keys__(labels);
		for (row in rows)
			clear(row);
	}

	/** Works out a row's state from its item and price. */
	static function price(row:Dynamic):Void {
		untyped __delete__(states, row);
		var auction = GameUi.isA(row, AUCTION_ROW);
		// Market rows are mostly materials; only books are worth a tooltip read.
		var title:String = auction || row.itemName == null ? "" : Text.stripTags(row.itemName);
		if (!auction && title.indexOf(BOOK) < 0)
			return;
		var slot:Dynamic = row.slot;
		var boxes = slot == null || row.itemData == null ? null : Tooltips.boxes(slot.binding, slot);
		var priced:Dynamic = boxes == null ? null : CombatPower.priced(boxes);
		if (priced == null)
			return;
		if (!auction && priced.book != true)
			return;
		var cost:Float = 0;
		var bid = false;
		if (auction) {
			cost = digits(row.buyNowPrice);
			if (cost <= 0) {
				cost = digits(row.lowestBidPrice);
				bid = true;
			}
		} else {
			cost = digits(row.lowestPrice) * BOOKS;
		}
		var change:Float = priced.change;
		var note:String = priced.note;
		untyped states[row] = {change: change, price: cost, bid: bid, note: note};
	}

	/** Draws every shown row of a list, the cheapest gain per 1% among them in gold. */
	static function showList(list:Dynamic):Void {
		var rows:Array<Dynamic> = [];
		var best:Dynamic = null;
		var bestValue:Float = 0;
		var count:Int = list.numChildren;
		for (i in 0...count) {
			var row:Dynamic = list.getChildAt(i);
			if (row == null)
				continue;
			var priced:Bool = untyped __in__(row, states);
			var drawn:Bool = untyped __in__(row, labels);
			if (!priced && !drawn)
				continue;
			rows.push(row);
			var value = perPercent(untyped states[row]);
			if (row.visible == true && value > 0 && (best == null || value < bestValue)) {
				best = row;
				bestValue = value;
			}
		}
		for (row in rows)
			show(row, row == best);
	}

	/** Gold per 1% of Combat Power, or 0 when the row has no gain or no buy-now price. */
	static function perPercent(state:Dynamic):Float {
		if (state == null || state.bid == true)
			return 0;
		var change:Float = state.change;
		var cost:Float = state.price;
		if (isNaN(change) || change < NONE || cost <= 0)
			return 0;
		return cost / (change * 100);
	}

	static function show(row:Dynamic, best:Bool):Void {
		var state:Dynamic = untyped states[row];
		if (state == null) {
			clear(row);
			return;
		}
		var name:Dynamic = row.itemName_txt;
		var high:Dynamic = row.itemNameDouble_txt;
		var pack:Dynamic = row.packString_txt;
		if (name == null || high == null || pack == null)
			return;
		var label = labelOf(row, name);
		restoreName(row);
		var packStr:String = row.packStr;
		var forms = texts(state, best);
		if (packStr == null || packStr == "") {
			// The game's own two-line layout: name on the upper line, ours where the trade count would be.
			untyped nameYs[row] = name.y;
			name.y = high.y;
			place(label, pack.x, pack.y, pack.width);
			label.htmlText = forms[0];
		} else {
			var used:Float = pack.textWidth + GAP;
			place(label, pack.x + used, pack.y, pack.width - used);
			label.htmlText = "";
			for (form in forms) {
				label.htmlText = form;
				if (label.textWidth + 4 <= label.width)
					break;
				label.htmlText = "";
			}
		}
		label.visible = true;
	}

	/** The row's line, longest form first: full, short (beside a trade count), shortest. */
	static function texts(state:Dynamic, best:Bool):Array<String> {
		var change:Float = state.change;
		if (isNaN(change)) {
			var note:String = state.note;
			if (note == null || note == "")
				return [""];
			return [font(DIM, "CP: " + note), font(DIM, "CP: " + note), ""];
		}
		if (Math.abs(change) < NONE)
			return [font(DIM, "No CP change"), font(DIM, "No CP change"), font(DIM, "+0 CP")];
		var percent = signed(change * 100) + "%";
		if (change < 0)
			return [font(LOSS, percent + " CP"), font(LOSS, percent + " CP"), font(LOSS, percent)];
		var cost:Float = state.price;
		if (cost <= 0)
			return [font(GAIN, percent + " CP"), font(GAIN, percent + " CP"), font(GAIN, percent)];
		var per = cost / (change * 100);
		var bid = state.bid == true ? " (bid)" : "";
		var color = best ? BEST : GAIN;
		var lead = best ? "Best: " : "";
		return [font(color, lead + percent + " CP · " + (per < 1000000000 ? Tooltips.grouped(Math.round(per)) : compact(per)) + " g per 1%" + bid),
			font(color, percent + " · " + compact(per) + " g/1%" + bid), font(color, compact(per) + " g/1%")];
	}

	static function labelOf(row:Dynamic, like:Dynamic):TextField {
		var label:TextField = untyped labels[row];
		if (label != null && label.parent == like.parent)
			return label;
		var format = new TextFormat();
		var own:Dynamic = like.getTextFormat();
		if (own != null && own.font != null)
			format.font = own.font;
		format.size = SIZE;
		label = new TextField();
		label.embedFonts = like.embedFonts;
		label.defaultTextFormat = format;
		label.selectable = false;
		label.mouseEnabled = false;
		label.multiline = false;
		label.wordWrap = false;
		label.height = LINE_HEIGHT;
		GameUi.addAbove(label, like);
		Options.own(TWEAK, label);
		untyped labels[row] = label;
		return label;
	}

	/** Puts the line in a column cell, centred on the line the game's field at y would show. */
	static function place(label:TextField, x:Float, y:Float, width:Float):Void {
		var cell:Float = 26;
		label.x = x;
		label.y = y + (cell - LINE_HEIGHT) / 2;
		label.width = width < 0 ? 0 : width;
	}

	/** Hides the row's line and puts its name back. */
	static function clear(row:Dynamic):Void {
		untyped __delete__(states, row);
		var label:TextField = untyped labels[row];
		if (label != null) {
			label.htmlText = "";
			label.visible = false;
		}
		restoreName(row);
	}

	static function restoreName(row:Dynamic):Void {
		if (!(untyped __in__(row, nameYs)))
			return;
		var y:Float = untyped nameYs[row];
		untyped __delete__(nameYs, row);
		if (row.itemName_txt != null)
			row.itemName_txt.y = y;
	}

	// ---- Numbers and text ----

	/** The number made of the digits in a price label ("1,234,567" or HTML); 0 for "-" or nothing. */
	static function digits(label:Dynamic):Float {
		if (label == null || !Tooltips.isString(label))
			return 0;
		var text = Text.stripTags(label);
		var value:Float = 0;
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c >= "0".code && c <= "9".code)
				value = value * 10 + (c - "0".code);
			else if (c == ".".code)
				break;
		}
		return value;
	}

	/** "+0.84" with two decimals. */
	static function signed(value:Float):String {
		var cents:Int = Math.round(Math.abs(value) * 100);
		var whole:Int = Math.floor(cents / 100);
		var rest:Int = cents - whole * 100;
		var two:String = untyped __global__["String"](rest);
		return (value < 0 ? "-" : "+") + Tooltips.grouped(whole) + "." + (rest < 10 ? "0" + two : two);
	}

	/** "940", "9,400", "94k", "1.2m". */
	static function compact(value:Float):String {
		if (value >= 1000000) {
			var tenths:Int = Math.round(value / 100000);
			var whole:Int = Math.floor(tenths / 10);
			var rest:Int = tenths - whole * 10;
			var one:String = untyped __global__["String"](rest);
			return Tooltips.grouped(whole) + "." + one + "m";
		}
		if (value >= 100000)
			return Tooltips.grouped(Math.round(value / 1000)) + "k";
		return Tooltips.grouped(Math.round(value));
	}

	/** The player's isNaN: Haxe's Math.isNaN is set up by Haxe's Boot, which Arkive's empty Boot leaves out. */
	static function isNaN(value:Float):Bool {
		return untyped __global__["isNaN"](value);
	}

	static function font(color:String, text:String):String {
		return "<FONT COLOR='" + color + "'>" + text + "</FONT>";
	}
}
