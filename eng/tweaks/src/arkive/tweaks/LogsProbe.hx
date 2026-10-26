package arkive.tweaks;

import flash.events.Event;
import flash.events.TimerEvent;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	Logs probe (diagnostic, not a player-facing tweak): a "Logs" tab beside the character window's tabs that checks
	what a lostark.bible Logs tab would need. Can the interface read the player's name and server, leave a note through
	the game's settings call, copy to the clipboard, and read (and keep polling for) a file Arkive writes? Each check
	runs on its own timer tick, so one that throws doesn't stop the others; the results show in a panel.
	Only absolute paths are tried: loading the game's own file by a relative path froze the game (test 1).
**/
@:keep
class LogsProbe {
	static inline var WEB = "https://lostark.bible/character/NA/Reymu/logs/__data.json";
	static inline var PROBE = "C:/Users/User/AppData/Local/Arkive/logs-probe.txt";
	static inline var MISSING = "C:/Users/User/AppData/Local/Arkive/logs-missing.txt";
	static inline var NOTE_KEY = "ArkiveLogsProbe";
	static inline var STEP_MS = 250;
	static inline var POLLS = 8;
	static inline var WAIT_MS = 5000;

	static var panel:TextField;
	static var lines:Array<String> = [];
	static var window:Dynamic;
	static var tab:Dynamic;
	static var stepper:Timer;
	static var step = 0;
	static var loaders:Array<Dynamic> = [];
	/** Checks that started but haven't finished: a check that threw, or a load nothing answered yet. */
	static var open:Array<String> = [];

	/** CharInfoWndContent.configUI: a copy of the last tab, placed right after it, and the panel below the tabs. */
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
		button.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) showTab(self, tabs, button));
		// A click on any real tab (even the one that was selected) closes the panel; the tab group reselects it.
		tabs.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) hideTab(button));
	}

	static function showTab(self:Dynamic, tabs:Dynamic, button:Dynamic):Void {
		var current:Dynamic = tabs.getButtonAt(tabs.selectedIndex);
		if (current != null)
			current.selected = false;
		button.selected = true;
		tab = button;
		start(self);
	}

	static function hideTab(button:Dynamic):Void {
		button.selected = false;
		if (panel != null && tab == button)
			panel.visible = false;
	}

	static function start(self:Dynamic):Void {
		window = self;
		if (panel == null || panel.parent != self) {
			var tabs:Dynamic = self.tabGroupManager;
			var format = new TextFormat();
			format.font = "$YG760";
			format.size = 13;
			panel = new TextField();
			panel.defaultTextFormat = format;
			panel.textColor = 0xFFFFFF;
			panel.background = true;
			panel.backgroundColor = 0x101418;
			panel.border = true;
			panel.borderColor = 0xC8A050;
			panel.multiline = true;
			panel.wordWrap = true;
			panel.selectable = true;
			panel.x = tabs.x;
			panel.y = tabs.y + tabs.height + 6;
			panel.width = 900;
			panel.height = 520;
			self.addChild(panel);
		}
		panel.visible = true;
		lines = [];
		loaders = [];
		open = [];
		step = 0;
		say("Arkive Logs Test 2 - take a screenshot when the last line says DONE.");
		if (stepper != null)
			stepper.stop();
		stepper = new Timer(STEP_MS);
		stepper.addEventListener(TimerEvent.TIMER, next);
		stepper.start();
	}

	/** Runs the next check. The step counter moves first, so a check that throws is reported and skipped. */
	static function next(event:Dynamic):Void {
		var at = step;
		step++;
		var total = 5 + POLLS;
		if (at >= total) {
			stepper.stop();
			var done = new Timer(WAIT_MS, 1);
			done.addEventListener(TimerEvent.TIMER, function(e:Dynamic) {
				var left:String = open.join(", ");
				say("DONE. No result from: [" + left + "]");
			});
			done.start();
			return;
		}
		if (at == 0)
			run("name", checkLabels);
		else if (at == 1)
			run("note", checkNote);
		else if (at == 2)
			run("clipboard", checkClipboard);
		else if (at == 3)
			load("web", WEB);
		else if (at == 4)
			load("file", PROBE);
		else
			load("poll " + (at - 4), MISSING);
	}

	/** Runs a check; if it throws, its name stays in `open` and the DONE line lists it. */
	static function run(name:String, check:Void->Void):Void {
		open.push(name);
		check();
		finish(name);
	}

	static function finish(name:String):Void {
		var at = open.indexOf(name);
		if (at >= 0)
			open.splice(at, 1);
	}

	static function checkLabels():Void {
		var name:String = labelText(window.equipPanel.infoPage.equipInfoCharacterNameLabel);
		var server:String = labelText(window.equipPanel.equipItemServerNameLabel);
		var mine:String = window.parent == null ? "?" : window.parent.name;
		say("1 NAME [" + name + "]  SERVER [" + server + "]  window [" + mine + "]");
	}

	/** Leaves a note through the game's own settings call, then reads it back. */
	static function checkNote():Void {
		var name:String = labelText(window.equipPanel.infoPage.equipInfoCharacterNameLabel);
		var server:String = labelText(window.equipPanel.equipItemServerNameLabel);
		var stamp:String = now();
		var value:String = name + "|" + server + "|" + stamp;
		flash.external.ExternalInterface.call("SetStringINI", NOTE_KEY, value);
		var back:Dynamic = flash.external.ExternalInterface.call("GetStringINI", NOTE_KEY);
		var backText:String = back;
		say("2 NOTE wrote [" + value + "] read back [" + backText + "]");
	}

	static function checkClipboard():Void {
		var name:String = labelText(window.equipPanel.infoPage.equipInfoCharacterNameLabel);
		var link:String = "https://lostark.bible/character/NA/" + name + "/logs";
		flash.system.System.setClipboard(link);
		say("3 CLIPBOARD set to [" + link + "] - paste it into Notepad to check");
	}

	/** Starts a URLLoader and reports whichever answer comes back, with how long it took in ms. */
	static function load(label:String, url:String):Void {
		var loader = new URLLoader();
		loaders.push(loader);
		open.push(label);
		var started:Int = untyped __global__["flash.utils.getTimer"]();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) {
			var data:String = loader.data;
			var size = data == null ? -1 : data.length;
			var head:String = data == null ? "" : data.substr(0, 40);
			finish(label);
			say("4 " + label + " LOADED " + size + " chars [" + head + "] " + took(started) + " ms");
		});
		loader.addEventListener("ioError", function(e:Dynamic) {
			finish(label);
			say("4 " + label + " not found " + took(started) + " ms");
		});
		loader.addEventListener("securityError", function(e:Dynamic) {
			var text:String = e.text;
			finish(label);
			say("4 " + label + " SECURITY ERROR [" + text + "]");
		});
		loader.load(new URLRequest(url));
	}

	static function took(started:Int):String {
		var ended:Int = untyped __global__["flash.utils.getTimer"]();
		var ms:String = untyped (ended - started);
		return ms;
	}

	static function now():String {
		var ms:String = untyped __global__["flash.utils.getTimer"]();
		return ms;
	}

	static function labelText(label:Dynamic):String {
		if (label == null)
			return "(no label)";
		var field:Dynamic = label.textField;
		var text:String = field == null ? label.text : field.text;
		return text;
	}

	static function say(line:String):Void {
		lines.push(line);
		if (panel != null)
			panel.text = lines.join("\n");
	}
}
