package arkive.tweaks;

/**
	The tweak settings Arkive writes into the movie: {"tweaks":{"extracurrency":{"showLeapstones":true}, ...}}. Read
	only when a tweak asks for an option, never on a hook's way in.
**/
@:keep
class Options {
	/** Replaced by Arkive with the settings (JSON) when it builds the movie. */
	public static var RAW:String = "__ARKIVE_TWEAKS_CONFIG__";

	static var parsed:Dynamic;

	/** The option's value, or `fallback` when the tweak or option isn't in the settings. */
	public static function get(tweak:String, key:String, fallback:Dynamic):Dynamic {
		if (parsed == null) {
			try {
				parsed = haxe.format.JsonParser.parse(RAW);
			} catch (error:Dynamic) {
				parsed = {};
			}
		}
		var tweaks:Dynamic = parsed.tweaks;
		if (tweaks == null || !untyped tweaks.hasOwnProperty(tweak))
			return fallback;
		var options:Dynamic = untyped tweaks[tweak];
		return options != null && untyped options.hasOwnProperty(key) ? untyped options[key] : fallback;
	}
}
