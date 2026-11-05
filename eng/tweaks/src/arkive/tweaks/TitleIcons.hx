package arkive.tweaks;

import flash.utils.Dictionary;

/**
	Title Icons Only: on the name tags above players, a title that comes with an icon shows just the icon beside the name.

	The tag is one line of game HTML built by BaseHeadStatus.setCompleteName: `_titleName + " " + _name`, or with
	"&sp;" between them on AncientPcHeadStatus (the tag players get, which also draws the animated Kazeros titles).
	Every title with an icon starts with one <img> tag, so keeping the title's tags and dropping its text leaves the icon.

	The label is redone just before the tag lays itself out (changeHeadStatus, Before), so the game's full title never
	shows; tags already on screen are redone when an option changes or the tweak is switched on or off.
**/
@:keep
class TitleIcons {
	static inline var ID = "titleicons";

	/** The HP gauge type the game gives your own character's tag (HpGauge.HP_GAUGE_TYPE_MINE). */
	static inline var MINE = 6;

	/** Player tags seen (weak keys), so a change of options can redo them. */
	static var tags = new Dictionary(true);

	/** The head status frames (weak keys), to find tags built while the tweak was off. */
	static var frames = new Dictionary(true);

	static var listening = false;

	/** BaseHeadStatus.changeHeadStatus, Before: put the label right before the tag lays it out. */
	public static function beforeLayout(self:Dynamic, args:Array<Dynamic>):Dynamic {
		listen();
		apply(self, false);
		return null;
	}

	/** HeadStatusFrame.configUI: remember the frame. Also replayed when the tweak is switched on. */
	public static function onFrameConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		listen();
		if (self == null)
			return;
		untyped frames[self] = true;
		refresh();
	}

	static function listen():Void {
		if (listening)
			return;
		listening = true;
		Options.listen(function(tweak:String, key:String) {
			if (tweak == ID || key == "*")
				refresh();
		});
	}

	/** Redoes every player tag the tweak knows of or finds under the frames. */
	static function refresh():Void {
		var frameList:Array<Dynamic> = untyped __keys__(frames);
		for (frame in frameList)
			collect(frame, 0);
		var list:Array<Dynamic> = untyped __keys__(tags);
		for (tag in list) {
			try {
				apply(tag, true);
			} catch (error:Dynamic) {}
		}
	}

	/** Finds player tags under a frame (they sit two levels down, in its widget canvas). */
	static function collect(node:Dynamic, depth:Int):Void {
		if (node == null || depth > 3)
			return;
		if (isPlayer(node)) {
			untyped tags[node] = true;
			return;
		}
		if (!(untyped __is__(node, flash.display.DisplayObjectContainer)))
			return;
		var count:Int = node.numChildren;
		for (i in 0...count) {
			var child:Dynamic = null;
			try {
				child = node.getChildAt(i);
			} catch (error:Dynamic) {}
			collect(child, depth + 1);
		}
	}

	static function isPlayer(tag:Dynamic):Bool {
		var linkage:String = null;
		try {
			// Only head statuses have it; other display objects in the frame throw on the lookup.
			linkage = tag.linkageName;
		} catch (error:Dynamic) {
			return false;
		}
		return linkage == "AncientPcHeadStatusMc" || linkage == "PcHeadStatusMc";
	}

	/**
		Sets the tag's label to the game's own text, with the title cut to its icon when the options say so. `redraw` lays
		the tag out again (needed outside the game's own layout pass).
	**/
	static function apply(tag:Dynamic, redraw:Bool):Void {
		if (tag == null || !isPlayer(tag))
			return;
		var title:String = tag._titleName;
		var name:String = tag._name;
		if (title == null || name == null)
			return;
		untyped tags[tag] = true;
		var shown = Options.on(ID) && wanted(tag) ? cut(title) : title;
		var ancient = tag.linkageName == "AncientPcHeadStatusMc";
		var plate:Dynamic = ancient ? tag.ancientNamePlate : tag.namePlate;
		if (plate == null)
			return;
		var label = shown != "" ? shown + (ancient ? "&sp;" : " ") + name : name;
		var current:String = plate.label;
		if (current == label)
			return;
		plate.label = label;
		if (ancient)
			plate.originString = label;
		if (redraw)
			tag.invalidateData();
	}

	/** True when the options cover this tag: your own character's, or another player's. */
	static function wanted(tag:Dynamic):Bool {
		var gauge:Dynamic = tag.hpGauge;
		var type:Int = gauge == null ? -1 : gauge.targetType;
		var key = type == MINE ? "mine" : "others";
		return Options.get(ID, key, true) == true;
	}

	/**
		The title as the tweak shows it: only its tags (the icon, and any colour around it) when it has an icon; empty when
		it has none and "plain" is on; else unchanged. Animated titles are drawn by their own clip and stay as they are.
	**/
	static function cut(title:String):String {
		if (title == "" || title.indexOf("AnimateTitle_") >= 0)
			return title;
		if (title.toLowerCase().indexOf("<img") < 0)
			return Options.get(ID, "plain", false) == true ? "" : title;
		report(title);
		var pieces:Array<String> = [];
		var carried = false;
		var at = 0;
		while (at < title.length) {
			var open = title.indexOf("<", at);
			if (open < 0)
				break;
			var close = title.indexOf(">", open);
			if (close < 0)
				break;
			var tag = title.substring(open, close + 1);
			var lower = tag.toLowerCase();
			// The game's player gives the icon to every following character when nothing comes between the icon and the
			// end of its span (round 1 drew the name as a row of icons), so one space stays where the title's text was.
			if (!carried && pieces.length > 0 && pieces[pieces.length - 1].toLowerCase().indexOf("<img") == 0
				&& lower.indexOf("</img") != 0) {
				pieces.push(" ");
				carried = true;
			}
			pieces.push(tag);
			if (!carried && lower.indexOf("</img") == 0) {
				pieces.push(" ");
				carried = true;
			}
			at = close + 1;
		}
		if (!carried)
			pieces.push(" ");
		return pieces.join("");
	}

	/** Raw titles already sent to Arkive's log this session. */
	static var reported:Dynamic = {};

	static var reportCount = 0;

	/** With "showData" on, sends each different title the game gives (its raw HTML) to Arkive's log, up to 30. */
	static function report(title:String):Void {
		if (Options.get(ID, "showData", false) != true || reportCount >= 30 || Text.has(reported, title))
			return;
		untyped reported[title] = true;
		reportCount++;
		Bus.send(ID, title);
	}
}
