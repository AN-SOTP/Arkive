package arkive.tweaks;

/**
	Exchange Max: "Exchange Max" (and "Exchange Max (Bound)" where the window can use bound items) under the exchange
	popup's OK button. Each sets the bound-items checkbox as named, presses the window's own maximum button, then its OK.
**/
@:keep
class ExchangeMax {
	static inline var GAP = 6;
	static inline var PADDING = 14;
	static inline var ENABLE_FRAMES = 30;
	static inline var PLAIN_NAME = "arkiveExchangeMax";
	static inline var BOUND_NAME = "arkiveExchangeMaxBound";

	static var running = false;
	static var window:Dynamic;
	static var maxed = false;
	static var waited = 0;

	/** ShopTradePopupWndContent.configUI: add the buttons. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var ok:Dynamic = self.ok_btn;
		if (ok == null || self.maximum_btn == null || ok.parent == null || self.bg_mc == null)
			return;
		var plain:Dynamic = GameUi.button("Exchange Max");
		var bound:Dynamic = GameUi.on("exchangemax", "bound") ? GameUi.button("Exchange Max (Bound)") : null;
		if (plain == null)
			return;
		plain.name = PLAIN_NAME;
		if (bound != null) {
			bound.name = BOUND_NAME;
			GameUi.addAbove(bound, ok);
			Options.own("exchangemax", bound);
			bound.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) start(self, true));
		}
		GameUi.addAbove(plain, ok);
		Options.own("exchangemax", plain);
		plain.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) start(self, false));
		plain.addEventListener("enterFrame", function(event:Dynamic) {
			var ready:Bool = !running && ok.enabled;
			var check:Dynamic = self.bindItemCheckBox;
			GameUi.enable(plain, ready);
			if (bound != null)
				GameUi.enable(bound, ready && check != null && check.enabled);
		});
		layout(self);
	}

	/** ShopTradePopupWndContent.invalidateSizeUpdate: the window laid itself out again; put the buttons back under OK. */
	public static function onInvalidateSizeUpdate(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		layout(self);
	}

	static function hasBoundCheckBox(self:Dynamic):Bool {
		var check:Dynamic = self.bindItemCheckBox;
		return check != null && check.visible == true;
	}

	/** One row under OK, as wide as the window's divider line, and the background grown to hold it. */
	static function layout(self:Dynamic):Void {
		var ok:Dynamic = self.ok_btn;
		var parent:Dynamic = ok == null ? null : ok.parent;
		var plain:Dynamic = parent == null ? null : parent.getChildByName(PLAIN_NAME);
		if (plain == null)
			return;
		var bound:Dynamic = parent.getChildByName(BOUND_NAME);
		var background:Dynamic = self.bg_mc;
		var both:Bool = bound != null && hasBoundCheckBox(self);
		plain.visible = ok.visible && Options.on("exchangemax");
		if (bound != null)
			bound.visible = ok.visible && both && Options.on("exchangemax");
		if (ok.visible != true)
			return;
		var divider:Dynamic = self.line_2;
		var left:Float = divider != null ? divider.x : background.x + GAP;
		var span:Float = divider != null ? divider.width : background.width - 2 * GAP;
		var width:Float = (span - GAP) / 2;
		var margin:Dynamic = self.margin_mc;
		var gap:Float = margin != null && margin.height > 0 ? margin.height : GAP;
		var y:Float = ok.y + ok.height + gap;
		var x:Float = both ? left : left + (span - width) / 2;
		var row:Array<Dynamic> = both ? [plain, bound] : [plain];
		for (button in row) {
			if (button.width != width)
				button.width = width;
			button.x = x;
			button.y = y;
			x += width + GAP;
		}
		background.height = y + plain.height + PADDING;
		self.invalidateSize();
	}

	static function start(self:Dynamic, bound:Bool):Void {
		if (running)
			return;
		var check:Dynamic = self.bindItemCheckBox;
		if (hasBoundCheckBox(self) && check.selected != bound) {
			check.dispatchEvent(new scaleform.gfx.MouseEventEx(flash.events.MouseEvent.CLICK));
			if (check.selected != bound)
				return;
		}
		running = true;
		window = self;
		maxed = false;
		waited = 0;
		self.ok_btn.addEventListener("enterFrame", step);
	}

	/** Frame 1: press maximum. Then press OK as soon as it is enabled (or give up after a while). */
	static function step(event:Dynamic):Void {
		var ok:Dynamic = window.ok_btn;
		if (window.stage == null) {
			stop();
			return;
		}
		if (!maxed) {
			maxed = true;
			var max:Dynamic = window.maximum_btn;
			if (max.enabled)
				GameUi.press(max);
			return;
		}
		if (ok.enabled) {
			stop();
			GameUi.press(ok);
		} else if (++waited >= ENABLE_FRAMES) {
			stop();
		}
	}

	static function stop():Void {
		window.ok_btn.removeEventListener("enterFrame", step);
		running = false;
		window = null;
	}
}
