package scaleform.gfx;

/** Scaleform's mouse event, which says which button was used (0 left, 1 right). */
extern class MouseEventEx extends flash.events.MouseEvent {
	var buttonIdx:UInt;
	function new(type:String):Void;
}
