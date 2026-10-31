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
	Spotify Player: a small player under the minimap for the Spotify app running on the PC, laid out like Spotify's own
	bar but in the game's pieces. The track's cover sits in the game's item slot frame with the song and artist beside
	it; under them shuffle, previous, play or pause, next and repeat (gold with a mark under them while on, a "1" for
	repeat one); then the game's gauge with the time at both ends (click it to jump there), and Spotify's volume on the
	game's slider. Drag it by its background; the fold button turns it into a thin strip with a small cover.

	The game can't reach Spotify, so Arkive does it (through Windows' media controls): it writes Spotify's state to
	<home>/spotify/now.txt every second (plain ASCII: the game got nothing after a Japanese title, so Arkive spells other
	letters as \u and four hex digits) and the pictures to art.txt (the cover, plus the song and artist drawn when the
	game's fonts lack their letters, as with Japanese or Korean), which the player reads; the player sends its buttons,
	volume, jumps and position on the Data Bus (channel "spotify"). Pictures arrive as pixel values and are drawn into a
	BitmapData here, the way the game builds bitmaps itself; nothing is loaded as an image file.
**/
@:keep
class MusicPlayer {
	static inline var ID = "spotify";
	static inline var TICK_MS = 250;
	/** Ticks between reads of Arkive's file (one second). */
	static inline var READ_EVERY = 4;
	/** Reads without a new write from Arkive after which it counts as not running. */
	static inline var STALE_READS = 4;
	/** Ticks between looks at what the game has under the minimap. */
	static inline var MEASURE_EVERY = 16;
	/** After a volume drag, the file's volume is ignored this long, so the slider doesn't jump back. */
	static inline var VOLUME_HOLD_MS = 2500;
	static inline var VOLUME_SEND_MS = 200;

	/** The player is as wide as the minimap's backing, within these bounds. */
	static inline var MIN_WIDTH = 270;
	static inline var MAX_WIDTH = 360;
	static inline var PAD = 10;
	static inline var GAP = 6;
	static inline var SLOT = 56;
	static inline var TEXT_X = 76;
	static inline var BUTTON = 26;
	static inline var BUTTON_HEIGHT = 24;
	static inline var PLAY = 34;
	static inline var PLAY_HEIGHT = 30;
	static inline var CONTROLS_Y = 74;
	static inline var GAUGE_Y = 114;
	static inline var VOLUME_Y = 132;
	static inline var FULL_HEIGHT = 162;
	static inline var STRIP = 30;
	static inline var MINI = 22;
	/** The game's row of shortcut buttons under the minimap's backing (another HUD frame); the player goes below it. */
	static inline var SHORTCUTS = 34;
	/** The line under the player that says what went wrong, for in-game tests. */
	static inline var ERROR_HEIGHT = 16;
	static inline var RED = 0xFF6A5A;
	/** The game's window colour (the Tweak Settings window's own), solid under the panel art. */
	static inline var PANEL_COLOR = 0x15181D;

	static inline var WHITE = 0xFFFFFF;
	static inline var TEXT = 0xD8D8D8;
	static inline var DIM = 0x9A9A9A;
	/** The game's highlight gold (switched-on toggles, the gauge when drawn by hand). */
	static inline var GOLD = 0xFFD200;

	/** The game's small plain button (12 px label), the base of the media buttons. */
	static inline var SMALL_BUTTON = "AnimatedButton_renew12_V2";

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
		next: "Next",
		fold: "Fold",
		unfold: "Unfold",
		shuffleOn: "Shuffle: On",
		shuffleOff: "Shuffle: Off",
		repeatOff: "Repeat: Off",
		repeatAll: "Repeat: All",
		repeatOne: "Repeat: One"
	};

	/** The player's width, set from the first minimap it is built under. */
	static var panelWidth:Float = 280;

	static var font:String = "$YG760";
	static var embed = true;

	/** Minimap frames (weak keys), with their default spot for the player: {x, y}. */
	static var hosts = new flash.utils.Dictionary(true);
	static var host:Dynamic;

	static var player:Sprite;
	static var panel:Dynamic;
	/** True when the game's window backing wasn't there and the panel is drawn. */
	static var drawnPanel = false;
	static var underlay:Shape;
	static var slot:Sprite;
	static var slotFrame:Dynamic;
	static var cover:Sprite;
	static var mini:Sprite;
	static var title:TextField;
	static var artist:TextField;
	static var stripTitle:TextField;
	static var titleImage:Sprite;
	static var artistImage:Sprite;
	static var stripImage:Sprite;
	static var elapsed:TextField;
	static var total:TextField;
	static var gauge:Dynamic;
	static var gaugeDrawn:Shape;
	static var gaugeHit:Sprite;
	static var volumeText:TextField;
	static var speaker:Shape;
	static var shuffleButton:Dynamic;
	static var shuffleMark:Shape;
	static var shuffleDot:Shape;
	static var previousButton:Dynamic;
	static var playButton:Dynamic;
	static var playMark:Shape;
	static var nextButton:Dynamic;
	static var repeatButton:Dynamic;
	static var repeatMark:Shape;
	static var repeatDot:Shape;
	static var repeatOne:TextField;
	static var stripPlay:Dynamic;
	static var stripMark:Shape;
	static var foldButton:Dynamic;
	static var unfoldButton:Dynamic;
	static var slider:Dynamic;

	static var timer:Timer;
	static var ticks = 0;
	static var reading = false;
	static var lastSequence = "";
	static var staleReads = STALE_READS;

	/** The last state read: "noarkive", "none", "playing", "paused" or "stopped", and its fields. */
	static var state = "noarkive";
	static var song = "";
	static var singer = "";
	static var position:Float = 0;
	static var readAt = 0;
	static var length = 0;
	static var can = "";
	static var shuffle = "";
	static var repeat = "";
	static var volume = -1;

	/** The pictures: the art id shown and being read, and whether the game has drawn one yet (reported once). */
	static var art = "";
	static var artWanted = "";
	static var artReading = false;
	static var artReported = false;
	static var hasCover = false;

	static var folded = false;
	static var offsetX:Float = 0;
	static var offsetY:Float = 0;
	static var layoutRead = false;

	static var dragging = false;
	static var dragStage:Dynamic;
	/** Presses noted to Arkive so far (the first two of a session), and whether the drop of this one is noted too. */
	static var drags = 0;
	static var noteDrop = false;

	/** The panel's current height, which the screen check uses rather than the bounds of every hidden piece. */
	static var panelHeight:Float = 54;
	/** What went wrong last (shown on the player's last line), and the places already reported to Arkive. */
	static var problem = "";
	static var errorLine:TextField;
	static var reported:Dynamic = {};
	static var noted = false;
	/** The game's names of the pieces the player was built from, for the note to Arkive. */
	static var pieces = "";
	/** The last word to Arkive about what is under the minimap, and how many were sent. */
	static var measured = "";
	static var measuredNotes = 0;

	static var settingSlider = false;
	static var volumeAt = -VOLUME_HOLD_MS;
	static var volumeWanted = -1;
	static var volumeSender:Timer;

	/** MiniMapFrame.configUI (and MiniMapExFrame's): note the frame, then build the player once and show it under it. */
	public static function onMiniMapConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		try {
			// The map's own backing (MiniMapExFrame has its mask instead); the player goes under it and the shortcut
			// buttons below it, right edges lined up.
			var area:Dynamic = self.background != null ? self.background : self.minimapMaskMc != null ? self.minimapMaskMc : self;
			var bounds:Dynamic = area.getBounds(self);
			if (player == null) {
				var wide:Float = Math.round(bounds.width);
				panelWidth = wide < MIN_WIDTH ? MIN_WIDTH : wide > MAX_WIDTH ? MAX_WIDTH : wide;
			}
			var base:Float = bounds.y + bounds.height;
			untyped hosts[self] = {
				x: bounds.x + bounds.width - panelWidth,
				y: base + SHORTCUTS + GAP,
				left: bounds.x,
				right: bounds.x + bounds.width,
				base: base
			};
			measure(self);
			gameFont(self);
			if (player == null && !build())
				return;
			if (timer == null) {
				timer = new Timer(TICK_MS);
				timer.addEventListener(TimerEvent.TIMER, onTick);
				timer.start();
			}
			attach();
			poll();
		} catch (error:Dynamic) {
			fail("setup", error);
		}
	}

	/**
		Something threw: shows it on the player's last line and tells Arkive once per place (Arkive's log reads "Spotify
		Player error in game: ..."), so an in-game test says what failed.
	**/
	static function fail(where:String, error:Dynamic):Void {
		try {
			var message:String = untyped __global__["String"](error);
			problem = where + ": " + message;
			if (untyped reported[where] != true) {
				untyped reported[where] = true;
				Bus.send(ID, "error\t" + problem.substr(0, 160));
			}
			if (errorLine != null) {
				errorLine.text = problem;
				drawPanel(panelHeight);
			}
		} catch (ignored:Dynamic) {}
	}

	/** Tells Arkive one line about the player (its log reads "Spotify Player in game: ..."). */
	static function note(text:String):Void {
		Bus.send(ID, "note\t" + text.substr(0, 200));
	}

	static function str(value:Dynamic):String {
		var text:String = untyped __global__["String"](value);
		return text;
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
		if (GameUi.definition(SMALL_BUTTON) == null)
			return false;
		try {
			return buildPlayer();
		} catch (error:Dynamic) {
			// Half built: start again at the next configUI.
			player = null;
			fail("build", error);
			return false;
		}
	}

	static function buildPlayer():Bool {
		player = new Sprite();
		player.name = "arkiveSpotifyPlayer";
		player.addEventListener(MouseEvent.MOUSE_DOWN, grab);

		// The game's window backing, as its own windows and the Tweak Settings window have it (nine-slice, so it keeps
		// its edges at this size; the "renew" one is the HUD's see-through bar); a drawn one without either.
		var kind = "WindowBG_V2";
		panel = make(kind);
		if (panel == null) {
			kind = "WindowBG_renew_V2";
			panel = make(kind);
		}
		if (panel == null) {
			kind = "drawn";
			panel = new Shape();
			drawnPanel = true;
		} else {
			panel.mouseChildren = false;
			// The game's window art lets the world show through (its windows sit over a dimmed screen); a solid fill
			// in the window's colour under it keeps the quest list and buttons beneath from showing.
			underlay = new Shape();
			player.addChild(underlay);
		}
		player.addChild(panel);
		pieces = "panel " + kind;

		// The cover in the game's item slot: its backing, the picture, its frame on top.
		slot = new Sprite();
		slot.x = PAD;
		slot.y = PAD;
		slot.mouseChildren = false;
		var backing:Dynamic = make("ARKSlotBackgroundV2");
		if (backing != null) {
			backing.width = SLOT;
			backing.height = SLOT;
			slot.addChild(backing);
		} else {
			var g = slot.graphics;
			g.beginFill(0x101215, 1);
			g.drawRect(0, 0, SLOT, SLOT);
			g.endFill();
		}
		cover = new Sprite();
		cover.x = 3;
		cover.y = 3;
		slot.addChild(cover);
		// The game's slot edge (a bitmap of its shared image library, as item slots draw it).
		slotFrame = image("V2Slot_border");
		if (slotFrame != null) {
			slotFrame.width = SLOT;
			slotFrame.height = SLOT;
			slot.addChild(slotFrame);
		} else {
			var frame = new Shape();
			frame.graphics.lineStyle(1, 0x6E675C, 1);
			frame.graphics.drawRect(0, 0, SLOT, SLOT);
			slot.addChild(frame);
		}
		player.addChild(slot);
		drawNote(cover, SLOT - 6);
		pieces += ", slot " + (backing != null ? "ARKSlotBackgroundV2" : "drawn") + (slotFrame != null ? "+V2Slot_border" : "");

		title = text(TEXT_X, 14, panelWidth - TEXT_X - PAD - 22, 14, WHITE, false);
		artist = text(TEXT_X, 35, panelWidth - TEXT_X - PAD, 12, DIM, false);
		titleImage = picture(TEXT_X, 14);
		artistImage = picture(TEXT_X, 35);

		shuffleButton = button(BUTTON, BUTTON_HEIGHT, WORDS.shuffleOff, "shuffle");
		shuffleMark = mark(shuffleButton, "shuffle", 12, BUTTON, BUTTON_HEIGHT);
		shuffleDot = dot(shuffleButton, BUTTON, BUTTON_HEIGHT);
		previousButton = button(BUTTON, BUTTON_HEIGHT, WORDS.previous, "previous");
		mark(previousButton, "previous", 12, BUTTON, BUTTON_HEIGHT);
		playButton = button(PLAY, PLAY_HEIGHT, WORDS.play, "playpause");
		playMark = mark(playButton, "play", 14, PLAY, PLAY_HEIGHT);
		nextButton = button(BUTTON, BUTTON_HEIGHT, WORDS.next, "next");
		mark(nextButton, "next", 12, BUTTON, BUTTON_HEIGHT);
		repeatButton = button(BUTTON, BUTTON_HEIGHT, WORDS.repeatOff, "repeat");
		repeatMark = mark(repeatButton, "repeat", 12, BUTTON, BUTTON_HEIGHT);
		repeatDot = dot(repeatButton, BUTTON, BUTTON_HEIGHT);
		repeatOne = text(BUTTON - 10, 0, 10, 9, GOLD, false);
		repeatOne.text = "1";
		repeatOne.visible = false;
		player.removeChild(repeatOne);
		overlay(repeatButton).addChild(repeatOne);

		// The time and the game's gauge (a drawn bar without it); a click on it jumps there.
		elapsed = text(PAD, GAUGE_Y - 7, 40, 11, DIM, false);
		total = text(panelWidth - PAD - 40, GAUGE_Y - 7, 40, 11, DIM, true);
		gauge = make("SimpleProgressBar_01");
		if (gauge != null) {
			try {
				gauge.minimum = 0;
				gauge.maximum = 100;
				gauge.completeTime = 0;
				gauge.targetFixed = -1;
				gauge.width = panelWidth - 2 * PAD - 84;
				gauge.height = 8;
				gauge.x = PAD + 42;
				gauge.y = GAUGE_Y - 4;
				gauge.mouseEnabled = false;
				gauge.mouseChildren = false;
				player.addChild(gauge);
			} catch (error:Dynamic) {
				gauge = null;
			}
		}
		gaugeDrawn = new Shape();
		gaugeDrawn.x = PAD + 42;
		gaugeDrawn.y = GAUGE_Y - 2;
		player.addChild(gaugeDrawn);
		gaugeHit = new Sprite();
		gaugeHit.x = PAD + 42;
		gaugeHit.y = GAUGE_Y - 7;
		gaugeHit.graphics.beginFill(0x000000, 0.01);
		gaugeHit.graphics.drawRect(0, 0, panelWidth - 2 * PAD - 84, 14);
		gaugeHit.graphics.endFill();
		gaugeHit.buttonMode = true;
		gaugeHit.addEventListener(MouseEvent.MOUSE_DOWN, function(event:Dynamic) event.stopPropagation());
		gaugeHit.addEventListener(MouseEvent.CLICK, seek);
		player.addChild(gaugeHit);

		// Spotify's volume on the game's slider, centred under the gauge.
		speaker = new Shape();
		drawMark(speaker.graphics, "speaker", 12, TEXT);
		var middle:Float = Math.round(panelWidth / 2);
		speaker.x = middle - 68;
		speaker.y = VOLUME_Y + 5;
		player.addChild(speaker);
		slider = make("ARKDefaultSlider_V2");
		if (slider != null) {
			slider.minimum = 0;
			slider.maximum = 100;
			slider.snapping = true;
			slider.snapInterval = 1;
			try {
				slider.showValue = false;
			} catch (error:Dynamic) {}
			slider.width = 120;
			slider.x = middle - 50;
			slider.y = VOLUME_Y;
			slider.addEventListener("valueChange", slid);
			player.addChild(slider);
		}
		volumeText = text(middle + 74, VOLUME_Y + 3, 34, 11, DIM, false);
		pieces += ", gauge " + (gauge != null ? "SimpleProgressBar_01" : "drawn") + ", slider " + (slider != null ? "ARKDefaultSlider_V2" : "none");

		// The strip: a small cover, play or pause, the song, and the unfold button.
		mini = new Sprite();
		mini.x = 5;
		mini.y = (STRIP - MINI) / 2;
		mini.mouseChildren = false;
		player.addChild(mini);
		stripPlay = button(24, 22, WORDS.play, "playpause");
		stripMark = mark(stripPlay, "play", 10, 24, 22);
		put(stripPlay, 32, (STRIP - 22) / 2);
		stripTitle = text(62, (STRIP - 20) / 2, panelWidth - 62 - 30, 13, WHITE, false);
		stripImage = picture(62, (STRIP - 20) / 2);

		foldButton = foldControl("AnimatedButton_Minus_V2", WORDS.fold, true);
		unfoldButton = foldControl("AnimatedButton_Plus_V2", WORDS.unfold, false);
		errorLine = text(PAD, 0, panelWidth - 2 * PAD, 10, RED, false);
		Options.own(ID, player);
		render();
		return true;
	}

	/** A small plain game button of this size that sends `command` when clicked. */
	static function button(width:Float, height:Float, tooltip:String, command:String):Dynamic {
		var made:Dynamic = GameUi.buttonOf("", [SMALL_BUTTON, GameUi.PLAIN_BUTTON]);
		made.width = width;
		made.height = height;
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

	/** The fold (minus) or unfold (plus) button at the top right; a small plain button with a drawn mark without the symbol. */
	static function foldControl(symbol:String, tooltip:String, fold:Bool):Dynamic {
		var made:Dynamic = make(symbol);
		if (made == null) {
			made = GameUi.buttonOf("", [SMALL_BUTTON, GameUi.PLAIN_BUTTON]);
			made.width = 20;
			made.height = 18;
			player.addChild(made);
			mark(made, fold ? "minus" : "plus", 10, 20, 18);
		} else {
			player.addChild(made);
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
		return made;
	}

	static function make(symbol:String):Dynamic {
		var type:Dynamic = GameUi.definition(symbol);
		return type == null ? null : untyped __new__(type);
	}

	/** A Bitmap of one of the game's shared images (a BitmapData class, made as the game makes them), or null. */
	static function image(name:String):Dynamic {
		var type:Dynamic = GameUi.definition(name);
		if (type == null)
			return null;
		try {
			var data:Dynamic = untyped __new__(type, 0, 0);
			return untyped __new__(flash.display.Bitmap, data, "auto", true);
		} catch (error:Dynamic) {
			return null;
		}
	}

	/**
		The layer over a button that holds its marks. A game button takes its size by scaling itself (its nine-slice keeps
		the edges), so anything inside it would be squeezed; the layer lies on top instead, moves and hides with the
		button, and lets the mouse through to it.
	**/
	static function overlay(button:Dynamic):Sprite {
		var layer:Sprite = untyped overlays[button];
		if (layer != null)
			return layer;
		layer = new Sprite();
		layer.mouseEnabled = false;
		layer.mouseChildren = false;
		try {
			scaleform.gfx.InteractiveObjectEx.setHitTestDisable(layer, true);
		} catch (error:Dynamic) {}
		layer.x = button.x;
		layer.y = button.y;
		player.addChildAt(layer, player.getChildIndex(button) + 1);
		untyped overlays[button] = layer;
		return layer;
	}

	static var overlays = new flash.utils.Dictionary(true);

	/** A media mark drawn in the label colour, centred on the button. */
	static function mark(button:Dynamic, kind:String, size:Int, width:Float, height:Float):Shape {
		var shape = new Shape();
		drawMark(shape.graphics, kind, size, TEXT);
		shape.x = Math.round((width - size) / 2);
		shape.y = Math.round((height - size) / 2);
		overlay(button).addChild(shape);
		return shape;
	}

	/** The small gold mark under a switched-on toggle. */
	static function dot(button:Dynamic, width:Float, height:Float):Shape {
		var shape = new Shape();
		shape.graphics.beginFill(GOLD, 1);
		shape.graphics.drawRect(0, 0, 4, 2);
		shape.graphics.endFill();
		shape.x = Math.round((width - 4) / 2);
		shape.y = height - 4;
		shape.visible = false;
		overlay(button).addChild(shape);
		return shape;
	}

	/** Moves a control and the layer of marks over it. */
	static function put(control:Dynamic, x:Float, y:Float):Void {
		control.x = x;
		control.y = y;
		var layer:Sprite = untyped overlays[control];
		if (layer != null) {
			layer.x = x;
			layer.y = y;
		}
	}

	/**
		Draws a mark into a box of `size` pixels: "previous", "next", "play", "pause", "shuffle", "repeat", "speaker",
		"minus" or "plus".
	**/
	static function drawMark(g:flash.display.Graphics, kind:String, size:Int, color:Int):Void {
		var s:Float = size / 12;
		g.clear();
		switch (kind) {
			case "shuffle":
				g.lineStyle(1.5 * s, color, 1);
				g.moveTo(0, 3 * s);
				g.lineTo(3 * s, 3 * s);
				g.lineTo(8 * s, 9 * s);
				g.lineTo(11 * s, 9 * s);
				g.moveTo(0, 9 * s);
				g.lineTo(3 * s, 9 * s);
				g.lineTo(8 * s, 3 * s);
				g.lineTo(11 * s, 3 * s);
				g.lineStyle();
				g.beginFill(color, 1);
				g.moveTo(9 * s, 0.5 * s);
				g.lineTo(12 * s, 3 * s);
				g.lineTo(9 * s, 5.5 * s);
				g.lineTo(9 * s, 0.5 * s);
				g.moveTo(9 * s, 6.5 * s);
				g.lineTo(12 * s, 9 * s);
				g.lineTo(9 * s, 11.5 * s);
				g.lineTo(9 * s, 6.5 * s);
				g.endFill();
				return;
			case "repeat":
				g.lineStyle(1.5 * s, color, 1);
				g.moveTo(9 * s, 3 * s);
				g.lineTo(2 * s, 3 * s);
				g.lineTo(1 * s, 4 * s);
				g.lineTo(1 * s, 8 * s);
				g.lineTo(2 * s, 9 * s);
				g.lineTo(10 * s, 9 * s);
				g.lineTo(11 * s, 8 * s);
				g.lineTo(11 * s, 5 * s);
				g.lineStyle();
				g.beginFill(color, 1);
				g.moveTo(8 * s, 0.5 * s);
				g.lineTo(11 * s, 3 * s);
				g.lineTo(8 * s, 5.5 * s);
				g.lineTo(8 * s, 0.5 * s);
				g.endFill();
				return;
			case "speaker":
				g.beginFill(color, 1);
				g.drawRect(0, 4 * s, 3 * s, 4 * s);
				g.moveTo(3 * s, 4 * s);
				g.lineTo(7 * s, 1 * s);
				g.lineTo(7 * s, 11 * s);
				g.lineTo(3 * s, 8 * s);
				g.lineTo(3 * s, 4 * s);
				g.endFill();
				g.lineStyle(1, color, 0.8);
				g.moveTo(9 * s, 4 * s);
				g.curveTo(10.5 * s, 6 * s, 9 * s, 8 * s);
				return;
			default:
		}
		g.beginFill(color, 1);
		switch (kind) {
			case "previous":
				g.drawRect(1 * s, 1 * s, 2 * s, 10 * s);
				g.moveTo(11 * s, 1 * s);
				g.lineTo(4 * s, 6 * s);
				g.lineTo(11 * s, 11 * s);
				g.lineTo(11 * s, 1 * s);
			case "next":
				g.moveTo(1 * s, 1 * s);
				g.lineTo(8 * s, 6 * s);
				g.lineTo(1 * s, 11 * s);
				g.lineTo(1 * s, 1 * s);
				g.drawRect(9 * s, 1 * s, 2 * s, 10 * s);
			case "play":
				g.moveTo(2 * s, 0);
				g.lineTo(11 * s, 6 * s);
				g.lineTo(2 * s, 12 * s);
				g.lineTo(2 * s, 0);
			case "pause":
				g.drawRect(2 * s, 1 * s, 3 * s, 10 * s);
				g.drawRect(7 * s, 1 * s, 3 * s, 10 * s);
			case "minus":
				g.drawRect(1 * s, 5 * s, 10 * s, 2 * s);
			case "plus":
				g.drawRect(1 * s, 5 * s, 10 * s, 2 * s);
				g.drawRect(5 * s, 1 * s, 2 * s, 10 * s);
		}
		g.endFill();
	}

	/** The empty slot's music note, while there is no cover. */
	static function drawNote(holder:Sprite, size:Float):Void {
		while (holder.numChildren > 0)
			holder.removeChildAt(0);
		var note = new Shape();
		var g = note.graphics;
		var s:Float = size / 50;
		g.beginFill(0x5A5650, 1);
		g.drawEllipse(14 * s, 30 * s, 10 * s, 8 * s);
		g.drawEllipse(28 * s, 27 * s, 10 * s, 8 * s);
		g.drawRect(22 * s, 13 * s, 2 * s, 21 * s);
		g.drawRect(36 * s, 10 * s, 2 * s, 21 * s);
		g.moveTo(22 * s, 13 * s);
		g.lineTo(38 * s, 10 * s);
		g.lineTo(38 * s, 14 * s);
		g.lineTo(22 * s, 17 * s);
		g.lineTo(22 * s, 13 * s);
		g.endFill();
		holder.addChild(note);
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

	/**
		Where under the minimap the player goes: below the game's row of shortcut buttons, which is another HUD frame the
		game places along the backing's bottom edge. Looks through the frames the game's LayoutManager has on screen for
		ones whose visible part starts at that edge and lies across the backing (a row, not a tall panel) and goes below
		the lowest; SHORTCUTS below the backing when there is none. The quest list's top is noted for in-game tests.
	**/
	static function measure(frame:Dynamic):Void {
		var spot:Dynamic = untyped hosts[frame];
		if (spot == null || frame.stage == null)
			return;
		var base:Float = spot.base;
		var left:Float = spot.left;
		var right:Float = spot.right;
		var below:Float = SHORTCUTS;
		var found = "";
		var quests:Float = -1;
		try {
			var manager:Dynamic = GameUi.definition("ark.managers.LayoutManager");
			var list:Dynamic = manager == null ? null : manager.managingLayoutSetters;
			var count:Int = list == null ? 0 : list.length;
			var reach:Float = base;
			for (i in 0...count) {
				var setter:Dynamic = list[i];
				var widget:Dynamic = setter == null ? null : setter.widget;
				if (widget == null || widget.stage == null || (untyped hosts[widget]) != null || !showing(widget))
					continue;
				var box:Dynamic = shownBounds(widget, frame, 0);
				if (box == null)
					continue;
				var top:Float = box.y;
				var bottom:Float = top + box.height;
				var x0:Float = box.x;
				var x1:Float = x0 + box.width;
				if (!isFinite(top + bottom + x0 + x1) || x1 <= left || x0 >= right)
					continue;
				var name:String = widget.name;
				if (name != null && name.toLowerCase().indexOf("quest") >= 0) {
					if (top >= base - 4 && (quests < 0 || top < quests))
						quests = top;
					continue;
				}
				if (top < base - 12 || top > base + 30 || bottom - top > 80)
					continue;
				if (bottom > reach)
					reach = bottom;
				found += (found == "" ? "" : "+") + name;
			}
			if (found != "")
				below = reach - base;
		} catch (error:Dynamic) {
			fail("measure", error);
		}
		spot.y = Math.round(base + below + GAP);
		var said = (found == "" ? "no row under the minimap" : "row " + found) + ", player top " + str(spot.y - base)
			+ " below the minimap" + (quests < 0 ? ", no quest list under it" : ", quest list at " + str(Math.round(quests - base)));
		if (said != measured && measuredNotes < 3) {
			measured = said;
			measuredNotes++;
			note(said);
		}
	}

	/**
		The part of `node` that shows, in `root`'s units: hidden pieces left out (frames keep tooltips, effects and closed
		lists hidden around them, and getBounds counts those), masked ones cut to their mask. Null when nothing shows.
	**/
	static function shownBounds(node:Dynamic, root:Dynamic, depth:Int):Dynamic {
		if (depth > 0 && (node.visible != true || node.alpha < 0.02))
			return null;
		if (depth > 0 && node.mask != null)
			return node.mask.getBounds(root);
		if (depth < 5 && (untyped __is__(node, flash.display.DisplayObjectContainer))) {
			var count:Int = node.numChildren;
			if (count > 0) {
				var result:Dynamic = null;
				for (i in 0...count) {
					var child:Dynamic = node.getChildAt(i);
					var part:Dynamic = child == null ? null : shownBounds(child, root, depth + 1);
					if (part != null)
						result = result == null ? part : result.union(part);
				}
				return result;
			}
		}
		if (untyped __is__(node, TextField)) {
			var words:String = node.text;
			if (words == null || words.length == 0)
				return null;
		}
		var bounds:Dynamic = node.getBounds(root);
		var wide:Float = bounds.width;
		var high:Float = bounds.height;
		return wide > 0 && high > 0 ? bounds : null;
	}

	static function place():Void {
		if (host == null)
			return;
		var spot:Dynamic = untyped hosts[host];
		if (spot == null)
			return;
		player.x = Math.round(spot.x + offsetX);
		player.y = Math.round(spot.y + offsetY);
		keepOnScreen();
	}

	/**
		Pulls the player back inside the game's visible screen, the area the game keeps its own windows in. It measures the
		panel itself on the stage (not the bounds of every hidden piece) and moves it by the difference; when the numbers
		make no sense (missing, or a screen smaller than the panel) it leaves the player where it is and tells Arkive.
	**/
	static function keepOnScreen():Void {
		var parent:Dynamic = player.parent;
		if (parent == null || parent.stage == null)
			return;
		var screen:Dynamic = screenArea(parent.stage);
		var corner:Dynamic = player.localToGlobal(new Point(0, 0));
		var far:Dynamic = player.localToGlobal(new Point(panelWidth, panelHeight));
		var left:Float = screen.x;
		var top:Float = screen.y;
		var right:Float = left + screen.width;
		var bottom:Float = top + screen.height;
		var x0:Float = corner.x;
		var y0:Float = corner.y;
		var x1:Float = far.x;
		var y1:Float = far.y;
		if (!isFinite(left + top + right + bottom + x0 + y0 + x1 + y1) || x1 - x0 > right - left || y1 - y0 > bottom - top) {
			if (untyped reported["screen"] != true) {
				untyped reported["screen"] = true;
				note("screen check skipped: screen " + str(left) + "," + str(top) + "," + str(right) + "," + str(bottom) + " panel "
					+ str(x0) + "," + str(y0) + "," + str(x1) + "," + str(y1));
			}
			return;
		}
		var dx:Float = 0;
		var dy:Float = 0;
		if (x1 > right)
			dx = right - x1;
		if (x0 + dx < left)
			dx = left - x0;
		if (y1 > bottom)
			dy = bottom - y1;
		if (y0 + dy < top)
			dy = top - y0;
		if (dx == 0 && dy == 0)
			return;
		// The same move in the parent's own units.
		var from:Dynamic = parent.globalToLocal(new Point(x0, y0));
		var to:Dynamic = parent.globalToLocal(new Point(x0 + dx, y0 + dy));
		var moveX:Float = to.x - from.x;
		var moveY:Float = to.y - from.y;
		if (isFinite(moveX + moveY)) {
			player.x = Math.round(player.x + moveX);
			player.y = Math.round(player.y + moveY);
		}
	}

	/** The game's visible screen in stage coordinates (Scaleform's visibleRect; the stage's own size can differ). */
	static function screenArea(stage:Dynamic):Dynamic {
		var extensions:Dynamic = GameUi.definition("scaleform.gfx.Extensions");
		var rect:Dynamic = extensions == null ? null : extensions.visibleRect;
		if (rect != null && rect.width > 0 && rect.height > 0)
			return rect;
		return {x: 0, y: 0, width: stage.stageWidth, height: stage.stageHeight};
	}

	static function onTick(event:Dynamic):Void {
		// Runs outside any hook's guard: a surprise here stops this tick, not the game's interface.
		try {
			if (!Options.on(ID) || player == null)
				return;
			ticks++;
			if (ticks % READ_EVERY == 0) {
				if (host == null || host.stage == null || !showing(host)) {
					host = null;
					attach();
				}
				// The game lays out its HUD frames after the minimap's configUI and again when the screen changes.
				if (host != null && !dragging && ticks % MEASURE_EVERY == 0) {
					var spot:Dynamic = untyped hosts[host];
					var was:Float = spot == null ? 0 : spot.y;
					measure(host);
					if (spot != null && spot.y != was)
						place();
				}
				poll();
			}
			if (state == "playing")
				showTime();
		} catch (error:Dynamic) {
			fail("tick", error);
		}
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
			} catch (error:Dynamic) {
				fail("read", error);
			}
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
		// Arkive ends the file with its sequence again: without it the game got only part of the file.
		if (value(fields, "end", 1, "") != head[1] && untyped reported["partial"] != true) {
			untyped reported["partial"] = true;
			var got:Int = data.length;
			note("now.txt read only in part (" + got + " characters, last line " + lines[lines.length > 1 ? lines.length - 2 : 0].substr(0, 40)
				+ ")");
		}
		state = value(fields, "state", 1, "none");
		song = unescape(value(fields, "title", 1, ""));
		singer = unescape(value(fields, "artist", 1, ""));
		position = number(value(fields, "time", 1, ""), 0);
		readAt = flash.Lib.getTimer();
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
		var wanted = value(fields, "art", 1, "");
		if (wanted != art && wanted != artWanted)
			readArt(wanted);
		render();
	}

	static function value(fields:Dynamic, key:String, index:Int, fallback:String):String {
		var parts:Array<String> = untyped fields[key];
		if (parts == null || parts.length <= index)
			return fallback;
		var found:String = parts[index];
		return found;
	}

	/** Arkive's text with the letters it spelled as "\u" and four hex digits turned back (the file itself is ASCII). */
	static function unescape(text:String):String {
		var at = text.indexOf("\\u");
		if (at < 0)
			return text;
		var out = "";
		var from = 0;
		while (at >= 0 && at + 6 <= text.length) {
			var code:Float = untyped __global__["parseInt"](text.substr(at + 2, 4), 16);
			if (isNaN(code))
				break;
			var letter:String = untyped __global__["String"].fromCharCode(code);
			out = out + text.substring(from, at) + letter;
			from = at + 6;
			at = text.indexOf("\\u", from);
		}
		return out + text.substring(from);
	}

	static function number(text:String, fallback:Int):Int {
		var parsed:Float = untyped __global__["parseInt"](text, 10);
		return isNaN(parsed) ? fallback : Std.int(parsed);
	}

	/**
		The player's own isNaN and isFinite, as the game's code calls them: Haxe's Math.isNaN and Math.isFinite are put
		on Math by Haxe's Boot, which Arkive leaves empty, so calling them threw "Error #1006" (not a function).
	**/
	static function isNaN(value:Float):Bool {
		return untyped __global__["isNaN"](value);
	}

	static function isFinite(value:Float):Bool {
		return untyped __global__["isFinite"](value);
	}

	/** Reads art.txt for a new track and draws its pictures; "" clears them. */
	static function readArt(id:String):Void {
		artWanted = id;
		if (id == "") {
			showPictures({});
			art = "";
			return;
		}
		if (artReading)
			return;
		artReading = true;
		var loader = new URLLoader();
		loader.addEventListener("complete", function(e:Dynamic) {
			artReading = false;
			var data:String = loader.data;
			var pictures:Dynamic = {};
			try {
				pictures = decode(data);
				art = id;
				showPictures(pictures);
				if (!artReported && hasCover) {
					artReported = true;
					Bus.send(ID, "art\tshown");
				}
			} catch (error:Dynamic) {
				art = id;
				showPictures({});
				if (!artReported) {
					artReported = true;
					Bus.send(ID, "art\tfailed");
				}
			}
			// A newer track came while reading: read again.
			if (artWanted != art)
				readArt(artWanted);
		});
		loader.addEventListener("ioError", function(e:Dynamic) {
			artReading = false;
			artWanted = "";
		});
		loader.addEventListener("securityError", function(e:Dynamic) {
			artReading = false;
			artWanted = "";
		});
		loader.load(new URLRequest(Options.home() + "/spotify/art.txt"));
	}

	/** The pictures of art.txt, by name, as BitmapData filled from their pixel values (8 hex digits each, ARGB). */
	static function decode(data:String):Dynamic {
		var pictures:Dynamic = {};
		if (data == null)
			return pictures;
		var lines = data.split("\n");
		for (line in lines) {
			var parts = line.split("\t");
			if (parts.length != 5 || parts[0] != "image")
				continue;
			var name:String = parts[1];
			var width = number(parts[2], 0);
			var height = number(parts[3], 0);
			var pixels:String = parts[4];
			if (width <= 0 || height <= 0 || width > 512 || height > 128 || pixels.length != width * height * 8)
				continue;
			var bitmap:Dynamic = new flash.display.BitmapData(width, height, true, 0);
			bitmap.lock();
			var at = 0;
			for (y in 0...height) {
				for (x in 0...width) {
					var color:Float = untyped __global__["parseInt"](pixels.substr(at, 8), 16);
					at += 8;
					bitmap.setPixel32(x, y, color);
				}
			}
			bitmap.unlock();
			untyped pictures[name] = bitmap;
		}
		return pictures;
	}

	/** Puts the cover in the slot and the strip, and the drawn song and artist in place of the text (when sent). */
	static function showPictures(pictures:Dynamic):Void {
		var picture:Dynamic = untyped pictures["cover"];
		hasCover = picture != null;
		if (picture != null) {
			fill(cover, picture, SLOT - 6, SLOT - 6);
			fill(mini, picture, MINI, MINI);
		} else {
			drawNote(cover, SLOT - 6);
			drawNote(mini, MINI);
		}
		var name:Dynamic = untyped pictures["title"];
		var who:Dynamic = untyped pictures["artist"];
		fill(titleImage, name, -1, -1);
		fill(stripImage, name, -1, -1);
		fill(artistImage, who, -1, -1);
		render();
	}

	/** Shows the picture in the holder, scaled smoothly to width x height (-1: its own size); null empties it. */
	static function fill(holder:Sprite, picture:Dynamic, width:Float, height:Float):Void {
		while (holder.numChildren > kept(holder))
			holder.removeChildAt(holder.numChildren - 1);
		if (picture == null)
			return;
		var shown:Dynamic = untyped __new__(flash.display.Bitmap, picture, "auto", true);
		if (width > 0) {
			shown.width = width;
			shown.height = height;
		}
		holder.addChild(shown);
	}

	/** Draws the player for the last state read: the full player or the strip, with only the buttons that work. */
	static function render():Void {
		if (player == null)
			return;
		try {
			draw();
		} catch (error:Dynamic) {
			fail("draw", error);
		}
		if (!noted && (state == "playing" || state == "paused" || state == "stopped")) {
			// Once a session: what the player was built from and where it sits, for the next in-game test.
			noted = true;
			var parent:Dynamic = player.parent;
			var name:String = parent == null ? "none" : parent.name;
			note(pieces + "; on " + name + " at " + str(player.x) + "," + str(player.y) + " width " + str(panelWidth) + "; state " + state
				+ ", can " + can + ", length " + length + ", volume " + volume + ", art " + (art != "" ? "yes" : "no"));
		}
	}

	static function draw():Void {
		var live = state == "playing" || state == "paused" || state == "stopped";
		var playing = state == "playing";
		var heading:String = WORDS.title;
		var line:String = state == "noarkive" ? WORDS.noArkive : state == "none" ? WORDS.noSpotify : WORDS.nothing;
		if (live && song != "") {
			heading = song;
			line = singer != "" ? singer : WORDS.unknown;
		}
		drawMark(playMark.graphics, playing ? "pause" : "play", 14, TEXT);
		drawMark(stripMark.graphics, playing ? "pause" : "play", 10, TEXT);
		var playTip:String = playing ? WORDS.pause : WORDS.play;
		try {
			playButton.tooltip = playTip;
			stripPlay.tooltip = playTip;
		} catch (error:Dynamic) {}

		var full = !folded;
		var track = live && song != "";
		var height:Float = full ? (live ? FULL_HEIGHT : 54) : STRIP;
		drawPanel(height);

		// The full player: the slot, the song and artist (drawn by Arkive when the font lacks the letters).
		slot.visible = full && live;
		var textX:Float = slot.visible ? TEXT_X : PAD;
		var textWidth:Float = panelWidth - textX - PAD;
		var drawnTitle = track && titleImage.numChildren > kept(titleImage);
		var drawnArtist = track && singer != "" && artistImage.numChildren > kept(artistImage);
		title.visible = full && !drawnTitle;
		titleImage.visible = full && drawnTitle;
		artist.visible = full && !drawnArtist;
		artistImage.visible = full && drawnArtist;
		clip(titleImage, textWidth - 22);
		clip(artistImage, textWidth);
		title.x = textX;
		artist.x = textX;
		title.y = live ? 14 : 8;
		artist.y = live ? 35 : 28;
		if (title.visible)
			fit(title, heading, textWidth - 22);
		if (artist.visible)
			fit(artist, line, textWidth);

		// The buttons Spotify allows, centred in one row.
		var row:Array<Dynamic> = [];
		var shown:Array<Bool> = [has("h"), has("b"), has("p"), has("n"), has("r")];
		var controls:Array<Dynamic> = [shuffleButton, previousButton, playButton, nextButton, repeatButton];
		for (i in 0...controls.length) {
			var on = full && live && shown[i];
			show(controls[i], on);
			if (on)
				row.push(controls[i]);
		}
		var span:Float = 0;
		for (control in row) {
			var w:Float = control.width;
			span += w + GAP;
		}
		var x:Float = Math.round((panelWidth - span + GAP) / 2);
		for (control in row) {
			var w:Float = control.width;
			var h:Float = control.height;
			put(control, x, CONTROLS_Y + (PLAY_HEIGHT - h) / 2);
			x += w + GAP;
		}
		var shuffleOn = shuffle == "on";
		drawMark(shuffleMark.graphics, "shuffle", 12, shuffleOn ? GOLD : TEXT);
		shuffleDot.visible = shuffleOn;
		var repeatOn = repeat == "list" || repeat == "track";
		drawMark(repeatMark.graphics, "repeat", 12, repeatOn ? GOLD : TEXT);
		repeatDot.visible = repeatOn;
		repeatOne.visible = repeat == "track";
		try {
			var shuffleTip:String = shuffleOn ? WORDS.shuffleOn : WORDS.shuffleOff;
			var repeatTip:String = repeat == "track" ? WORDS.repeatOne : repeat == "list" ? WORDS.repeatAll : WORDS.repeatOff;
			shuffleButton.tooltip = shuffleTip;
			repeatButton.tooltip = repeatTip;
		} catch (error:Dynamic) {}

		var timed = full && live && length > 0;
		elapsed.visible = timed;
		total.visible = timed;
		gaugeHit.visible = timed && has("k");
		if (gauge != null)
			show(gauge, timed);
		gaugeDrawn.visible = timed && gauge == null;
		showTime();

		var loud = full && live && slider != null && volume >= 0;
		show(slider, loud);
		speaker.visible = loud;
		volumeText.visible = loud;
		if (loud) {
			if (slider.value != volume) {
				settingSlider = true;
				slider.value = volume;
				settingSlider = false;
			}
			volumeText.text = volume + "%";
		}

		// The strip: small cover, play or pause, and the song.
		mini.visible = !full && live;
		show(stripPlay, !full && live && has("p"));
		var stripX:Float = !full && live ? (stripPlay.visible ? 62 : 32) : PAD;
		var drawnStrip = !full && track && stripImage.numChildren > kept(stripImage);
		stripImage.visible = drawnStrip;
		stripImage.x = stripX;
		clip(stripImage, panelWidth - stripX - 30);
		stripTitle.visible = !full && !drawnStrip;
		stripTitle.x = stripX;
		if (stripTitle.visible)
			fit(stripTitle, track ? song + " - " + line : line, panelWidth - stripX - 30);

		show(foldButton, full);
		show(unfoldButton, !full);
		var corner:Dynamic = full ? foldButton : unfoldButton;
		if (corner != null) {
			var w:Float = corner.width > 0 ? corner.width : 20;
			var h:Float = corner.height > 0 ? corner.height : 18;
			corner.x = panelWidth - PAD + 3 - w;
			corner.y = full ? 7 : (STRIP - h) / 2;
		}
	}

	/** The time at both ends and the gauge, moved on since the last read while playing. */
	static function showTime():Void {
		if (!elapsed.visible)
			return;
		var now:Float = position;
		if (state == "playing")
			now += (flash.Lib.getTimer() - readAt) / 1000;
		if (now > length)
			now = length;
		var seconds = Std.int(now);
		var text = clock(seconds);
		if (elapsed.text != text)
			elapsed.text = text;
		var end = clock(length);
		if (total.text != end)
			total.text = end;
		var share:Float = length > 0 ? now / length : 0;
		if (gauge != null) {
			var percent:Float = Math.round(share * 1000) / 10;
			if (gauge.value != percent)
				gauge.value = percent;
		} else {
			var g = gaugeDrawn.graphics;
			var span:Float = panelWidth - 2 * PAD - 84;
			g.clear();
			g.beginFill(WHITE, 0.15);
			g.drawRect(0, 0, span, 3);
			g.endFill();
			g.beginFill(GOLD, 1);
			g.drawRect(0, 0, span * share, 3);
			g.endFill();
		}
	}

	/**
		The game's window backing at this height (plus the error line while something has gone wrong); drawn in the
		colours of the game's windows without the symbol.
	**/
	static function drawPanel(height:Float):Void {
		panelHeight = height;
		var shown:Float = height;
		if (errorLine != null) {
			errorLine.visible = problem != "";
			errorLine.y = height - 4;
			if (problem != "")
				shown += ERROR_HEIGHT - 4;
		}
		if (drawnPanel) {
			var g = panel.graphics;
			g.clear();
			g.lineStyle(1, 0x4A4F57, 1);
			g.beginFill(PANEL_COLOR, 1);
			g.drawRect(0, 0, panelWidth, shown);
			g.endFill();
			return;
		}
		panel.width = panelWidth;
		panel.height = shown;
		if (underlay != null) {
			var fill = underlay.graphics;
			fill.clear();
			fill.beginFill(PANEL_COLOR, 1);
			fill.drawRect(2, 2, panelWidth - 4, shown - 4);
			fill.endFill();
		}
	}

	static function has(letter:String):Bool {
		return can.indexOf(letter) >= 0;
	}

	/** Shows or hides a control and the layer of marks over it. */
	static function show(control:Dynamic, on:Bool):Void {
		if (control == null)
			return;
		if (control.visible != on)
			control.visible = on;
		var layer:Sprite = untyped overlays[control];
		if (layer != null && layer.visible != on)
			layer.visible = on;
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
			if (state == "playing")
				position += (flash.Lib.getTimer() - readAt) / 1000;
			readAt = flash.Lib.getTimer();
			state = state == "playing" ? "paused" : "playing";
		} else if (command == "shuffle") {
			shuffle = shuffle == "on" ? "off" : "on";
		} else if (command == "repeat") {
			repeat = repeat == "list" ? "track" : repeat == "track" ? "off" : "list";
		}
		render();
		Bus.send(ID, command);
	}

	/** A click on the gauge: jump to that point of the track. */
	static function seek(event:Dynamic):Void {
		event.stopPropagation();
		if (!Options.on(ID) || length <= 0)
			return;
		var span:Float = panelWidth - 2 * PAD - 84;
		var share:Float = gaugeHit.mouseX / span;
		if (!isFinite(share))
			return;
		if (share < 0)
			share = 0;
		if (share > 1)
			share = 1;
		var seconds = Std.int(share * length);
		position = seconds;
		readAt = flash.Lib.getTimer();
		showTime();
		Bus.send(ID, "seek\t" + seconds);
	}

	/** The volume slider moved: send the newest value a moment later, so a drag sends a few messages, not dozens. */
	static function slid(event:Dynamic):Void {
		if (settingSlider)
			return;
		var at:Float = slider.value;
		volumeWanted = Std.int(at);
		volume = volumeWanted;
		volumeAt = flash.Lib.getTimer();
		volumeText.text = volumeWanted + "%";
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

	/** Mouse down on the backing, the cover or the text (not a control): drag the player until the button is let go. */
	static function grab(event:Dynamic):Void {
		try {
			// The minimap frame's own mouse handling (focus, its sliders closing) stays out of it.
			event.stopPropagation();
			var target:Dynamic = event.target;
			var mine = target == player || target == panel || target == slot || target == mini;
			// The first presses of a session go to Arkive's log, so a test shows whether the mouse reaches the player.
			noteDrop = drags < 2;
			if (noteDrop) {
				drags++;
				var parent:Dynamic = player.parent;
				var targetName:String = target == null ? "none" : target.name;
				note("press on " + targetName + (mine ? " (drags)" : " (a control)") + ", button " + mouseButton(event) + ", mouse "
					+ (parent == null ? "?" : str(parent.mouseX) + "," + str(parent.mouseY)) + ", player " + str(player.x) + "," + str(player.y));
			}
			// Only the left button drags, as the game's DragModel has it.
			if (!mine || player.parent == null || player.stage == null || mouseButton(event) != 0)
				return;
			// Scaleform moves it with the mouse, as it does the game's own draggable pieces: no mouse numbers to read.
			dragging = true;
			dragStage = player.stage;
			player.startDrag(false);
			dragStage.addEventListener(MouseEvent.MOUSE_UP, drop);
			player.addEventListener(MouseEvent.MOUSE_UP, drop);
		} catch (error:Dynamic) {
			fail("drag", error);
		}
	}

	/** Which mouse button (0 left) a press came from: Scaleform's MouseEventEx says, a plain MouseEvent is the left one. */
	static function mouseButton(event:Dynamic):Int {
		try {
			var index:Dynamic = event.buttonIdx;
			return index == null ? 0 : index;
		} catch (error:Dynamic) {
			return 0;
		}
	}

	/** Let go: keep the player on screen and remember where it is, as an offset from its spot under the minimap. */
	static function drop(event:Dynamic):Void {
		if (!dragging)
			return;
		dragging = false;
		try {
			player.stopDrag();
			player.removeEventListener(MouseEvent.MOUSE_UP, drop);
			if (dragStage != null)
				dragStage.removeEventListener(MouseEvent.MOUSE_UP, drop);
			dragStage = null;
			if (player.parent == null || host == null)
				return;
			keepOnScreen();
			var spot:Dynamic = untyped hosts[host];
			if (spot == null)
				return;
			var x:Float = player.x;
			var y:Float = player.y;
			if (!isFinite(x + y)) {
				// Never keep a missing number: back to the spot under the minimap.
				offsetX = 0;
				offsetY = 0;
				place();
			} else {
				offsetX = x - spot.x;
				offsetY = y - spot.y;
			}
			if (noteDrop)
				note("dropped at " + str(player.x) + "," + str(player.y));
			sendLayout();
		} catch (error:Dynamic) {
			fail("drop", error);
		}
	}

	/** Tells Arkive where the player is and whether it is folded, for next time. */
	static function sendLayout():Void {
		layoutRead = true;
		var x:Int = Math.round(offsetX);
		var y:Int = Math.round(offsetY);
		Bus.send(ID, "layout\t" + x + "\t" + y + "\t" + (folded ? "1" : "0"));
	}

	/** An empty holder for a drawn picture (song or artist), placed like the text it stands in for. */
	static function picture(x:Float, y:Float):Sprite {
		var holder = new Sprite();
		holder.x = x;
		holder.y = y;
		holder.mouseEnabled = false;
		holder.mouseChildren = false;
		holder.visible = false;
		player.addChild(holder);
		var mask = new Shape();
		holder.addChild(mask);
		untyped masks[holder] = mask;
		return holder;
	}

	/** The drawn pictures' masks, so a picture sent wider than its column is cut at the column's edge. */
	static var masks = new flash.utils.Dictionary(true);

	/** How many children of a holder aren't pictures (a drawn name's holder keeps its mask first). */
	static function kept(holder:Sprite):Int {
		var mask:Shape = untyped masks[holder];
		return mask == null ? 0 : 1;
	}

	/** Cuts a drawn picture at this width (it is drawn as wide as the widest player allows). */
	static function clip(holder:Sprite, width:Float):Void {
		var mask:Shape = untyped masks[holder];
		if (mask == null)
			return;
		var g = mask.graphics;
		g.clear();
		g.beginFill(0xFFFFFF, 1);
		g.drawRect(0, 0, width, 24);
		g.endFill();
		if (holder.numChildren > 1) {
			var shown:Dynamic = holder.getChildAt(1);
			if (shown.mask != mask)
				shown.mask = mask;
		}
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
