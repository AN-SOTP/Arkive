package arkive.tweaks;

import flash.events.Event;
import flash.text.TextField;
import flash.text.TextFormat;

/**
	Netherworld Advisor. In Paradise's Netherworld, two lines under the floor tracker's info label say whether to descend
	or to quit and claim the current floor's chest, and what that's worth: "Descend" / "Expected reward +28%, 5% chance to
	lose it all", in green or red. They're worked out again after every descent and death. Regular Hell has nothing to
	lose, so the lines stay out of it.

	The rules come from the game's own text and tables (TrinityInferno*): a descent moves 1 to 20 floors with equal odds,
	the chest steps up every 10 floors, and the curse kills you on every odd or every even floor. The first death uses a
	revive; a death with none left ends the run with no rewards. Assumed and still to be checked in game: a descent past
	Floor 100 stops on it, and Floor 100 is safe. Which floors kill is read from the run itself: the floor you stand on is
	safe unless you died there.

	The value of stopping on a floor is that floor's Netherworld Bound Gold chest (tier V); the other chests grow with the
	floor in the same proportions, so the percentages hold for any chest you'd pick. Advice for each state is the best of
	stopping and descending, solved exactly over floors, descents left and revives left.

	The floor and deaths come from the floor group's setData/rowUpdate, revives from the frame's heart count methods,
	descents left from the "N Descents Available" label (the game sets it directly, so it's read every frame).
**/
@:keep
class Netherworld {
	static inline var TWEAK = "netherworld";
	static inline var FONT = "$YG760";
	static inline var SIZE = 13;
	static inline var GREEN = "#99FF99";
	static inline var RED = "#FF503E";
	static inline var GOLD = "#FFD200";
	static inline var TOP = 100;
	static inline var MOST_DESCENTS = 16;
	static inline var MOST_REVIVES = 4;
	static inline var GAP = 2;
	static inline var LOG = "netherworld";

	/** The Netherworld tier V Bound Gold chest per 10 floors (TrinityInfernoReward 1124): floors 1-9, 10-19 ... 100. */
	static var CHEST:Array<Float> = [22500, 36000, 47500, 60000, 80000, 110000, 160000, 225000, 325000, 475000, 650000];

	/** Run state per floor group (weak keys): {row, floor, dead, deadly, lucky, log}. */
	static var groups = new flash.utils.Dictionary(true);

	/** Heart counts per frame (weak keys): {start, now}. */
	static var hearts = new flash.utils.Dictionary(true);

	static var memo:Array<Dynamic> = [];
	static var memoDeadly = -1;

	/** TrinityInfernoProgressFrame.configUI: add the two lines under the floor group's info label, refreshed every frame. */
	public static function onFrameConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var frame:Dynamic = self;
		var group:Dynamic = frame.floorGroup_mc;
		if (group == null || group.info_lb == null)
			return;
		var line = new TextField();
		line.selectable = false;
		line.mouseEnabled = false;
		line.multiline = true;
		line.wordWrap = true;
		line.embedFonts = true;
		line.visible = false;
		GameUi.addAbove(line, group.info_lb);
		Options.own(TWEAK, line);
		var state:Dynamic = {line: line, key: "", logged: "", font: null};
		line.addEventListener(Event.ENTER_FRAME, function(event:Dynamic) {
			// Runs outside the hook's guard: a frame shaped other than expected stops the lines, not the tracker.
			try {
				refresh(frame, state);
			} catch (error:Dynamic) {
				line.visible = false;
			}
		});
	}

	/** TrinityInfernoProgressFrame.setAbyssInitHeartCount(count): a new run's revives. */
	public static function onInitHearts(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var count:Int = args[0];
		untyped hearts[self] = {start: count, now: count};
	}

	/** TrinityInfernoProgressFrame.setAbyssCurrentHeartCount(count): revives left. */
	public static function onCurrentHearts(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var count:Int = args[0];
		var known:Dynamic = untyped hearts[self];
		if (known == null)
			untyped hearts[self] = {start: count, now: count};
		else
			known.now = count;
	}

	/** TrinityInfernoFloorGroup.allClear: a new run. */
	public static function onAllClear(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		untyped groups[self] = fresh();
	}

	/** TrinityInfernoFloorGroup.setData(row, data): you're now on that row's floor (row -1 = the lobby). */
	public static function onSetData(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var row:Int = args[0];
		var data:Dynamic = args[1];
		var run = runOf(self);
		run.row = row;
		run.floor = row < 0 ? 0 : floorIn(data);
		run.dead = deadIn(data);
		run.lucky = luckyIn(data);
		learn(run);
		note(run, "set", row, data);
	}

	/** TrinityInfernoFloorGroup.rowUpdate(row, data): the game redraws a row (a death or an effect shown later). */
	public static function onRowUpdate(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var row:Int = args[0];
		var data:Dynamic = args[1];
		var run = runOf(self);
		if (row == run.row) {
			var floor = floorIn(data);
			if (floor > 0)
				run.floor = floor;
			run.dead = deadIn(data);
			run.lucky = luckyIn(data);
			learn(run);
		}
		note(run, "row", row, data);
	}

	static function fresh():Dynamic {
		return {row: -1, floor: 0, dead: false, deadly: -1, lucky: -1, log: 0};
	}

	static function runOf(group:Dynamic):Dynamic {
		var run:Dynamic = untyped groups[group];
		if (run == null) {
			run = fresh();
			untyped groups[group] = run;
		}
		return run;
	}

	/** Which floors kill (0 even, 1 odd): the one you're on does if you died there, else the other kind does. */
	static function learn(run:Dynamic):Void {
		var floor:Int = run.floor;
		if (floor <= 0 || floor >= TOP)
			return;
		var parity = floor % 2;
		run.deadly = run.dead == true ? parity : 1 - parity;
	}

	static function refresh(frame:Dynamic, state:Dynamic):Void {
		var line:TextField = state.line;
		var group:Dynamic = frame.floorGroup_mc;
		var run:Dynamic = untyped groups[group];
		var on = Options.on(TWEAK);
		var nether = netherworld(frame);
		var descents = firstInt(labelText(frame.floorInfo_lb));
		var revives = revivesOf(frame, run);
		var data = Options.get(TWEAK, "showData", false) == true;
		var info:Dynamic = group.info_lb;
		var key = (on ? "on|" : "off|") + (nether ? "n|" : "h|") + (frame.visible == true && frame.stage != null ? "v|" : "|")
			+ (run == null ? "-" : whole(run.floor) + "," + (run.dead == true ? "d" : "") + "," + whole(run.deadly)) + "|"
			+ whole(descents) + "|" + whole(revives) + "|" + (data ? "data|" : "|") + whole(Math.round(info.y)) + ","
			+ whole(Math.round(info.height));
		if (key == state.key)
			return;
		state.key = key;
		if (on && run != null && nether)
			logState(frame, run, descents, revives);
		var advice:String = !on || run == null || !nether || frame.visible != true ? null : adviceFor(run, descents, revives);
		if (data && on && run != null)
			advice = (advice == null ? "" : advice + "<br>") + dataLine(frame, run, descents, revives);
		if (advice == null) {
			line.visible = false;
			return;
		}
		show(line, state, group, advice);
	}

	/** The two lines for this state, or null when there's nothing to decide (lobby, Floor 100, no descents left). */
	static function adviceFor(run:Dynamic, descents:Int, revives:Int):String {
		var floor:Int = run.floor;
		var deadly:Int = run.deadly;
		if (floor <= 0 || floor >= TOP || descents <= 0 || deadly < 0 || revives < 0)
			return null;
		if (descents > MOST_DESCENTS)
			descents = MOST_DESCENTS;
		if (revives > MOST_REVIVES)
			revives = MOST_REVIVES;
		var stop = chest(floor);
		var go = descend(floor, descents, revives, deadly);
		var change = Math.round((go / stop - 1) * 100);
		var lose = revives > 0 ? 0 : loseChance(floor, deadly);
		var risk = revives > 0 ? percent(loseChance(floor, deadly)) + " chance to use your revive" : percent(lose) + " chance to lose it all";
		if (go > stop) {
			return "<font color='" + GREEN + "'>Descend</font><br>Expected reward <font color='" + GOLD + "'>+" + whole(change)
				+ "%</font>, " + risk;
		}
		return "<font color='" + RED + "'>Quit and claim this floor's chest</font><br>Descending: <font color='" + GOLD + "'>"
			+ whole(change) + "%</font>, " + risk;
	}

	/** The share of the next descent's 20 landings that kill (fewer near the end: rolls past 100 stop safely on it). */
	static function loseChance(floor:Int, deadly:Int):Float {
		var deaths = 0;
		for (step in 1...21) {
			var land = floor + step;
			if (land < TOP && land % 2 == deadly)
				deaths++;
		}
		return deaths / 20;
	}

	/** The chest for the floor you stand on: Floor 1-9 is the first step, then one every 10 floors up to 100. */
	static function chest(floor:Int):Float {
		var step = Std.int(floor / 10);
		if (step > 10)
			step = 10;
		return CHEST[step];
	}

	/** Expected reward of descending once from here and then playing on as well as possible. */
	static function descend(floor:Int, descents:Int, revives:Int, deadly:Int):Float {
		if (deadly != memoDeadly) {
			memo = [];
			memoDeadly = deadly;
		}
		var total:Float = 0;
		for (step in 1...21) {
			var land = floor + step;
			if (land > TOP)
				land = TOP;
			if (land < TOP && land % 2 == deadly) {
				if (revives > 0)
					total += best(land, descents - 1, revives - 1, deadly);
			} else {
				total += best(land, descents - 1, revives, deadly);
			}
		}
		return total / 20;
	}

	/** The most a state is worth: the chest here, or descending when that's worth more. */
	static function best(floor:Int, descents:Int, revives:Int, deadly:Int):Float {
		if (descents <= 0 || floor >= TOP)
			return chest(floor);
		var index = (floor * (MOST_DESCENTS + 1) + descents) * (MOST_REVIVES + 1) + revives;
		// Dynamic: a Float array would read a missing entry as NaN, not null.
		var known:Dynamic = memo[index];
		if (known != null) {
			var kept:Float = known;
			return kept;
		}
		var go = descend(floor, descents, revives, deadly);
		var here = chest(floor);
		var value = go > here ? go : here;
		memo[index] = value;
		return value;
	}

	/** Revives left: the hearts the game last showed; without them, one until the first death. */
	static function revivesOf(frame:Dynamic, run:Dynamic):Int {
		var known:Dynamic = untyped hearts[frame];
		if (known != null) {
			var now:Int = known.now;
			return now;
		}
		return run != null && run.dead == true ? 0 : 1;
	}

	/** The frame's background shows its dungeon: frame 1 Hell, 2 Netherworld (dungeonType + 1). */
	static function netherworld(frame:Dynamic):Bool {
		var bg:Dynamic = frame.bg_mc;
		if (bg == null)
			return false;
		var shown:Int = bg.currentFrame;
		return shown == 2;
	}

	/** Puts the lines under the info label, centred across it, in the label's own font. */
	static function show(line:TextField, state:Dynamic, group:Dynamic, html:String):Void {
		var info:Dynamic = group.info_lb;
		var font:TextFormat = state.font;
		if (font == null) {
			var from = formatOf(info);
			if (from != null) {
				font = new TextFormat();
				font.font = from.font;
				font.size = from.size;
				state.font = font;
			}
		}
		var look = new TextFormat();
		look.font = font != null ? font.font : FONT;
		look.size = font != null ? font.size : SIZE;
		look.color = 0xFFFFFF;
		look.align = flash.text.TextFormatAlign.CENTER;
		line.defaultTextFormat = look;
		line.width = info.width > 40 ? info.width : 240;
		line.x = info.x;
		line.htmlText = html;
		// htmlText with font tags drops the face; set it back over the whole text, colours kept.
		var face = new TextFormat();
		face.font = look.font;
		face.size = look.size;
		face.align = flash.text.TextFormatAlign.CENTER;
		line.setTextFormat(face);
		line.height = line.textHeight + 6;
		line.y = info.y + info.height + GAP;
		line.visible = true;
	}

	/** The text format of a game label's first character; null when it shows no text. */
	static function formatOf(owner:Dynamic):TextFormat {
		var field:Dynamic = owner == null ? null : owner.textField;
		if (field == null || field.text == null)
			return null;
		var text:String = field.text;
		if (text.length == 0)
			return null;
		var format:TextFormat = field.getTextFormat(0, 1);
		return format == null || format.font == null || format.font == "" ? null : format;
	}

	static function floorIn(data:Dynamic):Int {
		if (data == null)
			return 0;
		var floor:Dynamic = data.floor;
		if (floor == null || !Tooltips.isString(floor))
			return 0;
		var value = firstInt(Text.stripTags(floor));
		return value < 0 ? 0 : value;
	}

	static function deadIn(data:Dynamic):Bool {
		if (data == null || !Text.has(data, "deadType"))
			return false;
		var dead:Int = data.deadType;
		return dead > -1;
	}

	static function luckyIn(data:Dynamic):Int {
		if (data == null || !Text.has(data, "luckyType"))
			return -1;
		var lucky:Int = data.luckyType;
		return lucky;
	}

	/** A game label's text without tags, "" when it has none. */
	static function labelText(label:Dynamic):String {
		if (label == null)
			return "";
		var html:Dynamic = label.htmlText;
		if (html == null || !Tooltips.isString(html))
			return "";
		return Text.stripTags(html);
	}

	/** The first whole number in the text, or -1. */
	static function firstInt(text:String):Int {
		if (text == null)
			return -1;
		var value = -1;
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c >= "0".code && c <= "9".code)
				value = (value < 0 ? 0 : value * 10) + (c - "0".code);
			else if (value >= 0)
				break;
		}
		return value;
	}

	/** What Arkive reads, for testing: floor, death, deadly floors, descents, revives, effect. */
	static function dataLine(frame:Dynamic, run:Dynamic, descents:Int, revives:Int):String {
		var known:Dynamic = untyped hearts[frame];
		var start:Int = known == null ? -1 : known.start;
		var deadly:Int = run.deadly;
		var bg:Dynamic = frame.bg_mc;
		var shown:Int = bg == null ? 0 : bg.currentFrame;
		return "<font color='#787878'>F" + whole(run.floor) + (run.dead == true ? " died" : "") + " row " + whole(run.row)
			+ " kills " + (deadly == 1 ? "odd" : deadly == 0 ? "even" : "?") + " desc " + whole(descents) + " rev "
			+ whole(revives) + "/" + whole(start) + " fx " + whole(run.lucky) + " bg " + whole(shown) + "</font>";
	}

	/** With "Keep a run log" on, each change of the tracker goes to Arkive (Data Bus "netherworld"). */
	static function note(run:Dynamic, kind:String, row:Int, data:Dynamic):Void {
		if (Options.get(TWEAK, "logRuns", false) != true)
			return;
		var floor:String = data == null || !Tooltips.isString(data.floor) ? "" : Text.stripTags(data.floor);
		var dead:String = data == null || !Text.has(data, "deadType") ? "" : whole(data.deadType);
		var lucky:String = data == null || !Text.has(data, "luckyType") ? "" : whole(data.luckyType);
		var value:String = data == null || !Tooltips.isString(data.luckyValue) ? "" : Text.stripTags(data.luckyValue);
		var tip:String = data == null || !Tooltips.isString(data.luckyTooltip) ? "" : Text.stripTags(data.luckyTooltip);
		Bus.send(LOG, kind + "\t" + whole(row) + "\t" + floor + "\t" + dead + "\t" + lucky + "\t" + value + "\t" + tip);
	}

	static function logState(frame:Dynamic, run:Dynamic, descents:Int, revives:Int):Void {
		if (Options.get(TWEAK, "logRuns", false) != true)
			return;
		var group:Dynamic = frame.floorGroup_mc;
		var text = "ui\t" + whole(run.floor) + "\t" + whole(descents) + "\t" + whole(revives) + "\t"
			+ labelText(frame.currentFloor_lb) + "\t" + labelText(frame.floorPenalty_lb) + "\t" + labelText(group.info_lb);
		if (text == run.logged)
			return;
		run.logged = text;
		Bus.send(LOG, text);
	}

	static function percent(share:Float):String {
		return whole(Math.round(share * 100)) + "%";
	}

	static function whole(value:Dynamic):String {
		var text:String = untyped __global__["String"](value);
		return text;
	}
}
