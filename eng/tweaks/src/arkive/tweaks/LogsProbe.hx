package arkive.tweaks;

import flash.display.Sprite;
import flash.events.Event;
import flash.events.TimerEvent;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Timer;

/**
	Logs probe (diagnostic, not a player-facing tweak): a "Logs Test" button beside the character window's tabs that
	checks what a lostark.bible Logs tab would need. Can the interface read the player's name and server, load a web
	address, leave a note through the game's settings call, read a file Arkive writes, and copy to the clipboard? Each
	check runs on its own timer tick, so one that throws doesn't stop the others; the results show in a panel.
**/
@:keep
class LogsProbe {
	static inline var WEB = "https://lostark.bible/character/NA/Reymu/logs/__data.json";
	static inline var NOTE_KEY = "ArkiveLogsProbe";
	static inline var STEP_MS = 250;
	static inline var WAIT_MS = 15000;

	/** Files the check tries to read: Arkive's probe file (absolute, as a file: URL, with the game's ext: prefix) and the game's own settings file by relative paths. */
	static var FILES:Array<String> = [
		"C:/Users/User/AppData/Local/Arkive/logs-probe.txt",
		"file:///C:/Users/User/AppData/Local/Arkive/logs-probe.txt",
		"ext:C:/Users/User/AppData/Local/Arkive/logs-probe.txt",
		"E:/MariStudy/logs-probe.txt",
		"../EFGame/Config/UserOption.xml",
		"EFGame/Config/UserOption.xml",
		"../../EFGame/Config/UserOption.xml",
		"Config/UserOption.xml"
	];

	static var panel:TextField;
	static var lines:Array<String> = [];
	static var window:Dynamic;
	static var stepper:Timer;
	static var step = 0;
	static var loaders:Array<Dynamic> = [];
	/** Checks that started but haven't finished: a check that threw, or a load nothing answered yet. */
	static var open:Array<String> = [];

	/** CharInfoWndContent.configUI: the button goes to the right of the last tab, the panel below the tabs. */
	public static function onWndConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var tabs:Dynamic = self.tabGroupManager;
		if (tabs == null)
			return;
		var last:Dynamic = tabs.getButtonAt(4);
		var button:Dynamic = GameUi.button("Logs Test");
		var gameButton = button != null;
		if (!gameButton)
			button = drawnButton("Logs Test");
		var right:Float = last == null ? tabs.width : last.x + last.width;
		button.x = tabs.x + right + 12;
		button.y = tabs.y + (last == null ? 0 : last.y);
		self.addChild(button);
		if (gameButton)
			button.addEventListener(scaleform.clik.events.ButtonEvent.CLICK, function(event:Dynamic) start(self));
		else
			button.addEventListener("click", function(event:Dynamic) start(self));
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
		say("Arkive Logs Test - take a screenshot of this panel when the last line says DONE.");
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
		var total = 7 + FILES.length;
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
			run("player", checkPlayer);
		else if (at == 2)
			load("web https", WEB);
		else if (at == 3)
			load("web http", "http://lostark.bible/character/NA/Reymu/logs/__data.json");
		else if (at == 4)
			run("note", checkNote);
		else if (at == 5)
			run("clipboard", checkClipboard);
		else if (at == 6)
			run("settings", checkGetKnown);
		else
			load("file " + (at - 7), FILES[at - 7]);
	}

	/** Runs a check; if it throws, its name stays in `open` and the DONE line lists it. */
	static function run(name:String, check:Void->Void):Void {
		open.push(name);
		check();
		close(name);
	}

	static function close(name:String):Void {
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

	static function checkPlayer():Void {
		var caps:Dynamic = GameUi.definition("flash.system.Capabilities");
		var version:String = caps == null ? "?" : caps.version;
		var kind:String = caps == null ? "?" : caps.playerType;
		say("  player " + version + " / " + kind);
	}

	/** Leaves a note through the game's own settings call (the UI scale uses it), then reads it back. */
	static function checkNote():Void {
		var name:String = labelText(window.equipPanel.infoPage.equipInfoCharacterNameLabel);
		var server:String = labelText(window.equipPanel.equipItemServerNameLabel);
		var stamp:String = untyped __global__["flash.utils.getTimer"]();
		var value:String = name + "|" + server + "|" + stamp;
		var saved:Dynamic = flash.external.ExternalInterface.call("SetStringINI", NOTE_KEY, value);
		var savedText:String = saved;
		var back:Dynamic = flash.external.ExternalInterface.call("GetStringINI", NOTE_KEY);
		var backText:String = back;
		say("3 NOTE wrote [" + value + "] returned [" + savedText + "] read back [" + backText + "]");
	}

	static function checkGetKnown():Void {
		var scale:Dynamic = flash.external.ExternalInterface.call("GetStringINI", "UIScale");
		var scaleText:String = scale;
		var inbox:Dynamic = flash.external.ExternalInterface.call("GetStringINI", "ArkiveLogsInbox");
		var inboxText:String = inbox;
		say("4 SETTINGS UIScale [" + scaleText + "] ArkiveLogsInbox [" + inboxText + "]");
	}

	static function checkClipboard():Void {
		var name:String = labelText(window.equipPanel.infoPage.equipInfoCharacterNameLabel);
		var link:String = "https://lostark.bible/character/NA/" + name + "/logs";
		flash.system.System.setClipboard(link);
		say("5 CLIPBOARD set to [" + link + "] - paste it into Notepad to check");
	}

	/** Starts a URLLoader and reports whichever answer comes back: data, an error, or a security refusal. */
	static function load(label:String, url:String):Void {
		var loader = new URLLoader();
		loaders.push(loader);
		open.push(label);
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) {
			var data:String = loader.data;
			var size = data == null ? -1 : data.length;
			var head:String = data == null ? "" : data.substr(0, 60);
			close(label);
			say("2 " + label + " LOADED " + size + " chars [" + head + "] from " + url);
		});
		loader.addEventListener("ioError", function(e:Dynamic) {
			var text:String = e.text;
			close(label);
			say("2 " + label + " IO ERROR [" + text + "] " + url);
		});
		loader.addEventListener("securityError", function(e:Dynamic) {
			var text:String = e.text;
			close(label);
			say("2 " + label + " SECURITY ERROR [" + text + "] " + url);
		});
		loader.addEventListener("httpStatus", function(e:Dynamic) {
			var status:String = e.status;
			say("2 " + label + " http status " + status);
		});
		loader.load(new URLRequest(url));
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

	/** A plain framed button for when the game's button symbol isn't loaded. */
	static function drawnButton(label:String):Dynamic {
		var button = new Sprite();
		button.buttonMode = true;
		button.mouseChildren = false;
		var g = button.graphics;
		g.beginFill(0x1C1C1C);
		g.lineStyle(1, 0xC8A050);
		g.drawRect(0, 0, 110, 28);
		g.endFill();
		var format = new TextFormat();
		format.font = "$YG760";
		format.size = 13;
		var text = new TextField();
		text.defaultTextFormat = format;
		text.textColor = 0xFFFFFF;
		text.autoSize = flash.text.TextFieldAutoSize.CENTER;
		text.selectable = false;
		text.mouseEnabled = false;
		text.text = label;
		text.x = (110 - text.width) / 2;
		text.y = (28 - text.height) / 2;
		button.addChild(text);
		return button;
	}
}
