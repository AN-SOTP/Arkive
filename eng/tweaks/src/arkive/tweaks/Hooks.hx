package arkive.tweaks;

/** Runs before a hooked game method; return false to skip the game's own method. `args` may be changed in place. */
typedef Before = Dynamic->Array<Dynamic>->Bool;

/** Runs after a hooked game method (or instead of it, when a Before skipped it); returns the method's result. */
typedef After = Dynamic->Array<Dynamic>->Dynamic->Dynamic;

/**
	The dispatcher every hooked game method calls. Arkive's injector writes:
	`args = [arguments]; if (Hooks.before(id, this, args)) result = this.original(args...); return Hooks.after(id, this, args, result);`
	with both calls guarded, so a failure here falls back to the game's own method. Ids are "package.Class.method".
**/
@:keep
class Hooks {
	/** Replaced by Arkive with the tweak settings (JSON) when it builds the movie. */
	public static var config:String = "__ARKIVE_TWEAKS_CONFIG__";

	/** The first error the tweak code hit, for diagnostic builds. */
	public static var lastError:String = null;

	static var befores:Map<String, Array<Before>>;
	static var afters:Map<String, Array<After>>;

	public static function before(id:String, self:Dynamic, args:Array<Dynamic>):Bool {
		if (!start())
			return true;
		var list = befores.get(id);
		if (list == null)
			return true;
		var run = true;
		var i = 0;
		while (i < list.length) {
			try {
				if (list[i](self, args) == false)
					run = false;
			} catch (error:Dynamic) {
				note(error);
			}
			i++;
		}
		return run;
	}

	public static function after(id:String, self:Dynamic, args:Array<Dynamic>, result:Dynamic):Dynamic {
		var list = start() ? afters.get(id) : null;
		if (list != null) {
			var i = 0;
			while (i < list.length) {
				try {
					result = list[i](self, args, result);
				} catch (error:Dynamic) {
					note(error);
				}
				i++;
			}
		}
		#if arkive_diag
		if (lastError != null && self != null) {
			try {
				if (self.itemLevel_txt != null)
					self.itemLevel_txt.htmlText = "<font color='#ff5050'>" + lastError + "</font>";
			} catch (error:Dynamic) {}
		}
		#end
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

	/** Records an error once, as "#id message" when it is a Flash error. */
	public static function note(error:Dynamic):Void {
		if (lastError != null)
			return;
		try {
			var id:Dynamic = untyped error.errorID;
			var message:Dynamic = untyped error.message;
			lastError = message != null ? "#" + id + " " + message : Std.string(error);
		} catch (e:Dynamic) {
			lastError = "unknown error";
		}
	}

	static var started = false;

	static function start():Bool {
		if (started)
			return befores != null;
		started = true;
		try {
			befores = new Map();
			afters = new Map();
			Tweaks.start(config);
			return true;
		} catch (error:Dynamic) {
			note(error);
			befores = null;
			return false;
		}
	}
}
