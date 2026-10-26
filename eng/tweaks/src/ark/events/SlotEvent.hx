package ark.events;

/** The game's item slot event (efui_base). */
extern class SlotEvent extends flash.events.Event {
	static var RCLICK(default, never):String;
	static var ROLL_OVER(default, never):String;
	static var ROLL_OUT(default, never):String;
	function new(type:String, bubbles:Bool, cancelable:Bool, mouseIdx:UInt, buttonIdx:UInt, bindID:String, slotID:Int):Void;
}
