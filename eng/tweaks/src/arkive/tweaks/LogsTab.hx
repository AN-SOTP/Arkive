package arkive.tweaks;

import flash.display.Shape;
import flash.display.Sprite;
import flash.events.Event;
import flash.events.MouseEvent;
import flash.events.TimerEvent;
import flash.net.URLLoader;
import flash.net.URLRequest;
import flash.text.TextField;
import flash.text.TextFormat;
import flash.utils.Timer;
import scaleform.clik.events.ButtonEvent;

/**
	Logs Tab: a "Logs" tab after Ark Grid in the character profile and inspect windows with the player's logs from
	lostark.bible. It is an empty tab of its own (the other tabs' pages are hidden while it shows): raid tabs along the
	top ("All" groups the recent logs by raid, a raid's tab groups that raid's logs by gate), and each group is a
	dropdown that opens and closes. Parse % and difficulty are coloured the way lostark.bible colours them.

	The game's interface can't go online, write files or reach the Windows clipboard; it can read files by their full
	path. So the tab spells the click's request id, raid and the player's name to Arkive by reading Arkive's small
	signal files in order ("go", one file per hex digit, "end"; Arkive notices each read), then polls for Arkive's
	answer, "<request>.txt". Only absolute paths: the game froze loading a file by a relative path.
**/
@:keep
class LogsTab {
	static inline var POLL_MS = 300;
	static inline var POLLS = 50;
	/** Arkive's signal file sets, cycled so a file is read again only sixteen reads later. */
	static inline var SETS = "abcdefghijklmnop";
	/** Polls (about 3 s) after which the click is spelled once more, in case Arkive missed a read. */
	static inline var RESPELL_AFTER = 10;

	static inline var TITLE = 0xE3C27F;
	static inline var HEADING = 0xA59C8E;
	static inline var TEXT = 0xE6E6E6;
	static inline var DIM = 0x8C8C8C;
	static inline var ROW_HEIGHT = 26;
	static inline var RAID_BUTTON_WIDTH = 168;

	/** Columns: boss, difficulty, class, DPS, parse, date, time, died. */
	static var LEFTS:Array<Int> = [34, 430, 560, 690, 790, 890, 970, 1050];
	static var WIDTHS:Array<Int> = [390, 125, 125, 95, 95, 75, 75, 70];
	static var TITLES:Array<String> = ["Boss", "Difficulty", "Class", "DPS", "Parse %", "Date", "Time", ""];

	static var window:Dynamic;
	static var tabs:Dynamic;
	static var tab:Dynamic;
	static var showing = false;
	static var wasVisible = true;
	static var font:String = "$YG760";
	static var embed = false;

	static var panel:Sprite;
	static var title:TextField;
	static var status:TextField;
	static var raidBar:Sprite;
	static var headings:Sprite;
	static var list:Sprite;
	static var listMask:Shape;
	static var listHeight:Float = 500;
	static var scroll:Float = 0;

	static var raid = 0;
	static var raidNames:Array<String> = [];
	static var shownRaids:Array<String> = [];
	static var lines:Array<String> = [];
	static var collapsed:Dynamic = {};

	static var poller:Timer;
	static var polls = 0;
	static var request:String = "";
	static var file:String = "";
	static var name:String = "";
	static var folder:String = "";
	static var spelling:Array<String> = [];
	static var spelled = 0;
	static var started = 0;
	static var signalMs = 0;
	static var signalErrors = 0;
	static var respelled = false;

	/** CharInfoWndContent.configUI: a copy of the last tab, placed right after it. */
	public static function onWndConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var group:Dynamic = self.tabGroupManager;
		if (group == null)
			return;
		var last:Dynamic = group.getButtonAt(4);
		if (last == null)
			return;
		var button:Dynamic = GameUi.copy(last, "Logs");
		button.autoSize = "none";
		button.width = last.width;
		button.height = last.height;
		button.x = group.x + last.x + last.width;
		button.y = group.y + last.y;
		GameUi.addAbove(button, group);
		button.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) show(self, group, button));
		// Any real tab, clicked or chosen by the game (an inspect starts on Gear), closes the Logs tab.
		group.addEventListener(ButtonEvent.CLICK, function(event:Dynamic) closeFor(button));
		group.addEventListener("clikIndexChange", function(event:Dynamic) closeFor(button));
	}

	static function mine(self:Dynamic):Bool {
		return self.parent != null && self.parent.name == "charInfoWnd";
	}

	static function show(self:Dynamic, group:Dynamic, button:Dynamic):Void {
		var current:Dynamic = group.getButtonAt(group.selectedIndex);
		if (current != null)
			current.selected = false;
		button.selected = true;
		if (window != self)
			lines = [];
		window = self;
		tabs = group;
		tab = button;
		showing = true;
		wasVisible = true;
		pages(self, false);
		build(self, group, button);
		panel.visible = true;
		ask(0);
	}

	/** Closes the Logs tab of this window (if it shows) and lets the window's own tab show again. */
	static function closeFor(button:Dynamic):Void {
		button.selected = false;
		if (!showing || tab != button)
			return;
		showing = false;
		stop();
		if (panel != null)
			panel.visible = false;
		pages(window, true);
		var current:Dynamic = tabs.getButtonAt(tabs.selectedIndex);
		if (current != null)
			current.selected = true;
	}

	/**
		Hides the other tabs' pages, or shows the one the tab group has selected again (as the window's own tab change
		does: Gear also shows the character picture).
	**/
	static function pages(self:Dynamic, restore:Bool):Void {
		var index:Int = restore ? tabs.selectedIndex : -1;
		var own = mine(self);
		visible(self.texturePanel, own && index == 0);
		visible(self.otherTexturePanel, !own && index == 0);
		visible(self.equipPanel, index == 0);
		visible(self.engravePanel, index == 1);
		visible(self.gemPanel, index == 2);
		visible(self.cardPanel, index == 3);
		visible(self.arkGridPanel, index == 4);
	}

	static function visible(page:Dynamic, on:Bool):Void {
		if (page != null && page.visible != on)
			page.visible = on;
	}

	/** Watches the window while the tab shows: a new inspect, or the window closing, returns an inspect to Gear. */
	static function watch(event:Dynamic):Void {
		if (!showing || window == null)
			return;
		var shown:Bool = window.parent != null && window.parent.visible && window.visible;
		var current:String = labelText(window.equipPanel.infoPage.equipInfoCharacterNameLabel);
		if (!mine(window)) {
			if (!shown || (current.length > 0 && current != name))
				closeFor(tab);
			return;
		}
		// Your own profile keeps the Logs tab, as it keeps any tab; it refreshes when the window opens again.
		if (shown && !wasVisible)
			ask(raid);
		wasVisible = shown;
	}

	static function build(self:Dynamic, group:Dynamic, button:Dynamic):Void {
		if (panel != null && panel.parent == self)
			return;
		if (panel != null && panel.parent != null) {
			panel.removeEventListener(Event.ENTER_FRAME, watch);
			panel.parent.removeChild(panel);
		}
		gameFont(self);
		panel = new Sprite();
		panel.x = group.x;
		panel.y = button.y + button.height + 10;
		var width:Float = button.x + button.width - group.x;
		var height:Float = self.height - panel.y - 60;
		if (height < 300)
			height = 640;
		// Nearly clear: it takes the mouse wheel and keeps clicks off the hidden pages underneath.
		var g = panel.graphics;
		g.beginFill(0x000000, 0.01);
		g.drawRect(0, 0, width, height);
		g.endFill();
		title = text(16, 0, width - 32, 20, TITLE, "");
		panel.addChild(title);
		status = text(16, 30, width - 32, 14, DIM, "");
		status.multiline = true;
		status.wordWrap = true;
		status.height = 44;
		panel.addChild(status);
		raidBar = new Sprite();
		raidBar.y = 60;
		panel.addChild(raidBar);
		headings = new Sprite();
		headings.y = 108;
		for (i in 0...LEFTS.length)
			headings.addChild(text(LEFTS[i], 0, WIDTHS[i], 13, HEADING, TITLES[i]));
		var rule = new Shape();
		rule.graphics.lineStyle(1, 0x6E6250, 0.8);
		rule.graphics.moveTo(16, 24);
		rule.graphics.lineTo(width - 16, 24);
		headings.addChild(rule);
		panel.addChild(headings);
		list = new Sprite();
		list.y = 138;
		listHeight = height - list.y - 8;
		listMask = new Shape();
		listMask.graphics.beginFill(0xFFFFFF);
		listMask.graphics.drawRect(0, list.y, width, listHeight);
		listMask.graphics.endFill();
		panel.addChild(listMask);
		panel.addChild(list);
		list.mask = listMask;
		panel.addEventListener(MouseEvent.MOUSE_WHEEL, wheel);
		panel.addEventListener(Event.ENTER_FRAME, watch);
		self.addChild(panel);
	}

	/** The font of the window's own name label, so the tab's text looks like the rest of the window. */
	static function gameFont(self:Dynamic):Void {
		var label:Dynamic = self.equipPanel == null ? null : self.equipPanel.infoPage.equipInfoCharacterNameLabel;
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

	static function wheel(event:Dynamic):Void {
		var delta:Float = event.delta;
		scrollTo(scroll - delta * ROW_HEIGHT);
	}

	static function scrollTo(value:Float):Void {
		var most:Float = list.height - listHeight;
		if (value > most)
			value = most;
		if (value < 0)
			value = 0;
		scroll = value;
		list.y = 138 - scroll;
	}

	/** Spells the click (request id, raid, name) to Arkive through its signal files, then polls for the answer. */
	static function ask(wanted:Int):Void {
		var current:String = labelText(window.equipPanel.infoPage.equipInfoCharacterNameLabel);
		if (current != name) {
			collapsed = {};
			shownRaids = [];
			wanted = 0;
		}
		name = current;
		raid = wanted;
		stop();
		clearList();
		markRaid();
		if (name.length == 0) {
			say("", "No character name in this window.");
			return;
		}
		folder = Options.get("logs", "folder", "");
		var code:Int = untyped Math.floor(Math.random() * 65536);
		request = hex4(code);
		file = folder + "/" + request + ".txt";
		var message:String = request + hex4(raid) + hexName(name);
		message = message + hex4(checksum(message));
		spelling = ["go"];
		for (i in 0...message.length)
			spelling.push(message.charAt(i));
		spelling.push("end");
		spelled = 0;
		signalErrors = 0;
		respelled = false;
		started = untyped __global__["flash.utils.getTimer"]();
		say(name, "Looking up " + name + " on lostark.bible...");
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
			say(name, "No answer from Arkive. Keep Arkive open with the Logs Tab tweak on, then click Logs again. (signal: "
				+ reads + " reads, " + errors + " failed, " + ms + " ms)");
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

	/** Shows Arkive's answer if it answers this click (its first line carries our request id). */
	static function answer(data:String):Void {
		var all = data.split("\r").join("").split("\n");
		var head = all[0].split("\t");
		if (head.length < 6 || head[4] != request)
			return;
		stop();
		var state:String = head[0];
		var region:String = head[1];
		var count:String = head[3];
		var raidName:String = head[5];
		if (all.length > 1 && all[1].indexOf("#raids") == 0)
			raidNames = all[1].split("\t").slice(1);
		lines = [];
		for (i in 2...all.length)
			if (all[i].length > 0)
				lines.push(all[i]);
		if (state == "hidden") {
			say(name + "  -  " + region, name + "'s logs are private. The character is on lostark.bible, but its owner has hidden their logs.");
			return;
		}
		if (state != "found") {
			say(name, "lostark.bible has no character named " + name + " in EU Central or North America.");
			return;
		}
		if (raid == 0)
			raidTabs();
		if (count == "0") {
			say(name + "  -  " + region, raid == 0 ? name + " has no recent logs on lostark.bible." : "No " + raidName + " logs for " + name + " on lostark.bible.");
			return;
		}
		say(name + "  -  " + region, raid == 0
			? count + " recent logs from lostark.bible, by raid. Click a raid to open or close it; pick a raid above for more of its logs."
			: count + " " + raidName + " logs from lostark.bible, by gate.");
		render();
	}

	/** "All" and the raids the recent logs come from, as the game's own smaller tab buttons. */
	static function raidTabs():Void {
		shownRaids = [];
		for (known in raidNames)
			for (line in lines)
				if (line.indexOf(known + "\t") == 0) {
					shownRaids.push(known);
					break;
				}
		while (raidBar.numChildren > 0)
			raidBar.removeChildAt(0);
		var labels = ["All"].concat(shownRaids);
		var x:Float = 16;
		for (i in 0...labels.length) {
			var button:Dynamic = raidButton(labels[i]);
			if (button == null)
				return;
			button.x = x;
			button.y = 0;
			x += RAID_BUTTON_WIDTH + 6;
			var number:Int = i == 0 ? 0 : raidNames.indexOf(labels[i]) + 1;
			button.name = "raid" + number;
			raidBar.addChild(button);
			button.addEventListener(ButtonEvent.CLICK, raidClicked);
		}
		markRaid();
	}

	/** A raid tab: its name is "raid" and the raid's number (0 for All). */
	static function raidClicked(event:Dynamic):Void {
		var button:Dynamic = event.currentTarget;
		var label:String = button.name;
		var number:Int = untyped __global__["parseInt"](label.substr(4), 10);
		ask(number);
	}

	/** A copy of the window's detail-page tab (the game's smaller tab), or of its main tab when there is none. */
	static function raidButton(label:String):Dynamic {
		var source:Dynamic = null;
		var detail:Dynamic = window.detailPanel;
		if (detail != null && detail.detailTabGroupManager != null)
			source = detail.detailTabGroupManager.getButtonAt(0);
		if (source == null)
			source = tabs.getButtonAt(0);
		if (source == null)
			return null;
		var button:Dynamic = GameUi.copy(source, label);
		button.autoSize = "none";
		button.width = RAID_BUTTON_WIDTH;
		if (source.height > 40)
			button.height = 36;
		else
			button.height = source.height;
		return button;
	}

	static function markRaid():Void {
		if (raidBar == null)
			return;
		for (i in 0...raidBar.numChildren) {
			var button:Dynamic = raidBar.getChildAt(i);
			var on:Bool = button.name == "raid" + raid;
			if (button.selected != on)
				button.selected = on;
		}
	}

	/** Draws the rows: a dropdown line per group (raid, or gate in a raid's tab), then its logs unless it is closed. */
	static function render():Void {
		clearList();
		var groups:Array<String> = [];
		var keys:Array<String> = [];
		for (line in lines) {
			var row = line.split("\t");
			if (row.length < 10)
				continue;
			var key:String = groupOf(row);
			if (keys.indexOf(key) < 0)
				keys.push(key);
		}
		if (raid == 0) {
			// Raids in the site's order, then anything it doesn't know.
			for (known in raidNames)
				if (keys.indexOf(known) >= 0)
					groups.push(known);
			for (key in keys)
				if (groups.indexOf(key) < 0)
					groups.push(key);
		} else {
			groups = keys;
			groups.sort(compareText);
		}
		var y:Float = 0;
		for (group in groups) {
			var count = 0;
			for (line in lines)
				if (groupOf(line.split("\t")) == group)
					count++;
			var closed:Bool = untyped collapsed[raid + ":" + group] == true;
			list.addChild(groupLine(group, count, closed, y));
			y += ROW_HEIGHT + 2;
			if (closed)
				continue;
			var shade = false;
			for (line in lines) {
				var row = line.split("\t");
				if (row.length < 10 || groupOf(row) != group)
					continue;
				list.addChild(logLine(row, y, shade));
				shade = !shade;
				y += ROW_HEIGHT;
			}
			y += 6;
		}
		scrollTo(scroll);
	}

	static function groupOf(row:Array<String>):String {
		if (raid == 0)
			return row[0];
		var gate:String = row[1];
		return gate.length > 0 ? gate : row[2];
	}

	static function compareText(a:String, b:String):Int {
		return a < b ? -1 : (a > b ? 1 : 0);
	}

	/** A group's dropdown line: arrow, name and how many logs; clicking it opens or closes the group. */
	static function groupLine(group:String, count:Int, closed:Bool, y:Float):Sprite {
		var line = new Sprite();
		line.y = y;
		var width:Float = LEFTS[LEFTS.length - 1] + WIDTHS[WIDTHS.length - 1];
		var g = line.graphics;
		g.beginFill(0x2A241B, 0.85);
		g.lineStyle(1, 0x6E6250, 0.6);
		g.drawRect(16, 0, width - 16, ROW_HEIGHT);
		g.endFill();
		line.buttonMode = true;
		line.mouseChildren = false;
		var number:String = untyped count;
		line.addChild(text(24, 2, 700, 15, TITLE, (closed ? "▶  " : "▼  ") + group + "   (" + number + ")"));
		line.addEventListener(MouseEvent.CLICK, function(event:Dynamic) toggle(group));
		return line;
	}

	static function toggle(group:String):Void {
		var key:String = raid + ":" + group;
		var closed:Bool = untyped collapsed[key] == true;
		untyped collapsed[key] = !closed;
		render();
	}

	/** One log: boss, difficulty (coloured), class, DPS, parse (coloured), date, time, died. */
	static function logLine(row:Array<String>, y:Float, shade:Bool):Sprite {
		var line = new Sprite();
		line.y = y;
		line.mouseEnabled = false;
		line.mouseChildren = false;
		if (shade) {
			var g = line.graphics;
			g.beginFill(0xFFFFFF, 0.04);
			g.drawRect(16, 0, LEFTS[LEFTS.length - 1] + WIDTHS[WIDTHS.length - 1] - 16, ROW_HEIGHT);
			g.endFill();
		}
		var parse:String = row[6];
		var values = [row[2], row[3], row[4], row[5], parse.length > 0 ? parse : "-", row[7], row[8], row[9] == "died" ? "Died" : ""];
		var colors = [TEXT, difficultyColor(row[3]), TEXT, TEXT, parseColor(parse), DIM, DIM, 0xC86464];
		for (i in 0...LEFTS.length)
			line.addChild(text(LEFTS[i], 3, WIDTHS[i], 14, colors[i], values[i]));
		return line;
	}

	/** lostark.bible's parse colours: grey, green 25+, blue 50+, purple 75+, orange 95+, pink 99, gold 100. */
	static function parseColor(parse:String):Int {
		if (parse.length == 0)
			return DIM;
		var value:Int = untyped __global__["parseInt"](parse, 10);
		if (value >= 100)
			return 0xDCC999;
		if (value == 99)
			return 0xFF69B4;
		if (value >= 95)
			return 0xFFA441;
		if (value >= 75)
			return 0xCE84FF;
		if (value >= 50)
			return 0x0096FF;
		if (value >= 25)
			return 0x3DD351;
		return 0xAFAFAF;
	}

	/** lostark.bible's difficulty colours. */
	static function difficultyColor(difficulty:String):Int {
		if (difficulty == "Hard" || difficulty == "Extreme Hard" || difficulty == "Level 2")
			return 0xFDE047;
		if (difficulty == "Inferno" || difficulty == "Challenge" || difficulty == "Trial")
			return 0xD97706;
		if (difficulty == "Solo")
			return 0x22D3EE;
		if (difficulty == "Nightmare" || difficulty == "Extreme Nightmare")
			return 0xA78BFA;
		if (difficulty == "Extreme" || difficulty == "The First")
			return 0xA855F7;
		if (difficulty == "Level 3")
			return 0xFDA4AF;
		return TEXT;
	}

	static function clearList():Void {
		if (list == null)
			return;
		while (list.numChildren > 0)
			list.removeChildAt(0);
		scroll = 0;
		list.y = 138;
	}

	static function say(heading:String, message:String):Void {
		if (title != null)
			title.text = heading;
		if (status != null)
			status.text = message;
	}

	/** The name as four lowercase hex digits per character. */
	static function hexName(value:String):String {
		var parts:Array<String> = [];
		for (i in 0...value.length)
			parts.push(hex4(StringTools.fastCodeAt(value, i)));
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
		var value:String = field == null ? label.text : field.text;
		return value == null ? "" : value;
	}
}
