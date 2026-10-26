package arkive.tweaks;

/** Turns on the tweaks the settings name. Settings: {"tweaks": {"lobbycp": {}, "extracurrency": {"showLeapstones": true}}}. */
@:keep
class Tweaks {
	public static var options:Dynamic = {};

	public static function start(config:String):Void {
		var settings:Dynamic = null;
		try {
			settings = haxe.Json.parse(config);
		} catch (error:Dynamic) {
			Hooks.note(error);
			return;
		}
		var tweaks:Dynamic = settings == null ? null : settings.tweaks;
		if (tweaks == null)
			return;
		for (name in Reflect.fields(tweaks)) {
			var register = registry.get(name);
			if (register == null)
				continue;
			try {
				register(Reflect.field(tweaks, name));
			} catch (error:Dynamic) {
				Hooks.note(error);
			}
		}
	}

	static var registry:Map<String, Dynamic->Void> = [
		"lobbycp" => LobbyCp.register,
	];
}
