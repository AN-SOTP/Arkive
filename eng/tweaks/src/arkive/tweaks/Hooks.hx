package arkive.tweaks;

/** A tweak's handler for one hooked method: it gets the object, the call's arguments and `next`, which runs the
    remaining handlers and then the game's own method, and returns what the method should return. */
typedef Handler = Dynamic->Array<Dynamic>->(Void->Dynamic)->Dynamic;

/** The dispatcher every hooked game method calls (Arkive's injector writes `Hooks.run(id, this, [args], original)`). */
@:keep
class Hooks {
	/** Replaced by Arkive with the tweak settings (JSON) when it builds the movie. */
	public static var config:String = "__ARKIVE_TWEAKS_CONFIG__";

	static var handlers:Map<String, Array<Handler>>;

	public static function run(id:String, self:Dynamic, args:Array<Dynamic>, original:Dynamic):Dynamic {
		if (handlers == null) {
			handlers = new Map();
			Tweaks.start(config);
		}
		var list = handlers.get(id);
		if (list == null)
			return Reflect.callMethod(self, original, args);
		return call(list, 0, self, args, original);
	}

	/** Registers a handler for a hook id ("package.Class.method"). */
	public static function on(id:String, handler:Handler):Void {
		var list = handlers.get(id);
		if (list == null) {
			list = [];
			handlers.set(id, list);
		}
		list.push(handler);
	}

	static function call(list:Array<Handler>, index:Int, self:Dynamic, args:Array<Dynamic>, original:Dynamic):Dynamic {
		if (index >= list.length)
			return Reflect.callMethod(self, original, args);
		var ran = false;
		var result:Dynamic = null;
		var next = function():Dynamic {
			ran = true;
			result = call(list, index + 1, self, args, original);
			return result;
		};
		try {
			return list[index](self, args, next);
		} catch (error:Dynamic) {
			// A broken tweak must never break the game's window: finish the call as if the tweak weren't there.
			return ran ? result : next();
		}
	}
}
