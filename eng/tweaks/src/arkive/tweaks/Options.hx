package arkive.tweaks;

import flash.events.Event;
import flash.events.TimerEvent;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.utils.Dictionary;
import flash.utils.Timer;

/**
	The tweak settings. Arkive writes the ones it built with into the movie (RAW):
	{"tweaks":{"extracurrency":{"showLeapstones":true}, ...}, "on":{"extracurrency":true}, "home":"C:/.../tweaks",
	"live":true, "panel":[...]}. "tweaks" holds every tweak the build has hooks for, "on" the ones that act.

	With In-game Tweak Settings built ("live"), Arkive also keeps <home>/live.json ({"on":{..},"tweaks":{..},"ack":".."}),
	the choices saved since the build. It is read when the game starts and every few seconds after. The panel's switches
	change the same store straight away (every movie shares this class), so a change acts at once and lasts the session
	even when Arkive isn't running to save it.

	Values can change while the game runs: a tweak should call get() each time it needs an option, not keep it.
**/
@:keep
class Options {
	/** Replaced by Arkive with the settings (JSON) when it builds the movie. */
	public static var RAW:String = "__ARKIVE_TWEAKS_CONFIG__";

	/**
		Replaced by Arkive with the game tables some tweaks read (Combat Power's, the skills' stagger), kept out of RAW:
		every movie parses RAW as it loads, and the game rebuilds its movies on every trip to character select, so big
		tables there froze the game. A table is read when a tweak first asks for it. Sections are split by \x01, a section
		is "tweak\x02key\x02kind\x02values": kind "s" texts split by \x03, "n" numbers split by commas, "e" an empty list,
		"j" any other value as JSON.
	**/
	public static var TABLES:String = "__ARKIVE_TWEAKS_TABLES__";

	static inline var POLL_MS = 3000;

	static var parsed:Dynamic;

	/** TABLES' sections by "tweak\x02key", split on first use, and the tables read so far by the same key. */
	static var sections:Dynamic;
	static var tables:Dynamic = {};

	/** The choices since the build: Arkive's live.json, then the panel's changes. Null until either exists. */
	static var live:Dynamic;

	/** The text of live.json last read, so an unchanged file doesn't undo the panel's unsaved changes. */
	static var liveText:String = "";

	static var poller:Timer;

	/** Things tweaks added to game windows, with the tweak's id (weak keys: a closed window's buttons go away). */
	static var owned = new Dictionary(true);

	static var listeners:Array<Dynamic> = [];

	/** Setup calls made while their tweak was off, by the window or control they were for (weak keys). */
	static var pending = new Dictionary(true);

	/**
		A setup hook (configUI) ran while its tweak was off: keep the call, so switching the tweak on later sets up this
		window too. The game reads live.json a moment after it starts, so this also covers windows built at login.
	**/
	public static function defer(tweak:String, handler:Dynamic, self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (self == null)
			return;
		var calls:Array<Dynamic> = untyped pending[self];
		if (calls == null) {
			calls = [];
			untyped pending[self] = calls;
		}
		// Setup handlers don't read their arguments, and keeping them (an addedToStage event points back at its control)
		// would keep every control alive through the weak keys.
		calls.push({tweak: tweak, handler: handler});
	}

	/** Runs the setup calls kept for a tweak that has just been switched on, once each. */
	static function replay(tweak:String):Void {
		var owners:Array<Dynamic> = untyped __keys__(pending);
		for (owner in owners) {
			var calls:Array<Dynamic> = untyped pending[owner];
			var kept:Array<Dynamic> = [];
			var due:Array<Dynamic> = [];
			for (call in calls) {
				if (call.tweak == tweak)
					due.push(call);
				else
					kept.push(call);
			}
			if (due.length == 0)
				continue;
			if (kept.length == 0)
				untyped __delete__(pending, owner);
			else
				untyped pending[owner] = kept;
			for (call in due) {
				try {
					call.handler(owner, [], null);
				} catch (error:Dynamic) {}
			}
		}
	}

	static function raw():Dynamic {
		if (parsed == null) {
			try {
				parsed = haxe.format.JsonParser.parse(RAW);
			} catch (error:Dynamic) {
				parsed = {};
			}
			if (parsed.live == true)
				watchLive();
		}
		return parsed;
	}

	/**
		True while the tweak acts. Every hook's wrapper asks this before calling the tweak, so tweaks only need it in code
		that runs outside their hooks (key listeners, timers). Builds without "on" (older ones) have every built tweak on.
	**/
	public static function on(tweak:String):Bool {
		var settings:Dynamic = raw();
		var state:Dynamic = live != null ? live.on : settings.on;
		if (state == null)
			return Text.has(settings.tweaks, tweak);
		var value:Dynamic = untyped state[tweak];
		return value == true;
	}

	/** The option's value, or `fallback` when the tweak or option isn't in the settings. */
	public static function get(tweak:String, key:String, fallback:Dynamic):Dynamic {
		if (live != null) {
			var changed:Dynamic = find(live.tweaks, tweak, key);
			if (changed != null)
				return changed;
		}
		var built:Dynamic = find(raw().tweaks, tweak, key);
		if (built == null)
			built = table(tweak, key);
		return built == null ? fallback : built;
	}

	/** A table from TABLES, read the first time it is asked for; null when the build has none by that name. */
	static function table(tweak:String, key:String):Dynamic {
		var id = tweak + "\x02" + key;
		if (Text.has(tables, id))
			return untyped tables[id];
		if (sections == null) {
			sections = {};
			if (TABLES.indexOf("\x02") >= 0) {
				for (section in TABLES.split("\x01")) {
					var first = section.indexOf("\x02");
					var second = first < 0 ? -1 : section.indexOf("\x02", first + 1);
					if (second >= 0)
						untyped sections[section.substr(0, second)] = section.substr(second + 1);
				}
			}
		}
		if (!Text.has(sections, id))
			return null;
		var text:String = untyped sections[id];
		var value:Dynamic = null;
		try {
			var kind = text.substr(0, 1);
			var values = text.substr(2);
			if (kind == "s")
				value = values.split("\x03");
			else if (kind == "e")
				value = [];
			else if (kind == "n") {
				var numbers:Array<Dynamic> = [];
				for (number in values.split(","))
					numbers.push(untyped __global__["Number"](number));
				value = numbers;
			} else
				value = haxe.format.JsonParser.parse(values);
		} catch (error:Dynamic) {
			value = null;
		}
		untyped tables[id] = value;
		untyped __delete__(sections, id);
		return value;
	}

	static function find(tweaks:Dynamic, tweak:String, key:String):Dynamic {
		if (!Text.has(tweaks, tweak))
			return null;
		var options:Dynamic = untyped tweaks[tweak];
		if (!Text.has(options, key))
			return null;
		return untyped options[key];
	}

	/** The folder Arkive keeps tweak files in (forward slashes); "" when the build doesn't say. */
	public static function home():String {
		var value:Dynamic = raw().home;
		return value == null ? "" : value;
	}

	/**
		What the panel lists, in order: [{id, name, options:[{key, label, kind, choices:[{value, label}]}]}].
		kind is "switch", "single" (one choice) or "list" (several; changed in Arkive only).
	**/
	public static function panel():Array<Dynamic> {
		var value:Dynamic = raw().panel;
		return value == null ? [] : value;
	}

	/** Switches a tweak on or off for this session (the panel tells Arkive separately, to keep it). */
	public static function setOn(tweak:String, value:Bool):Void {
		var state:Dynamic = ensureLive();
		var switches:Dynamic = state.on;
		untyped switches[tweak] = value;
		showOwned(tweak);
		if (value)
			replay(tweak);
		changed(tweak, "");
	}

	/** Changes an option for this session. */
	public static function set(tweak:String, key:String, value:Dynamic):Void {
		var state:Dynamic = ensureLive();
		var tweaks:Dynamic = state.tweaks;
		if (!Text.has(tweaks, tweak))
			untyped tweaks[tweak] = {};
		var options:Dynamic = untyped tweaks[tweak];
		untyped options[key] = value;
		changed(tweak, key);
	}

	/**
		Marks something a tweak added to a game window (a button, a label) as the tweak's: it hides while the tweak is
		switched off and shows again when it's switched back on.
	**/
	public static function own(tweak:String, item:Dynamic):Void {
		if (item == null)
			return;
		untyped owned[item] = tweak;
		if (!on(tweak))
			item.visible = false;
	}

	/** Calls `listener(tweak, key)` after every live change; key "" means switched on or off, "*" that anything may have. */
	public static function listen(listener:Dynamic):Void {
		listeners.push(listener);
	}

	/** The request id of the last change Arkive saved, "" when none. */
	public static function saved():String {
		var value:Dynamic = live == null ? null : live.ack;
		return value == null ? "" : value;
	}

	static function ensureLive():Dynamic {
		if (live == null) {
			var settings:Dynamic = raw();
			var switches:Dynamic = {};
			var built:Dynamic = settings.on;
			var ids:Array<Dynamic> = untyped __keys__(built == null ? settings.tweaks : built);
			for (id in ids) {
				var key:String = id;
				untyped switches[key] = built == null || built[key] == true;
			}
			live = {on: switches, tweaks: {}, ack: ""};
		}
		return live;
	}

	static function showOwned(tweak:String):Void {
		var visible = on(tweak);
		var items:Array<Dynamic> = untyped __keys__(owned);
		for (item in items) {
			var owner:Dynamic = untyped owned[item];
			if (owner == tweak)
				item.visible = visible;
		}
	}

	static function changed(tweak:String, key:String):Void {
		for (listener in listeners) {
			try {
				listener(tweak, key);
			} catch (error:Dynamic) {}
		}
	}

	static function watchLive():Void {
		if (home() == "")
			return;
		readLive(null);
		poller = new Timer(POLL_MS);
		poller.addEventListener(TimerEvent.TIMER, readLive);
		poller.start();
	}

	/** Reads live.json; a new text replaces the live choices (Arkive saved a change, from the panel or the Tweaks tab). */
	public static function readLive(event:Dynamic):Void {
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) {
			var text:String = loader.data;
			if (text == null || text == liveText)
				return;
			liveText = text;
			var next:Dynamic = null;
			try {
				next = haxe.format.JsonParser.parse(text);
			} catch (error:Dynamic) {
				return;
			}
			if (next == null || next.on == null || next.tweaks == null)
				return;
			// Only items whose tweak was switched change, so a tweak's own hiding of its button is left alone.
			var before:Array<String> = [];
			var items:Array<Dynamic> = untyped __keys__(owned);
			for (item in items) {
				var owner:String = untyped owned[item];
				before.push(on(owner) ? "1" : "0");
			}
			live = next;
			for (i in 0...items.length) {
				var item:Dynamic = items[i];
				var owner:String = untyped owned[item];
				var now = on(owner);
				if ((before[i] == "1") != now)
					item.visible = now;
			}
			// Setup calls kept while a tweak was off (windows built before this read, or in a session it was off).
			var waiting:Dynamic = {};
			var owners:Array<Dynamic> = untyped __keys__(pending);
			for (owner in owners) {
				var calls:Array<Dynamic> = untyped pending[owner];
				for (call in calls) {
					var tweak:String = call.tweak;
					untyped waiting[tweak] = true;
				}
			}
			var tweaks:Array<Dynamic> = untyped __keys__(waiting);
			for (tweak in tweaks) {
				var id:String = tweak;
				if (on(id))
					replay(id);
			}
			changed("*", "*");
		});
		loader.addEventListener("ioError", function(e:Dynamic) {});
		loader.addEventListener("securityError", function(e:Dynamic) {});
		loader.load(new URLRequest(home() + "/live.json"));
	}
}
