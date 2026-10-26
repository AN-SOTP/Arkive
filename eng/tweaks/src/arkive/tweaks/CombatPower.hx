package arkive.tweaks;

import flash.events.Event;
import flash.text.TextField;
import flash.text.TextFormat;

/**
	Combat Power: Gems. A gem's tooltip gets a line under its title: how much your Combat Power would change if the gem
	took the place of your weakest equipped gem of the same kind (or filled an empty slot), green for a gain, red for a
	loss. The game prices a gem by its tier and level alone (its BattlePoint rows, which Arkive copies from data2.lpk
	into the "gems" option as tier, level, DPS value, support value; values are hundredths of a percent that multiply
	the rest), and a tier 4 gem's Basic Attack Power % (the "gemBase" option, level and hundredths of a percent, from
	the gem table: the equipped-gem slot tooltips don't carry it where the tweak looks) raises the base they multiply.

	With the "showData" option every item tooltip also lists what the tweak can read from it, and the profile's
	figures, so the other gear types can be priced from real tooltips.

	Your Combat Power, whether it counts as DPS or support, and your equipped gems are read from your own Character
	Profile (an inspected player's window is ignored), so the line asks you to open it once. When the profile shows a
	new figure and your gems have changed since the last one, a line under the figure shows what Arkive predicted from
	the gem change, to check the pricing against the game. Gem names are matched in English.
**/
@:keep
class CombatPower {
	static inline var TWEAK = "cpgems";
	static inline var GEM_SLOTS = 11;
	static inline var GAIN = "#7EE08F";
	static inline var LOSS = "#FF8A80";
	static inline var DIM = "#A9A9A9";
	static inline var CHECK_OK = 0x7EE08F;
	static inline var CHECK_OFF = 0xFFC861;
	/** The game shows two decimals; a prediction this close counts as a match. */
	static inline var MATCH = 0.015;

	/** Gem names (English) and the tier and kind each one is. "both" is a gem that can sit in either kind of slot. */
	static var NAMES:Array<String> = ["Annihilation", "Crimson Flame", "Doomfire", "Blazing", "Brilliant"];
	static var TIERS:Array<Int> = [3, 3, 4, 4, 4];
	static var KINDS:Array<String> = ["damage", "cooldown", "damage", "cooldown", "both"];

	/** Your Combat Power as the profile last showed it, or -1. */
	static var power:Float = -1;
	/** 0 DPS, 1 support, -1 not known yet. */
	static var role = -1;
	/** Your equipped gems when the profile last showed your Combat Power ({tier, level, kind, base}), or null. */
	static var equipped:Array<Dynamic>;
	static var prices:Array<Int>;
	static var basics:Array<Int>;
	/** What the profile showed last, for the showData listing. */
	static var profileData:String = "";

	static var pendingLabel:Dynamic;
	static var pendingText:String = "";
	static var check:TextField;

	/** TooltipDocument.setMainTooltipData, before the game draws it: adds the line after a gem's title. */
	public static function onMainTooltip(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var boxes:Dynamic = args[0];
		if (boxes == null || !(untyped __is__(boxes, Array)))
			return null;
		var list:Array<Dynamic> = boxes;
		if (showData())
			addData(list);
		var gem = gemOf(list);
		if (gem == null)
			return null;
		var line = lineFor(gem);
		var at = 0;
		for (i in 0...list.length) {
			var box:Dynamic = list[i];
			if (box == null)
				continue;
			if (box.type == "SingleTextBox" && box.value == line)
				return null;
			if (box.type == "NameTagBox" || box.type == "ItemTitle")
				at = i + 1;
		}
		untyped boxes.splice(at, 0, {type: "SingleTextBox", value: line, dirty: true});
		return null;
	}

	/** CharInfoEquipAbilityPage.configUI: watch the label the game writes the Combat Power figure into. */
	public static function onAbilityConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var label:Dynamic = self.abilityAchievementLabel;
		if (label != null)
			label.addEventListener("UpdateText", onPowerText);
	}

	/** CharInfoEquipAbilityPage.combatType: 0 DPS, 1 support, 2 while the figure loads. */
	public static function onCombatType(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var type:Int = args[0];
		if (type < 2 && mine(self))
			role = type;
	}

	static function onPowerText(event:Dynamic):Void {
		var label:Dynamic = event.currentTarget;
		var text:String = event.text;
		if (label == null || text == null || !mine(label))
			return;
		// Read the gems a frame later, outside the game's own update of the window.
		pendingText = text;
		if (pendingLabel == null) {
			pendingLabel = label;
			label.addEventListener(Event.ENTER_FRAME, onFrame);
		}
	}

	static function onFrame(event:Dynamic):Void {
		var label:Dynamic = pendingLabel;
		pendingLabel = null;
		if (label == null)
			return;
		label.removeEventListener(Event.ENTER_FRAME, onFrame);
		var shown = number(Text.stripTags(pendingText));
		if (shown <= 0)
			return;
		var gems = readEquipped();
		if (showData())
			profileData = readProfile(label, gems);
		if (power > 0 && equipped != null && signature(gems) != signature(equipped))
			showCheck(label, power * ratio(equipped, gems), shown);
		power = shown;
		equipped = gems;
	}

	/** True when the window this belongs to is your own profile (the inspect window has another name). */
	static function mine(o:Dynamic):Bool {
		var at:Dynamic = o;
		while (at != null) {
			if (at.name == "charInfoWnd")
				return true;
			at = at.parent;
		}
		return false;
	}

	/** The gem a tooltip describes ({tier, level, kind, base}), or null when it isn't a tier 3 or 4 gem. */
	static function gemOf(boxes:Array<Dynamic>):Dynamic {
		var name:String = null;
		for (box in boxes) {
			if (box != null && box.type == "NameTagBox") {
				name = Text.stripTags(Tooltips.htmlOf(box.value));
				break;
			}
		}
		if (name == null)
			return null;
		var kind = -1;
		for (i in 0...NAMES.length)
			if (name.indexOf(NAMES[i] + " Gem") >= 0)
				kind = i;
		var at = name.indexOf("Lv.");
		if (kind < 0 || at < 0)
			return null;
		var level = Tooltips.leadingInt(name.substr(at + 3), 0);
		if (level < 1)
			return null;
		return {tier: TIERS[kind], level: level, kind: KINDS[kind], base: basicAttack(TIERS[kind], level), name: name};
	}

	/** A gem's Basic Attack Power in percent (0.8 for +0.80%), from the gem table; only tier 4 gems have it. */
	static function basicAttack(tier:Int, level:Int):Float {
		if (tier != 4)
			return 0;
		if (basics == null)
			basics = Options.get(TWEAK, "gemBase", null);
		if (basics == null)
			return 0;
		var at = 0;
		while (at + 1 < basics.length) {
			if (basics[at] == level)
				return basics[at + 1] / 100;
			at += 2;
		}
		return 0;
	}

	/** Your equipped gems, read through the equipped-gem slots' tooltips. */
	static function readEquipped():Array<Dynamic> {
		var gems:Array<Dynamic> = [];
		for (slot in 0...GEM_SLOTS) {
			var boxes = Slots.tooltip(Slots.GEMS, slot);
			var gem:Dynamic = boxes == null ? null : gemOf(boxes);
			if (gem != null)
				gems.push(gem);
		}
		return gems;
	}

	/** The tooltip line for a gem. */
	static function lineFor(gem:Dynamic):String {
		if (prices == null)
			prices = Options.get(TWEAK, "gems", null);
		if (prices == null)
			return font(DIM, "Combat Power: Arkive couldn't read the game's gem table");
		if (equipped == null || role < 0)
			return font(DIM, "Combat Power: open your Character Profile once to compare");
		var target:Dynamic = weakest(gem);
		var after:Array<Dynamic> = [];
		for (owned in equipped)
			if (owned != target)
				after.push(owned);
		after.push(gem);
		var change = ratio(equipped, after) - 1;
		var targetName:String = target == null ? "" : target.name;
		var against:String = target == null ? "in an empty slot" : "vs your " + targetName;
		if (power <= 0)
			return font(change >= 0 ? GAIN : LOSS, "Combat Power " + signed(change * 100, "%")) + " " + font(DIM, against);
		return font(change >= 0 ? GAIN : LOSS, "Combat Power " + signed(power * change, "") + " (" + signed(change * 100, "%") + ")")
			+ " " + font(DIM, against);
	}

	/**
		The equipped gem the new one would replace: the lowest-priced gem it can share a slot kind with. Null (an empty
		slot) when none matches and a slot is free; the lowest-priced gem of any kind when none matches and all are full.
	**/
	static function weakest(gem:Dynamic):Dynamic {
		var best:Dynamic = null;
		var bestPrice:Float = 0;
		for (owned in equipped) {
			if (gem.kind != "both" && owned.kind != "both" && owned.kind != gem.kind)
				continue;
			var price = factor(owned);
			if (best == null || price < bestPrice) {
				best = owned;
				bestPrice = price;
			}
		}
		if (best != null || equipped.length < GEM_SLOTS)
			return best;
		for (owned in equipped) {
			var price = factor(owned);
			if (best == null || price < bestPrice) {
				best = owned;
				bestPrice = price;
			}
		}
		return best;
	}

	/** How much Combat Power a set of gems gives compared with another: each gem's factor and the gems' Basic Attack Power. */
	static function ratio(before:Array<Dynamic>, after:Array<Dynamic>):Float {
		if (prices == null)
			prices = Options.get(TWEAK, "gems", null);
		var value:Float = 1;
		var baseBefore:Float = 0;
		var baseAfter:Float = 0;
		for (gem in before) {
			value = value / (1 + factor(gem));
			baseBefore += gem.base;
		}
		for (gem in after) {
			value = value * (1 + factor(gem));
			baseAfter += gem.base;
		}
		return value * (1 + baseAfter / 100) / (1 + baseBefore / 100);
	}

	/** A gem's Combat Power factor (0.064 for +6.40%) for your role, from the game's table. */
	static function factor(gem:Dynamic):Float {
		if (prices == null)
			return 0;
		var column = role == 1 ? 3 : 2;
		var at = 0;
		while (at + 3 < prices.length) {
			if (prices[at] == gem.tier && prices[at + 1] == gem.level)
				return prices[at + column] / 10000;
			at += 4;
		}
		return 0;
	}

	/** One text per gem, sorted, so two readings compare equal when the same gems are equipped. */
	static function signature(gems:Array<Dynamic>):String {
		var parts:Array<String> = [];
		for (gem in gems) {
			var name:String = gem.name;
			parts.push(name + "|" + hundredths(gem.base));
		}
		var plain:Dynamic = parts;
		plain.sort();
		return parts.join(";");
	}

	/**
		The check beside your Combat Power figure, at its left on two small lines: what Arkive predicted from the gem
		change, green when it equals the game's figure and amber when it doesn't.
	**/
	static function showCheck(label:Dynamic, predicted:Float, shown:Float):Void {
		var page:Dynamic = label.parent;
		if (page == null)
			return;
		if (check == null || check.parent != page) {
			var field:Dynamic = label.textField;
			var format = new TextFormat();
			var embed = false;
			if (field != null) {
				var own:Dynamic = field.getTextFormat();
				if (own != null && own.font != null)
					format.font = own.font;
				embed = field.embedFonts;
			}
			format.size = 11;
			check = new TextField();
			check.embedFonts = embed;
			check.defaultTextFormat = format;
			check.selectable = false;
			check.mouseEnabled = false;
			check.multiline = true;
			check.wordWrap = false;
			page.addChild(check);
		}
		check.x = label.x + 6;
		check.y = label.y + 2;
		check.width = 90;
		check.height = 34;
		var off = Math.abs(predicted - shown);
		check.textColor = off < MATCH ? CHECK_OK : CHECK_OFF;
		check.text = (off < MATCH ? "Arkive: match" : "Arkive predicted") + "
" + money(predicted);
	}

	/** A number shown by the game ("2,345.67"): commas skipped, one decimal point; -1 when there are no digits. */
	static function number(text:String):Float {
		var value:Float = 0;
		var scale:Float = 0;
		var digits = 0;
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c >= "0".code && c <= "9".code) {
				if (scale > 0) {
					value += (c - "0".code) * scale;
					scale = scale / 10;
				} else {
					value = value * 10 + (c - "0".code);
				}
				digits++;
			} else if (c == ".".code && scale == 0 && digits > 0) {
				scale = 0.1;
			} else if (c != ",".code && digits > 0) {
				break;
			}
		}
		return digits == 0 ? -1 : value;
	}

	/** "+12.34" or "-0.53%", two decimals. */
	static function signed(value:Float, unit:String):String {
		return (value < 0 ? "-" : "+") + money(Math.abs(value)) + unit;
	}

	/** "1,234.56". */
	static function money(value:Float):String {
		var cents:Int = Math.round(Math.abs(value) * 100);
		var whole:Int = Math.floor(cents / 100);
		var rest:Int = cents - whole * 100;
		var two:String = untyped __global__["String"](rest);
		return Tooltips.grouped(whole) + "." + (rest < 10 ? "0" + two : two);
	}

	static function hundredths(value:Float):String {
		var cents:Int = Math.round(value * 100);
		return untyped __global__["String"](cents);
	}

	static function showData():Bool {
		return Options.get(TWEAK, "showData", false) == true;
	}

	/** For showData: one grey box at the end of an item tooltip listing each box's type and contents, then the profile's figures. */
	static function addData(list:Array<Dynamic>):Void {
		var item = false;
		var lines:Array<String> = [];
		for (i in 0...list.length) {
			var box:Dynamic = list[i];
			if (box == null)
				continue;
			var type:String = box.type;
			if (type == "ItemTitle")
				item = true;
			if (type == "SingleTextBox" && Tooltips.isString(box.value) && (box.value : String).indexOf("Arkive data") >= 0)
				return;
			var index:String = untyped __global__["String"](i);
			lines.push("[" + index + "] " + type + ": " + describe(box.value, 0));
		}
		if (!item)
			return;
		var text = "Arkive data
" + lines.join("
") + (profileData == "" ? "
(profile not read yet)" : "
" + profileData);
		if (text.length > 3500)
			text = text.substr(0, 3500) + " …";
		list.push({type: "SingleTextBox", value: "<FONT SIZE='10' COLOR='#9A9A9A'>" + escape(text).split("
").join("<BR>") + "</FONT>", dirty: true});
	}

	/** A tooltip value as short text: strings without tags, arrays as a | b, objects as key=value for a few levels. */
	static function describe(value:Dynamic, depth:Int):String {
		if (value == null)
			return "null";
		var type:String = untyped __typeof__(value);
		if (type == "string") {
			var plain:String = Text.stripTags((value : String).split("<BR>").join(" / ").split("<br>").join(" / "));
			return plain.length > 160 ? plain.substr(0, 160) + "…" : plain;
		}
		if (type != "object" || depth > 3) {
			var shown:String = untyped __global__["String"](value);
			return shown;
		}
		var parts:Array<String> = [];
		if (untyped __is__(value, Array)) {
			var items:Array<Dynamic> = value;
			for (item in items)
				parts.push(describe(item, depth + 1));
			return "[" + parts.join(" | ") + "]";
		}
		var keys:Array<String> = untyped __keys__(value);
		for (key in keys)
			if (key != "slotData" || depth > 0)
				parts.push(key + "=" + describe(untyped value[key], depth + 1));
		if (Text.has(value, "slotData") && value.slotData != null) {
			var slot:Dynamic = value.slotData;
			var icon:String = describe(slot.iconPath, 3);
			parts.push("slotData.iconPath=" + icon);
		}
		return "{" + parts.join(", ") + "}";
	}

	/** For showData: your role, Combat Power, the profile's stat lists, your equipment slots and the gems read. */
	static function readProfile(label:Dynamic, gems:Array<Dynamic>):String {
		var lines:Array<String> = [];
		var shownRole:String = untyped __global__["String"](role);
		lines.push("Profile: role " + shownRole + " (0 DPS, 1 support), Combat Power " + money(power));
		var page:Dynamic = label.parent;
		var basic:Dynamic = page == null || page.abilityDefaultList == null ? null : page.abilityDefaultList.dataProvider;
		lines.push("Basic: " + rows(basic, "titleStr", "valueStr"));
		var window:Dynamic = GameUi.ancestor(label, "ark.ui.charInfo.CharInfoWndContent");
		var stats:Dynamic = window == null || window.detailPanel == null || window.detailPanel.statPage == null ? null
			: window.detailPanel.statPage.detailStatList.dataProvider;
		lines.push("Detail: " + rows(stats, "statName", "value"));
		var slots:Dynamic = window == null || window.equipPanel == null || window.equipPanel.itemPage == null ? null
			: window.equipPanel.itemPage.equipItemSlotManager;
		if (slots != null) {
			var count:Int = slots.slotLength;
			var shown:Array<String> = [];
			for (i in 0...count) {
				var slot:Dynamic = slots.getSlotAtIndex(i);
				if (slot == null)
					continue;
				var boxes = Tooltips.boxes(slot.binding, slot);
				var name = "";
				if (boxes != null)
					for (box in boxes)
						if (box != null && box.type == "NameTagBox") {
							name = Text.stripTags(Tooltips.htmlOf(box.value));
							break;
						}
				var index:String = untyped __global__["String"](i);
				var kind:String = untyped __global__["String"](slot.slotType);
				var id:String = untyped __global__["String"](slot.bindID);
				shown.push(index + ":" + kind + "/" + id + " " + name);
			}
			lines.push("Equipment slots: " + shown.join("; "));
		}
		var names:Array<String> = [];
		for (gem in gems) {
			var name:String = gem.name;
			names.push(name);
		}
		lines.push("Gems read: " + names.join(", "));
		var probe = Slots.tooltip(Slots.GEMS, 0);
		lines.push("Gem slot 0 probe: " + (probe == null ? "null" : describe(probe, 0)));
		return lines.join("
");
	}

	static function rows(list:Dynamic, name:String, value:String):String {
		if (list == null)
			return "none";
		var shown:Array<String> = [];
		var count:Int = list.length;
		for (i in 0...count) {
			var row:Dynamic = list[i];
			if (row == null)
				continue;
			shown.push(describe(untyped row[name], 3) + "=" + describe(untyped row[value], 3));
		}
		return shown.join("; ");
	}

	static function escape(text:String):String {
		return text.split("&").join("&amp;").split("<").join("&lt;").split(">").join("&gt;");
	}

	static function font(color:String, text:String):String {
		return "<FONT COLOR='" + color + "'>" + text + "</FONT>";
	}
}
