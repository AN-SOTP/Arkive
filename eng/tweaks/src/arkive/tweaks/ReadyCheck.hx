package arkive.tweaks;

import flash.utils.Dictionary;

/**
	Ready Check: entrance prompts with a countdown (raid ready checks, matchmaking) get red warning lines when fewer than
	11 gems are equipped or no feast buff is active. With autoAccept on, such a prompt is accepted by pressing its
	"Accept" button once it has settled, unless it says the keyboard input limit applies. Gems are counted with the
	slot tooltip probe in Slots, as Mari does.
**/
@:keep
class ReadyCheck {
	static inline var COUNTDOWN = "ark.ui.dialogSystem.controls::DialogRemainProgress";
	static inline var LABEL = "DialogLabel330";
	static inline var GEM_SLOTS = 11;
	static inline var KEYBOARD_LIMIT = "Keyboard input limit";
	/** Frames the prompt's lines must stay unchanged before it counts as built, and frames to wait at most. */
	static inline var SETTLE_FRAMES = 10;
	static inline var GIVE_UP_FRAMES = 120;
	static inline var WARNING = "<img src='emoticon_warning_1' width='20' height='20' vspace='-10'></img><FONT COLOR='#C24B46'>";

	/** The quick slot bar's buff list (efui_quickslot), shared by every movie. */
	static var buffs:Dynamic;

	/** Prompts already looked at, and the countdown watch of each one being built. */
	static var seen:Dictionary = new Dictionary(true);
	static var watches:Dictionary = new Dictionary(true);

	/** QuickSlotFrame.configUI: remember the buff list, to look for a feast later. */
	public static function onQuickSlotConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		buffs = self.buffList;
	}

	/** DialogSystemFactory.addSource returns the new prompt; watch it until its lines are in. */
	public static function onAddSource(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var dialog:Dynamic = result;
		if (dialog == null || (untyped seen[dialog]) == true)
			return;
		untyped seen[dialog] = true;
		untyped watches[dialog] = {frames: 0, settled: 0, lines: -1};
		dialog.addEventListener("enterFrame", onFrame);
	}

	public static function onFrame(event:Dynamic):Void {
		var dialog:Dynamic = event.currentTarget;
		var watch:Dynamic = untyped watches[dialog];
		var middle:Dynamic = dialog.middleComponent;
		if (watch == null || dialog.stage == null || middle == null) {
			stop(dialog);
			return;
		}
		watch.frames = watch.frames + 1;
		if (watch.frames > GIVE_UP_FRAMES) {
			stop(dialog);
			return;
		}
		var countdown:Dynamic = find(middle, COUNTDOWN, 3);
		if (countdown == null)
			return;
		var lines:Int = countdown.parent.numChildren;
		watch.settled = lines == watch.lines ? watch.settled + 1 : 0;
		watch.lines = lines;
		if (watch.settled < SETTLE_FRAMES)
			return;
		stop(dialog);
		warn(dialog);
		if (Options.get("readycheck", "autoAccept", false) == true && !says(countdown.parent, KEYBOARD_LIMIT))
			accept(dialog);
	}

	static function stop(dialog:Dynamic):Void {
		dialog.removeEventListener("enterFrame", onFrame);
		untyped __delete__(watches, dialog);
	}

	static function warn(dialog:Dynamic):Void {
		var gems = 0;
		for (slot in 0...GEM_SLOTS)
			if (Slots.filled(Slots.GEMS, slot))
				gems++;
		if (gems < GEM_SLOTS)
			line(dialog, gems + " of " + GEM_SLOTS + " gems equipped");
		if (!feastActive())
			line(dialog, "No feast active");
	}

	static function line(dialog:Dynamic, text:String):Void {
		var label:Dynamic = dialog.addComponent(LABEL);
		if (label != null)
			label.htmlText = WARNING + text + "</FONT>";
	}

	/** Presses the prompt's Accept button the way the game presses its buttons, if it is there and enabled. */
	static function accept(dialog:Dynamic):Void {
		var bottom:Dynamic = dialog.bottomComponent;
		var button:Dynamic = bottom == null ? null : bottom.btn_positive;
		if (button != null && button.enabled == true && button.visible == true)
			GameUi.press(button);
	}

	/** True when one of the container's lines shows this text. */
	static function says(container:Dynamic, text:String):Bool {
		for (i in 0...container.numChildren) {
			var child:Dynamic = container.getChildAt(i);
			if (untyped __in__("htmlText", child)) {
				var html:String = child.htmlText;
				if (html != null && plain(html).indexOf(text) != -1)
					return true;
			}
		}
		return false;
	}

	/** The first display object of this class within `depth` levels below the container. */
	static function find(container:Dynamic, className:String, depth:Int):Dynamic {
		for (i in 0...container.numChildren) {
			var child:Dynamic = container.getChildAt(i);
			var name:String = untyped __global__["flash.utils.getQualifiedClassName"](child);
			if (name == className)
				return child;
			if (depth > 0 && (untyped __is__(child, flash.display.DisplayObjectContainer))) {
				var inner:Dynamic = find(child, className, depth - 1);
				if (inner != null)
					return inner;
			}
		}
		return null;
	}

	/** True when a buff on the quick slot bar is a feast (its tooltip has a line ending in "Feast" or with "Feast:"). */
	static function feastActive():Bool {
		var bar:Dynamic = buffs;
		if (bar == null || bar._pool == null || bar._pool.activeList == null)
			return false;
		var slots:Array<Dynamic> = bar._pool.activeList;
		for (slot in slots) {
			if (slot == null || slot.data == null)
				continue;
			var parts = Tooltips.boxes(slot.binding, slot);
			if (parts == null)
				continue;
			for (part in parts) {
				var html = part == null ? null : Tooltips.htmlOf(part.value);
				if (html == null)
					continue;
				for (text in plain(html).split("\n")) {
					var shown = StringTools.trim(text);
					if (StringTools.endsWith(shown, "Feast") || shown.indexOf("Feast:") != -1)
						return true;
				}
			}
		}
		return false;
	}

	/** Game HTML as plain text, with <br> as a line break. */
	static function plain(html:String):String {
		return Text.stripTags(html.split("<br>").join("\n").split("<BR>").join("\n").split("<br/>").join("\n").split("<BR/>").join("\n"));
	}
}
