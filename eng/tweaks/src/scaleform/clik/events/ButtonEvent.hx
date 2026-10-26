package scaleform.clik.events;

/** The game's button event (defined in efui_base, shared by every movie). */
extern class ButtonEvent extends flash.events.Event {
	static var CLICK(default, never):String;
	function new(type:String, ?bubbles:Bool, ?cancelable:Bool):Void;
}
