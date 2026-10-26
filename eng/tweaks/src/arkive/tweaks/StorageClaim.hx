package arkive.tweaks;

/**
	Universal Storage Claim: "Claim Page" and "Claim All" beside the universal storage window's own claim button. Both
	tick the rows they claim through the window's own checkbox callback and press its claim button; Claim All presses
	it again while claimed rows keep disappearing, for storage that hands out a limited number per claim.
**/
@:keep
class StorageClaim {
	static inline var GAP = 6;
	static inline var ENABLE_FRAMES = 30;
	static inline var UPDATE_FRAMES = 300;

	static var running = false;
	static var window:Dynamic;
	static var ids:Array<String>;
	static var again = false;
	static var pressed = false;
	static var frames = 0;

	/** CurrencyStorageWndContents.configUI: split the claim button's row into it and the new buttons. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var receive:Dynamic = self.receive_btn;
		var list:Dynamic = self.currencyStorageList;
		if (receive == null || receive.parent == null || list == null)
			return;
		var page:Dynamic = GameUi.on("storageclaim", "page") ? GameUi.copy(receive, "Claim Page") : null;
		var all:Dynamic = GameUi.on("storageclaim", "all") ? GameUi.copy(receive, "Claim All") : null;
		var added:Array<Dynamic> = [];
		if (page != null)
			added.push(page);
		if (all != null)
			added.push(all);
		if (added.length == 0)
			return;

		// The row spans the list when the list is wider than the button.
		var parent:Dynamic = receive.parent;
		var left:Float = receive.x;
		var span:Float = receive.width;
		if (list.parent == parent) {
			var bounds:Dynamic = list.getBounds(parent);
			if (bounds.width > span) {
				left = bounds.x;
				span = bounds.width;
			}
		}
		var width:Float = (span - GAP * added.length) / (added.length + 1);
		receive.x = left;
		receive.width = width;
		var i = added.length;
		while (i > 0) {
			i--;
			GameUi.addAbove(added[i], receive);
		}
		for (n in 0...added.length) {
			var button:Dynamic = added[n];
			button.width = width;
			button.height = receive.height;
			button.x = left + (n + 1) * (width + GAP);
			button.y = receive.y;
		}
		if (page != null)
			page.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) start(self, pageRows(self), false));
		if (all != null)
			all.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) start(self, rows(self), true));
		added[0].addEventListener("enterFrame", function(event:Dynamic) {
			if (page != null)
				GameUi.enable(page, !running && pageRows(self).length > 0);
			if (all != null)
				GameUi.enable(all, !running && rows(self).length > 0);
		});
	}

	static function rows(self:Dynamic):Array<Dynamic> {
		var data:Array<Dynamic> = self.currencyStorageList.dataProvider;
		return data == null ? [] : data;
	}

	/** The rows of the page the stepper shows. */
	static function pageRows(self:Dynamic):Array<Dynamic> {
		var size:Int = self.currencyStorageList.numOfPageRenderItemLength;
		var page:Int = self.currencyStorageStepper.value;
		if (size <= 0 || page < 1)
			return [];
		var first = (page - 1) * size;
		return rows(self).slice(first, first + size);
	}

	static function idsOf(list:Array<Dynamic>):Array<String> {
		var found:Array<String> = [];
		for (row in list) {
			var id:String = row.uniqueIndex;
			found.push(id);
		}
		return found;
	}

	static function start(self:Dynamic, chosen:Array<Dynamic>, repeat:Bool):Void {
		if (running || chosen.length == 0)
			return;
		running = true;
		window = self;
		ids = idsOf(chosen);
		again = repeat;
		tick();
		self.receive_btn.addEventListener("enterFrame", step);
	}

	/** Ticks exactly the rows still to claim, through the callback the window's own checkboxes use. */
	static function tick():Void {
		var callback:Dynamic = window.CallbackCheckBoxClicked;
		if (callback != null)
			for (id in idsOf(rows(window)))
				callback(id, ids.indexOf(id) >= 0);
		pressed = false;
		frames = 0;
	}

	static function step(event:Dynamic):Void {
		var receive:Dynamic = window.receive_btn;
		frames++;
		if (window.stage == null) {
			stop();
			return;
		}
		if (!pressed) {
			if (receive.enabled) {
				pressed = true;
				frames = 0;
				GameUi.press(receive);
				if (!again)
					stop();
			} else if (frames >= ENABLE_FRAMES) {
				stop();
			}
			return;
		}
		var present = idsOf(rows(window));
		var left:Array<String> = [];
		for (id in ids)
			if (present.indexOf(id) >= 0)
				left.push(id);
		if (left.length < ids.length) {
			if (left.length == 0) {
				stop();
			} else {
				ids = left;
				tick();
			}
		} else if (frames >= UPDATE_FRAMES) {
			stop();
		}
	}

	static function stop():Void {
		window.receive_btn.removeEventListener("enterFrame", step);
		running = false;
		window = null;
	}
}
