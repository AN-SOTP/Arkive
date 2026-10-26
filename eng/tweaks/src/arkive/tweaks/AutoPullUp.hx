package arkive.tweaks;

import scaleform.clik.events.ButtonEvent;

/**
	Auto Pull Up, in the party finder's waiting room: an "Auto Pull Up" tick box beside the host's Pull up button. While
	it is ticked and the Find Party window is open, the button is pressed again 1 to 4 seconds (at random) after the game
	ends its wait. The box unticks itself when the party is full, when the time limit runs out, or when a press doesn't
	take. It starts unticked each time the game starts.

	The game itself sends the pull up and runs the wait (the button has no handler in the movie and is greyed out by the
	engine), so this only presses the button the way a click does.
**/
@:keep
class AutoPullUp {
	static inline var ID = "autopullup";
	static inline var LABEL = "Auto Pull Up";
	static inline var MIN_DELAY_MS = 1000;
	static inline var MAX_DELAY_MS = 4000;
	/** A press the game took greys the button out well within this; still enabled after it, the press didn't take. */
	static inline var PRESS_CHECK_MS = 5000;
	static inline var GROUPS = 4;
	static inline var GROUP_SIZE = 4;
	static inline var GAP = 8;

	/** Ticked; when it was ticked; when the next press is due (0: not planned); when the last press was (0: none). */
	static var armed = false;
	static var armedAt = 0;
	static var dueAt = 0;
	static var pressedAt = 0;

	/** FindPartyNewContent.configUI: put the tick box beside Pull up, right of the game's countdown. */
	public static function onContentConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var page:Dynamic = self.settingPage;
		var pull:Dynamic = page == null ? null : page.pullUp_btn;
		if (pull == null || pull.parent == null)
			return;
		var box:Dynamic = tickBox(page.privateCheckBox);
		if (box == null)
			return;
		// The countdown ("59s left") sits right of the button and is half as wide.
		box.x = pull.x + pull.width * 1.5 + 2 * GAP;
		GameUi.addAbove(box, pull);
		box.validateNow();
		box.y = pull.y + (pull.height - box.height) / 2;
		box.selected = armed;
		Options.own(ID, box);
		box.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) ticked(box));
		box.addEventListener("enterFrame", function(event:Dynamic) {
			// Runs outside the hook's guard: a lobby shaped other than expected stops the tweak, not the window.
			try {
				onFrame(self, pull, box);
			} catch (error:Dynamic) {
				armed = false;
			}
		});
	}

	/** A tick box like the page's own ones (the game's settings tick box when the page has none). */
	static function tickBox(like:Dynamic):Dynamic {
		var type:Dynamic = like != null ? like.constructor : GameUi.definition("DefaultCheckBox_V2");
		if (type == null)
			return null;
		var box:Dynamic = untyped __new__(type);
		box.autoSize = "left";
		box.label = LABEL;
		return box;
	}

	static function ticked(box:Dynamic):Void {
		armed = box.selected == true;
		armedAt = flash.Lib.getTimer();
		dueAt = 0;
		pressedAt = 0;
	}

	static function untick(box:Dynamic):Void {
		armed = false;
		dueAt = 0;
		pressedAt = 0;
		if (box.selected == true)
			box.selected = false;
	}

	static function onFrame(content:Dynamic, pull:Dynamic, box:Dynamic):Void {
		if (!armed) {
			if (box.selected == true)
				box.selected = false;
			return;
		}
		if (!Options.on(ID) || full(content) || timeUp()) {
			untick(box);
			return;
		}
		var now = flash.Lib.getTimer();
		var ready = pull.enabled == true && pull.visible == true;
		if (pressedAt != 0) {
			if (ready && now - pressedAt >= PRESS_CHECK_MS) {
				untick(box);
				return;
			}
			if (!ready)
				pressedAt = 0;
			return;
		}
		if (!ready || !shown(content)) {
			dueAt = 0;
			return;
		}
		if (dueAt == 0) {
			dueAt = now + MIN_DELAY_MS + Std.int(Math.random() * (MAX_DELAY_MS - MIN_DELAY_MS));
			return;
		}
		if (now < dueAt)
			return;
		dueAt = 0;
		pressedAt = now;
		GameUi.press(pull);
	}

	/** True when the time limit (minutes; "0" for none) has passed since the box was ticked. */
	static function timeUp():Bool {
		// A typed String local: "" + a Dynamic would go through Std.string, which needs Boot's patches.
		var limit:String = Options.get(ID, "minutes", "30");
		var minutes:Int = switch (limit) {
			case "15": 15;
			case "60": 60;
			case "120": 120;
			case "0": 0;
			default: 30;
		}
		return minutes > 0 && flash.Lib.getTimer() - armedAt >= minutes * 60000;
	}

	/** True while the Find Party window is open: the waiting room and every parent up to the stage are shown. */
	static function shown(content:Dynamic):Bool {
		if (content.stage == null || content.waitingTabList == null || content.waitingTabList.visible != true)
			return false;
		var at:Dynamic = content;
		while (at != null) {
			if (at.visible != true)
				return false;
			at = at.parent;
		}
		return true;
	}

	/**
		True when every party of the lobby (the member groups not locked or hidden) has all its places taken. A group with
		fewer rows than places, or rows the game doesn't mark, never counts as full.
	**/
	static function full(content:Dynamic):Bool {
		var joining:Dynamic = content.joiningPage;
		if (joining == null)
			return false;
		var counted = 0;
		for (g in 0...GROUPS) {
			var group:Dynamic = untyped joining["group_" + g];
			var lock:Dynamic = untyped joining["lock_" + g];
			if (group == null || group.visible != true || (lock != null && lock.visible == true))
				continue;
			var list:Dynamic = group.memberList;
			var rows:Array<Dynamic> = list == null ? null : list.dataProvider;
			if (rows == null || rows.length < GROUP_SIZE)
				return false;
			for (row in rows) {
				if (row == null || row.emptySlot == null || row.emptySlot == true || row.emptySlot == 1)
					return false;
			}
			counted++;
		}
		return counted > 0;
	}
}
