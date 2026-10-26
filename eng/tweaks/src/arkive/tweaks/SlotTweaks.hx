package arkive.tweaks;

import flash.display.Graphics;
import flash.display.Sprite;
import flash.filters.GlowFilter;
import flash.text.TextField;
import flash.text.TextFieldAutoSize;
import flash.text.TextFormat;
import flash.utils.Dictionary;

/**
	Slot Tweaks: marks on item slots, read from each item's own tooltip when the slot gets a new item.
	- accessories and ability stones get an inner glow in their quality colour;
	- bracelets get a pip per bonus line (coloured by its stat, grey when the bonus can still be granted);
	- ability stones show their first two engraving levels ("7/6");
	- stacks above 9999 show their full count (up to 99999, then "123k") instead of "9999+".
**/
@:keep
class SlotTweaks {
	/** Quality thresholds and their colours, best first (the game's quality bar colours). */
	static var QUALITY:Array<Dynamic> = [
		{min: 100, color: 0xFE9600},
		{min: 90, color: 0xCE43FC},
		{min: 70, color: 0x00B5FF},
		{min: 30, color: 0x91FE02},
		{min: 10, color: 0xFFD200},
		{min: 1, color: 0xFF6000},
		{min: 0, color: 0x686660},
	];

	/** Bracelet bonus lines by stat (as the tooltip writes them) and their pip colour. */
	static var STATS:Array<Dynamic> = [
		{text: "Crit <FONT", color: 0xFF5F5F},
		{text: "Specialization <FONT", color: 0x5FB9FF},
		{text: "Swiftness <FONT", color: 0x91FE02},
	];

	static inline var OTHER_STAT = 0xF0F0F0;
	static inline var OPEN = -1;
	static inline var OPEN_TEXT = "Bonus can be granted";
	static inline var OPEN_FILL = 0x8C8C8C;
	static inline var PIP = 8;
	static inline var PIP_GAP = 2;
	static inline var PIP_MARGIN = 4;
	static inline var GAME_LIMIT = 9999;
	static inline var FULL_LIMIT = 99999;

	/** Per slot: the item last read, its quality (-1 none), bracelet pips, stone levels and the overlay drawn on it. */
	static var readData = new Dictionary(true);

	static var readKey = new Dictionary(true);
	static var qualities = new Dictionary(true);
	static var pips = new Dictionary(true);
	static var levels = new Dictionary(true);
	static var overlays = new Dictionary(true);

	/** ARKNewSlot.draw: read the item when it changed, then redraw the marks and the count. */
	public static function onDraw(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var data:Dynamic = self.data;
		var key:String = data == null ? null : [self.bindID, data.iconPath].join("|");
		var lastData:Dynamic = untyped readData[self];
		var lastKey:Dynamic = untyped readKey[self];
		if (lastData != data || lastKey != key) {
			untyped readData[self] = data;
			untyped readKey[self] = key;
			read(self);
		}
		apply(self);
		if (option("fullCounts"))
			showCount(self);
	}

	/** ARKIcon.applyDisableFilter: the game replaced the icon's filters (greyed out or not); put the quality glow back. */
	public static function onApplyDisableFilter(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (slotClass == null)
			slotClass = GameUi.definition("ark.controls.ARKNewSlot");
		if (slotClass != null && untyped __is__(self, slotClass))
			apply(self);
	}

	static var slotClass:Dynamic;

	/** ARKNewSlot.clearContentData: the slot is empty now. */
	public static function onClearContentData(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		forget(self);
		apply(self);
	}

	/** ARKSlotManager.setSlotCount(index, count): the game wrote "9999+"; write the full count. */
	public static function onSetSlotCount(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (!option("fullCounts"))
			return;
		var slot:Dynamic = self.getItemSlot(args[0]);
		if (slot != null)
			showCount(slot);
	}

	static function option(key:String):Bool {
		return GameUi.on("slottweaks", key);
	}

	static function forget(slot:Dynamic):Void {
		untyped readData[slot] = null;
		untyped readKey[slot] = null;
		untyped qualities[slot] = -1;
		untyped pips[slot] = null;
		untyped levels[slot] = null;
	}

	static function apply(slot:Dynamic):Void {
		var quality:Dynamic = untyped qualities[slot];
		setGlow(slot, quality == null || !option("showQuality") ? -1 : quality);
		setPips(slot, option("showPips") ? untyped pips[slot] : null);
		setLevels(slot, option("showPoints") ? untyped levels[slot] : null);
	}

	static function showCount(slot:Dynamic):Void {
		var field:Dynamic = slot.valueAmount;
		if (field == null || slot.limitMaxCount != GAME_LIMIT)
			return;
		var count:Int = slot.count;
		if (count <= GAME_LIMIT)
			return;
		var text:String = count <= FULL_LIMIT ? untyped __global__["String"](count) : untyped __global__["String"](Math.floor(count / 1000)) + "k";
		if (field.text != text)
			field.text = text;
	}

	/** Only accessories and ability stones (by icon path) with the game's own item tooltip are read. */
	static function wanted(slot:Dynamic):Bool {
		if (!option("showQuality") && !option("showPips") && !option("showPoints"))
			return false;
		var custom:Dynamic = slot.tooltip;
		if (slot.bindID == null || (custom != null && custom != ""))
			return false;
		var path:Dynamic = slot.data.iconPath;
		if (!Tooltips.isString(path))
			return false;
		var icon:String = path;
		return icon.indexOf("/Acc") >= 0 || icon.indexOf("/Ability") >= 0;
	}

	static function read(slot:Dynamic):Void {
		forget(slot);
		untyped readData[slot] = slot.data;
		untyped readKey[slot] = slot.data == null ? null : [slot.bindID, slot.data.iconPath].join("|");
		if (slot.data == null || !wanted(slot))
			return;
		var list = Tooltips.boxes(slot.binding, slot);
		if (list == null)
			return;
		for (box in list) {
			if (box == null)
				continue;
			var value:Dynamic = box.value;
			if (box.type == "ItemTitle" && value != null && value.qualityValue != null) {
				var quality:Float = value.qualityValue;
				untyped qualities[slot] = Math.floor(quality);
			} else if (box.type == "ItemPartBox") {
				var found = braceletPips(value);
				var known:Dynamic = untyped pips[slot];
				if (found != null && (known == null || found.length > known.length))
					untyped pips[slot] = found;
			} else if (box.type == "IndentStringGroup") {
				var found = stoneLevels(value);
				if (found != null)
					untyped levels[slot] = found;
			}
		}
	}

	/** ["Bracelet Bonus …", "line<BR>line…"]: one colour per bonus line, OPEN for one that can still be granted. */
	static function braceletPips(value:Dynamic):Array<Int> {
		if (!untyped __is__(value, Array) || value.length < 2 || !Tooltips.isString(value[0]) || !Tooltips.isString(value[1]))
			return null;
		var title:String = value[0];
		if (title.indexOf("Bracelet Bonus") < 0)
			return null;
		var lines:String = value[1];
		var found:Array<Int> = [];
		for (line in lines.split("<BR>")) {
			if (Text.trim(line) == "")
				continue;
			found.push(line.indexOf(OPEN_TEXT) >= 0 ? OPEN : gradeColor(line));
		}
		return found.length == 0 ? null : found;
	}

	/**
		A bonus line's pip colour: the colour the tooltip gives its value, which shows the bonus's grade (blue, purple,
		orange …); the stat's own colour when the line has none.
	**/
	static function gradeColor(line:String):Int {
		var at = line.toUpperCase().lastIndexOf("COLOR=");
		if (at < 0)
			return statColor(line);
		var start = at + 6;
		while (start < line.length && (StringTools.fastCodeAt(line, start) == "'".code || StringTools.fastCodeAt(line, start) == "\"".code
			|| StringTools.fastCodeAt(line, start) == "#".code))
			start++;
		var value = 0;
		for (i in 0...6) {
			var c = StringTools.fastCodeAt(line, start + i);
			var digit = c >= "0".code && c <= "9".code ? c - "0".code
				: (c >= "a".code && c <= "f".code ? c - "a".code + 10 : (c >= "A".code && c <= "F".code ? c - "A".code + 10 : -1));
			if (digit < 0)
				return statColor(line);
			value = value * 16 + digit;
		}
		return value;
	}

	static function statColor(line:String):Int {
		for (stat in STATS) {
			var text:String = stat.text;
			if (line.indexOf(text) >= 0)
				return stat.color;
		}
		return OTHER_STAT;
	}

	/** An ability stone lists three "Lv. N" (two engravings and the penalty): the first two as "a/b". */
	static function stoneLevels(value:Dynamic):String {
		var text = strings(value, [], 0).join(" ");
		var found:Array<String> = [];
		var at = text.indexOf("Lv.");
		while (at >= 0) {
			var i = at + 3;
			while (i < text.length && isSpace(StringTools.fastCodeAt(text, i)))
				i++;
			var start = i;
			while (i < text.length && isDigit(StringTools.fastCodeAt(text, i)))
				i++;
			if (i > start)
				found.push(text.substring(start, i));
			at = text.indexOf("Lv.", i);
		}
		return found.length == 3 ? found[0] + "/" + found[1] : null;
	}

	static function isSpace(c:Int):Bool {
		return c == " ".code || c == "\t".code || c == "\r".code || c == "\n".code;
	}

	static function isDigit(c:Int):Bool {
		return c >= "0".code && c <= "9".code;
	}

	/** Every string inside a tooltip value (arrays and objects searched a few levels deep). */
	static function strings(value:Dynamic, into:Array<String>, depth:Int):Array<String> {
		if (value == null || depth > 8)
			return into;
		var kind:String = untyped __typeof__(value);
		if (kind == "string") {
			into.push(value);
		} else if (untyped __is__(value, Array)) {
			var items:Array<Dynamic> = value;
			for (item in items)
				strings(item, into, depth + 1);
		} else if (kind == "object") {
			for (field in Reflect.fields(value))
				strings(Reflect.field(value, field), into, depth + 1);
		}
		return into;
	}

	/** Replaces the slot icon's glow (keeping the game's own filters) with the quality colour, or removes it. */
	static function setGlow(slot:Dynamic, quality:Int):Void {
		var canvas:Dynamic = slot.contentCanvas;
		if (canvas == null)
			return;
		var kept:Array<Dynamic> = [];
		var current:Array<Dynamic> = canvas.filters;
		if (current != null) {
			for (filter in current) {
				if (!untyped __is__(filter, GlowFilter))
					kept.push(filter);
			}
		}
		if (quality >= 0)
			kept.push(new GlowFilter(qualityColor(quality), 1, 6, 6, 4, 1, true, false));
		canvas.filters = kept;
	}

	static function qualityColor(quality:Int):Int {
		for (step in QUALITY) {
			var min:Int = step.min;
			if (quality >= min)
				return step.color;
		}
		return 0x686660;
	}

	/** The sprite (drawn pips) and label (stone levels) on top of a slot, made the first time it's needed. */
	static function overlay(slot:Dynamic):Dynamic {
		var made:Dynamic = untyped overlays[slot];
		if (made != null)
			return made;
		var canvas:Dynamic = slot.contentCanvas;
		if (canvas == null)
			return null;
		var sprite = new Sprite();
		sprite.mouseEnabled = false;
		sprite.mouseChildren = false;
		var hitTest:Dynamic = GameUi.definition("scaleform.gfx.InteractiveObjectEx");
		if (hitTest != null)
			hitTest.setHitTestDisable(sprite, true);
		var format = new TextFormat();
		format.font = "$YG760";
		format.size = 14;
		format.bold = true;
		var label = new TextField();
		label.defaultTextFormat = format;
		label.textColor = 0xFFFFFF;
		label.autoSize = TextFieldAutoSize.LEFT;
		label.selectable = false;
		label.mouseEnabled = false;
		label.filters = [new GlowFilter(0, 1, 2, 2, 8, 1)];
		label.visible = false;
		sprite.addChild(label);
		slot.addChild(sprite);
		made = {sprite: sprite, label: label};
		untyped overlays[slot] = made;
		return made;
	}

	/** Pips along the bottom-left of the icon, snapped to whole screen pixels so their edges stay sharp. */
	static function setPips(slot:Dynamic, colors:Dynamic):Void {
		var made:Dynamic = untyped overlays[slot];
		if (colors == null) {
			if (made != null)
				made.sprite.graphics.clear();
			return;
		}
		made = overlay(slot);
		if (made == null)
			return;
		var canvas:Dynamic = slot.contentCanvas;
		var g:Graphics = made.sprite.graphics;
		g.clear();
		var m:Dynamic = made.sprite.transform.concatenatedMatrix;
		var sx:Float = m.a == 0 ? 1 : m.a;
		var sy:Float = m.d == 0 ? 1 : m.d;
		var tx:Float = m.tx;
		var ty:Float = m.ty;
		var top:Float = canvas.y + canvas.height - PIP_MARGIN - PIP;
		var left:Float = canvas.x + PIP_MARGIN;
		var list:Array<Int> = colors;
		for (i in 0...list.length) {
			var x = left + i * (PIP + PIP_GAP);
			var open = list[i] == OPEN;
			g.beginFill(0, 0.85);
			box(g, snap(x, tx, sx), snap(top, ty, sy), snap(x + PIP, tx, sx), snap(top + PIP, ty, sy));
			g.endFill();
			g.beginFill(open ? OPEN_FILL : list[i]);
			box(g, snap(x + 1, tx, sx), snap(top + 1, ty, sy), snap(x + PIP - 1, tx, sx), snap(top + PIP - 1, ty, sy));
			g.endFill();
		}
	}

	static function snap(value:Float, offset:Float, scale:Float):Float {
		return (Math.round(offset + value * scale) - offset) / scale;
	}

	static function box(g:Graphics, x1:Float, y1:Float, x2:Float, y2:Float):Void {
		g.moveTo(x1, y1);
		g.lineTo(x2, y1);
		g.lineTo(x2, y2);
		g.lineTo(x1, y2);
		g.lineTo(x1, y1);
	}

	static function setLevels(slot:Dynamic, text:Dynamic):Void {
		var made:Dynamic = untyped overlays[slot];
		if (text == null) {
			if (made != null)
				made.label.visible = false;
			return;
		}
		made = overlay(slot);
		if (made == null)
			return;
		var canvas:Dynamic = slot.contentCanvas;
		var label:Dynamic = made.label;
		label.text = text;
		label.x = canvas.x + PIP_MARGIN + 2;
		label.y = canvas.y + canvas.height - label.height - PIP_MARGIN + 2;
		label.visible = true;
	}
}
