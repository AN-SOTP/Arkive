package arkive.tweaks;

import flash.display.Shape;
import flash.display.Sprite;
import flash.events.MouseEvent;
import flash.events.TimerEvent;
import flash.geom.Point;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	Spotify Player: a small player under the minimap for the Spotify app running on the PC. It shows the song and
	artist with the time, and has previous, play or pause, stop and next, Spotify's volume, and shuffle and repeat when
	Spotify lets them be changed. Drag it by its background; the fold button turns it into a thin now-playing strip.

	The game can't reach Spotify, so Arkive does it (through Windows' media controls): it writes Spotify's state to
	<home>/spotify/now.txt every second, which the player reads, and the player sends its buttons, volume and position
	on the Data Bus (channel "spotify"). Without Arkive running the player says so and shows no buttons.

	The game's own controls are used: its small plain buttons with the media marks drawn in its label colour, its tick
	boxes and slider (componentsV2), and the minimap's label font.
**/
@:keep
class MusicPlayer {
	static inline var ID = "spotify";
	static inline var POLL_MS = 1000;
	/** Reads without a new write from Arkive after which it counts as not running. */
	static inline var STALE_READS = 4;
	/** After a volume drag, the file's volume is ignored this long, so the slider doesn't jump back. */
	static inline var VOLUME_HOLD_MS = 2500;
	static inline var VOLUME_SEND_MS = 200;

	static inline var WIDTH = 260;
	static inline var PAD = 10;
	static inline var GAP = 6;
	static inline var BUTTON = 28;
	static inline var BUTTON_HEIGHT = 24;
	static inline var STRIP = 26;
	/** The game's small plain button (12 px label), the base of the media buttons. */
	static inline var SMALL_BUTTON = "AnimatedButton_renew12_V2";

	static inline var WHITE = 0xFFFFFF;
	static inline var TEXT = 0xD8D8D8;
	static inline var DIM = 0x8C8C8C;
	static inline var GOLD = 0xE3C27F;

	/** Everything the player says, in one place for translating. */
	static var WORDS:Dynamic = {
		title: "Spotify Player",
		noArkive: "Start Arkive to use the player.",
		noSpotify: "Open Spotify on this PC.",
		nothing: "Nothing is playing in Spotify.",
		unknown: "Unknown Artist",
		previous: "Previous",
		play: "Play",
		pause: "Pause",
		stop: "Stop",
		next: "Next",
		fold: "Fold",
		unfold: "Unfold",
		volume: "Volume",
		shuffle: "Shuffle",
		repeat: "Repeat",
		repeatAll: "Repeat All",
		repeatOne: "Repeat One"
	};

	static var font:String = "$YG760";
	static var embed = true;

	/** Minimap frames (weak keys), with their default spot for the player: {x, y}. */
	static var hosts = new flash.utils.Dictionary(true);
	static var host:Dynamic;

	static var player:Sprite;
	static var back:Shape;
	static var title:TextField;
	static var artist:TextField;
	static var time:TextField;
	static var progress:Shape;
	static var volumeText:TextField;
	static var speaker:Shape;
	static var previousButton:Dynamic;
	static var playButton:Dynamic;
	static var playMark:Shape;
	static var stopButton:Dynamic;
	static var nextButton:Dynamic;
	static var stripPlay:Dynamic;
	static var stripMark:Shape;
	static var foldButton:Dynamic;
	static var unfoldButton:Dynamic;
	static var slider:Dynamic;
	static var shuffleBox:Dynamic;
	static var repeatBox:Dynamic;

	static var timer:Timer;
	static var reading = false;
	static var lastSequence = "";
	static var staleReads = STALE_READS;

	/** The last state read: "noarkive", "none", "playing", "paused" or "stopped", and its fields. */
	static var state = "noarkive";
	static var song = "";
	static var singer = "";
	static var position = 0;
	static var length = 0;
	static var can = "";
	static var shuffle = "";
	static var repeat = "";
	static var volume = -1;

	static var folded = false;
	static var offsetX:Float = 0;
	static var offsetY:Float = 0;
	static var layoutRead = false;

	static var dragging = false;
	static var grabX:Float = 0;
	static var grabY:Float = 0;

	static var settingSlider = false;
	static var volumeAt = -VOLUME_HOLD_MS;
	static var volumeWanted = -1;
	static var volumeSender:Timer;

	/** MiniMapFrame.configUI (and MiniMapExFrame's): note the frame, then build the player once and show it under it. */
	public static function onMiniMapConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		// The map's own backing (MiniMapExFrame has its mask instead); the player goes under it, right edges lined up.
		var area:Dynamic = self.background != null ? self.background : self.minimapMaskMc != null ? self.minimapMaskMc : self;
		var bounds:Dynamic = area.getBounds(self);
		untyped hosts[self] = {x: bounds.x + bounds.width - WIDTH, y: bounds.y + bounds.height + GAP};
		gameFont(self);
		if (player == null && !build())
			return;
		if (timer == null) {
			timer = new Timer(POLL_MS);
			timer.addEventListener(TimerEvent.TIMER, onTick);
			timer.start();
		}
		attach();
		poll();
	}

	/** The minimap's area name label's font, the HUD's own. */
	static function gameFont(self:Dynamic):Void {
		var label:Dynamic = self.Areaname_Minimap;
		var field:Dynamic = label == null ? null : label.textField;
		if (field == null)
			return;
		var format:Dynamic = field.getTextFormat();
		if (format != null && format.font != null) {
			font = format.font;
			embed = field.embedFonts;
		}
	}

	/** Builds the player from the game's controls; false while componentsV2 isn't loaded (the next configUI tries again). */
	static function build():Bool {
		if (GameUi.definition(SMALL_BUTTON) == null || GameUi.definition("DefaultCheckBox_V2") == null)
			return false;
		player = new Sprite();
		player.name = "arkiveSpotifyPlayer";
		back = new Shape();
		player.addChild(back);
		player.addEventListener(MouseEvent.MOUSE_DOWN, grab);

		title = text(PAD, 6, WIDTH - 2 * PAD - 24, 14, WHITE, false);
		artist = text(PAD, 26, WIDTH - 2 * PAD - 80, 12, DIM, false);
		time = text(WIDTH - PAD - 80, 26, 80, 12, DIM, true);
		progress = new Shape();
		progress.x = PAD;
		progress.y = 47;
		player.addChild(progress);

		var x:Float = PAD;
		previousButton = button(x, 54, WORDS.previous, "previous");
		previousButton.addChild(mark("previous"));
		x += BUTTON + 4;
		playButton = button(x, 54, WORDS.play, "playpause");
		playMark = mark("play");
		playButton.addChild(playMark);
		x += BUTTON + 4;
		stopButton = button(x, 54, WORDS.stop, "stop");
		stopButton.addChild(mark("stop"));
		x += BUTTON + 4;
		nextButton = button(x, 54, WORDS.next, "next");
		nextButton.addChild(mark("next"));
		x += BUTTON + 12;

		speaker = mark("speaker");
		speaker.x = x;
		speaker.y = 54 + (BUTTON_HEIGHT - 12) / 2;
		player.addChild(speaker);
		x += 16;
		slider = make("ARKDefaultSlider_V2");
		if (slider != null) {
			slider.minimum = 0;
			slider.maximum = 100;
			slider.snapping = true;
			slider.snapInterval = 1;
			try {
				slider.showValue = false;
			} catch (error:Dynamic) {}
			slider.width = WIDTH - PAD - 28 - x;
			slider.x = x;
			slider.y = 54 + (BUTTON_HEIGHT - 22) / 2;
			slider.addEventListener("valueChange", slid);
			player.addChild(slider);
		}
		volumeText = text(WIDTH - PAD - 26, 58, 26, 12, DIM, true);

		shuffleBox = tickBox(PAD, 84, WORDS.shuffle, "shuffle");
		repeatBox = tickBox(PAD + 100, 84, WORDS.repeat, "repeat");

		// The strip: play or pause, then the song, and the unfold button.
		stripPlay = button(PAD - 4, 2, WORDS.play, "playpause");
		stripPlay.width = 24;
		stripPlay.height = 22;
		stripMark = mark("play");
		stripMark.x = 6;
		stripMark.y = 5;
		stripPlay.addChild(stripMark);

		foldButton = foldControl("AnimatedButton_Minus_V2", WORDS.fold, true);
		unfoldButton = foldControl("AnimatedButton_Plus_V2", WORDS.unfold, false);
		Options.own(ID, player);
		render();
		return true;
	}

	/** A small plain game button that sends `command` when clicked. */
	static function button(x:Float, y:Float, tooltip:String, command:String):Dynamic {
		var made:Dynamic = GameUi.buttonOf("", [SMALL_BUTTON, GameUi.PLAIN_BUTTON]);
		made.x = x;
		made.y = y;
		made.width = BUTTON;
		made.height = BUTTON_HEIGHT;
		try {
			made.tooltip = tooltip;
		} catch (error:Dynamic) {}
		made.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
			event.stopPropagation();
			pressed(command);
		});
		player.addChild(made);
		return made;
	}

	/** The game's tick box with this label, sending `command` when clicked. */
	static function tickBox(x:Float, y:Float, label:String, command:String):Dynamic {
		var box:Dynamic = make("DefaultCheckBox_V2");
		box.autoSize = "left";
		box.label = label;
		box.x = x;
		box.y = y;
		box.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
			event.stopPropagation();
			pressed(command);
		});
		player.addChild(box);
		return box;
	}

	/** The fold (minus) or unfold (plus) button at the top right; a small plain button with a drawn mark without the symbol. */
	static function foldControl(symbol:String, tooltip:String, fold:Bool):Dynamic {
		var made:Dynamic = make(symbol);
		if (made == null) {
			made = GameUi.buttonOf("", [SMALL_BUTTON, GameUi.PLAIN_BUTTON]);
			made.width = 20;
			made.height = 18;
			made.addChild(mark(fold ? "minus" : "plus"));
		}
		try {
			made.tooltip = tooltip;
		} catch (error:Dynamic) {}
		made.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
			event.stopPropagation();
			folded = fold;
			render();
			sendLayout();
		});
		player.addChild(made);
		return made;
	}

	static function make(symbol:String):Dynamic {
		var type:Dynamic = GameUi.definition(symbol);
		return type == null ? null : untyped __new__(type);
	}

	/**
		A media mark drawn in the game's label colour, about 10 px, for a 28 x 24 button: "previous", "next", "play",
		"pause", "stop", "speaker", "minus" or "plus".
	**/
	static function mark(kind:String):Shape {
		var shape = new Shape();
		drawMark(shape, kind);
		shape.x = (BUTTON - 12) / 2;
		shape.y = (BUTTON_HEIGHT - 12) / 2;
		return shape;
	}

	static function drawMark(shape:Shape, kind:String):Void {
		var g = shape.graphics;
		g.clear();
		g.beginFill(TEXT, 1);
		switch (kind) {
			case "previous":
				g.drawRect(1, 1, 2, 10);
				g.moveTo(11, 1);
				g.lineTo(4, 6);
				g.lineTo(11, 11);
				g.lineTo(11, 1);
			case "next":
				g.moveTo(1, 1);
				g.lineTo(8, 6);
				g.lineTo(1, 11);
				g.lineTo(1, 1);
				g.drawRect(9, 1, 2, 10);
			case "play":
				g.moveTo(2, 0);
				g.lineTo(11, 6);
				g.lineTo(2, 12);
				g.lineTo(2, 0);
			case "pause":
				g.drawRect(2, 1, 3, 10);
				g.drawRect(7, 1, 3, 10);
			case "stop":
				g.drawRect(1, 1, 10, 10);
			case "speaker":
				g.drawRect(0, 4, 3, 4);
				g.moveTo(3, 4);
				g.lineTo(7, 1);
				g.lineTo(7, 11);
				g.lineTo(3, 8);
				g.lineTo(3, 4);
				g.endFill();
				g.lineStyle(1, TEXT, 0.8);
				g.moveTo(9, 4);
				g.curveTo(10.5, 6, 9, 8);
				return;
			case "minus":
				g.drawRect(1, 5, 10, 2);
			case "plus":
				g.drawRect(1, 5, 10, 2);
				g.drawRect(5, 1, 2, 10);
		}
		g.endFill();
	}

	/** Puts the player on the minimap that shows (moving it when the game swaps minimaps), at its saved place. */
	static function attach():Void {
		var best:Dynamic = null;
		var frames:Array<Dynamic> = untyped __keys__(hosts);
		for (frame in frames) {
			if (frame.stage != null && showing(frame)) {
				best = frame;
				break;
			}
		}
		if (best == null || best == host)
			return;
		host = best;
		best.addChild(player);
		place();
	}

	/** True when the object and every parent are visible. */
	static function showing(at:Dynamic):Bool {
		while (at != null) {
			if (at.visible != true)
				return false;
			at = at.parent;
		}
		return true;
	}

	static function place():Void {
		if (host == null)
			return;
		var spot:Dynamic = untyped hosts[host];
		if (spot == null)
			return;
		player.x = Math.round(spot.x + offsetX);
		player.y = Math.round(spot.y + offsetY);
	}

	static function onTick(event:Dynamic):Void {
		// Runs outside any hook's guard: a surprise here stops this tick, not the game's interface.
		try {
			if (!Options.on(ID) || player == null)
				return;
			if (host == null || host.stage == null || !showing(host)) {
				host = null;
				attach();
			}
			poll();
		} catch (error:Dynamic) {}
	}

	/** Reads Arkive's file (one read at a time); a missing file or one Arkive stopped writing means it isn't running. */
	static function poll():Void {
		if (reading || Options.home() == "")
			return;
		reading = true;
		var loader = new URLLoader();
		loader.addEventListener("complete", function(e:Dynamic) {
			reading = false;
			try {
				var data:String = loader.data;
				read(data);
			} catch (error:Dynamic) {}
		});
		loader.addEventListener("ioError", function(e:Dynamic) {
			reading = false;
			missed();
		});
		loader.addEventListener("securityError", function(e:Dynamic) {
			reading = false;
			missed();
		});
		loader.load(new URLRequest(Options.home() + "/spotify/now.txt"));
	}

	static function missed():Void {
		staleReads++;
		if (staleReads >= STALE_READS && state != "noarkive") {
			state = "noarkive";
			render();
		}
	}

	static function read(data:String):Void {
		if (data == null) {
			missed();
			return;
		}
		var fields:Dynamic = {};
		var lines = data.split("\n");
		for (line in lines) {
			var parts = line.split("\t");
			if (parts.length > 1)
				untyped fields[parts[0]] = parts;
		}
		var head:Array<String> = untyped fields["spotify"];
		if (head == null) {
			missed();
			return;
		}
		if (head[1] == lastSequence) {
			missed();
			return;
		}
		// The first file read may be left over from an earlier session: wait for Arkive to write again.
		var first = lastSequence == "";
		lastSequence = head[1];
		if (first)
			return;
		staleReads = 0;
		state = value(fields, "state", 1, "none");
		song = value(fields, "title", 1, "");
		singer = value(fields, "artist", 1, "");
		position = number(value(fields, "time", 1, ""), 0);
		length = number(value(fields, "time", 2, ""), 0);
		can = value(fields, "can", 1, "");
		shuffle = value(fields, "shuffle", 1, "");
		repeat = value(fields, "repeat", 1, "");
		if (flash.Lib.getTimer() - volumeAt > VOLUME_HOLD_MS)
			volume = number(value(fields, "volume", 1, ""), -1);
		if (!layoutRead) {
			layoutRead = true;
			var x = value(fields, "layout", 1, "");
			var y = value(fields, "layout", 2, "");
			if (x != "" && y != "") {
				offsetX = number(x, 0);
				offsetY = number(y, 0);
				place();
			}
			folded = value(fields, "layout", 3, "0") == "1";
		}
		render();
	}

	static function value(fields:Dynamic, key:String, index:Int, fallback:String):String {
		var parts:Array<String> = untyped fields[key];
		if (parts == null || parts.length <= index)
			return fallback;
		var found:String = parts[index];
		return found;
	}

	static function number(text:String, fallback:Int):Int {
		var parsed:Float = untyped __global__["parseInt"](text, 10);
		return Math.isNaN(parsed) ? fallback : Std.int(parsed);
	}

	/** Draws the player for the last state read: the full player or the strip, with only the buttons that work. */
	static function render():Void {
		if (player == null)
			return;
		var live = state == "playing" || state == "paused" || state == "stopped";
		var playing = state == "playing";
		var heading:String = WORDS.title;
		var line:String = state == "noarkive" ? WORDS.noArkive : state == "none" ? WORDS.noSpotify : WORDS.nothing;
		if (live && song != "") {
			heading = song;
			line = singer != "" ? singer : WORDS.unknown;
		}
		drawMark(playMark, playing ? "pause" : "play");
		drawMark(stripMark, playing ? "pause" : "play");
		var playTip:String = playing ? WORDS.pause : WORDS.play;
		try {
			playButton.tooltip = playTip;
			stripPlay.tooltip = playTip;
		} catch (error:Dynamic) {}

		var full = !folded;
		var toggles = live && full && (has("h") || has("r"));
		var height:Float = full ? (live ? (toggles ? 112 : 86) : 50) : STRIP;
		drawBack(height);

		title.visible = true;
		artist.visible = full;
		time.visible = full && live && length > 0;
		progress.visible = time.visible;
		show(previousButton, full && live && has("b"));
		show(playButton, full && live && has("p"));
		show(stopButton, full && live && has("s"));
		show(nextButton, full && live && has("n"));
		var loud = full && live && slider != null && volume >= 0;
		show(slider, loud);
		speaker.visible = loud;
		volumeText.visible = loud;
		show(stripPlay, !full && live && has("p"));
		show(shuffleBox, toggles && has("h"));
		show(repeatBox, toggles && has("r"));
		show(foldButton, full);
		show(unfoldButton, !full);

		var corner:Dynamic = full ? foldButton : unfoldButton;
		if (corner != null) {
			corner.x = WIDTH - PAD + 2 - (corner.width > 0 ? corner.width : 20);
			corner.y = full ? 5 : (STRIP - (corner.height > 0 ? corner.height : 18)) / 2;
		}
		if (full) {
			title.x = PAD;
			title.y = 6;
			fit(title, heading, WIDTH - 2 * PAD - 26);
			fit(artist, line, time.visible ? WIDTH - 2 * PAD - 84 : WIDTH - 2 * PAD);
			if (time.visible) {
				time.text = clock(position) + " / " + clock(length);
				var g = progress.graphics;
				var span:Float = WIDTH - 2 * PAD;
				g.clear();
				g.beginFill(WHITE, 0.15);
				g.drawRect(0, 0, span, 2);
				g.endFill();
				var done:Float = position >= length ? span : span * position / length;
				g.beginFill(GOLD, 1);
				g.drawRect(0, 0, done, 2);
				g.endFill();
			}
		} else {
			title.x = stripPlay.visible ? PAD + 24 : PAD;
			title.y = (STRIP - 22) / 2;
			var both = live && song != "" ? song + " - " + line : line;
			fit(title, both, WIDTH - title.x - PAD - 24);
		}
		if (loud) {
			if (slider.value != volume) {
				settingSlider = true;
				slider.value = volume;
				settingSlider = false;
			}
			volumeText.text = "" + volume;
		}
		if (shuffleBox != null && shuffleBox.visible == true)
			setTick(shuffleBox, shuffle == "on", WORDS.shuffle);
		if (repeatBox != null && repeatBox.visible == true)
			setTick(repeatBox, repeat == "list" || repeat == "track", repeat == "track" ? WORDS.repeatOne : repeat == "list" ? WORDS.repeatAll : WORDS.repeat);
	}

	static function has(letter:String):Bool {
		return can.indexOf(letter) >= 0;
	}

	static function show(control:Dynamic, on:Bool):Void {
		if (control != null && control.visible != on)
			control.visible = on;
	}

	static function setTick(box:Dynamic, on:Bool, label:String):Void {
		if (box.selected != on)
			box.selected = on;
		var now:String = box.label;
		if (now != label)
			box.label = label;
	}

	/**
		The HUD's dark backing (the shade the game's HUD panels use) with a faint edge; it is what the player is dragged
		by, and it keeps clicks on the player from reaching the world.
	**/
	static function drawBack(height:Float):Void {
		var g = back.graphics;
		g.clear();
		g.lineStyle(1, WHITE, 0.12);
		var matrix = new flash.geom.Matrix();
		matrix.createGradientBox(WIDTH, height, Math.PI / 2, 0, 0);
		g.beginGradientFill(flash.display.GradientType.LINEAR, [0x000000, 0x000000], [0.62, 0.45], [0, 255], matrix);
		g.drawRect(0, 0, WIDTH, height);
		g.endFill();
	}

	/** "m:ss" for seconds. */
	static function clock(seconds:Int):String {
		var minutes = Std.int(seconds / 60);
		var rest = seconds - minutes * 60;
		return minutes + ":" + (rest < 10 ? "0" : "") + rest;
	}

	/** Shows the text, cut short with "..." when it is wider than `width`. */
	static function fit(field:TextField, value:String, width:Float):Void {
		field.width = width;
		if (field.text == value && field.textWidth <= width - 4)
			return;
		field.text = value;
		if (field.textWidth <= width - 4)
			return;
		var low = 0;
		var high = value.length;
		while (low < high) {
			var middle = Std.int((low + high + 1) / 2);
			field.text = value.substr(0, middle) + "...";
			if (field.textWidth <= width - 4)
				low = middle;
			else
				high = middle - 1;
		}
		field.text = value.substr(0, low) + "...";
	}

	static function pressed(command:String):Void {
		if (!Options.on(ID))
			return;
		// Shows the change straight away; Arkive's next write confirms it.
		if (command == "playpause") {
			state = state == "playing" ? "paused" : "playing";
		} else if (command == "stop") {
			state = "stopped";
			position = 0;
		} else if (command == "shuffle") {
			shuffle = shuffle == "on" ? "off" : "on";
		} else if (command == "repeat") {
			repeat = repeat == "list" ? "track" : repeat == "track" ? "off" : "list";
		}
		render();
		Bus.send(ID, command);
	}

	/** The volume slider moved: send the newest value a moment later, so a drag sends a few messages, not dozens. */
	static function slid(event:Dynamic):Void {
		if (settingSlider)
			return;
		var at:Float = slider.value;
		volumeWanted = Std.int(at);
		volume = volumeWanted;
		volumeAt = flash.Lib.getTimer();
		volumeText.text = "" + volumeWanted;
		if (volumeSender == null) {
			volumeSender = new Timer(VOLUME_SEND_MS, 1);
			volumeSender.addEventListener(TimerEvent.TIMER, function(e:Dynamic) {
				if (volumeWanted >= 0 && Options.on(ID))
					Bus.send(ID, "volume\t" + volumeWanted);
				volumeWanted = -1;
			});
		}
		if (!volumeSender.running) {
			volumeSender.reset();
			volumeSender.start();
		}
	}

	/** Mouse down on the backing (not on a control): drag the player until the button is let go. */
	static function grab(event:Dynamic):Void {
		// The minimap frame's own mouse handling (focus, its sliders closing) stays out of it.
		event.stopPropagation();
		if (event.target != player || player.parent == null || player.stage == null)
			return;
		var at:Dynamic = player.parent.globalToLocal(new Point(event.stageX, event.stageY));
		grabX = at.x - player.x;
		grabY = at.y - player.y;
		dragging = true;
		player.stage.addEventListener(MouseEvent.MOUSE_MOVE, drag);
		player.stage.addEventListener(MouseEvent.MOUSE_UP, drop);
	}

	static function drag(event:Dynamic):Void {
		if (!dragging || player.parent == null)
			return;
		var at:Dynamic = player.parent.globalToLocal(new Point(event.stageX, event.stageY));
		player.x = Math.round(at.x - grabX);
		player.y = Math.round(at.y - grabY);
	}

	/** Let go: keep the player on screen and remember where it is, as an offset from its spot under the minimap. */
	static function drop(event:Dynamic):Void {
		dragging = false;
		var stage:Dynamic = event.currentTarget;
		stage.removeEventListener(MouseEvent.MOUSE_MOVE, drag);
		stage.removeEventListener(MouseEvent.MOUSE_UP, drop);
		if (player.parent == null || host == null)
			return;
		var box:Dynamic = player.getBounds(stage);
		var dx:Float = 0;
		var dy:Float = 0;
		var stageWidth:Float = stage.stageWidth;
		var stageHeight:Float = stage.stageHeight;
		if (box.x < 0)
			dx = -box.x;
		else if (box.x + box.width > stageWidth)
			dx = stageWidth - box.x - box.width;
		if (box.y < 0)
			dy = -box.y;
		else if (box.y + box.height > stageHeight)
			dy = stageHeight - box.y - box.height;
		if (dx != 0 || dy != 0) {
			var from:Dynamic = player.parent.globalToLocal(new Point(box.x, box.y));
			var to:Dynamic = player.parent.globalToLocal(new Point(box.x + dx, box.y + dy));
			player.x = Math.round(player.x + to.x - from.x);
			player.y = Math.round(player.y + to.y - from.y);
		}
		var spot:Dynamic = untyped hosts[host];
		if (spot == null)
			return;
		offsetX = player.x - spot.x;
		offsetY = player.y - spot.y;
		sendLayout();
	}

	/** Tells Arkive where the player is and whether it is folded, for next time. */
	static function sendLayout():Void {
		layoutRead = true;
		var x:Int = Math.round(offsetX);
		var y:Int = Math.round(offsetY);
		Bus.send(ID, "layout\t" + x + "\t" + y + "\t" + (folded ? "1" : "0"));
	}

	static function text(x:Float, y:Float, width:Float, size:Int, color:Int, right:Bool):TextField {
		var format = new TextFormat();
		format.font = font;
		format.size = size;
		if (right)
			format.align = flash.text.TextFormatAlign.RIGHT;
		var field = new TextField();
		field.embedFonts = embed;
		field.defaultTextFormat = format;
		field.textColor = color;
		field.multiline = false;
		field.wordWrap = false;
		field.selectable = false;
		field.mouseEnabled = false;
		field.x = x;
		field.y = y;
		field.width = width;
		field.height = size + 8;
		field.text = "";
		player.addChild(field);
		return field;
	}
}
