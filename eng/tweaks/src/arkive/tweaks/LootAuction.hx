package arkive.tweaks;

import flash.events.Event;
import flash.text.TextField;
import flash.text.TextFormat;
import scaleform.clik.events.ButtonEvent;

/**
	Loot Auction Fair Bid. Under the auction window's Market Lowest Price, rows made like that row (its words' format, a
	money label of its class with the gold icon, in the same columns) with the most a bid can be and still beat losing
	the auction: "Fair Bid to Resell 41,823" and, when there's room, "Fair Bid to Use 44,025". The game's small Fill
	button types the first row's price into the bid box the way typing does; the player still presses Bid.

	The winning bid B is split among everyone but the winner after a fee F(B), so winning leaves you value - B and
	losing leaves you (B - F(B)) / (N - 1). They are equal at B = ((N - 1) * value + F(B)) / N, worked out by
	repeating it from the fee-free value * (N - 1) / N. To use, value is the market price (a buyer pays no fee); to
	resell, the market price less the market's 5% fee (rounded up). The fee:
	- with the Currency Security Fee button shown, the game's brackets (EFTable_CommissionFixedAmount,
	  TrophyAuctionFeesFixedAmount) on the part of the bid over the threshold the button's tooltip names (0 when it
	  names none: since August 2025 the fee is on the whole bid);
	- otherwise the percentage in the fee tooltip ("minus the 5% fee"), 5% when it names none, none when it's empty.

	N is the number of named players in the raid member frame (another movie; every movie shares this class), or the
	"players" option, else 8. A price below the minimum bid turns red (the game's own way of colouring money labels),
	and Fill greys out when its price is below the minimum bid or more than your balance.

	The game hands the market price and the raid's players to setters, which hooks can't wrap, so both windows are
	hooked where they're built and the rows read the game's own labels every frame, working the prices out again when
	anything they show changes.
**/
@:keep
class LootAuction {
	static inline var TWEAK = "lootauction";
	static inline var MARKET_FEE = 0.05;
	static inline var DEFAULT_FEE = 0.05;
	static inline var DEFAULT_PLAYERS = 8;
	/** Used only when the Market Lowest Price label shows no text to copy its format from. */
	static inline var DESC = 0xA9A9A9;
	static inline var FONT = "$YG760";
	static inline var SIZE = 14;
	static inline var LINE_HEIGHT = 20;
	static inline var BUTTON_WIDTH = 44;
	/** Fill's height when the window has no Currency Security Fee button to match. */
	static inline var BUTTON_HEIGHT = 20;
	static inline var GAP = 6;
	static inline var SETTLE_FRAMES = 5;

	/** Fee brackets: a bid part of at least FEE_FROM[i] gold pays FEE_GOLD[i] (TrophyAuctionFeesFixedAmount). */
	static var FEE_FROM:Array<Float> = [1, 2, 20, 40, 60, 80, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 2000, 3000,
		4000, 5000, 6000, 7000, 8000, 9000, 10000, 20000, 30000, 40000, 50000, 60000, 70000, 80000, 90000, 100000, 200000,
		300000, 400000, 500000, 600000, 700000, 800000, 900000, 1000000, 2000000, 3000000, 4000000, 5000000, 6000000,
		7000000, 8000000, 9000000, 10000000];
	static var FEE_GOLD:Array<Float> = [0, 1, 2, 3, 4, 5, 8, 13, 18, 23, 28, 33, 38, 43, 48, 75, 125, 175, 225, 275, 325, 375,
		425, 475, 750, 1250, 1750, 2250, 2750, 3250, 3750, 4250, 4750, 7500, 12500, 17500, 22500, 27500, 32500, 37500,
		42500, 47500, 75000, 125000, 175000, 225000, 275000, 325000, 375000, 425000, 475000, 500000];

	/** The raid member frames (weak keys); their genomeData is the raid: an array of groups, each an array of players. */
	static var raidFrames = new flash.utils.Dictionary(true);

	/** RaidMemberFrame.configUI: keep the frame, to count the raid when an auction opens. */
	public static function onRaidConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		untyped raidFrames[self] = true;
	}

	/**
		DropItemAuctionWindowContent.configUI: add two rows made like the window's own Market Lowest Price row (its label's
		text format, and a money label of the same class with the gold icon) and the game's small button for Fill.
	**/
	public static function onWindowConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var window:Dynamic = self;
		if (window.marketPrice_lb == null || window.marketPriceDesc_lb == null || window.tender_btn == null
			|| window.priceInput_lb == null)
			return;
		var rows:Array<Dynamic> = [row(window), row(window)];
		// The game's small 12 px button, as on Currency Security Fee (its class is there even while it's hidden).
		var button:Dynamic = GameUi.copy(window.counterFees_btn != null ? window.counterFees_btn : window.tender_btn, "Fill");
		button.visible = false;
		GameUi.addAbove(button, window.marketPrice_lb);
		Options.own(TWEAK, button);
		var state:Dynamic = {rows: rows, button: button, key: "", settle: 0, toUse: 0, resell: 0};
		button.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
			try {
				fill(window, state);
			} catch (error:Dynamic) {}
		});
		button.addEventListener(Event.ENTER_FRAME, function(event:Dynamic) {
			// Runs outside the hook's guard: a window shaped other than expected stops the rows, not the window.
			try {
				refresh(window, state);
			} catch (error:Dynamic) {
				hide(state);
			}
		});
	}

	/** One row: {desc, money}. The words take the Market Lowest Price label's format, so they look like the game's own. */
	static function row(window:Dynamic):Dynamic {
		var desc = new TextField();
		var format:TextFormat = formatOf(window.marketPriceDesc_lb);
		if (format == null) {
			format = new TextFormat();
			format.font = FONT;
			format.size = SIZE;
			format.color = DESC;
		}
		format.align = flash.text.TextFormatAlign.LEFT;
		desc.embedFonts = true;
		desc.defaultTextFormat = format;
		desc.selectable = false;
		desc.mouseEnabled = false;
		desc.multiline = false;
		desc.wordWrap = false;
		desc.visible = false;
		GameUi.addAbove(desc, window.marketPrice_lb);
		Options.own(TWEAK, desc);
		var money:Dynamic = moneyLike(window.marketPrice_lb);
		money.visible = false;
		GameUi.addAbove(money, window.marketPrice_lb);
		Options.own(TWEAK, money);
		return {desc: desc, money: money};
	}

	/** A money label of the market price's own class and settings: the same digits and gold icon. */
	static function moneyLike(like:Dynamic):Dynamic {
		var type:Dynamic = like.constructor;
		var made:Dynamic = untyped __new__(type);
		made.autoSize = like.autoSize;
		made.fontSize = like.fontSize;
		made.icon = like.icon;
		made.iconX = like.iconX;
		made.iconY = like.iconY;
		made.useMotion = false;
		made.updateDelay = 0;
		return made;
	}

	/** The text format of a game label's first character (font, size, colour); null when it shows no text. */
	static function formatOf(owner:Dynamic):TextFormat {
		var field:Dynamic = owner == null ? null : GameUi.isA(owner, "flash.text.TextField") ? owner : owner.textField;
		if (field == null || field.text == null)
			return null;
		var text:String = field.text;
		if (text.length == 0)
			return null;
		var format:TextFormat = field.getTextFormat(0, 1);
		return format == null || format.font == null || format.font == "" ? null : format;
	}

	/** Works the rows out again when the market price, bids, balance, fee, raid, options or layout have changed. */
	static function refresh(window:Dynamic, state:Dynamic):Void {
		var on = Options.on(TWEAK);
		var price:Dynamic = window.marketPrice_lb;
		var market = on && price.visible == true ? digits(price.htmlText) : 0;
		var least = shownDigits(window.minimumPrice_lb);
		var balance = shownDigits(window.myMoney_lb);
		var count = players();
		var fee = feeOf(window);
		var fill:String = Options.get(TWEAK, "fill", "resell");
		var data = Options.get(TWEAK, "showData", false) == true;
		var y:Float = window.marketPriceDesc_lb.y;
		// "on" is in the key: switching the tweak back on shows its items again, and empty rows must hide them.
		var key = (on ? "on|" : "off|") + whole(Math.round(market)) + "|" + whole(Math.round(least)) + "|"
			+ whole(Math.round(balance)) + "|" + whole(count) + "|" + fee.text + "|" + fill + "|" + (data ? "data|" : "|")
			+ whole(Math.round(y));
		if (key == state.key) {
			// The money labels draw a frame or two after their text is set: lay out again until their size is final.
			var settle:Int = state.settle;
			if (settle > 0 && market > 0) {
				state.settle = settle - 1;
				layout(window, state);
			}
			return;
		}
		state.key = key;
		state.settle = SETTLE_FRAMES;
		if (market <= 0) {
			hide(state);
			return;
		}
		var toUse = fair(market, count, fee);
		var resell = fair(market - Math.ceil(market * MARKET_FEE), count, fee);
		state.toUse = toUse;
		state.resell = resell;
		var target = fill == "use" ? toUse : resell;
		var other = fill == "use" ? resell : toUse;
		var names = fill == "use" ? ["Fair Bid to Use", "Fair Bid to Resell"] : ["Fair Bid to Resell", "Fair Bid to Use"];
		if (data)
			names[0] = "N " + whole(count) + " fee " + fee.text + " M " + gold(market);
		var rows:Array<Dynamic> = state.rows;
		var values = [target, other];
		for (i in 0...rows.length) {
			var line:Dynamic = rows[i];
			var desc:TextField = line.desc;
			desc.text = names[i];
			var value:Float = values[i];
			// The game colours its own money labels with a font tag (Highest Bid when you're outbid).
			line.money.htmlText = least > 0 && value < least ? "<font color='#FF8A80'>" + gold(value) + "</font>" : gold(value);
			line.money.validateNow();
		}
		var shown = layout(window, state);
		for (i in 0...rows.length) {
			var line:Dynamic = rows[i];
			line.desc.visible = i < shown;
			line.money.visible = i < shown;
		}
		var button:Dynamic = state.button;
		button.visible = true;
		GameUi.enable(button, !(least > 0 && target < least) && (balance <= 0 || target <= balance));
	}

	/** The highest bid that leaves winning and losing level: B = ((N - 1) * value + F(B)) / N, floored. */
	static function fair(value:Float, players:Int, fee:Dynamic):Float {
		var bid:Float = value * (players - 1) / players;
		for (i in 0...4)
			bid = ((players - 1) * value + feeFor(fee, bid)) / players;
		return Math.floor(bid);
	}

	static function feeFor(fee:Dynamic, bid:Float):Float {
		if (fee.brackets == true)
			return bracket(bid - fee.threshold);
		var rate:Float = fee.rate;
		return bid * rate;
	}

	/** The bracket fee for this much gold: the fee of the highest bracket it reaches. */
	static function bracket(amount:Float):Float {
		var fee:Float = 0;
		for (i in 0...FEE_FROM.length) {
			if (amount < FEE_FROM[i])
				break;
			fee = FEE_GOLD[i];
		}
		return fee;
	}

	/**
		The fee the window shows: {brackets, threshold, rate, text}. Brackets while Currency Security Fee is shown, with
		the threshold its tooltip names; else the percentage in the fee tooltip.
	**/
	static function feeOf(window:Dynamic):Dynamic {
		var counter:Dynamic = window.counterFees_btn;
		if (counter != null && counter.visible == true) {
			var threshold = firstNumber(tooltipOf(counter));
			if (threshold < 0)
				threshold = 0;
			return {brackets: true, threshold: threshold, rate: 0, text: "brackets>" + gold(threshold)};
		}
		var tip = tooltipOf(window.feesTooltipBtn);
		var rate:Float = 0;
		if (tip != "" && tip.indexOf("[$]") != 0) {
			var percent = percentIn(tip);
			rate = percent > 0 && percent < 50 ? percent / 100 : DEFAULT_FEE;
		}
		return {brackets: false, threshold: 0, rate: rate, text: whole(Math.round(rate * 100)) + "%"};
	}

	static function tooltipOf(control:Dynamic):String {
		var tip:Dynamic = control == null ? null : control.tooltip;
		if (tip == null || !Tooltips.isString(tip))
			return "";
		return Text.trim(Text.stripTags(tip));
	}

	/** The number just before the first "%" ("5" in "minus the 5% fee"), or -1. */
	static function percentIn(text:String):Float {
		var mark = text.indexOf("%");
		if (mark < 0)
			return -1;
		var start = mark;
		while (start > 0) {
			var c = StringTools.fastCodeAt(text, start - 1);
			if ((c >= "0".code && c <= "9".code) || c == ".".code)
				start--;
			else
				break;
		}
		if (start == mark)
			return -1;
		var percent:Float = untyped __global__["parseFloat"](text.substring(start, mark));
		return percent;
	}

	/** The first whole number in the text, commas allowed ("100,000"), or -1. */
	static function firstNumber(text:String):Float {
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c >= "0".code && c <= "9".code) {
				var found = Text.numberAfter(text, i);
				return found == null ? -1 : digits(found);
			}
		}
		return -1;
	}

	/**
		Puts the rows under Market Lowest Price, one row apart, in the window's own columns: words under its words, money
		right-aligned with the market price, a small Fill after the first row's words. A second row shows only when it
		fits above Highest Bidder (items without an item level leave room for it). Returns how many rows show.
	**/
	static function layout(window:Dynamic, state:Dynamic):Int {
		var price:Dynamic = window.marketPrice_lb;
		var desc:Dynamic = window.marketPriceDesc_lb;
		var step:Float = desc.height > 0 ? desc.height : LINE_HEIGHT;
		var below:Dynamic = window.topTenderDesc_lb;
		var limit:Float = below != null ? below.y - 4 : desc.y + 2 * step;
		var shown = desc.y + 3 * step <= limit ? 2 : 1;
		var inner:Float = desc.textField != null ? desc.textField.y : 0;
		var rows:Array<Dynamic> = state.rows;
		for (i in 0...rows.length) {
			var line:Dynamic = rows[i];
			var words:TextField = line.desc;
			words.x = desc.x + (desc.textField != null ? desc.textField.x : 0);
			words.y = desc.y + (i + 1) * step + inner;
			words.width = price.x - words.x;
			words.height = step;
			line.money.x = price.x;
			line.money.y = price.y + (i + 1) * step;
		}
		// Fill sits right after the first row's words, as small as the game's own fee button and never under its price:
		// the price column runs to the window's edge, so there's no room beside it.
		var button:Dynamic = state.button;
		var counter:Dynamic = window.counterFees_btn;
		var height:Float = counter != null && counter.height > 0 ? counter.height : BUTTON_HEIGHT;
		button.width = BUTTON_WIDTH;
		button.height = height < step - 4 ? height : step - 4;
		button.validateNow();
		var words:TextField = rows[0].desc;
		var priceLeft:Float = leftOf(price);
		var moneyLeft:Float = leftOf(rows[0].money);
		var stop:Float = (moneyLeft < priceLeft ? moneyLeft : priceLeft) - GAP;
		var x:Float = words.x + words.textWidth + 2 * GAP;
		button.x = x + BUTTON_WIDTH > stop ? stop - BUTTON_WIDTH : x;
		button.y = desc.y + step + (step - button.height) / 2;
		return shown;
	}

	/** Where a money label's drawing starts on the left (its gold icon), in the window; its x when it draws nothing. */
	static function leftOf(label:Dynamic):Float {
		var bounds:Dynamic = label.getBounds(label.parent);
		return bounds.width > 0 ? bounds.x : label.x;
	}

	static function hide(state:Dynamic):Void {
		var rows:Array<Dynamic> = state.rows;
		for (line in rows) {
			line.desc.visible = false;
			line.money.visible = false;
		}
		state.button.visible = false;
		state.toUse = 0;
		state.resell = 0;
	}

	/** Types the price into the bid box: the text, then the change event a key press sends, which the game listens to. */
	static function fill(window:Dynamic, state:Dynamic):Void {
		var choice:String = Options.get(TWEAK, "fill", "resell");
		var value:Float = choice == "use" ? state.toUse : state.resell;
		var input:Dynamic = window.priceInput_lb;
		if (value <= 0 || input == null || input.enabled != true || input.visible != true)
			return;
		var text = whole(Math.round(value));
		var field:Dynamic = input.textField;
		if (field != null)
			field.text = text;
		input.text = text;
		if (field != null)
			field.dispatchEvent(new Event(Event.CHANGE));
	}

	/** Named players in the raid member frame on screen, or the "players" option; 8 when neither says. */
	static function players():Int {
		var choice:String = Options.get(TWEAK, "players", "auto");
		if (choice == "4")
			return 4;
		if (choice == "8")
			return 8;
		if (choice == "16")
			return 16;
		var count = 0;
		var frames:Array<Dynamic> = untyped __keys__(raidFrames);
		for (frame in frames) {
			if (frame == null || frame.stage == null)
				continue;
			var groups:Array<Dynamic> = frame.genomeData;
			if (groups == null)
				continue;
			for (group in groups) {
				var members:Array<Dynamic> = group;
				if (members == null)
					continue;
				for (member in members) {
					var name:Dynamic = member == null ? null : member.playerName;
					if (name != null && name != "")
						count++;
				}
			}
		}
		return count >= 2 ? count : DEFAULT_PLAYERS;
	}

	/** The gold a shown money label holds; 0 when it's hidden or empty. */
	static function shownDigits(label:Dynamic):Float {
		return label != null && label.visible == true ? digits(label.htmlText) : 0;
	}

	/** The number made of the digits in a price ("1,234,567" or HTML); 0 for "-" or nothing. */
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

	static function gold(value:Float):String {
		return Tooltips.grouped(Math.round(value));
	}

	static function whole(value:Int):String {
		var text:String = untyped __global__["String"](value);
		return text;
	}
}
