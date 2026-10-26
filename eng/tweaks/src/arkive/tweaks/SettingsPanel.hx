package arkive.tweaks;

import flash.display.Shape;
import flash.display.Sprite;
import flash.events.MouseEvent;
import flash.events.TimerEvent;
import flash.geom.Point;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	In-game Tweak Settings: an "Arkive Tweaks" button in the escape menu's bottom bar, beside Store and made from the
	bar's own button, opens a window laid out like the game's Settings: the game's window frame, title and close button,
	then one section per tweak going down, with the game's tick boxes for switches and its Settings drop-down for single
	choices, and the game's scroll bar. The controls come from the shared component movies (componentsV2), the ones
	Settings is built from; where one is missing a plain drawn stand-in is used.

	A change acts at once (Options' live store, which every window shares) and is sent to Arkive on the Data Bus to
	keep; the window says "Saved" once Arkive's live.json answers, or that the change lasts until the game closes.
	These controls report only to us: none of them is bound to the game the way Settings' own rows are.
**/
@:keep
class SettingsPanel {
	static inline var WIDTH = 640;
	static inline var HEIGHT = 680;
	static inline var BODY_TOP = 60;
	static inline var BODY_BOTTOM = 46;
	static inline var SIDE = 28;
	static inline var ROW = 32;
	static inline var INDENT = 30;
	static inline var COMBO_WIDTH = 190;

	static inline var WHITE = 0xFFFFFF;
	static inline var TEXT = 0xD8D8D8;
	static inline var DIM = 0x8C8C8C;
	static inline var GOLD = 0xFFD200;

	/** Checks of live.json after a change (300 ms apart) before saying Arkive didn't answer. */
	static inline var ACK_CHECKS = 20;

	/** The bottom bar's buttons (Store, Settings, Quit...): the game's renderer symbol and class. */
	static inline var BAR_SYMBOL = "ExitMenuBottomBtinListRenderer";
	static inline var BAR_ITEM = "ark.ui.systemMenu_new.ExitMenuBottomBtnListItem";

	static var shade:Sprite;
	static var window:Sprite;
	static var body:Sprite;
	static var bodyHeight:Float = 500;
	static var scrollBar:Dynamic;
	static var scroll:Float = 0;
	static var status:TextField;
	static var font:String = "$YG760";
	static var titleFont:String = "$YoonGasiIIM";
	static var embed = false;
	static var listening = false;
	static var pending = false;

	static var placedX:Float = -1;
	static var placedY:Float = -1;
	static var frames = 0;

	/** Where each drop-down's list opens (see dropDown), added to the body after the rows. */
	static var holders:Array<Dynamic> = [];

	/** The bar's Store button (or its first) that ours sits beside. */
	static var anchor:Dynamic;

	static var request:String = "";
	static var checks = 0;
	static var checker:Timer;

	/**
		MenuWndContent.configUI: an "Arkive Tweaks" button made from the bottom bar's own button symbol (an "A" for its
		icon), placed beside Store once the game has filled the bar, and the (hidden) window.
	**/
	public static function onMenuConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var bar:Dynamic = self.bottomBtnList;
		var type:Dynamic = GameUi.definition(BAR_SYMBOL);
		if (bar == null || type == null)
			return;
		gameFont(self.contentsGroup);
		var item:Dynamic = untyped __new__(type);
		try {
			item.setData({nameStr: "Arkive Tweaks", isSpacial: false, iconType: -1, isNew: false});
		} catch (error:Dynamic) {
			item.nameStr = "Arkive Tweaks";
		}
		item.visible = false;
		item.name = "arkiveTweaksButton";
		// Just above the bar, so the menu's child windows and popups (switch character, task tracker settings) cover it
		// as they cover the bar.
		self.addChildAt(item, self.getChildIndex(bar) + 1);
		placedX = -1;
		build(self);
		item.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
			event.stopPropagation();
			toggle();
		});
		item.addEventListener("enterFrame", place);
		if (!listening) {
			listening = true;
			Options.listen(function(tweak:String, key:String) {
				if (window != null && window.visible)
					later();
			});
		}
	}

	/**
		Every frame while the menu shows: finds the bar's buttons (the game fills and may refill the bar after configUI)
		and puts ours one button-width left of Store, the same size, with the game's own label font and an "A" icon. If
		that spot is taken by the character panel or off the menu, it goes after the last button instead.
	**/
	static function place(event:Dynamic):Void {
		var item:Dynamic = event.currentTarget;
		var self:Dynamic = item.parent;
		var bar:Dynamic = self == null ? null : self.bottomBtnList;
		var frame:Dynamic = self == null ? null : self.parent;
		if (bar == null || self.visible != true || (frame != null && frame.visible != true))
			return;
		// Hides with Store (the bar or the buttons hidden, as on the switch character screen); every frame, it's cheap.
		if (item.visible == true && !shown(anchor, self)) {
			item.visible = false;
			placedX = -1;
		}
		// Once placed, look again only every half second or so; while hidden, a few times a second.
		frames++;
		if (frames % (item.visible == true ? 30 : 10) != 0)
			return;
		var buttons:Array<Dynamic> = [];
		collect(bar, buttons, 0);
		if (buttons.length < 2 || bar.visible != true) {
			item.visible = false;
			return;
		}
		var origin = new Point(0, 0);
		var xs:Array<Float> = [];
		for (button in buttons) {
			var at:Dynamic = self.globalToLocal(button.localToGlobal(origin));
			xs.push(at.x);
		}
		var first = 0;
		var last = 0;
		var store = -1;
		for (i in 0...buttons.length) {
			if (xs[i] < xs[first])
				first = i;
			if (xs[i] > xs[last])
				last = i;
			var label:String = buttons[i].nameStr;
			if (label != null && label.indexOf("Store") >= 0)
				store = i;
		}
		if (store < 0)
			store = first;
		anchor = buttons[store];
		var spot:Dynamic = self.globalToLocal(anchor.localToGlobal(origin));
		var far:Dynamic = self.globalToLocal(anchor.localToGlobal(new Point(100, 0)));
		var scale:Float = (far.x - spot.x) / 100;
		var pitch:Float = 0;
		for (i in 0...buttons.length) {
			var gap:Float = xs[i] - spot.x;
			if (gap > 1 && (pitch == 0 || gap < pitch))
				pitch = gap;
		}
		if (pitch == 0)
			pitch = anchor.width * scale + 4;
		var x:Float = spot.x - pitch;
		var y:Float = spot.y;
		// Store hasn't moved since the last look: nothing to do.
		if (spot.x == placedX && spot.y == placedY && item.visible == true)
			return;
		var itemWidth:Float = anchor.width * scale;
		var itemHeight:Float = anchor.height * scale;
		var info:Dynamic = self.characterInfoGroup;
		var blocked = x < 0;
		if (!blocked && info != null && info.visible == true) {
			var box:Dynamic = info.getBounds(self);
			blocked = x < box.x + box.width && x + itemWidth > box.x && y < box.y + box.height && y + itemHeight > box.y;
		}
		if (blocked) {
			var end:Dynamic = self.globalToLocal(buttons[last].localToGlobal(origin));
			x = end.x + pitch;
			y = end.y;
		}
		item.x = x;
		item.y = y;
		item.width = itemWidth;
		item.height = itemHeight;
		dress(item, anchor);
		item.visible = true;
		placedX = spot.x;
		placedY = spot.y;
	}

	/** True when the object and every parent up to `top` are visible and not faded out. */
	static function shown(at:Dynamic, top:Dynamic):Bool {
		if (at == null)
			return false;
		while (at != null && at != top) {
			if (at.visible != true || at.alpha < 0.05)
				return false;
			at = at.parent;
		}
		return at == top;
	}

	/** The bar's buttons that show, searched a few levels down (the tile list keeps them in its own holder). */
	static function collect(at:Dynamic, found:Array<Dynamic>, depth:Int):Void {
		if (depth > 4 || at == null || !GameUi.isA(at, "flash.display.DisplayObjectContainer"))
			return;
		var count:Int = at.numChildren;
		for (i in 0...count) {
			var child:Dynamic = at.getChildAt(i);
			if (child == null || child.visible != true || child.name == "arkiveTweaksButton")
				continue;
			if (GameUi.isA(child, BAR_ITEM)) {
				var label:String = child.nameStr;
				if (label != null && label.length > 0)
					found.push(child);
			} else {
				collect(child, found, depth + 1);
			}
		}
	}

	/**
		Our label in the game's label font and colour, and an "A" in a ring where the game's buttons have their icon,
		set as the button's iconMc so it takes the game's hover and press tints.
	**/
	static function dress(item:Dynamic, like:Dynamic):Void {
		var label:Dynamic = item.nameTextField;
		var model:Dynamic = like.nameTextField;
		if (label != null && model != null) {
			var format:Dynamic = model.getTextFormat();
			label.embedFonts = model.embedFonts;
			label.defaultTextFormat = format;
			label.htmlText = "Arkive Tweaks";
			label.setTextFormat(format);
			label.textColor = model.textColor;
		}
		if (item.iconMc != null)
			return;
		var icon:Dynamic = like.iconMc;
		var size:Float = icon == null ? 30 : (icon.height > icon.width ? icon.height : icon.width);
		if (size < 16)
			size = 30;
		// iconMc is typed MovieClip in the game class, so the mark must be one.
		var mark = new flash.display.MovieClip();
		var letter = new TextField();
		var format = new TextFormat();
		format.font = model == null ? font : model.getTextFormat().font;
		format.size = Std.int(size * 0.8);
		format.align = flash.text.TextFormatAlign.CENTER;
		letter.embedFonts = model == null ? embed : model.embedFonts;
		letter.defaultTextFormat = format;
		letter.textColor = 0xD1D2D0;
		letter.selectable = false;
		letter.mouseEnabled = false;
		letter.width = size;
		letter.height = size;
		letter.text = "A";
		letter.y = (size - letter.textHeight) / 2 - 2;
		mark.addChild(letter);
		if (icon != null) {
			mark.x = icon.x;
			mark.y = icon.y;
		}
		item.container.addChild(mark);
		item.iconMc = mark;
	}

	/** A new instance of a symbol from the shared component movies, or null when none is loaded. */
	static function make(symbol:String):Dynamic {
		var type:Dynamic = GameUi.definition(symbol);
		return type == null ? null : untyped __new__(type);
	}

	/**
		The window: a dark shade over the menu (a click on it closes the window), the game's window frame with a title
		and its close button, a scrolling body and a status line along the bottom. Hidden until the button is pressed.
	**/
	static function build(self:Dynamic):Void {
		if (shade != null && shade.parent != null)
			shade.parent.removeChild(shade);
		shade = new Sprite();
		shade.visible = false;
		shade.addEventListener(MouseEvent.CLICK, function(event:Dynamic) {
			if (event.target == shade)
				hide();
		});
		window = new Sprite();
		var frame:Dynamic = make("WindowBG_V2");
		if (frame != null) {
			frame.width = WIDTH;
			frame.height = HEIGHT;
			frame.mouseEnabled = false;
			window.addChild(frame);
		} else {
			var g = window.graphics;
			g.lineStyle(1, 0x4A4F57, 1);
			g.beginFill(0x15181D, 0.98);
			g.drawRect(0, 0, WIDTH, HEIGHT);
			g.endFill();
		}
		// Keeps clicks inside the window from reaching the shade.
		var back = new Shape();
		back.graphics.beginFill(0x000000, 0.01);
		back.graphics.drawRect(0, 0, WIDTH, HEIGHT);
		back.graphics.endFill();
		window.addChildAt(back, 0);
		var title = text(SIDE, 14, WIDTH - 2 * SIDE - 40, 18, WHITE, "Arkive Tweaks", titleFont);
		window.addChild(title);
		var rule = new Shape();
		rule.graphics.lineStyle(1, 0xFFFFFF, 0.12);
		rule.graphics.moveTo(SIDE - 8, BODY_TOP - 8);
		rule.graphics.lineTo(WIDTH - SIDE + 8, BODY_TOP - 8);
		window.addChild(rule);
		var close:Dynamic = make("s_closeBtn_V2");
		if (close == null) {
			close = new Sprite();
			var x = text(0, 0, 24, 16, TEXT, "X", font);
			close.addChild(x);
			close.buttonMode = true;
			close.addEventListener(MouseEvent.CLICK, function(event:Dynamic) hide());
		} else {
			close.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) {
				event.stopPropagation();
				hide();
			});
		}
		close.x = WIDTH - 18 - (close.width > 0 ? close.width : 24);
		close.y = 14;
		window.addChild(close);

		body = new Sprite();
		body.y = BODY_TOP;
		bodyHeight = HEIGHT - BODY_TOP - BODY_BOTTOM;
		var mask = new Shape();
		mask.graphics.beginFill(0xFFFFFF);
		mask.graphics.drawRect(0, BODY_TOP, WIDTH, bodyHeight);
		mask.graphics.endFill();
		window.addChild(mask);
		window.addChild(body);
		body.mask = mask;
		scrollBar = make("DefaultScrollBar_V2");
		if (scrollBar != null) {
			scrollBar.x = WIDTH - 22;
			scrollBar.y = BODY_TOP;
			scrollBar.height = bodyHeight;
			scrollBar.addEventListener("scroll", function(event:Dynamic) {
				var position:Float = scrollBar.position;
				scrollTo(position, false);
			});
			window.addChild(scrollBar);
		}
		var footer = new Shape();
		footer.graphics.lineStyle(1, 0xFFFFFF, 0.12);
		footer.graphics.moveTo(SIDE - 8, HEIGHT - BODY_BOTTOM + 4);
		footer.graphics.lineTo(WIDTH - SIDE + 8, HEIGHT - BODY_BOTTOM + 4);
		window.addChild(footer);
		status = text(SIDE, HEIGHT - BODY_BOTTOM + 14, WIDTH - 2 * SIDE, 13, DIM, "", font);
		window.addChild(status);
		window.addEventListener(MouseEvent.MOUSE_WHEEL, wheel);
		shade.addChild(window);
		self.addChild(shade);
	}

	static function toggle():Void {
		if (shade == null)
			return;
		if (shade.visible) {
			hide();
			return;
		}
		layout();
		shade.visible = true;
		Options.readLive(null);
		say("Changes act straight away. Arkive keeps them while it's running.");
		render();
	}

	static function hide():Void {
		if (shade != null)
			shade.visible = false;
	}

	/** The shade covers the screen and the window sits in its middle, in the menu's own coordinates. */
	static function layout():Void {
		var parent:Dynamic = shade.parent;
		var stage:Dynamic = parent == null ? null : parent.stage;
		if (stage == null)
			return;
		var topLeft:Dynamic = parent.globalToLocal(new Point(0, 0));
		var bottomRight:Dynamic = parent.globalToLocal(new Point(stage.stageWidth, stage.stageHeight));
		var g = shade.graphics;
		g.clear();
		g.beginFill(0x000000, 0.45);
		g.drawRect(topLeft.x, topLeft.y, bottomRight.x - topLeft.x, bottomRight.y - topLeft.y);
		g.endFill();
		window.x = Std.int((topLeft.x + bottomRight.x - WIDTH) / 2);
		window.y = Std.int((topLeft.y + bottomRight.y - HEIGHT) / 2);
	}

	/** Redraws on the next frame, never from inside a control's own click. */
	static function later():Void {
		if (pending)
			return;
		pending = true;
		var timer = new Timer(1, 1);
		timer.addEventListener(TimerEvent.TIMER, function(event:Dynamic) {
			pending = false;
			render();
		});
		timer.start();
	}

	/** One section per tweak, top to bottom, with the options of the ones that are on. */
	static function render():Void {
		if (body == null)
			return;
		try {
			while (body.numChildren > 0)
				body.removeChildAt(0);
			var y:Float = 6;
			var tweaks = Options.panel();
			var first = true;
			holders = [];
			for (tweak in tweaks) {
				if (!first)
					y = divider(y);
				first = false;
				y = section(tweak, y);
			}
			// The drop-downs' lists open in these, added last so a list lies over the rows below its drop-down.
			for (holder in holders)
				body.addChild(holder);
			var content:Float = y + 8;
			if (scrollBar != null) {
				var most:Float = content - bodyHeight;
				scrollBar.visible = most > 0;
				scrollBar.setScrollProperties(bodyHeight, 0, most > 0 ? most : 0, ROW);
			}
			scrollTo(scroll, true);
		} catch (error:Dynamic) {
			say("Arkive couldn't draw this list.");
		}
	}

	static function divider(y:Float):Float {
		var line = new Shape();
		line.graphics.lineStyle(1, 0xFFFFFF, 0.08);
		line.graphics.moveTo(SIDE - 8, y + 6);
		line.graphics.lineTo(WIDTH - SIDE - 16, y + 6);
		body.addChild(line);
		return y + 14;
	}

	/** A tweak's tick box with its name, then its options, indented, while it is on. */
	static function section(tweak:Dynamic, y:Float):Float {
		var id:String = tweak.id;
		var name:String = tweak.name;
		if (tweak.restart == true) {
			body.addChild(text(SIDE, y + 6, WIDTH - 2 * SIDE - 20, 14, DIM, name, font));
			body.addChild(text(SIDE, y + 28, WIDTH - 2 * SIDE - 20, 12, DIM, "Changes the game's files: switch it in Arkive with the game closed.", font));
			return y + 52;
		}
		var on = Options.on(id);
		var box = tickBox(name, on, SIDE, y, WIDTH - 2 * SIDE - 20, id, "");
		body.addChild(box);
		y += ROW + 4;
		if (!on)
			return y;
		var options:Array<Dynamic> = tweak.options;
		for (option in options)
			y = optionRow(id, option, y);
		return y;
	}

	/** One option: a tick box, a Settings drop-down for a single choice, or a tick box per choice for a pick-list. */
	static function optionRow(id:String, option:Dynamic, y:Float):Float {
		var key:String = option.key;
		var label:String = option.label;
		var kind:String = option.kind;
		if (kind == "switch") {
			var on = Options.get(id, key, false) == true;
			body.addChild(tickBox(label, on, SIDE + INDENT, y, WIDTH - 2 * SIDE - INDENT - 20, id, key));
			return y + ROW;
		}
		var choices:Array<Dynamic> = option.choices;
		if (kind == "single") {
			var labelWidth:Float = WIDTH - 2 * SIDE - INDENT - COMBO_WIDTH - 36;
			var field = text(SIDE + INDENT, y + 6, labelWidth, 14, TEXT, label, font);
			field.multiline = true;
			field.wordWrap = true;
			field.height = field.textHeight + 8;
			body.addChild(field);
			var chosen:Dynamic = Options.get(id, key, "");
			var combo = dropDown(choices, chosen, WIDTH - SIDE - 20 - COMBO_WIDTH, y + 2, id, key);
			body.addChild(combo);
			var height:Float = field.height + 8;
			return y + (height > ROW + 4 ? height : ROW + 4);
		}
		var heading = text(SIDE + INDENT, y + 6, WIDTH - 2 * SIDE - INDENT - 20, 14, TEXT, label, font);
		body.addChild(heading);
		y += ROW;
		var chosenList = picked(id, key);
		for (choice in choices) {
			var value:String = choice.value;
			var name:String = choice.label;
			body.addChild(tickBox(name, contains(chosenList, value), SIDE + 2 * INDENT, y, WIDTH - 2 * SIDE - 2 * INDENT - 20, id, key + "\t" + value));
			y += ROW - 4;
		}
		return y + 4;
	}

	/**
		The game's tick box (DefaultCheckBox_V2) with this label; its name holds what it switches ("id\tkey", "" key for
		the tweak itself, "id\tkey\tvalue" for a pick-list choice). A drawn box stands in when the symbol isn't loaded.
	**/
	static function tickBox(label:String, on:Bool, x:Float, y:Float, width:Float, id:String, key:String):Dynamic {
		var box:Dynamic = make("DefaultCheckBox_V2");
		if (box != null) {
			box.autoSize = "none";
			box.label = label;
			box.width = width;
			box.selected = on;
			box.x = x;
			box.y = y + 4;
			box.name = id + "\t" + key;
			box.addEventListener(ButtonEvent.CLICK, ticked);
			return box;
		}
		var row = new Sprite();
		row.x = x;
		row.y = y;
		var mark = new Shape();
		var g = mark.graphics;
		g.lineStyle(1, 0xA59C8E, 1);
		g.beginFill(0x1A1C21, 1);
		g.drawRect(0, 7, 14, 14);
		g.endFill();
		if (on) {
			g.lineStyle(2, GOLD, 1);
			g.moveTo(3, 14);
			g.lineTo(6, 17);
			g.lineTo(11, 10);
		}
		row.addChild(mark);
		row.addChild(text(22, 4, width - 22, 14, on ? WHITE : TEXT, label, font));
		row.graphics.beginFill(0x000000, 0.01);
		row.graphics.drawRect(0, 0, width, ROW);
		row.graphics.endFill();
		row.buttonMode = true;
		row.mouseChildren = false;
		row.name = id + "\t" + key;
		// A Sprite takes no new fields: the drawn box keeps its state here.
		untyped drawnOn[row] = on;
		row.addEventListener(MouseEvent.CLICK, ticked);
		return row;
	}

	static var drawnOn = new flash.utils.Dictionary(true);

	/** A tick box was clicked: switch the tweak, an option, or a pick-list choice, as its name says. */
	static function ticked(event:Dynamic):Void {
		var box:Dynamic = event.currentTarget;
		var drawn:Dynamic = untyped drawnOn[box];
		var on:Bool = drawn == null ? box.selected == true : drawn != true;
		var name:String = box.name;
		var parts:Array<String> = name.split("\t");
		var id = parts[0];
		var key = parts[1];
		if (key == "") {
			Options.setOn(id, on);
			save(id, "", on ? "1" : "0");
		} else if (parts.length > 2) {
			var value = parts[2];
			var next:Array<Dynamic> = [];
			for (item in picked(id, key))
				if (item != value)
					next.push(item);
			if (on)
				next.push(value);
			Options.set(id, key, next);
			// Only the change goes to Arkive ("+value" or "-value"): the whole list would take seconds to spell.
			save(id, key, (on ? "+" : "-") + value);
		} else {
			Options.set(id, key, on);
			save(id, key, on ? "1" : "0");
		}
		later();
	}

	/**
		The Settings drop-down (SystemOptionComboBox_V2) set up as Settings sets up its own; its name holds "id\tkey" and
		its choices' values sit in the same order as its rows. A row of round marks stands in when the symbol isn't loaded.
	**/
	static function dropDown(choices:Array<Dynamic>, chosen:Dynamic, x:Float, y:Float, id:String, key:String):Dynamic {
		var rows:Array<Dynamic> = [];
		var index = 0;
		for (i in 0...choices.length) {
			var label:String = choices[i].label;
			rows.push({label: label, labelHtml: label});
			if (choices[i].value == chosen)
				index = i;
		}
		var combo:Dynamic = make("SystemOptionComboBox_V2");
		if (combo == null)
			combo = make("DefaultDropdownMenu_V2");
		if (combo != null) {
			try {
				combo.dropdown = "DefaultEFDropdownList_V2";
				combo.itemRenderer = "ComboBoxListRendererV2";
				combo.scrollBar = "DefaultEFScrollBarSmall_V2";
				// The list opens in a holder of its own in the window's body, as Settings opens its lists in a
				// "renderContainer" beside each drop-down; the game's pop-up layer put it outside the window. It opens
				// upwards when there isn't room below in the visible part of the list.
				var listHeight:Float = 28 * (choices.length < 5 ? choices.length : 5) + 8;
				combo.menuDirection = y - scroll + 34 + listHeight > bodyHeight ? "up" : "down";
				combo.menuRowCount = 5;
				combo.menuMargin = 1;
				combo.autoSize = "none";
				combo.width = COMBO_WIDTH;
				combo.x = x;
				combo.y = y;
				combo.name = id + "\t" + key;
				var holder = new flash.display.MovieClip();
				holder.name = "arkiveDropList" + holders.length;
				holder.x = x;
				holders.push(holder);
				combo.renderContainer = holder.name;
				combo.dataProviderArray = rows;
				combo.selectedIndex = index;
				combo.addEventListener("listIndexChange", function(event:Dynamic) {
					var at:Int = combo.selectedIndex;
					if (at < 0 || at >= choices.length)
						return;
					var value:String = choices[at].value;
					if (Options.get(id, key, "") == value)
						return;
					Options.set(id, key, value);
					save(id, key, value);
				});
				return combo;
			} catch (error:Dynamic) {}
		}
		var row = new Sprite();
		row.x = x;
		row.y = y + 4;
		var left:Float = 0;
		for (i in 0...choices.length) {
			var value:String = choices[i].value;
			var label:String = choices[i].label;
			var item = new Sprite();
			var mark = new Shape();
			mark.graphics.lineStyle(1, 0xA59C8E, 1);
			mark.graphics.beginFill(0x1A1C21, 1);
			mark.graphics.drawCircle(6, 10, 6);
			mark.graphics.endFill();
			if (i == index) {
				mark.graphics.lineStyle(0, GOLD, 0);
				mark.graphics.beginFill(GOLD, 1);
				mark.graphics.drawCircle(6, 10, 3);
				mark.graphics.endFill();
			}
			item.addChild(mark);
			var field = text(16, 2, 200, 13, i == index ? GOLD : TEXT, label, font);
			field.width = field.textWidth + 8;
			item.addChild(field);
			item.x = left;
			item.buttonMode = true;
			item.mouseChildren = false;
			item.name = id + "\t" + key + "\t" + value;
			item.addEventListener(MouseEvent.CLICK, picked1);
			row.addChild(item);
			left += field.x + field.width + 8;
		}
		return row;
	}

	/** A round mark of the drop-down's stand-in was clicked ("id\tkey\tvalue" in its name). */
	static function picked1(event:Dynamic):Void {
		var name:String = event.currentTarget.name;
		var parts = name.split("\t");
		Options.set(parts[0], parts[1], parts[2]);
		save(parts[0], parts[1], parts[2]);
		later();
	}

	/** The saved values of a pick-list, as a new array. */
	static function picked(id:String, key:String):Array<Dynamic> {
		var saved:Dynamic = Options.get(id, key, null);
		var values:Array<Dynamic> = [];
		if (saved == null)
			return values;
		var count:Int = saved.length;
		for (i in 0...count)
			values.push(saved[i]);
		return values;
	}

	static function contains(values:Array<Dynamic>, value:String):Bool {
		for (item in values)
			if (item == value)
				return true;
		return false;
	}

	/** Sends the change to Arkive and waits for live.json to answer it. */
	static function save(id:String, key:String, value:String):Void {
		var code:Int = untyped Math.floor(Math.random() * 65536);
		request = StringTools.hex(code, 4).toLowerCase();
		Bus.send("settings", request + "\t" + id + "\t" + key + "\t" + value);
		say("Saving...");
		checks = 0;
		if (checker == null) {
			checker = new Timer(300);
			checker.addEventListener(TimerEvent.TIMER, check300);
		}
		checker.reset();
		checker.start();
	}

	static function check300(event:Dynamic):Void {
		checks++;
		if (Options.saved() == request) {
			checker.stop();
			say("Saved.");
			return;
		}
		if (checks >= ACK_CHECKS) {
			checker.stop();
			say("Arkive didn't answer, so this change lasts until you close the game. Open Arkive to keep changes.");
			return;
		}
		Options.readLive(null);
	}

	static function say(message:String):Void {
		if (status != null)
			status.text = message;
	}

	static function wheel(event:Dynamic):Void {
		var delta:Float = event.delta;
		scrollTo(scroll - delta * ROW, true);
	}

	/** Moves the body; `moveBar` also moves the scroll bar's thumb (false when the bar itself was dragged). */
	static function scrollTo(value:Float, moveBar:Bool):Void {
		var most:Float = body.height + 14 - bodyHeight;
		if (value > most)
			value = most;
		if (value < 0)
			value = 0;
		scroll = value;
		body.y = BODY_TOP - scroll;
		if (moveBar && scrollBar != null)
			scrollBar.position = value;
	}

	/** The font of the content group's name label, the menu's own body font. */
	static function gameFont(group:Dynamic):Void {
		var label:Dynamic = group == null ? null : group.contentsCharacterNameLabel;
		var field:Dynamic = label == null ? null : label.textField;
		if (field == null)
			return;
		var format:Dynamic = field.getTextFormat();
		if (format != null && format.font != null) {
			font = format.font;
			embed = field.embedFonts;
		}
	}

	static function text(x:Float, y:Float, width:Float, size:Int, color:Int, value:String, face:String):TextField {
		var format = new TextFormat();
		format.font = face;
		format.size = size;
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
		field.height = size + 10;
		field.text = value;
		return field;
	}
}
