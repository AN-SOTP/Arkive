package arkive.tweaks;

import flash.display.Sprite;
import flash.events.Event;
import flash.events.TimerEvent;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	Logs Tab: a "Logs" tab after Ark Grid in the character profile and inspect windows, listing the player's recent
	logs from lostark.bible. The game's interface can't go online, write files or reach the Windows clipboard; it can
	read files by their full path. So the tab spells the click's request id and the player's name to Arkive by reading
	Arkive's small signal files in order ("go", one file per hex digit, "end"; Arkive notices each read), then polls
	for Arkive's answer, "<request>.txt". Arkive fetches the logs: EU Central first, then North America. Only absolute
	paths: the game froze loading a file by a relative path.
**/
@:keep
class LogsTab {
	static inline var POLL_MS = 300;
	static inline var POLLS = 50;
	static inline var GOLD = 0xD8B46A;
	static inline var GREY = 0xB4B4B4;
	/** Arkive's signal file sets, cycled so a file is read again only sixteen reads later. */
	static inline var SETS = "abcdefghijklmnop";
	/** Polls (about 3 s) after which the click is spelled once more, in case Arkive missed a read. */
	static inline var RESPELL_AFTER = 10;

	/** Column x positions and widths: boss, difficulty, class, DPS, percentile, date, time, died. */
	static var LEFTS:Array<Int> = [16, 396, 516, 646, 746, 816, 896, 966];
	static var WIDTHS:Array<Int> = [370, 110, 120, 90, 60, 70, 60, 60];
	static var TITLES:Array<String> = ["Boss", "Difficulty", "Class", "DPS", "%", "Date", "Time", ""];

	static var panel:Sprite;
	static var header:TextField;
	static var columns:Array<TextField>;
	static var tab:Dynamic;
	static var poller:Timer;
	static var polls = 0;
	static var request:String = "";
	static var file:String = "";
	static var name:String = "";
	static var folder:String = "";
	/** The signal files still to read for this click, and when spelling started. */
	static var spelling:Array<String> = [];
	static var spelled = 0;
	static var started = 0;
	static var signalMs = 0;
	static var signalErrors = 0;
	static var respelled = false;

	/** CharInfoWndContent.configUI: a copy of the last tab, placed right after it. */
	public static function onWndConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var tabs:Dynamic = self.tabGroupManager;
		if (tabs == null)
			return;
		var last:Dynamic = tabs.getButtonAt(4);
		if (last == null)
			return;
		var button:Dynamic = GameUi.copy(last, "Logs");
		button.autoSize = "none";
		button.width = last.width;
		button.height = last.height;
		button.x = tabs.x + last.x + last.width;
		button.y = tabs.y + last.y;
		GameUi.addAbove(button, tabs);
		button.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) show(self, tabs, button));
		// A click on any real tab (even the one that was selected) closes the panel; the tab group reselects it.
		tabs.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) hide(button));
	}

	static function show(self:Dynamic, tabs:Dynamic, button:Dynamic):Void {
		var current:Dynamic = tabs.getButtonAt(tabs.selectedIndex);
		if (current != null)
			current.selected = false;
		button.selected = true;
		tab = button;
		build(self, tabs, button);
		panel.visible = true;
		ask(self);
	}

	static function hide(button:Dynamic):Void {
		button.selected = false;
		if (panel != null && tab == button) {
			panel.visible = false;
			stop();
		}
	}

	/** The panel covers the window below the tabs, so nothing under it can be clicked while it shows. */
	static function build(self:Dynamic, tabs:Dynamic, button:Dynamic):Void {
		if (panel != null && panel.parent == self)
			return;
		if (panel != null && panel.parent != null)
			panel.parent.removeChild(panel);
		panel = new Sprite();
		panel.x = tabs.x;
		panel.y = button.y + button.height + 4;
		var width:Float = button.x + button.width - tabs.x;
		var height:Float = self.height - panel.y - 70;
		if (height < 300)
			height = 640;
		var g = panel.graphics;
		g.beginFill(0x0C0F14, 0.96);
		g.lineStyle(1, GOLD, 0.6);
		g.drawRect(0, 0, width, height);
		g.endFill();
		header = field(16, 12, width - 32, 15, GOLD);
		header.wordWrap = true;
		header.height = 44;
		panel.addChild(header);
		columns = [];
		for (i in 0...LEFTS.length) {
			var column = field(LEFTS[i], 64, WIDTHS[i], 14, i == 0 ? 0xFFFFFF : GREY);
			column.height = height - 76;
			panel.addChild(column);
			columns.push(column);
		}
		self.addChild(panel);
	}

	static function field(x:Float, y:Float, width:Float, size:Int, color:Int):TextField {
		var format = new TextFormat();
		format.font = "$YG760";
		format.size = size;
		format.leading = 6;
		var text = new TextField();
		text.defaultTextFormat = format;
		text.textColor = color;
		text.multiline = true;
		text.wordWrap = false;
		text.selectable = false;
		text.mouseEnabled = false;
		text.x = x;
		text.y = y;
		text.width = width;
		return text;
	}

	/** Spells the click to Arkive through its signal files, then polls for the answer. */
	static function ask(self:Dynamic):Void {
		name = labelText(self.equipPanel.infoPage.equipInfoCharacterNameLabel);
		clear();
		stop();
		if (name == null || name.length == 0) {
			say("No character name in this window.");
			return;
		}
		folder = Options.get("logs", "folder", "");
		var code:Int = untyped Math.floor(Math.random() * 65536);
		request = hex4(code);
		file = folder + "/" + request + ".txt";
		var message:String = request + hexName(name);
		message = message + hex4(checksum(message));
		spelling = ["go"];
		for (i in 0...message.length)
			spelling.push(message.charAt(i));
		spelling.push("end");
		spelled = 0;
		signalErrors = 0;
		respelled = false;
		started = untyped __global__["flash.utils.getTimer"]();
		say("Looking up " + name + " on lostark.bible...");
		spellNext();
	}

	/** Reads the next signal file once the previous read has finished, so Arkive sees them in order. */
	static function spellNext():Void {
		if (spelled >= spelling.length) {
			var now:Int = untyped __global__["flash.utils.getTimer"]();
			signalMs = now - started;
			polls = 0;
			poller = new Timer(POLL_MS);
			poller.addEventListener(TimerEvent.TIMER, poll);
			poller.start();
			return;
		}
		var set:String = SETS.charAt(spelled % SETS.length);
		var path:String = folder + "/signal/" + set + "-" + spelling[spelled] + ".txt";
		spelled++;
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) spellNext());
		loader.addEventListener("ioError", function(e:Dynamic) {
			signalErrors++;
			spellNext();
		});
		loader.addEventListener("securityError", function(e:Dynamic) {
			signalErrors++;
			spellNext();
		});
		loader.load(new URLRequest(path));
	}

	static function stop():Void {
		if (poller != null) {
			poller.stop();
			poller.removeEventListener(TimerEvent.TIMER, poll);
			poller = null;
		}
	}

	static function poll(event:Dynamic):Void {
		polls++;
		if (polls == RESPELL_AFTER && !respelled) {
			respelled = true;
			stop();
			spelled = 0;
			started = untyped __global__["flash.utils.getTimer"]();
			spellNext();
			return;
		}
		if (polls > POLLS) {
			stop();
			var reads:String = untyped spelling.length;
			var errors:String = untyped signalErrors;
			var ms:String = untyped signalMs;
			say("No answer from Arkive. Keep Arkive open with the Logs Tab tweak on, then click Logs again.\n"
				+ "(signal: " + reads + " reads, " + errors + " failed, " + ms + " ms)");
			return;
		}
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) {
			var data:String = loader.data;
			if (poller != null && data != null)
				answer(data);
		});
		loader.addEventListener("ioError", function(e:Dynamic) {});
		loader.addEventListener("securityError", function(e:Dynamic) {});
		loader.load(new URLRequest(file));
	}

	/** Shows Arkive's summary if it answers this click (its first line ends with our request id). */
	static function answer(data:String):Void {
		var lines = data.split("\r").join("").split("\n");
		var head = lines[0].split("\t");
		if (head.length < 5 || head[4] != request)
			return;
		stop();
		var status:String = head[0];
		var region:String = head[1];
		var count:String = head[3];
		if (status == "hidden") {
			say(name + " hides their logs on lostark.bible.");
			return;
		}
		if (status != "found") {
			say("lostark.bible has no character named " + name + " in EU Central or North America.");
			return;
		}
		if (count == "0") {
			say(name + " (" + region + ") has no recent logs on lostark.bible.");
			return;
		}
		say(name + "  -  " + region + "  -  " + count + " recent logs from lostark.bible");
		var cells:Array<Array<String>> = [];
		for (i in 0...LEFTS.length)
			cells.push([TITLES[i], ""]);
		for (r in 1...lines.length) {
			var row = lines[r].split("\t");
			if (row.length < LEFTS.length)
				continue;
			for (i in 0...LEFTS.length)
				cells[i].push(row[i]);
		}
		for (i in 0...LEFTS.length)
			columns[i].text = cells[i].join("\n");
	}

	static function clear():Void {
		if (columns == null)
			return;
		for (column in columns)
			column.text = "";
	}

	static function say(text:String):Void {
		if (header != null)
			header.text = text;
	}

	/** The name as four lowercase hex digits per character. */
	static function hexName(text:String):String {
		var parts:Array<String> = [];
		for (i in 0...text.length)
			parts.push(hex4(StringTools.fastCodeAt(text, i)));
		return parts.join("");
	}

	static function hex4(value:Int):String {
		var hex:String = StringTools.hex(value & 0xFFFF, 4);
		return hex.toLowerCase();
	}

	/** The sum of the message's 4-digit groups, wrapped to 16 bits (Arkive checks the same). */
	static function checksum(message:String):Int {
		var sum = 0;
		var at = 0;
		while (at + 4 <= message.length) {
			var group:Int = untyped __global__["parseInt"](message.substr(at, 4), 16);
			sum = (sum + group) & 0xFFFF;
			at += 4;
		}
		return sum;
	}

	static function labelText(label:Dynamic):String {
		if (label == null)
			return "";
		var field:Dynamic = label.textField;
		var text:String = field == null ? label.text : field.text;
		return text;
	}
}
