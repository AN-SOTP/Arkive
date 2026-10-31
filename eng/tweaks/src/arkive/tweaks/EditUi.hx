package arkive.tweaks;

import flash.display.Shape;
import flash.display.Sprite;
import flash.events.Event;
import flash.events.MouseEvent;
import flash.events.TimerEvent;
import flash.geom.Point;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.text.TextField;
import flash.utils.Dictionary;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	Edit UI: move and resize the HUD. Every HUD frame (skill bar, party, raid, boss bar, minimap, chat, top bar...)
	is placed by the game's LayoutManager, one LayoutSetter per frame (efui_base, shared by every movie). After the game
	places a frame (LayoutSetter.reposition) we add the player's offset and scale; just before it places it again we put
	the frame back where the game left it, so the game always works from its own position and ours never adds up.
	The skill bar is also split into parts (skills, HP, MP, battle items, buffs...), moved inside the bar the same way.

	Offsets are kept in 1920x1080 units from the game's own position, so they hold through resolution and HUD scale
	changes. Frames the game moves outside reposition (its own animations, the party frame's drag) are caught by a
	quarter-second check that applies the offset to wherever the game put them.

	An "Edit UI" button in the escape menu's bottom bar, beside Arkive Tweaks, closes the menu and lays a grid over the
	screen with an outline per piece: drag to move, mouse wheel to resize. A small game-style bar holds Snap to grid,
	Reset piece, Restore defaults and Done. Changes go to Arkive on the Data Bus ("editui") to keep in edit-ui.txt,
	read back when the game starts.
**/
@:keep
class EditUi {
	public static inline var ID = "editui";
	static inline var FHD_W = 1920;
	static inline var FHD_H = 1080;
	/** The grid's step, in 1920x1080 units. */
	static inline var GRID = 20;
	static inline var MIN_SCALE = 0.5;
	static inline var MAX_SCALE = 2.0;
	static inline var SCALE_STEP = 0.05;
	static inline var WATCH_MS = 250;
	static inline var BUTTON_NAME = "arkiveEditUiButton";
	static inline var HINT = "Drag a piece to move it, mouse wheel over it to resize. Shift-drag a skill bar part to move the whole bar.";

	static inline var WHITE = 0xFFFFFF;
	static inline var TEXT = 0xD8D8D8;
	static inline var DIM = 0x8C8C8C;
	static inline var GOLD = 0xFFD200;
	static inline var EDGE = 0xA59C8E;

	/** The skill bar's parts: children of quickSlotFrame (by child name, "." going down), moved inside the bar. */
	static var PARTS:Array<Dynamic> = [
		{id: "quickSlotFrame/skills", label: "Skills", paths: ["aniGroup.skillSlotList", "aniGroup.skillSlotList_1", "aniGroup.skillSlotList_2"]},
		{id: "quickSlotFrame/hp", label: "HP bar", paths: ["aniGroup.playerHP"]},
		{id: "quickSlotFrame/mp", label: "MP bar", paths: ["aniGroup.playerMP"]},
		{id: "quickSlotFrame/items", label: "Battle items", paths: ["aniGroup.battleSlotList", "newLayoutGroup.newBattleSlotList"]},
		{id: "quickSlotFrame/potion", label: "Potion", paths: ["aniGroup.potionSlotList"]},
		{id: "quickSlotFrame/awakening", label: "Awakening", paths: ["aniGroup.ultraSlotList", "aniGroup.ultimateSkill"]},
		{id: "quickSlotFrame/free", label: "Extra slots", paths: ["newLayoutGroup.freeSlotList"]},
		{id: "quickSlotFrame/buffs", label: "Buffs", paths: ["buffList"]},
		{id: "quickSlotFrame/debuffs", label: "Debuffs", paths: ["deBuffList"]},
		{id: "quickSlotFrame/cast", label: "Cast bar", paths: ["aniGroup.interactionProgressGroup"]},
	];

	/** Names for the HUD frames people know; others get their frame name spelled out. */
	static var LABELS:Dynamic = {
		quickSlotFrame: "Skill bar",
		partyFrame: "Party",
		raidMemberFrame: "Raid",
		targetStatusFrame: "Target / boss bar",
		miniMapFrame: "Minimap",
		miniMapExFrame: "Minimap",
		chattingDocument: "Chat",
		topHUDFrame: "Top bar",
		questPureMiniInfo: "Quest tracker",
		quickMenuFrame: "Quick menu",
		raidProcessor: "Raid progress",
		experienceFrame: "Experience",
		durabilityFrame: "Durability",
		topInfoNoticeFrame: "Notices",
		topRightInfoNoticeFrame: "Notices (right)",
		guideHUDFrame: "Guide",
		quickSlotIdentityFrame: "Class gauge",
		epicGateProgressFrame: "Gate progress",
	};

	/** Frames listed in Edit UI even while the game hides them (no party, no target), so they can be placed early. */
	static var ALWAYS:Array<String> = ["partyFrame", "raidMemberFrame", "targetStatusFrame"];

	/** The layout: piece id -> {x, y, s} (offset in 1920x1080 units, scale); null once reset. */
	static var layout:Dynamic = {};
	static var loading = false;

	/** The game's LayoutSetter of each HUD frame seen so far, by frame name. */
	static var setters:Dynamic = {};

	/** For each object we moved: the game's own transform (b*) and the one we set (a*). Weak keys. */
	static var bases = new Dictionary(true);

	static var watcher:Timer;
	static var listening = false;

	// The edit screen.
	static var overlay:Sprite;
	static var grid:Shape;
	static var boxes:Sprite;
	static var bar:Sprite;
	static var status:TextField;
	static var snapBox:Dynamic;
	static var snap = true;
	static var shown:Array<Dynamic> = [];
	static var selected:Dynamic;
	static var dragging:Dynamic;
	static var dragStart:Point;
	static var dragFrom:Dynamic;
	static var dragBounds:Dynamic;
	static var barDrag:Point;
	static var confirmReset = 0;
	static var saveTimer:Timer;
	static var frames = 0;

	// ---- Applying the layout ----------------------------------------------------------------------------------------

	/**
		LayoutSetter.reposition, before: puts the frame and its moved parts back where the game left them. The game
		places a frame by its outline (LayoutUtil measures its bounds and moves it by the difference), so a part moved
		out of the skill bar would drag the whole bar with it.
	**/
	public static function beforeReposition(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var widget:Dynamic = self.widget;
		if (widget == null)
			return null;
		var name:String = widget.name;
		for (part in PARTS) {
			var id:String = part.id;
			if (StringTools.startsWith(id, name + "/"))
				for (object in objectsOf(id))
					restore(object);
		}
		restore(widget);
		return null;
	}

	/** LayoutSetter.reposition, after: remembers the frame, then gives it and its parts their saved offset and size. */
	public static function onReposition(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var widget:Dynamic = self.widget;
		if (widget == null)
			return;
		var name:String = widget.name;
		untyped setters[name] = self;
		start();
		for (part in PARTS) {
			var id:String = part.id;
			if (StringTools.startsWith(id, name + "/"))
				placePiece(id, entryOf(id));
		}
		var entry:Dynamic = entryOf(name);
		if (entry != null || untyped bases[widget] != null)
			place(widget, entry, null);
	}

	/** First call: reads the saved layout, starts the check timer and follows the tweak being switched on or off. */
	static function start():Void {
		if (watcher == null) {
			watcher = new Timer(WATCH_MS);
			watcher.addEventListener(TimerEvent.TIMER, watch);
			watcher.start();
		}
		if (!listening) {
			listening = true;
			Options.listen(function(tweak:String, key:String) {
				if (tweak != ID && key != "*")
					return;
				if (Options.on(ID))
					applyAll(true);
				else {
					restoreAll();
					close();
				}
			});
		}
		if (!loading) {
			loading = true;
			load();
		}
	}

	/** The saved layout file Arkive keeps (lines of "piece\tx\ty\tscale"). */
	static function load():Void {
		var home = Options.home();
		if (home == "")
			return;
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(event:Dynamic) {
			var text:String = loader.data;
			if (text == null)
				return;
			var lines:Array<String> = text.split("\n");
			for (line in lines) {
				var parts = Text.trim(line).split("\t");
				if (parts.length < 4 || parts[0].length == 0)
					continue;
				// Changes made since the game started win over the file.
				if (Text.has(layout, parts[0]))
					continue;
				var x:Float = Std.parseFloat(parts[1]);
				var y:Float = Std.parseFloat(parts[2]);
				var s:Float = Std.parseFloat(parts[3]);
				if (Math.isNaN(x) || Math.isNaN(y) || Math.isNaN(s))
					continue;
				untyped layout[parts[0]] = {x: x, y: y, s: clampScale(s)};
			}
			applyAll(true);
		});
		loader.addEventListener("ioError", function(event:Dynamic) {});
		loader.addEventListener("securityError", function(event:Dynamic) {});
		loader.load(new URLRequest(home + "/edit-ui.txt"));
	}

	static function entryOf(id:String):Dynamic {
		if (!Text.has(layout, id))
			return null;
		return untyped layout[id];
	}

	/**
		Four times a second: applies offsets to skill bar parts the game moved itself (it sets their places outright, so
		where it put them is the new base). Frames are left to reposition: the game moves those relative to where they
		are, so taking such a place as the base would add the offset twice.
	**/
	static function watch(event:Dynamic):Void {
		try {
			if (!Options.on(ID))
				return;
			applyAll(false);
		} catch (error:Dynamic) {}
	}

	/** Every saved piece where it is now, frames too when `frames` (cheap when nothing moved: only compares). */
	static function applyAll(frames:Bool):Void {
		var ids:Array<Dynamic> = untyped __keys__(layout);
		for (id in ids) {
			var key:String = id;
			if (frames || key.indexOf("/") > 0)
				placePiece(key, untyped layout[key]);
		}
	}

	/**
		Every moved object back where the game had it (Restore defaults, or the tweak switched off), then the game lays
		every HUD frame out again from its own settings, so nothing keeps a place worked out while parts were moved.
	**/
	static function restoreAll():Void {
		var objects:Array<Dynamic> = untyped __keys__(bases);
		// Parts before frames: the frames are measured with their parts at home.
		for (object in objects)
			if (!isFrame(object))
				place(object, null, null);
		for (object in objects)
			if (isFrame(object))
				place(object, null, null);
		bases = new Dictionary(true);
		reflowGame(null);
	}

	static function isFrame(object:Dynamic):Bool {
		var name:String = object.name;
		return name != null && widgetOf(name) == object;
	}

	/** The game's own layout pass: one frame's (setter.reflow), or every HUD frame's, twice for frames placed beside others. */
	static function reflowGame(name:String):Void {
		try {
			if (name != null) {
				if (Text.has(setters, name)) {
					var setter:Dynamic = untyped setters[name];
					setter.reflow();
				}
				return;
			}
			var manager:Dynamic = GameUi.definition("ark.managers.LayoutManager");
			if (manager != null) {
				manager.reflowAllManagingWidgets();
				manager.reflowAllManagingWidgets();
			}
		} catch (error:Dynamic) {}
	}

	/**
		A piece's objects given its entry (null: back to the game's). A piece of several objects (the skill rows)
		resizes around their shared corner, so they stay together.
	**/
	static function placePiece(id:String, entry:Dynamic):Void {
		var objects = objectsOf(id);
		if (entry == null || objects.length < 2) {
			for (object in objects)
				place(object, entry, null);
			return;
		}
		var parent:Dynamic = null;
		var origin:Point = null;
		for (object in objects) {
			var base:Dynamic = sync(object, true);
			if (parent == null)
				parent = object.parent;
			if (object.parent != parent)
				continue;
			var bx:Float = base.bx;
			var by:Float = base.by;
			if (origin == null)
				origin = new Point(bx, by);
			else {
				if (bx < origin.x)
					origin.x = bx;
				if (by < origin.y)
					origin.y = by;
			}
		}
		for (object in objects)
			place(object, entry, object.parent == parent ? origin : null);
	}

	/** The display objects of a piece: a HUD frame by name, or "frame/part" for a part of the skill bar. */
	static function objectsOf(id:String):Array<Dynamic> {
		var found:Array<Dynamic> = [];
		var slash = id.indexOf("/");
		if (slash < 0) {
			var widget = widgetOf(id);
			if (widget != null)
				found.push(widget);
			return found;
		}
		var part = partOf(id);
		var frame = widgetOf(id.substring(0, slash));
		if (part == null || frame == null)
			return found;
		var paths:Array<Dynamic> = part.paths;
		for (path in paths) {
			var child = childAt(frame, path);
			if (child != null)
				found.push(child);
		}
		return found;
	}

	static function widgetOf(name:String):Dynamic {
		if (!Text.has(setters, name))
			return null;
		var setter:Dynamic = untyped setters[name];
		var widget:Dynamic = setter.widget;
		return widget == null || widget.parent == null ? null : widget;
	}

	static function partOf(id:String):Dynamic {
		for (part in PARTS)
			if (part.id == id)
				return part;
		return null;
	}

	/** A named child some levels down ("aniGroup.playerHP"), by child name only, so a missing one is just null. */
	static function childAt(from:Dynamic, path:String):Dynamic {
		var at:Dynamic = from;
		var names:Array<String> = path.split(".");
		for (name in names) {
			if (at == null || !GameUi.isA(at, "flash.display.DisplayObjectContainer"))
				return null;
			at = at.getChildByName(name);
		}
		return at;
	}

	/**
		The object's record of the game's own transform (b*) and ours (a*), made when `create`. A transform that isn't
		the one we set last is the game's newest, so it becomes the base.
	**/
	static function sync(object:Dynamic, create:Bool):Dynamic {
		var base:Dynamic = untyped bases[object];
		if (base == null) {
			if (!create)
				return null;
			base = {bx: object.x, by: object.y, bsx: object.scaleX, bsy: object.scaleY, ax: Math.NaN, ay: Math.NaN, asx: Math.NaN, asy: Math.NaN};
			untyped bases[object] = base;
			return base;
		}
		if (object.x != base.ax || object.y != base.ay) {
			base.bx = object.x;
			base.by = object.y;
		}
		if (object.scaleX != base.asx || object.scaleY != base.asy) {
			base.bsx = object.scaleX;
			base.bsy = object.scaleY;
		}
		return base;
	}

	/**
		Gives the object its offset and size on top of the game's own transform, or (entry null) puts the game's back.
		With an `origin` (parent coordinates) the size is taken around it rather than the object's own corner.
	**/
	static function place(object:Dynamic, entry:Dynamic, origin:Point):Void {
		var base:Dynamic = sync(object, entry != null);
		if (base == null)
			return;
		if (entry == null) {
			object.x = base.bx;
			object.y = base.by;
			object.scaleX = base.bsx;
			object.scaleY = base.bsy;
			untyped __delete__(bases, object);
			return;
		}
		var parent:Dynamic = object.parent;
		if (parent == null)
			return;
		var unit = unitSize();
		var scale:Float = entry.s;
		var bsx:Float = base.bsx;
		var bsy:Float = base.bsy;
		var bx:Float = base.bx;
		var by:Float = base.by;
		if (origin != null) {
			bx = origin.x + (bx - origin.x) * scale;
			by = origin.y + (by - origin.y) * scale;
		}
		var ex:Float = entry.x;
		var ey:Float = entry.y;
		var from:Point = parent.localToGlobal(new Point(bx, by));
		var to:Point = parent.globalToLocal(new Point(from.x + ex * unit, from.y + ey * unit));
		if (object.scaleX != bsx * scale)
			object.scaleX = bsx * scale;
		if (object.scaleY != bsy * scale)
			object.scaleY = bsy * scale;
		if (object.x != to.x)
			object.x = to.x;
		if (object.y != to.y)
			object.y = to.y;
		// Read back: the player keeps positions in twentieths of a pixel, so compare with what it kept.
		base.ax = object.x;
		base.ay = object.y;
		base.asx = object.scaleX;
		base.asy = object.scaleY;
	}

	/** Before the game places the frame again: the game's own transform back, if it's still the one we set. */
	static function restore(object:Dynamic):Void {
		var base:Dynamic = untyped bases[object];
		if (base == null)
			return;
		if (object.x == base.ax && object.y == base.ay) {
			object.x = base.bx;
			object.y = base.by;
		}
		if (object.scaleX == base.asx && object.scaleY == base.asy) {
			object.scaleX = base.bsx;
			object.scaleY = base.bsy;
		}
		base.ax = Math.NaN;
		base.ay = Math.NaN;
		base.asx = Math.NaN;
		base.asy = Math.NaN;
	}

	/** The game's visible screen area, in stage coordinates (the area the HUD is laid out in). */
	static function screen():Dynamic {
		var extensions:Dynamic = GameUi.definition("scaleform.gfx.Extensions");
		var rect:Dynamic = extensions == null ? null : extensions.visibleRect;
		if (rect != null && rect.height > 0)
			return rect;
		var stage:Dynamic = overlay == null ? null : overlay.stage;
		if (stage == null)
			return {x: 0, y: 0, width: FHD_W, height: FHD_H};
		return {x: 0, y: 0, width: stage.stageWidth, height: stage.stageHeight};
	}

	/** Stage units per 1920x1080 unit. */
	static function unitSize():Float {
		var rect:Dynamic = screen();
		var height:Float = rect.height;
		return height / FHD_H;
	}

	static function clampScale(s:Float):Float {
		return s < MIN_SCALE ? MIN_SCALE : (s > MAX_SCALE ? MAX_SCALE : s);
	}

	// ---- The escape menu button --------------------------------------------------------------------------------------

	/** MenuWndContent.configUI: an "Edit UI" button made from the bottom bar's own button, left of Arkive Tweaks. */
	public static function onMenuConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var bar:Dynamic = self.bottomBtnList;
		var type:Dynamic = GameUi.definition("ExitMenuBottomBtinListRenderer");
		if (bar == null || type == null)
			return;
		SettingsPanel.gameFont(self.contentsGroup);
		var item:Dynamic = untyped __new__(type);
		try {
			item.setData({nameStr: "Edit UI", isSpacial: false, iconType: -1, isNew: false});
		} catch (error:Dynamic) {
			item.nameStr = "Edit UI";
		}
		item.visible = false;
		item.name = BUTTON_NAME;
		self.addChildAt(item, self.getChildIndex(bar) + 1);
		Options.own(ID, item);
		item.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
			event.stopPropagation();
			openFromMenu(self);
		});
		item.addEventListener("enterFrame", placeButton);
	}

	/**
		Every few frames while the menu shows: one button-width left of Arkive Tweaks (or of Store when that isn't
		there), the size of the bar's buttons, hidden with the bar.
	**/
	static function placeButton(event:Dynamic):Void {
		var item:Dynamic = event.currentTarget;
		var self:Dynamic = item.parent;
		var bar:Dynamic = self == null ? null : self.bottomBtnList;
		if (bar == null || self.visible != true || !Options.on(ID))
			return;
		frames++;
		if (frames % 10 != 0)
			return;
		var buttons:Array<Dynamic> = [];
		SettingsPanel.collect(bar, buttons, 0);
		var store:Dynamic = null;
		for (button in buttons) {
			var label:String = button.nameStr;
			if (label != null && label.indexOf("Store") >= 0)
				store = button;
		}
		if (store == null && buttons.length > 0)
			store = buttons[0];
		if (store == null || bar.visible != true || !SettingsPanel.shown(store, self)) {
			item.visible = false;
			return;
		}
		var origin = new Point(0, 0);
		var spot:Dynamic = self.globalToLocal(store.localToGlobal(origin));
		var far:Dynamic = self.globalToLocal(store.localToGlobal(new Point(100, 0)));
		var scale:Float = (far.x - spot.x) / 100;
		var pitch:Float = 0;
		for (button in buttons) {
			var at:Dynamic = self.globalToLocal(button.localToGlobal(origin));
			var gap:Float = at.x - spot.x;
			if (gap > 1 && (pitch == 0 || gap < pitch))
				pitch = gap;
		}
		if (pitch == 0)
			pitch = store.width * scale + 4;
		var right:Float = spot.x;
		for (button in buttons) {
			var at:Dynamic = self.globalToLocal(button.localToGlobal(origin));
			if (at.x > right)
				right = at.x;
		}
		var x:Float = spot.x - pitch;
		var tweaks:Dynamic = self.getChildByName("arkiveTweaksButton");
		if (tweaks != null && tweaks.visible == true) {
			var tweaksX:Float = tweaks.x;
			// Arkive Tweaks went after the bar's last button when its own spot was taken: follow it there.
			x = tweaksX > right ? tweaksX + pitch : tweaksX - pitch;
		}
		var width:Float = store.width * scale;
		var blocked = x < 0;
		var info:Dynamic = self.characterInfoGroup;
		if (!blocked && info != null && info.visible == true) {
			var box:Dynamic = info.getBounds(self);
			blocked = x < box.x + box.width && x + width > box.x && spot.y < box.y + box.height && spot.y + store.height * scale > box.y;
		}
		if (blocked) {
			// After the bar's last button (and after Arkive Tweaks when that went there).
			if (tweaks != null && tweaks.visible == true && tweaks.x > right)
				right = tweaks.x;
			x = right + pitch;
		}
		if (item.x != x)
			item.x = x;
		if (item.y != spot.y)
			item.y = spot.y;
		item.width = width;
		item.height = store.height * scale;
		SettingsPanel.dress(item, store, "Edit UI", "", cornersMark);
		item.visible = true;
	}

	/**
		The button's icon: the four corners of a square, each an L pointing outwards (the usual "move / expand" mark),
		in the colour of the bar's other icons.
	**/
	static function cornersMark(size:Float):flash.display.MovieClip {
		var mark = new flash.display.MovieClip();
		var g = mark.graphics;
		var inset:Float = size * 0.2;
		var far:Float = size - inset;
		var arm:Float = size * 0.22;
		var gap:Float = size * 0.1;
		var thick:Float = Math.max(2, Math.round(size / 14));
		// Plain strings for the cap and joint: no lookup of the player's style classes.
		var pen:Dynamic = g;
		pen.lineStyle(thick, 0xD1D2D0, 1, false, "normal", "square", "miter");
		// Each corner: the L, then a short stroke from it towards the middle, so the corners read as pushed outwards.
		g.moveTo(inset, inset + arm);
		g.lineTo(inset, inset);
		g.lineTo(inset + arm, inset);
		g.moveTo(far - arm, inset);
		g.lineTo(far, inset);
		g.lineTo(far, inset + arm);
		g.moveTo(far, far - arm);
		g.lineTo(far, far);
		g.lineTo(far - arm, far);
		g.moveTo(inset + arm, far);
		g.lineTo(inset, far);
		g.lineTo(inset, far - arm);
		g.lineStyle(Math.max(1, Math.round(size / 20)), 0xD1D2D0, 1);
		g.moveTo(inset + gap, inset + gap);
		g.lineTo(inset + gap + arm * 0.6, inset + gap + arm * 0.6);
		g.moveTo(far - gap, inset + gap);
		g.lineTo(far - gap - arm * 0.6, inset + gap + arm * 0.6);
		g.moveTo(far - gap, far - gap);
		g.lineTo(far - gap - arm * 0.6, far - gap - arm * 0.6);
		g.moveTo(inset + gap, far - gap);
		g.lineTo(inset + gap + arm * 0.6, far - gap - arm * 0.6);
		return mark;
	}

	/** Closes the escape menu (its own close button, which the game listens to), then opens the edit screen. */
	static function openFromMenu(menu:Dynamic):Void {
		var stage:Dynamic = menu.stage;
		var window:Dynamic = GameUi.ancestor(menu, "ark.controls.UIWindow");
		var closed = false;
		if (window != null) {
			var close:Dynamic = window.closeBtn;
			if (close != null) {
				try {
					close.dispatchEventAndSound(new MouseEvent(MouseEvent.CLICK));
					closed = true;
				} catch (error:Dynamic) {}
			}
		}
		var timer = new Timer(50, 1);
		timer.addEventListener(TimerEvent.TIMER, function(event:Dynamic) {
			try {
				open(stage);
				if (!closed)
					say("Press Esc to close the menu, then drag the outlines.");
			} catch (error:Dynamic) {}
		});
		timer.start();
	}

	// ---- The edit screen ---------------------------------------------------------------------------------------------

	static function open(stage:Dynamic):Void {
		if (stage == null)
			return;
		start();
		if (overlay == null)
			build();
		stage.addChild(overlay);
		overlay.visible = true;
		selected = null;
		confirmReset = 0;
		drawGrid();
		refresh();
		placeBar(true);
		say(HINT);
	}

	static function close():Void {
		if (overlay == null)
			return;
		endDrag(null);
		overlay.visible = false;
		if (overlay.parent != null)
			overlay.parent.removeChild(overlay);
	}

	static function build():Void {
		overlay = new Sprite();
		overlay.name = "arkiveEditUi";
		grid = new Shape();
		overlay.addChild(grid);
		boxes = new Sprite();
		overlay.addChild(boxes);
		bar = new Sprite();
		overlay.addChild(bar);
		buildBar();
		overlay.addEventListener("enterFrame", onFrame);
		overlay.addEventListener(MouseEvent.MOUSE_MOVE, onMove);
		overlay.addEventListener(MouseEvent.MOUSE_UP, endDrag);
		overlay.addEventListener(MouseEvent.MOUSE_DOWN, function(event:Dynamic) {
			// A click on the grid itself drops the selection.
			if (event.target == overlay) {
				selected = null;
				refresh();
				say(HINT);
			}
		});
	}

	static inline var BAR_W = 560;
	static inline var BAR_H = 126;

	/** The bar: the game's window frame with a title, Snap to grid, Reset piece, Restore defaults and Done. */
	static function buildBar():Void {
		var frame:Dynamic = SettingsPanel.make("WindowBG_V2");
		if (frame != null) {
			frame.width = BAR_W;
			frame.height = BAR_H;
			frame.mouseEnabled = false;
			frame.mouseChildren = false;
			bar.addChild(frame);
		} else {
			var g = bar.graphics;
			g.lineStyle(1, 0x4A4F57, 1);
			g.beginFill(0x15181D, 0.98);
			g.drawRect(0, 0, BAR_W, BAR_H);
			g.endFill();
		}
		var back = new Sprite();
		back.graphics.beginFill(0x000000, 0.01);
		back.graphics.drawRect(0, 0, BAR_W, BAR_H);
		back.graphics.endFill();
		// The bar moves by its title area, in case it covers something.
		back.addEventListener(MouseEvent.MOUSE_DOWN, function(event:Dynamic) {
			event.stopPropagation();
			barDrag = new Point(bar.mouseX, bar.mouseY);
		});
		bar.addChildAt(back, 0);
		bar.addChild(SettingsPanel.text(20, 12, 200, 18, WHITE, "Edit UI", SettingsPanel.titleFont));

		snapBox = SettingsPanel.make("DefaultCheckBox_V2");
		if (snapBox != null) {
			snapBox.autoSize = "none";
			snapBox.label = "Snap to grid";
			snapBox.width = 150;
			snapBox.selected = snap;
			snapBox.x = BAR_W - 170;
			snapBox.y = 14;
			snapBox.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
				event.stopPropagation();
				snap = snapBox.selected == true;
				drawGrid();
			});
			bar.addChild(snapBox);
		}

		var left:Float = 20;
		left = addButton("Reset piece", left, resetPiece);
		left = addButton("Restore defaults", left, restoreDefaults);
		addButton("Done", left, function() close());

		status = SettingsPanel.text(20, BAR_H - 44, BAR_W - 40, 13, DIM, "", SettingsPanel.font);
		status.multiline = true;
		status.wordWrap = true;
		status.height = 38;
		bar.addChild(status);
	}

	static function addButton(label:String, left:Float, action:Void->Void):Float {
		var button:Dynamic = GameUi.button(label);
		if (button == null) {
			button = new Sprite();
			var field = SettingsPanel.text(8, 6, 150, 14, TEXT, label, SettingsPanel.font);
			field.width = field.textWidth + 12;
			button.graphics.lineStyle(1, EDGE, 1);
			button.graphics.beginFill(0x1A1C21, 1);
			button.graphics.drawRect(0, 0, field.width + 16, 30);
			button.graphics.endFill();
			button.addChild(field);
			button.buttonMode = true;
			button.mouseChildren = false;
			button.addEventListener(MouseEvent.CLICK, function(event:Dynamic) action());
		} else {
			button.autoSize = "none";
			button.width = label.length * 8 + 40;
			button.height = 30;
			button.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
				event.stopPropagation();
				action();
			});
		}
		button.addEventListener(MouseEvent.MOUSE_DOWN, function(event:Dynamic) event.stopPropagation());
		button.x = left;
		button.y = 44;
		bar.addChild(button);
		return left + button.width + 10;
	}

	/** Centred near the top the first time; kept on screen after that. */
	static function placeBar(first:Bool):Void {
		var rect:Dynamic = screen();
		var rx:Float = rect.x;
		var ry:Float = rect.y;
		var rw:Float = rect.width;
		var rh:Float = rect.height;
		if (first && bar.x == 0 && bar.y == 0) {
			bar.x = Std.int(rx + (rw - BAR_W) / 2);
			bar.y = Std.int(ry + rh * 0.22);
		}
		if (bar.x < rx)
			bar.x = rx;
		if (bar.y < ry)
			bar.y = ry;
		if (bar.x > rx + rw - BAR_W)
			bar.x = rx + rw - BAR_W;
		if (bar.y > ry + rh - BAR_H)
			bar.y = ry + rh - BAR_H;
	}

	/** A light shade over the screen (it also takes the clicks) and the grid lines, the centre lines a bit brighter. */
	static function drawGrid():Void {
		if (grid == null)
			return;
		var rect:Dynamic = screen();
		var rx:Float = rect.x;
		var ry:Float = rect.y;
		var rw:Float = rect.width;
		var rh:Float = rect.height;
		var g = overlay.graphics;
		g.clear();
		g.beginFill(0x000000, 0.25);
		g.drawRect(rx, ry, rw, rh);
		g.endFill();
		var lines = grid.graphics;
		lines.clear();
		if (snap) {
			var step:Float = GRID * unitSize();
			lines.lineStyle(1, WHITE, 0.06);
			var x:Float = rx;
			while (x <= rx + rw) {
				lines.moveTo(x, ry);
				lines.lineTo(x, ry + rh);
				x += step;
			}
			var y:Float = ry;
			while (y <= ry + rh) {
				lines.moveTo(rx, y);
				lines.lineTo(rx + rw, y);
				y += step;
			}
		}
		lines.lineStyle(1, GOLD, 0.25);
		lines.moveTo(rx + rw / 2, ry);
		lines.lineTo(rx + rw / 2, ry + rh);
		lines.moveTo(rx, ry + rh / 2);
		lines.lineTo(rx + rw, ry + rh / 2);
	}

	/**
		Every piece that can be placed now: the HUD frames the game's LayoutManager has on screen (not windows, which
		have their own drag, and not full-screen layers like name plates), then the skill bar's parts.
	**/
	static function collectPieces():Array<Dynamic> {
		var found:Array<Dynamic> = [];
		var manager:Dynamic = GameUi.definition("ark.managers.LayoutManager");
		var list:Dynamic = manager == null ? null : manager.managingLayoutSetters;
		var rect:Dynamic = screen();
		var rw:Float = rect.width;
		var rh:Float = rect.height;
		if (list != null) {
			var count:Int = list.length;
			for (i in 0...count) {
				var setter:Dynamic = list[i];
				var widget:Dynamic = setter == null ? null : setter.widget;
				if (widget == null || widget.stage == null || widget.parent == null)
					continue;
				var name:String = widget.name;
				if (name == null || name.length == 0 || GameUi.isA(widget, "ark.controls.UIWindow"))
					continue;
				untyped setters[name] = setter;
				var visible = onScreen(widget);
				if (!visible && ALWAYS.indexOf(name) < 0)
					continue;
				var bounds:Dynamic = widget.getBounds(overlay.stage);
				var bw:Float = bounds.width;
				var bh:Float = bounds.height;
				if (bw < 4 || bh < 4 || (bw > rw * 0.8 && bh > rh * 0.8))
					continue;
				if (seen(found, name))
					continue;
				found.push({id: name, label: labelOf(name), objects: [widget], hidden: !visible});
			}
		}
		var frame = widgetOf("quickSlotFrame");
		if (frame != null && onScreen(frame)) {
			for (part in PARTS) {
				var objects = objectsOf(part.id);
				if (objects.length == 0)
					continue;
				var any = false;
				var visibleOnes:Array<Dynamic> = [];
				for (object in objects)
					if (object.visible == true) {
						any = true;
						visibleOnes.push(object);
					}
				var label:String = part.label;
				found.push({id: part.id, label: label, objects: any ? visibleOnes : [objects[0]], hidden: !any});
			}
		}
		return found;
	}

	static function seen(pieces:Array<Dynamic>, id:String):Bool {
		for (piece in pieces)
			if (piece.id == id)
				return true;
		return false;
	}

	/** Shown when it and every parent up to the stage are visible. */
	static function onScreen(object:Dynamic):Bool {
		var at:Dynamic = object;
		while (at != null) {
			if (at.visible != true || at.alpha < 0.05)
				return false;
			at = at.parent;
		}
		return true;
	}

	/** The known name, else the frame name spelled out ("modBossProgressFrame" -> "Mod Boss Progress"). */
	static function labelOf(name:String):String {
		if (Text.has(LABELS, name)) {
			var known:String = untyped LABELS[name];
			return known;
		}
		if (StringTools.endsWith(name, "SkinFrame") || name.indexOf("identity") == 0)
			return "Class gauge";
		var plain = StringTools.endsWith(name, "Frame") ? name.substring(0, name.length - 5) : name;
		var out = "";
		for (i in 0...plain.length) {
			var c = plain.charAt(i);
			var code = StringTools.fastCodeAt(plain, i);
			if (i == 0)
				out += c.toUpperCase();
			else if (code >= 65 && code <= 90)
				out += " " + c;
			else
				out += c;
		}
		return out;
	}

	/** Builds the outlines again (pieces come and go as frames show and hide). */
	static function refresh():Void {
		if (boxes == null)
			return;
		while (boxes.numChildren > 0)
			boxes.removeChildAt(0);
		shown = collectPieces();
		for (piece in shown) {
			var box = new Sprite();
			box.buttonMode = true;
			box.mouseChildren = false;
			var label = SettingsPanel.text(4, 2, 300, 13, TEXT, "", SettingsPanel.font);
			var hidden:Bool = piece.hidden;
			var name:String = piece.label;
			label.text = hidden ? name + " (hidden now)" : name;
			label.width = label.textWidth + 10;
			box.addChild(label);
			box.addEventListener(MouseEvent.MOUSE_DOWN, beginDrag);
			box.addEventListener(MouseEvent.MOUSE_WHEEL, onWheel);
			piece.box = box;
			piece.label_ = label;
			piece.drawn = "";
			boxes.addChild(box);
		}
		drawBoxes();
	}

	/** Each outline over its piece's bounds; redrawn only when those change. */
	static function drawBoxes():Void {
		for (piece in shown) {
			var box:Dynamic = piece.box;
			var bounds = boundsOf(piece);
			if (bounds == null) {
				box.visible = false;
				continue;
			}
			var bx:Float = bounds.x;
			var by:Float = bounds.y;
			var bw:Float = bounds.width;
			var bh:Float = bounds.height;
			var isSelected = selected != null && selected.id == piece.id;
			var key = Std.int(bx) + "," + Std.int(by) + "," + Std.int(bw) + "," + Std.int(bh) + (isSelected ? "s" : "");
			var drawn:String = piece.drawn;
			if (key == drawn)
				continue;
			piece.drawn = key;
			box.visible = true;
			box.x = bx;
			box.y = by;
			var g = box.graphics;
			g.clear();
			var hidden:Bool = piece.hidden;
			g.beginFill(isSelected ? 0x2A2410 : 0x0B0E12, hidden ? 0.25 : 0.4);
			g.lineStyle(isSelected ? 2 : 1, isSelected ? GOLD : EDGE, hidden ? 0.5 : 0.9);
			g.drawRect(0, 0, bw, bh);
			g.endFill();
			var label:Dynamic = piece.label_;
			label.textColor = isSelected ? GOLD : (hidden ? DIM : TEXT);
		}
	}

	/** The union of the piece's objects' bounds on the stage. */
	static function boundsOf(piece:Dynamic):Dynamic {
		var objects:Array<Dynamic> = piece.objects;
		var stage:Dynamic = overlay.stage;
		var result:Dynamic = null;
		for (object in objects) {
			if (object.stage == null)
				continue;
			var b:Dynamic = object.getBounds(stage);
			var bw:Float = b.width;
			if (bw <= 0)
				continue;
			result = result == null ? b : result.union(b);
		}
		return result;
	}

	static function onFrame(event:Dynamic):Void {
		try {
			if (!Options.on(ID)) {
				close();
				return;
			}
			if (barDrag != null) {
				bar.x = overlay.mouseX - barDrag.x;
				bar.y = overlay.mouseY - barDrag.y;
				placeBar(false);
			}
			frames++;
			// New frames (party joined, target picked) every second; outlines follow their pieces every frame.
			if (dragging == null && frames % 60 == 0)
				refresh();
			else
				drawBoxes();
			if (confirmReset > 0) {
				confirmReset--;
				if (confirmReset == 0)
					say(selected == null ? HINT : describe(selected));
			}
			// Keeps the screen above windows the game opens later.
			var parent:Dynamic = overlay.parent;
			if (parent != null && parent.getChildIndex(overlay) != parent.numChildren - 1)
				parent.setChildIndex(overlay, parent.numChildren - 1);
		} catch (error:Dynamic) {}
	}

	static function pieceById(id:String):Dynamic {
		for (piece in shown)
			if (piece.id == id)
				return piece;
		return null;
	}

	static function pieceOfBox(box:Dynamic):Dynamic {
		for (piece in shown)
			if (piece.box == box)
				return piece;
		return null;
	}

	static function beginDrag(event:Dynamic):Void {
		event.stopPropagation();
		var piece = pieceOfBox(event.currentTarget);
		if (piece == null)
			return;
		// Shift-drag on a part of the skill bar moves the whole bar (the parts can cover most of it).
		var id:String = piece.id;
		var slash = id.indexOf("/");
		if (event.shiftKey == true && slash > 0) {
			var whole = pieceById(id.substring(0, slash));
			if (whole != null)
				piece = whole;
		}
		selected = piece;
		dragging = piece;
		dragStart = new Point(overlay.mouseX, overlay.mouseY);
		var entry:Dynamic = entryOf(piece.id);
		dragFrom = entry == null ? {x: 0.0, y: 0.0, s: 1.0} : {x: entry.x, y: entry.y, s: entry.s};
		dragBounds = boundsOf(piece);
		say(describe(piece));
		drawBoxes();
	}

	/** Moves the piece with the mouse; with Snap on, its top-left corner lands on the grid. */
	static function onMove(event:Dynamic):Void {
		if (dragging == null || dragBounds == null)
			return;
		try {
			var unit = unitSize();
			var dx:Float = overlay.mouseX - dragStart.x;
			var dy:Float = overlay.mouseY - dragStart.y;
			if (snap) {
				var rect:Dynamic = screen();
				var step:Float = GRID * unit;
				var left:Float = dragBounds.x + dx;
				var top:Float = dragBounds.y + dy;
				var rx:Float = rect.x;
				var ry:Float = rect.y;
				left = rx + Math.round((left - rx) / step) * step;
				top = ry + Math.round((top - ry) / step) * step;
				dx = left - dragBounds.x;
				dy = top - dragBounds.y;
			}
			var fromX:Float = dragFrom.x;
			var fromY:Float = dragFrom.y;
			var s:Float = dragFrom.s;
			setEntry(dragging.id, fromX + dx / unit, fromY + dy / unit, s);
			drawBoxes();
		} catch (error:Dynamic) {}
	}

	static function endDrag(event:Dynamic):Void {
		barDrag = null;
		if (dragging == null)
			return;
		var piece = dragging;
		dragging = null;
		var entry:Dynamic = entryOf(piece.id);
		if (entry != null)
			save(piece.id);
		say(describe(piece));
	}

	/** Mouse wheel over a piece: a size step up or down, around the piece's own corner. */
	static function onWheel(event:Dynamic):Void {
		event.stopPropagation();
		var piece = pieceOfBox(event.currentTarget);
		if (piece == null)
			return;
		selected = piece;
		var entry:Dynamic = entryOf(piece.id);
		var x:Float = entry == null ? 0 : entry.x;
		var y:Float = entry == null ? 0 : entry.y;
		var s:Float = entry == null ? 1 : entry.s;
		var delta:Float = event.delta;
		s = clampScale(Math.round((s + (delta > 0 ? SCALE_STEP : -SCALE_STEP)) * 100) / 100);
		setEntry(piece.id, x, y, s);
		say(describe(piece));
		drawBoxes();
		// The wheel turns in steps: keep once it stops.
		if (saveTimer == null) {
			saveTimer = new Timer(600, 1);
			saveTimer.addEventListener(TimerEvent.TIMER, function(e:Dynamic) {
				if (selected != null && entryOf(selected.id) != null)
					save(selected.id);
			});
		}
		saveTimer.reset();
		saveTimer.start();
	}

	/** Changes a piece's entry and moves its objects now. */
	static function setEntry(id:String, x:Float, y:Float, s:Float):Void {
		var entry:Dynamic = {x: Math.round(x * 10) / 10, y: Math.round(y * 10) / 10, s: s};
		untyped layout[id] = entry;
		placePiece(id, entry);
	}

	static function resetPiece():Void {
		if (selected == null) {
			say("Pick a piece first, then Reset piece puts it back where the game has it.");
			return;
		}
		var id:String = selected.id;
		var label:String = selected.label;
		clearEntry(id);
		Bus.send("editui", "clear\t" + id);
		say(label + " is back where the game puts it.");
	}

	static function clearEntry(id:String):Void {
		untyped layout[id] = null;
		untyped __delete__(layout, id);
		placePiece(id, null);
		// The frame (or the part's frame) laid out again by the game, from its own settings.
		var slash = id.indexOf("/");
		reflowGame(slash < 0 ? id : id.substring(0, slash));
	}

	/** Two clicks within about four seconds: every piece back where the game puts it. */
	static function restoreDefaults():Void {
		if (confirmReset == 0) {
			confirmReset = 240;
			say("Click Restore defaults again to put every piece back where the game puts it.");
			return;
		}
		confirmReset = 0;
		var ids:Array<Dynamic> = untyped __keys__(layout);
		for (id in ids) {
			var key:String = id;
			untyped __delete__(layout, key);
		}
		restoreAll();
		Bus.send("editui", "reset");
		selected = null;
		refresh();
		say("Every piece is back where the game puts it.");
	}

	static function save(id:String):Void {
		var entry:Dynamic = entryOf(id);
		if (entry == null)
			return;
		var x:Float = entry.x;
		var y:Float = entry.y;
		var s:Float = entry.s;
		Bus.send("editui", "set\t" + id + "\t" + number(x) + "\t" + number(y) + "\t" + number(s));
	}

	static function describe(piece:Dynamic):String {
		var entry:Dynamic = entryOf(piece.id);
		var label:String = piece.label;
		if (entry == null)
			return label + ": where the game puts it. Drag to move, mouse wheel to resize.";
		var s:Float = entry.s;
		return label + ": moved, size " + number(Math.round(s * 100)) + "%. Drag to move, mouse wheel to resize.";
	}

	static function number(value:Float):String {
		var text:String = untyped value.toString();
		return text;
	}

	static function say(message:String):Void {
		if (status != null)
			status.text = message;
	}
}
