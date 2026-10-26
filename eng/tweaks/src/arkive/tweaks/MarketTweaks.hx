package arkive.tweaks;

/**
	Market Tweaks, on the market's listing page: "Max" beside the quantity sets it to everything you can list, and
	"Lowest" / "-1" beside the unit price type in the lowest listed price, or one gold under it.
**/
@:keep
class MarketTweaks {
	/** Typed into the quantity; the stepper lowers it to the most it allows. */
	static inline var TYPED_COUNT = "9999999999";
	static inline var MAX_WIDTH = 50;
	static inline var LOWEST_WIDTH = 56;
	static inline var MINUS_WIDTH = 30;
	static inline var GAP = 4;
	/** How far left the two inputs start, to make room for the buttons. */
	static inline var SHIFT = 50;

	/** ExchangeShopItemRegistrationPage.configUI: narrow the inputs and put the buttons at their right. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var stepper:Dynamic = self.saleCountStepper;
		var price:Dynamic = self.eaPriceInput;
		if (stepper == null || stepper.parent == null || stepper.textField == null)
			return;
		var right:Float = stepper.x + stepper.width;
		var left:Float = stepper.x - SHIFT;
		var max:Dynamic = GameUi.on("markettweaks", "maxCount") ? GameUi.button("Max") : null;
		if (max != null) {
			stepper.x = left;
			stepper.width = right - left - MAX_WIDTH - GAP;
			place(max, stepper, right - MAX_WIDTH, MAX_WIDTH);
			max.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) fillCount(stepper));
		}

		var lowest:Dynamic = null;
		var minus:Dynamic = null;
		if (GameUi.on("markettweaks", "prices") && price != null && price.parent != null && price.textField != null) {
			lowest = GameUi.button("Lowest");
			minus = GameUi.button("-1");
		}
		if (lowest != null && minus != null) {
			var lowestX:Float = right - MINUS_WIDTH - GAP - LOWEST_WIDTH;
			place(lowest, price, lowestX, LOWEST_WIDTH);
			place(minus, price, right - MINUS_WIDTH, MINUS_WIDTH);
			var end:Float = lowestX - GAP;
			var gold:Dynamic = self.registrationPageGoldIcon_mc;
			if (gold != null) {
				var space:Float = gold.x - (price.x + price.width);
				gold.x = end - gold.width;
				end = gold.x - space;
			}
			price.x = left;
			price.width = end - left;
			lowest.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) {
				var value:Float = lowestPrice(self);
				if (value > 0)
					fillPrice(price, value);
			});
			minus.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) {
				var value:Float = lowestPrice(self);
				if (value > 1)
					fillPrice(price, value - 1);
			});
		}
		var first:Dynamic = max != null ? max : lowest;
		if (first == null)
			return;
		first.addEventListener("enterFrame", function(event:Dynamic) {
			if (max != null)
				GameUi.enable(max, stepper.enabled == true && stepper.visible == true);
			if (lowest == null)
				return;
			var value:Float = price.enabled == true && price.visible == true ? lowestPrice(self) : 0;
			GameUi.enable(lowest, value > 0);
			GameUi.enable(minus, value > 1);
		});
	}

	static function place(button:Dynamic, beside:Dynamic, x:Float, width:Float):Void {
		button.width = width;
		button.height = beside.height;
		button.x = x;
		button.y = beside.y;
		GameUi.addAbove(button, beside);
	}

	/** The listed price the game marks as lowest, else the smallest one; 0 when nothing is listed. */
	static function lowestPrice(self:Dynamic):Float {
		var list:Dynamic = self.registPriceList;
		var rows:Array<Dynamic> = list == null ? null : list.dataProvider;
		if (rows == null)
			return 0;
		var best:Float = 0;
		for (row in rows) {
			if (row == null || row.price == null)
				continue;
			var value = digits(row.price);
			if (value <= 0)
				continue;
			if (row.isLowestPrice == true || row.isLowestPrice == 1)
				return value;
			if (best == 0 || value < best)
				best = value;
		}
		return best;
	}

	/** The number made of the digits in a price label ("1,234" or HTML), or 0. */
	static function digits(label:Dynamic):Float {
		var shown:String = label;
		var text:String = Text.stripTags(shown);
		var value:Float = 0;
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c >= "0".code && c <= "9".code)
				value = value * 10 + (c - "0".code);
		}
		return value;
	}

	/** Types into the quantity's text field, then tells the stepper the typing is done, as leaving the field does. */
	static function fillCount(stepper:Dynamic):Void {
		var field:Dynamic = stepper.textField.textField;
		if (field == null)
			return;
		field.text = TYPED_COUNT;
		field.dispatchEvent(new flash.events.Event(flash.events.Event.CHANGE));
		stepper.dispatchEvent(new ark.events.ProgressEvent(ark.events.ProgressEvent.COMPLETE));
	}

	static function fillPrice(price:Dynamic, value:Float):Void {
		var field:Dynamic = price.textField;
		if (field == null)
			return;
		var text:String = "" + value;
		field.text = text;
		field.dispatchEvent(new flash.events.Event(flash.events.Event.CHANGE));
	}
}
