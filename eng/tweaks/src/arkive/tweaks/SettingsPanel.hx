package arkive.tweaks;

import flash.display.Shape;
import flash.display.Sprite;
import flash.events.MouseEvent;
import flash.events.TimerEvent;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	In-game Tweak Settings: an "Arkive" button beside the escape menu's content-status gear opens a panel over the
	content status list. Each tweak has a switch, and a click on its name folds its options out. A change acts at once
	(Options' live store, which every window shares) and is sent to Arkive on the Data Bus to keep; the panel says
	"Saved" once Arkive's live.json answers the change, or that the change lasts only until the game closes.
	The panel draws its own rows and switches rather than using the menu's controls, which report to the game.
**/
@:keep
class SettingsPanel {
	static inline var TITLE = 0xE3C27F;
	static inline var TEXT = 0xE6E6E6;
	static inline var DIM = 0x8C8C8C;
	static inline var GOLD = 0xFFD200;
	static inline var LINE = 0x6E6250;
	static inline var ROW = 26;
	static inline var OPTION_ROW = 22;
	static inline var LIST_TOP = 52;
	/** Checks of live.json after a change (300 ms apart) before saying Arkive didn't answer. */
	static inline var ACK_CHECKS = 20;

	static var panel:Sprite;
	static var list:Sprite;
	static var listHeight:Float = 300;
	static var width:Float = 400;
	static var scroll:Float = 0;
	static var status:TextField;
	static var opened:Dynamic = {};
	static var font:String = "$YG760";
	static var embed = false;
	static var listening = false;

	static var request:String = "";
	static var checks = 0;
	static var checker:Timer;

	/** MenuWndContent.configUI: the button beside the content-status gear, and the (hidden) panel. */
	public static function onMenuConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var group:Dynamic = self.contentsGroup;
		if (group == null || group.settingButton == null)
			return;
		var gear:Dynamic = group.settingButton;
		var button:Dynamic = GameUi.button("Arkive");
		if (button == null)
			return;
		gameFont(group);
		button.autoSize = "none";
		button.width = 84;
		GameUi.setHeight(button, gear.height > 20 ? gear.height : 24);
		button.x = gear.x - button.width - 8;
		button.y = gear.y + (gear.height - button.height) / 2;
		// Keep clear of "show all": narrower first, then above the gear's row.
		var other:Dynamic = group.showAllContentsButton;
		if (other != null && other.visible && other.x + other.width + 6 > button.x) {
			var room:Float = gear.x - 8 - (other.x + other.width + 6);
			if (room >= 60) {
				button.width = room;
				button.x = gear.x - room - 8;
			} else {
				button.x = gear.x + gear.width - button.width;
				button.y = gear.y - button.height - 6;
			}
		}
		group.addChild(button);
		build(group);
		button.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) toggle());
		if (!listening) {
			listening = true;
			Options.listen(function(tweak:String, key:String) {
				if (panel != null && panel.visible)
					render();
			});
		}
	}

	/** The panel covers the content status list (the roster tree and its scroll bar). */
	static function build(group:Dynamic):Void {
		if (panel != null && panel.parent != null)
			panel.parent.removeChild(panel);
		var tree:Dynamic = group.contentsTreeList;
		var bar:Dynamic = group.contentsScrollBar;
		var left:Float = tree == null ? 30 : tree.x - 4;
		var top:Float = tree == null ? 112 : tree.y - 4;
		var right:Float = bar == null ? left + 400 : bar.x + bar.width + 4;
		var height:Float = tree == null ? 420 : tree.height + 8;
		if (height < 240)
			height = 420;
		width = right - left;
		panel = new Sprite();
		panel.x = left;
		panel.y = top;
		var g = panel.graphics;
		g.lineStyle(1, LINE, 0.9);
		g.beginFill(0x0B0D11, 0.97);
		g.drawRect(0, 0, width, height);
		g.endFill();
		panel.addChild(text(12, 6, width - 24, 16, TITLE, "Arkive tweaks"));
		status = text(12, 28, width - 24, 12, DIM, "");
		panel.addChild(status);
		var rule = new Shape();
		rule.graphics.lineStyle(1, LINE, 0.8);
		rule.graphics.moveTo(10, LIST_TOP - 4);
		rule.graphics.lineTo(width - 10, LIST_TOP - 4);
		panel.addChild(rule);
		list = new Sprite();
		list.y = LIST_TOP;
		listHeight = height - LIST_TOP - 6;
		var mask = new Shape();
		mask.graphics.beginFill(0xFFFFFF);
		mask.graphics.drawRect(0, LIST_TOP, width, listHeight);
		mask.graphics.endFill();
		panel.addChild(mask);
		panel.addChild(list);
		list.mask = mask;
		panel.addEventListener(MouseEvent.MOUSE_WHEEL, wheel);
		panel.visible = false;
		group.addChild(panel);
	}

	static function toggle():Void {
		if (panel == null)
			return;
		panel.visible = !panel.visible;
		if (panel.visible) {
			Options.readLive(null);
			say(Options.home() == "" ? "" : "Changes act straight away. Arkive keeps them while it's running.");
			render();
		}
	}

	/** Draws every tweak row, and the options of the ones folded out. */
	static function render():Void {
		while (list.numChildren > 0)
			list.removeChildAt(0);
		var y:Float = 0;
		var tweaks = Options.panel();
		for (tweak in tweaks) {
			var id:String = tweak.id;
			var name:String = tweak.name;
			if (tweak.restart == true) {
				list.addChild(text(36, y + 4, width - 48, 13, DIM, name + " (needs the game closed: change it in Arkive)"));
				y += ROW;
				continue;
			}
			var on = Options.on(id);
			var options:Array<Dynamic> = tweak.options;
			var open = untyped opened[id] == true;
			var row = new Sprite();
			row.y = y;
			row.addChild(check(12, 6, on));
			var arrow = options.length == 0 ? "" : (open ? "- " : "+ ");
			row.addChild(text(36, 3, width - 48, 14, on ? TEXT : DIM, arrow + name));
			hit(row, width, ROW);
			row.name = id;
			row.addEventListener(MouseEvent.CLICK, tweakClicked);
			list.addChild(row);
			y += ROW;
			if (!open)
				continue;
			for (option in options)
				y = optionRow(id, option, y);
		}
		scrollTo(scroll);
	}

	/** One option under its tweak: a switch, a row of choices, or a note for long pick-lists. */
	static function optionRow(id:String, option:Dynamic, y:Float):Float {
		var key:String = option.key;
		var label:String = option.label;
		var kind:String = option.kind;
		if (kind == "switch") {
			var on = Options.get(id, key, false) == true;
			var row = new Sprite();
			row.y = y;
			row.addChild(check(40, 5, on));
			var field = text(62, 2, width - 76, 12, TEXT, label);
			field.multiline = true;
			field.wordWrap = true;
			field.height = field.textHeight + 6;
			row.addChild(field);
			var height:Float = field.height > OPTION_ROW ? field.height + 2 : OPTION_ROW;
			hit(row, width, height);
			row.name = id + "\t" + key;
			row.addEventListener(MouseEvent.CLICK, switchClicked);
			list.addChild(row);
			return y + height;
		}
		var heading = text(40, y + 2, width - 54, 12, DIM, kind == "list" ? label + " (change it in Arkive)" : label);
		heading.multiline = true;
		heading.wordWrap = true;
		heading.height = heading.textHeight + 6;
		list.addChild(heading);
		y += heading.height;
		if (kind != "single")
			return y + 2;
		var chosen:Dynamic = Options.get(id, key, "");
		var x:Float = 40;
		var choices:Array<Dynamic> = option.choices;
		for (choice in choices) {
			var value:String = choice.value;
			var picked = chosen == value;
			var name:String = choice.label;
			var item = new Sprite();
			item.addChild(radio(0, 5, picked));
			var field = text(18, 0, 400, 12, picked ? GOLD : TEXT, name);
			field.width = field.textWidth + 10;
			item.addChild(field);
			var itemWidth:Float = field.x + field.width;
			hit(item, itemWidth, OPTION_ROW);
			if (x + itemWidth > width - 12) {
				x = 40;
				y += OPTION_ROW;
			}
			item.x = x;
			item.y = y;
			item.name = id + "\t" + key + "\t" + value;
			item.addEventListener(MouseEvent.CLICK, choiceClicked);
			list.addChild(item);
			x += itemWidth + 10;
		}
		return y + OPTION_ROW + 2;
	}

	/** A click on the switch flips the tweak; a click on the name folds its options in or out. */
	static function tweakClicked(event:Dynamic):Void {
		var row:Dynamic = event.currentTarget;
		var id:String = row.name;
		if (row.mouseX < 32) {
			var on = !Options.on(id);
			Options.setOn(id, on);
			save(id, "", on ? "1" : "0");
		} else {
			var open = untyped opened[id] == true;
			untyped opened[id] = !open;
		}
		render();
	}

	static function switchClicked(event:Dynamic):Void {
		var parts:Array<String> = event.currentTarget.name.split("\t");
		var on = Options.get(parts[0], parts[1], false) != true;
		Options.set(parts[0], parts[1], on);
		save(parts[0], parts[1], on ? "1" : "0");
		render();
	}

	static function choiceClicked(event:Dynamic):Void {
		var parts:Array<String> = event.currentTarget.name.split("\t");
		Options.set(parts[0], parts[1], parts[2]);
		save(parts[0], parts[1], parts[2]);
		render();
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

	/** A switch drawn like the game's check boxes: a dark box with a gold tick when on. */
	static function check(x:Float, y:Float, on:Bool):Shape {
		var box = new Shape();
		var g = box.graphics;
		g.lineStyle(1, 0xA59C8E, 1);
		g.beginFill(0x1A1C21, 1);
		g.drawRect(0, 0, 14, 14);
		g.endFill();
		if (on) {
			g.lineStyle(2, GOLD, 1);
			g.moveTo(3, 7);
			g.lineTo(6, 10);
			g.lineTo(11, 3);
		}
		box.x = x;
		box.y = y;
		return box;
	}

	/** A round choice mark, filled gold when picked. */
	static function radio(x:Float, y:Float, picked:Bool):Shape {
		var mark = new Shape();
		var g = mark.graphics;
		g.lineStyle(1, 0xA59C8E, 1);
		g.beginFill(0x1A1C21, 1);
		g.drawCircle(6, 6, 6);
		g.endFill();
		if (picked) {
			g.lineStyle(0, GOLD, 0);
			g.beginFill(GOLD, 1);
			g.drawCircle(6, 6, 3);
			g.endFill();
		}
		mark.x = x;
		mark.y = y;
		return mark;
	}

	/** Makes a row take clicks everywhere, not only on its text. */
	static function hit(item:Sprite, width:Float, height:Float):Void {
		var g = item.graphics;
		g.beginFill(0x000000, 0.01);
		g.drawRect(0, 0, width, height);
		g.endFill();
		item.buttonMode = true;
		item.mouseChildren = false;
	}

	static function wheel(event:Dynamic):Void {
		var delta:Float = event.delta;
		scrollTo(scroll - delta * ROW);
	}

	static function scrollTo(value:Float):Void {
		var most:Float = list.height - listHeight;
		if (value > most)
			value = most;
		if (value < 0)
			value = 0;
		scroll = value;
		list.y = LIST_TOP - scroll;
	}

	/** The font of the content group's name label, so the panel's text looks like the rest of the menu. */
	static function gameFont(group:Dynamic):Void {
		var label:Dynamic = group.contentsCharacterNameLabel;
		var field:Dynamic = label == null ? null : label.textField;
		if (field == null)
			return;
		var format:Dynamic = field.getTextFormat();
		if (format != null && format.font != null) {
			font = format.font;
			embed = field.embedFonts;
		}
	}

	static function text(x:Float, y:Float, width:Float, size:Int, color:Int, value:String):TextField {
		var format = new TextFormat();
		format.font = font;
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
