package arkive.tweaks;

import flash.events.TimerEvent;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	Auto Pull Up, in the party finder's waiting room: an "Auto Pull Up" tick box beside the host's Pull up button. While
	it is ticked, the button is pressed again 1 to 4 seconds (at random) after the game ends its wait, also while the Find
	Party window is closed. The box unticks itself when the party is full, when the time limit runs out, or when a press
	doesn't take. It starts unticked each time the game starts.

	The game itself sends the pull up and runs the wait (the button has no handler in the movie and is greyed out by the
	engine), so this only presses the button the way a click does. The checks run on a timer of their own rather than on
	the window's frames. While the window shows, the button says when the wait is over. Minimised to the small Find
	Party bar, the button is no longer kept up to date (it stays greyed out, or leaves the stage), so the presses go by
	the clock instead: one a minute plus the delay after the last one. The first look at the button once the window
	shows again tells whether those presses took; when they didn't, it stops pressing while minimised. Each pull up,
	each press while minimised and each stop is reported to Arkive (channel "autopullup"), which logs it.
**/
@:keep
class AutoPullUp {
	static inline var ID = "autopullup";
	static inline var LABEL = "Auto Pull Up";
	static inline var MIN_DELAY_MS = 1000;
	static inline var MAX_DELAY_MS = 4000;
	/** A press the game took greys the button out well within this; still enabled after it, the press didn't take. */
	static inline var PRESS_CHECK_MS = 5000;
	static inline var TICK_MS = 250;
	/** The game's wait between pull ups. */
	static inline var COOLDOWN_MS = 60000;
	/** Once the window shows again, the engine gets this long to bring the button up to date before it's read. */
	static inline var SETTLE_MS = 2000;
	static inline var GROUPS = 4;
	static inline var GROUP_SIZE = 4;
	static inline var GAP = 8;

	/** Ticked; when it was ticked; when the next press is due (0: not planned); when the last press was (0: none). */
	static var armed = false;
	static var armedAt = 0;
	static var dueAt = 0;
	static var pressedAt = 0;

	/**
		When the last pull up was (0: none yet); whether the window showed at the last tick and since when; the last press
		made while minimised and not yet checked (0: none); whether those presses proved not to take.
	**/
	static var pulledAt = 0;
	static var shown = false;
	static var shownAt = 0;
	static var blindAt = 0;
	static var blindFails = false;

	/** The party finder last built: its content, its Pull up button and the tick box beside it. */
	static var content:Dynamic;
	static var pull:Dynamic;
	static var box:Dynamic;

	static var timer:Timer;

	/** FindPartyNewContent.configUI: put the tick box beside Pull up, right of the game's countdown. */
	public static function onContentConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var page:Dynamic = self.settingPage;
		var button:Dynamic = page == null ? null : page.pullUp_btn;
		if (button == null || button.parent == null)
			return;
		var made:Dynamic = tickBox(page.privateCheckBox);
		if (made == null)
			return;
		// The countdown ("59s left") sits right of the button and is half as wide.
		made.x = button.x + button.width * 1.5 + 2 * GAP;
		GameUi.addAbove(made, button);
		made.validateNow();
		made.y = button.y + (button.height - made.height) / 2;
		made.selected = armed;
		Options.own(ID, made);
		made.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) ticked(made));
		content = self;
		pull = button;
		box = made;
		shown = false;
	}

	/** A tick box like the page's own ones (the game's settings tick box when the page has none). */
	static function tickBox(like:Dynamic):Dynamic {
		var type:Dynamic = like != null ? like.constructor : GameUi.definition("DefaultCheckBox_V2");
		if (type == null)
			return null;
		var made:Dynamic = untyped __new__(type);
		made.autoSize = "left";
		made.label = LABEL;
		return made;
	}

	static function ticked(clicked:Dynamic):Void {
		armed = clicked.selected == true;
		armedAt = flash.Lib.getTimer();
		reset();
		if (!armed) {
			Bus.send(ID, "off");
			return;
		}
		Bus.send(ID, "on");
		if (timer == null) {
			timer = new Timer(TICK_MS);
			timer.addEventListener(TimerEvent.TIMER, onTick);
		}
		timer.start();
	}

	/** Stops, unticks the box and tells Arkive why. */
	static function stop(reason:String):Void {
		armed = false;
		reset();
		if (timer != null)
			timer.stop();
		if (box != null && box.selected == true)
			box.selected = false;
		Bus.send(ID, "stop\t" + reason + "\t" + where());
	}

	static function reset():Void {
		dueAt = 0;
		pressedAt = 0;
		pulledAt = 0;
		blindAt = 0;
		shown = false;
	}

	static function onTick(event:Dynamic):Void {
		// Runs outside any hook's guard: a lobby shaped other than expected stops the tweak, not the game's interface.
		try {
			step();
		} catch (error:Dynamic) {
			stop("error");
		}
	}

	static function step():Void {
		if (!armed) {
			timer.stop();
			return;
		}
		if (!Options.on(ID)) {
			stop("off");
			return;
		}
		if (pull == null)
			return;
		if (full(content)) {
			stop("full");
			return;
		}
		if (timeUp()) {
			stop("time");
			return;
		}
		var now = flash.Lib.getTimer();
		var at = where();
		if (at != "open") {
			hidden(now, at);
			return;
		}
		if (!shown) {
			shown = true;
			shownAt = now;
			dueAt = 0;
		}
		if (now - shownAt < SETTLE_MS)
			return;
		var ready = pull.enabled == true && pull.visible == true;
		if (blindAt != 0) {
			// Ready again sooner than the game's wait after a press made while minimised: that press didn't take.
			var took = !ready || now - blindAt >= COOLDOWN_MS - SETTLE_MS;
			if (!took)
				blindFails = true;
			blindAt = 0;
			Bus.send(ID, "blind\t" + (took ? "yes" : "no"));
		}
		if (pressedAt != 0) {
			if (ready && now - pressedAt >= PRESS_CHECK_MS) {
				stop("no effect");
				return;
			}
			if (!ready) {
				pulledAt = pressedAt;
				pressedAt = 0;
				Bus.send(ID, "pulled\t" + at);
			}
			return;
		}
		if (!ready) {
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

	/**
		The window is minimised or closed: the button isn't kept up to date, so press it by the clock, a minute plus the
		delay after the last pull up (or just the delay when there was none yet). Its greying can't be seen, so the press
		is reported as made rather than as a pull up, and checked when the window shows again.
	**/
	static function hidden(now:Int, at:String):Void {
		if (shown) {
			shown = false;
			Bus.send(ID, "hidden\t" + at + "\t" + (pull.enabled == true ? "ready" : "waiting"));
			if (pressedAt != 0)
				pulledAt = pressedAt;
			pressedAt = 0;
			dueAt = 0;
		}
		if (blindFails)
			return;
		if (dueAt == 0) {
			dueAt = (pulledAt == 0 ? now : pulledAt + COOLDOWN_MS) + MIN_DELAY_MS + Std.int(Math.random() * (MAX_DELAY_MS - MIN_DELAY_MS));
			return;
		}
		if (now < dueAt)
			return;
		dueAt = 0;
		pulledAt = blindAt = now;
		GameUi.press(pull);
		Bus.send(ID, "pressed\t" + at);
	}

	/** "open" while the Find Party window shows, "closed" while it is hidden (or anything above it is). */
	static function where():String {
		var at:Dynamic = content;
		if (at == null || at.stage == null)
			return "gone";
		while (at != null) {
			if (at.visible != true)
				return "closed";
			at = at.parent;
		}
		return "open";
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

	/**
		True when every party of the lobby (the member groups not locked or hidden) has all its places taken. A group with
		fewer rows than places, or rows the game doesn't mark, never counts as full.
	**/
	static function full(content:Dynamic):Bool {
		var joining:Dynamic = content == null ? null : content.joiningPage;
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
