package arkive.tweaks;

import flash.utils.Dictionary;

/**
	Chest Opening Tweaks:
	- "Open All" beside the chest window's open button, and on each row of a selectable chest: sets the count to the
	  most you can open (the game's own max button), then presses open.
	- Open Confirmation: when a chest's confirmation dialog has a check box and the open button was pressed in the last
	  3 seconds, tick it (Auto-Tick), or tick it and press OK (Auto-Accept).
**/
@:keep
class BoxTweaks {
	static inline var OPEN_SYMBOL:String = "AnimatedButton_renew12_V2_Boxsystem_Open_Yellow";
	static inline var SELECT_CONTENT:String = "ark.ui.boxSystem.BoxSystemSelectItem";
	static inline var CHECKBOX:String = "ark.ui.dialogSystem.controls.DialogCheckBox";
	static inline var DIALOG:String = "ark.ui.dialogSystem.controls.DialogWindow";
	static inline var GAP:Int = 4;
	static inline var ROW_BUTTON_WIDTH:Int = 72;
	/** How many frames to wait for a game button to become clickable. */
	static inline var WAIT_FRAMES:Int = 30;
	/** How long after pressing open a confirmation dialog counts as the chest's. */
	static inline var OPEN_WINDOW_MS:Int = 3000;

	static var openedAt:Int = -OPEN_WINDOW_MS;
	static var rowButtons:Dictionary = new Dictionary(true);

	/** BoxSystemItemContent.configUI (chests that give everything) */
	public static function onItemConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		attach(self.receiveAllBtn, self.receiveBtn);
	}

	/** BoxSystemSelectItem.configUI (chests where you pick an item) */
	public static function onSelectConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		attach(self.receiveAllBtn, self.receiveBtn);
	}

	/** BoxSystemRandomItem.configUI (random chests) */
	public static function onRandomConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		attach(self.play10Btn, self.playBtn);
	}

	/** Splits the open button in two and puts "Open All" in its right half; `max` is the game's max-count button. */
	static function attach(open:Dynamic, max:Dynamic):Void {
		if (open == null || max == null || open.parent == null)
			return;
		open.addEventListener("buttonClick", noteOpened);
		if (!GameUi.on("boxtweaks", "openAllButton"))
			return;
		var all:Dynamic = GameUi.buttonOf("Open All", [OPEN_SYMBOL]);
		if (all == null)
			return;
		var half:Float = (open.width - GAP) / 2;
		open.width = half;
		all.width = half;
		all.height = open.height;
		all.x = open.x + half + GAP;
		all.y = open.y;
		GameUi.addAbove(all, open);
		all.visible = open.visible;
		all.addEventListener("enterFrame", function(event:Dynamic):Void {
			if (all.enabled != open.enabled)
				all.enabled = open.enabled;
			if (all.visible != open.visible)
				all.visible = open.visible;
		});
		all.addEventListener("buttonClick", function(event:Dynamic):Void {
			if (open.enabled)
				openAll(open, max);
		});
	}

	/** BoxSystemRollingItem.setData(data): an "Open All" button on each row of a selectable chest's list. */
	public static function onSetData(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (!GameUi.on("boxtweaks", "openAllButton"))
			return;
		var content:Dynamic = GameUi.ancestor(self, SELECT_CONTENT);
		var preview:Dynamic = self.previewBtn;
		if (content == null || preview == null)
			return;
		var button:Dynamic = untyped rowButtons[self];
		if (button == null) {
			button = GameUi.buttonOf("Open All", [OPEN_SYMBOL]);
			if (button == null)
				return;
			untyped rowButtons[self] = button;
			button.width = ROW_BUTTON_WIDTH;
			button.y = preview.y + (preview.height - button.height) / 2;
			self.addChild(button);
			button.addEventListener("buttonClick", function(event:Dynamic):Void {
				event.stopPropagation();
				openAll(content.receiveAllBtn, content.receiveBtn);
			});
		}
		var data:Dynamic = args[0];
		button.visible = data != null && data.available == true && data.expire != true;
		var right:Float = preview.visible == true ? preview.x - GAP : preview.x + preview.width;
		button.x = right - button.width;
		var description:Dynamic = self.description;
		if (description != null && description.x + description.width > button.x - GAP)
			description.width = button.x - GAP - description.x;
	}

	/** Waits for open to be clickable, presses max, then on the next frame presses open. */
	static function openAll(open:Dynamic, max:Dynamic):Void {
		if (open == null || max == null)
			return;
		var left = WAIT_FRAMES;
		var maxed = false;
		function step(event:Dynamic):Void {
			if (open.enabled != true) {
				left--;
				if (left <= 0)
					open.removeEventListener("enterFrame", step);
				return;
			}
			if (!maxed) {
				maxed = true;
				GameUi.press(max);
				return;
			}
			open.removeEventListener("enterFrame", step);
			GameUi.press(open);
		}
		open.addEventListener("enterFrame", step);
	}

	static function noteOpened(event:Dynamic):Void {
		openedAt = flash.Lib.getTimer();
	}

	/** MiddleContentGroup.addComponent(name, w, h) (after, result kept): a check box in a dialog shown just after opening a chest. */
	public static function onAddComponent(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Dynamic {
		if (result == null || flash.Lib.getTimer() - openedAt >= OPEN_WINDOW_MS)
			return null;
		var confirm:String = Options.get("boxtweaks", "confirm", "normal");
		if (confirm != "tick" && confirm != "accept")
			return null;
		if (!GameUi.isA(result, CHECKBOX))
			return null;
		var box:Dynamic = result;
		var accept = confirm == "accept";
		function tick(event:Dynamic):Void {
			box.removeEventListener("enterFrame", tick);
			if (box.selected != true && box.enabled == true && box.mouseHit_mc != null)
				GameUi.click(box.mouseHit_mc);
			if (box.selected != true)
				box.selected = true;
			if (accept)
				pressOk(GameUi.ancestor(self, DIALOG));
		}
		box.addEventListener("enterFrame", tick);
		return null;
	}

	/** Clicks the dialog's OK button once it is shown and clickable (within WAIT_FRAMES frames). */
	static function pressOk(dialog:Dynamic):Void {
		if (dialog == null || dialog.bottomComponent == null)
			return;
		var ok:Dynamic = dialog.bottomComponent.btn_positive;
		if (ok == null)
			return;
		var left = WAIT_FRAMES;
		function wait(event:Dynamic):Void {
			if (ok.enabled == true && ok.visible == true) {
				ok.removeEventListener("enterFrame", wait);
				GameUi.click(ok);
			} else {
				left--;
				if (left <= 0)
					ok.removeEventListener("enterFrame", wait);
			}
		}
		ok.addEventListener("enterFrame", wait);
	}
}
