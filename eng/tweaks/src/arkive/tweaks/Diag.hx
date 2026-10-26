package arkive.tweaks;

/** Errors caught by diagnostic builds' hooks (tweaks-mod --diagnostic), for a tweak to show on screen. */
@:keep
class Diag {
	public static var log:String = "";

	public static function note(message:String):Void {
		if (log.length < 600)
			log = log + message + " | ";
	}
}
