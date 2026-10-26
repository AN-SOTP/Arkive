package arkive.tweaks;

import flash.events.KeyboardEvent;

/**
	Hold Tab to Right-Click: while Tab is held, pointing at an item slot right-clicks it once. The click goes through the
	slot's own mouse-up handler (made callable by plain name in efui_base), so it does exactly what a right-click does.
**/
@:keep
class TabSlot {
	static inline var TAB = 9;

	static var stage:Dynamic;
	static var held = false;
	static var current:Dynamic;

	/** ARKNewSlot.addedToStage: the first slot shown starts listening for Tab on the interface's stage. */
	public static function onAddedToStage(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		listen(self.stage);
	}

	/** ARKNewSlot.handleMouseRollOver: right-click the slot while Tab is held (real pointer moves only). */
	public static function onRollOver(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (held && current != self && untyped __is__(args[0], scaleform.gfx.MouseEventEx)) {
			current = self;
			rightClick(self);
		}
	}

	/** ARKNewSlot.handleMouseRollOut: the next time the pointer comes back to this slot it is clicked again. */
	public static function onRollOut(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (current == self)
			current = null;
	}

	static function listen(on:Dynamic):Void {
		if (on == null || on == stage)
			return;
		stage = on;
		on.addEventListener(KeyboardEvent.KEY_DOWN, onKeyDown, false, 0, false);
		on.addEventListener(KeyboardEvent.KEY_UP, onKeyUp, false, 0, false);
		on.addEventListener(flash.events.Event.DEACTIVATE, onDeactivate, false, 0, false);
	}

	static function onKeyDown(event:Dynamic):Void {
		if (event.keyCode == TAB)
			held = true;
	}

	static function onKeyUp(event:Dynamic):Void {
		if (event.keyCode == TAB)
			held = false;
	}

	/** The game lost focus: a Tab released elsewhere never reaches us. */
	static function onDeactivate(event:Dynamic):Void {
		held = false;
	}

	/** A right-button mouse-up sent to the slot's own handler, as the game's stage listener would after a press. */
	static function rightClick(slot:Dynamic):Void {
		if (scaleform.clik.managers.DragManager.inDrag())
			return;
		var up = new scaleform.gfx.MouseEventEx(flash.events.MouseEvent.MOUSE_UP);
		up.buttonIdx = 1;
		var handler:Dynamic = slot.handleMouseUp;
		slot.addEventListener(flash.events.MouseEvent.MOUSE_UP, handler, false, 0, false);
		slot.dispatchEvent(up);
		slot.removeEventListener(flash.events.MouseEvent.MOUSE_UP, handler, false);
	}
}
