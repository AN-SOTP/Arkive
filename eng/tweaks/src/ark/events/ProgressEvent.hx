package ark.events;

/** The game's "progressComplete" event, which a number stepper sends when its typed value is final. */
extern class ProgressEvent extends flash.events.Event {
	static var COMPLETE(default, never):String;
	function new(type:String):Void;
}
