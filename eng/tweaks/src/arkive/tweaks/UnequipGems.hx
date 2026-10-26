package arkive.tweaks;

import flash.events.TimerEvent;
import flash.utils.Timer;

/**
	Unequip Gems: "Unequip All" on the character profile's gem page right-clicks each equipped gem in turn, as the
	player would. "Unequip Gems" in roster storage moves every equipped gem that isn't bound to the character into
	roster storage, through a free bag slot (gems can't go straight from the gem slots to storage).
**/
@:keep
class UnequipGems {
	static inline var GEM_SLOTS = 11;
	static inline var BAG_SLOTS = 100;
	static inline var STORAGE_SLOTS = 60;
	static inline var CHARACTER_BOUND = "Bound to Character";
	static inline var CLICK_MS = 150;
	static inline var POLL_MS = 33;
	static inline var RETRY_POLLS = 6;
	static inline var MAX_TRIES = 10;

	static var clicker:Timer;
	static var slots:Dynamic;
	static var nextGem = 0;

	/** CharInfoGemPanel.configUI: the button goes under the gem recommendation button. */
	public static function onGemPanelConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var recommend:Dynamic = self.gemRecommendButton;
		if (!GameUi.on("unequipgems", "profile") || recommend == null || recommend.parent == null)
			return;
		var button:Dynamic = GameUi.button("Unequip All");
		if (button == null)
			return;
		button.width = recommend.width;
		button.height = recommend.height;
		button.x = recommend.x;
		button.y = recommend.y + recommend.height + 6;
		button.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) rightClickAll(self.gemSlotManager));
		GameUi.addAbove(button, recommend);
		Options.own("unequipgems", button);
	}

	static function rightClickAll(manager:Dynamic):Void {
		if (manager == null || clicker != null)
			return;
		slots = manager;
		nextGem = 0;
		clicker = new Timer(CLICK_MS);
		clicker.addEventListener(TimerEvent.TIMER, clickNext);
		clickNext(null);
		if (clicker != null)
			clicker.start();
	}

	/** Right-clicks the next gem slot that holds a gem; stops after the last slot. */
	static function clickNext(event:Dynamic):Void {
		while (nextGem < GEM_SLOTS) {
			var slot:Dynamic = slots.getItemSlot(nextGem);
			nextGem++;
			if (slot != null && slot.content != null) {
				GameUi.rightClickSlot(slot);
				return;
			}
		}
		clicker.stop();
		clicker.removeEventListener(TimerEvent.TIMER, clickNext);
		clicker = null;
		slots = null;
	}

	// Roster storage.

	static var moving = false;
	static var mover:Timer;
	static var storageButton:Dynamic;
	static var gem = 0;
	static var bagCursor = 0;
	static var storageCursor = 0;
	static var bagSlot = -1;
	static var storageSlot = -1;
	/** 1: gem to bag, 2: bag to roster storage. */
	static var leg = 0;
	static var polls = 0;
	static var tries = 0;

	/** ExpeditionWindowGoContent.configUI: the move button's row is split between it and "Unequip Gems". */
	public static function onStorageConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var move:Dynamic = self.expeditionItemMove_btn;
		if (!GameUi.on("unequipgems", "storage") || move == null || move.parent == null)
			return;
		var button:Dynamic = GameUi.copy(move, "Unequip Gems");
		GameUi.addAbove(button, move);
		Options.own("unequipgems", button);
		var parent:Dynamic = move.parent;
		var left:Float = move.x;
		var span:Float = move.width;
		var grid:Dynamic = self.expeditionSlotManager;
		if (grid != null && grid.parent == parent) {
			var bounds:Dynamic = grid.getBounds(parent);
			if (bounds.width > span) {
				left = bounds.x;
				span = bounds.width;
			}
		}
		var width:Float = (span - 8) / 2;
		move.x = left;
		move.width = width;
		button.width = width;
		button.height = move.height;
		button.x = left + width + 8;
		button.y = move.y;
		button.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) storeAll(button));
	}

	static function storeAll(button:Dynamic):Void {
		if (moving)
			return;
		moving = true;
		storageButton = button;
		button.enabled = false;
		gem = 0;
		bagCursor = 0;
		storageCursor = 0;
		mover = new Timer(POLL_MS);
		mover.addEventListener(TimerEvent.TIMER, poll);
		if (startNext())
			mover.start();
	}

	/** Starts moving the next movable gem into a free bag slot; false (and done) when there is none or no room. */
	static function startNext():Bool {
		while (gem < GEM_SLOTS && !(Slots.filled(Slots.GEMS, gem) && !Slots.tooltipShows(Slots.GEMS, gem, CHARACTER_BOUND)))
			gem++;
		if (gem >= GEM_SLOTS)
			return finish();
		bagSlot = Slots.firstEmpty(Slots.BAG, bagCursor, BAG_SLOTS);
		storageSlot = Slots.firstEmpty(Slots.ROSTER_STORAGE, storageCursor, STORAGE_SLOTS);
		if (bagSlot == -1 || storageSlot == -1)
			return finish();
		bagCursor = bagSlot + 1;
		storageCursor = storageSlot + 1;
		leg = 1;
		return ask();
	}

	static function ask():Bool {
		polls = 0;
		tries = 1;
		var asked = leg == 1 ? Slots.move(Slots.GEMS, gem, Slots.BAG, bagSlot) : Slots.move(Slots.BAG, bagSlot, Slots.ROSTER_STORAGE, storageSlot);
		return asked ? true : finish();
	}

	/** Waits for the current move to show up; asks again every few polls, and gives up after several tries. */
	static function poll(event:Dynamic):Void {
		var done = leg == 1
			? Slots.filled(Slots.BAG, bagSlot) && !Slots.filled(Slots.GEMS, gem)
			: Slots.filled(Slots.ROSTER_STORAGE, storageSlot) && !Slots.filled(Slots.BAG, bagSlot);
		if (done) {
			if (leg == 1) {
				leg = 2;
				ask();
			} else {
				gem++;
				startNext();
			}
			return;
		}
		if (++polls < RETRY_POLLS)
			return;
		polls = 0;
		if (++tries > MAX_TRIES) {
			finish();
			return;
		}
		if (leg == 1)
			Slots.move(Slots.GEMS, gem, Slots.BAG, bagSlot);
		else
			Slots.move(Slots.BAG, bagSlot, Slots.ROSTER_STORAGE, storageSlot);
	}

	static function finish():Bool {
		if (mover != null) {
			mover.stop();
			mover.removeEventListener(TimerEvent.TIMER, poll);
			mover = null;
		}
		moving = false;
		storageButton.enabled = true;
		storageButton = null;
		return false;
	}
}
