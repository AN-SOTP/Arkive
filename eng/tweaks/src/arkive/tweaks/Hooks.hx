package arkive.tweaks;

/** Runs before a hooked game method; return false to skip the game's own method. `args` may be changed in place. */
typedef Before = Dynamic->Array<Dynamic>->Bool;

/** Runs after a hooked game method (or instead of it, when a Before skipped it); returns the method's result. */
typedef After = Dynamic->Array<Dynamic>->Dynamic->Dynamic;

/**
	The dispatcher every hooked game method calls. Arkive's injector writes:
	`args = [arguments]; if (Hooks.before(id, this, args)) result = this.original(args...); return Hooks.after(id, this, args, result);`
	Ids are "package.Class.method".
**/
@:keep
class Hooks {
	/** Replaced by Arkive with the tweak settings (JSON) when it builds the movie. */
	public static var config:String = "__ARKIVE_TWEAKS_CONFIG__";

	static var befores:Map<String, Array<Before>>;
	static var afters:Map<String, Array<After>>;

	public static function before(id:String, self:Dynamic, args:Array<Dynamic>):Bool {
		start();
		var list = befores.get(id);
		if (list == null)
			return true;
		var run = true;
		var i = 0;
		while (i < list.length) {
			try {
				if (list[i](self, args) == false)
					run = false;
			} catch (error:Dynamic) {}
			i++;
		}
		return run;
	}

	public static function after(id:String, self:Dynamic, args:Array<Dynamic>, result:Dynamic):Dynamic {
		start();
		var list = afters.get(id);
		if (list == null)
			return result;
		var i = 0;
		while (i < list.length) {
			try {
				result = list[i](self, args, result);
			} catch (error:Dynamic) {}
			i++;
		}
		return result;
	}

	public static function onBefore(id:String, handler:Before):Void {
		var list = befores.get(id);
		if (list == null) {
			list = [];
			befores.set(id, list);
		}
		list.push(handler);
	}

	public static function onAfter(id:String, handler:After):Void {
		var list = afters.get(id);
		if (list == null) {
			list = [];
			afters.set(id, list);
		}
		list.push(handler);
	}

	static function start():Void {
		if (befores != null)
			return;
		befores = new Map();
		afters = new Map();
		Tweaks.start(config);
	}
}
