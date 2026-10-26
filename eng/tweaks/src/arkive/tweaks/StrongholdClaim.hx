package arkive.tweaks;

/**
	Stronghold Crafting Claim All: a "Claim EVERYTHING" button under the crafting window's claim button. It selects each
	workshop line with finished items in turn, as a click on the line would, and presses the window's own claim button,
	waiting for any result window to close before the next line.
**/
@:keep
class StrongholdClaim {
	static inline var GAP = 3;
	static inline var FINISHED = 3;
	static inline var SETTLE_FRAMES = 3;
	static inline var ENABLE_FRAMES = 30;
	static inline var UPDATE_FRAMES = 300;

	static inline var SELECT = 0;
	static inline var SETTLE = 1;
	static inline var CLAIMED = 2;
	static inline var RESULT = 3;

	static var running = false;
	static var window:Dynamic;
	static var line = -1;
	static var phase = 0;
	static var frames = 0;

	/** TownPWndContent.configUI: halve the claim button and put the new one in the lower half. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var get:Dynamic = self.getResultButton;
		if (get == null || get.parent == null || self.productionList == null)
			return;
		var claim:Dynamic = GameUi.button("Claim EVERYTHING");
		if (claim == null)
			return;
		var top:Float = get.y;
		var full:Float = get.height;
		var half:Float = (full - GAP) / 2;
		var glow:Dynamic = self.getResultEffectMc;
		if (glow != null) {
			glow.y = top + (glow.y - top) * half / full;
			glow.height = glow.height * half / full;
		}
		GameUi.setHeight(get, half);
		claim.x = get.x;
		claim.y = top + half + GAP;
		claim.width = get.width;
		claim.height = half;
		GameUi.addAbove(claim, get);
		claim.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) start(self));
		claim.addEventListener("enterFrame", function(event:Dynamic) {
			GameUi.enable(claim, !running && nextLine(self, 0) != -1);
			if (claim.visible != get.visible)
				claim.visible = get.visible;
		});
	}

	/** The first line at or after `from` with finished items, or -1. */
	static function nextLine(self:Dynamic, from:Int):Int {
		var data:Array<Dynamic> = self.productionList.dataProvider;
		if (data == null)
			return -1;
		for (i in from...data.length)
			if (finished(self, i))
				return i;
		return -1;
	}

	/** True when the line (its shown row, else its data) is finished or has a finished slot. */
	static function finished(self:Dynamic, index:Int):Bool {
		var list:Dynamic = self.productionList;
		var row:Dynamic = list.getRendererAtDataIndex(index);
		if (row == null)
			row = list.dataProvider[index];
		if (row == null)
			return false;
		if (row.stateType == FINISHED)
			return true;
		var slots:Array<Dynamic> = row.slotList;
		if (slots != null)
			for (slot in slots)
				if (slot != null && slot.conditionType == FINISHED)
					return true;
		return false;
	}

	static function start(self:Dynamic):Void {
		if (running)
			return;
		running = true;
		window = self;
		line = nextLine(self, 0);
		phase = SELECT;
		frames = 0;
		self.getResultButton.addEventListener("enterFrame", step);
	}

	static function next():Void {
		line = nextLine(window, line + 1);
		phase = SELECT;
		frames = 0;
	}

	static function step(event:Dynamic):Void {
		if (window.stage == null || line == -1) {
			window.getResultButton.removeEventListener("enterFrame", step);
			running = false;
			window = null;
			return;
		}
		var list:Dynamic = window.productionList;
		var get:Dynamic = window.getResultButton;
		frames++;
		if (phase == SELECT) {
			if (!GameUi.clickRow(list, line)) {
				next();
				return;
			}
			phase = SETTLE;
			frames = 0;
		} else if (phase == SETTLE) {
			if (list.selectedIndex != line) {
				if (frames >= ENABLE_FRAMES)
					next();
				return;
			}
			if (frames < SETTLE_FRAMES)
				return;
			if (get.enabled) {
				GameUi.press(get);
				phase = CLAIMED;
				frames = 0;
			} else if (frames >= ENABLE_FRAMES) {
				next();
			}
		} else if (phase == CLAIMED) {
			if (window.childVisible == true)
				phase = RESULT;
			else if (!finished(window, line) || frames >= UPDATE_FRAMES)
				next();
		} else if (window.childVisible != true) {
			next();
		}
	}
}
