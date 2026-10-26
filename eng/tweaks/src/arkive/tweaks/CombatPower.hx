package arkive.tweaks;

import flash.events.Event;
import flash.text.TextField;
import flash.text.TextFormat;

/**
	Combat Power on item tooltips. A gem, weapon, armour piece, vambrace or accessory gets a line under its title: how
	much your Combat Power would change if you equipped it in place of what you wear in that slot (for gems your
	weakest gem of the same kind; for earrings and rings the one of the pair that counts for less), green for a gain,
	red for a loss.

	The game's Combat Power is a base attack times one factor per source, each from its BattlePoint rows (Arkive copies
	the ones it prices from data2.lpk into the "rows" option as key, type, A, B, C; key 1 DPS, 2 support; values are
	hundredths of a percent). The base is the square root of main stat times weapon power over six, plus flat Basic Atk.
	Power, raised by the gems' Basic Attack Power % (the "gemBase" option, from the gem table). Factors priced here:
	gems by tier and level (type 22), weapon quality (4), refinement lines (15, by stat; Outgoing Damage counts as
	shown) and Enlightenment points (6). Main stat and weapon power are summed over your equipment, which leaves out
	your character's own stats, so gear lines start with "~" until the profile check confirms them. Support characters
	get gem lines only for now.

	Your Combat Power, DPS or support, equipment and gems are read from your own Character Profile (an inspected
	player's window is ignored), so the lines ask you to open it once. When the profile shows a new figure and your
	gear has changed since the last one, a note at the left of the figure shows what Arkive predicted. Tooltip text is
	matched in English. With the "showData" option every item tooltip also lists what the tweak can read from it.
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
	static inline var NEWLINE = "\n";

	/** BattlePoint types. */
	static inline var QUALITY = 4;
	static inline var ENLIGHTENMENT = 6;
	static inline var REFINEMENT = 15;
	static inline var GEM = 22;

	/** Gem names (English) and the tier and kind each one is. "both" is a gem that can sit in either kind of slot. */
	static var NAMES:Array<String> = ["Annihilation", "Crimson Flame", "Doomfire", "Blazing", "Brilliant"];
	static var TIERS:Array<Int> = [3, 3, 4, 4, 4];
	static var KINDS:Array<String> = ["damage", "cooldown", "damage", "cooldown", "both"];

	/** The profile's equipment slots, in its order. */
	static var SLOTS:Array<String> = ["weapon", "helm", "chest", "pants", "gloves", "shoulder", "necklace", "earring", "earring",
		"ring", "ring", "bracelet", "stone", "orb", "vambrace"];
	/** Words in an item's name that say which slot it goes in (English), checked in this order. */
	static var WORDS:Array<String> = ["Vambrace", "Necklace", "Earring", "Ring", "Bracelet", "Stone", "Orb", "Helm", "Chestpiece",
		"Pants", "Gloves", "Pauldrons"];
	static var WORD_SLOTS:Array<String> = ["vambrace", "necklace", "earring", "ring", "bracelet", "stone", "orb", "helm", "chest",
		"pants", "gloves", "shoulder"];
	/** Slots the tweak doesn't price yet. */
	static var UNPRICED:Array<String> = ["bracelet", "stone", "orb"];
	static var STATS:Array<String> = ["Strength", "Dexterity", "Intelligence"];
	/** Refinement lines and the stat each one is in the game's table (Outgoing Damage, -1, counts as shown). */
	static var REFINE_LABELS:Array<String> = ["Outgoing Damage", "Additional Damage", "Crit Rate", "Crit Damage", "Atk. Power"];
	static var REFINE_STATS:Array<Int> = [-1, 50, 74, 76, 49];
	/** The table's stat for flat Atk. Power, which a refinement line without % adds. */
	static inline var FLAT_ATTACK_STAT = 124;

	/** Your Combat Power as the profile last showed it, or -1. */
	static var power:Float = -1;
	/** 0 DPS, 1 support, -1 not known yet. */
	static var role = -1;
	/** Your equipment by profile slot (parsed items, null where empty) and gems, when the profile last showed your Combat Power. */
	static var gear:Array<Dynamic>;
	static var equipped:Array<Dynamic>;
	/** "Strength", "Dexterity" or "Intelligence": the stat your armour has. */
	static var mainStat:String = "";
	static var rows:Array<Int>;
	static var basics:Array<Int>;
	/** What the profile showed last, for the showData listing. */
	static var profileData:String = "";

	static var pendingLabel:Dynamic;
	static var pendingText:String = "";
	static var check:TextField;

	/** TooltipDocument.setMainTooltipData, before the game draws it: adds the line after an item's title. */
	public static function onMainTooltip(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var boxes:Dynamic = args[0];
		if (boxes == null || !(untyped __is__(boxes, Array)))
			return null;
		var list:Array<Dynamic> = boxes;
		if (showData())
			addData(list);
		var line = lineFor(list);
		if (line == null)
			return null;
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
		// Read the equipment a frame later, outside the game's own update of the window.
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
		var window:Dynamic = GameUi.ancestor(label, "ark.ui.charInfo.CharInfoWndContent");
		var items = readGear(window);
		var gems = readEquipped();
		if (power > 0 && gear != null && equipped != null
			&& (signature(gems) != signature(equipped) || gearSignature(items) != gearSignature(gear)))
			showCheck(label, power * value(items, gems) / value(gear, equipped), shown);
		power = shown;
		gear = items;
		equipped = gems;
		if (showData())
			profileData = readProfile(label, window, items, gems);
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

	// ---- The tooltip line ----

	/** The tooltip line for an item, or null when the tweak doesn't price it. */
	static function lineFor(list:Array<Dynamic>):String {
		var gem = gemOf(list);
		if (gem != null)
			return gemLine(gem);
		var item = itemOf(list);
		if (item == null || item.slot == "" || UNPRICED.indexOf(item.slot) >= 0)
			return null;
		if (table() == null)
			return font(DIM, "Combat Power: Arkive couldn't read the game's table");
		if (gear == null || role < 0)
			return font(DIM, "Combat Power: open your Character Profile once to compare");
		if (role == 1)
			return font(DIM, "Combat Power: gear isn't priced for support yet");
		var slot = targetSlot(item);
		if (slot < 0)
			return null;
		var worn:Dynamic = gear[slot];
		var wornText:String = worn == null ? "" : worn.text;
		var itemText:String = item.text;
		if (worn != null && wornText == itemText)
			return font(DIM, "Combat Power: this is what you wear");
		var exclusive:String = item.exclusive;
		var wornExclusive:String = worn == null ? "" : worn.exclusive;
		if (exclusive != "" && wornExclusive != "" && exclusive != wornExclusive)
			return font(DIM, "Combat Power: not for your class");
		var after = copyOf(gear);
		after[slot] = item;
		var change = value(after, equipped) / value(gear, equipped) - 1;
		var wornName:String = worn == null ? "" : worn.name;
		var against:String = worn == null ? "in an empty slot" : "vs your " + wornName;
		return changeText(change, true) + " " + font(DIM, against);
	}

	static function gemLine(gem:Dynamic):String {
		if (table() == null)
			return font(DIM, "Combat Power: Arkive couldn't read the game's gem table");
		if (equipped == null || role < 0)
			return font(DIM, "Combat Power: open your Character Profile once to compare");
		var target:Dynamic = weakest(gem);
		var after:Array<Dynamic> = [];
		for (owned in equipped)
			if (owned != target)
				after.push(owned);
		after.push(gem);
		var change = gemRatio(equipped, after) - 1;
		var targetName:String = target == null ? "" : target.name;
		var against:String = target == null ? "in an empty slot" : "vs your " + targetName;
		return changeText(change, false) + " " + font(DIM, against);
	}

	/** "Combat Power +12.34 (+0.53%)" in green or red; "~" in front when it rests on summed main stat and weapon power. */
	static function changeText(change:Float, rough:Bool):String {
		var lead:String = rough ? "Combat Power ~" : "Combat Power ";
		var text:String = power <= 0 ? lead + signed(change * 100, "%")
			: lead + signed(power * change, "") + " (" + signed(change * 100, "%") + ")";
		return font(change >= 0 ? GAIN : LOSS, text);
	}

	/** The profile slot an item would go in: for earrings and rings the one of the pair that counts for less. */
	static function targetSlot(item:Dynamic):Int {
		var slot:String = item.slot;
		var first = SLOTS.indexOf(slot);
		if (first < 0)
			return -1;
		if (slot != "earring" && slot != "ring")
			return first;
		var second = first + 1;
		if (gear[first] == null)
			return first;
		if (gear[second] == null)
			return second;
		var without = copyOf(gear);
		without[first] = null;
		var keepSecond = value(without, equipped);
		without = copyOf(gear);
		without[second] = null;
		var keepFirst = value(without, equipped);
		return keepFirst >= keepSecond ? second : first;
	}

	// ---- Reading items ----

	/**
		An item from its tooltip boxes: {slot, name, text, stats {Strength, …}, weapon, flat, quality, refine (factors),
		enlightenment, exclusive}, or null when the tooltip isn't an item's. slot is "" when the tweak can't tell where it goes.
	**/
	static function itemOf(boxes:Array<Dynamic>):Dynamic {
		var name:String = null;
		var quality = -1;
		var basic:Array<String> = [];
		var refine:Array<Float> = [];
		var enlightenment = 0;
		var exclusive = "";
		var title = false;
		var parts:Array<String> = [];
		for (box in boxes) {
			if (box == null)
				continue;
			var type:String = box.type;
			if (type == "NameTagBox" && name == null) {
				name = Text.stripTags(Tooltips.htmlOf(box.value));
			} else if (type == "ItemTitle") {
				title = true;
				var shown:Dynamic = box.value == null ? null : box.value.qualityValue;
				if (shown != null) {
					var at:Float = shown;
					quality = Math.floor(at);
				}
			} else if (type == "SingleTextBox" && Tooltips.isString(box.value)) {
				var text = Text.stripTags(box.value);
				if (StringTools.endsWith(text, " Exclusive"))
					exclusive = text;
			} else if (type == "ItemPartBox" && (untyped __is__(box.value, Array))) {
				var part:Array<Dynamic> = box.value;
				if (part.length < 2 || !Tooltips.isString(part[0]) || !Tooltips.isString(part[1]))
					continue;
				var heading = Text.trim(Text.stripTags(part[0]));
				var body = splitLines(part[1]);
				parts.push(heading + ":" + body.join(","));
				if (heading.indexOf("Refinement") >= 0) {
					for (line in body) {
						var factor = refinement(line);
						if (factor != 0)
							refine.push(factor);
					}
				} else if (heading.indexOf("Ark Passive") >= 0) {
					for (line in body)
						if (StringTools.startsWith(line, "Enlightenment +"))
							enlightenment += Math.floor(number(line.substr(15)));
				} else if (heading == "Basic Effect") {
					for (line in body)
						basic.push(line);
				}
			}
		}
		if (!title || name == null)
			return null;
		var stats:Dynamic = {};
		var hasStat = false;
		var weapon:Float = 0;
		var flat:Float = 0;
		for (line in basic) {
			for (stat in STATS)
				if (StringTools.startsWith(line, stat + " +")) {
					untyped stats[stat] = number(line.substr(stat.length + 2));
					hasStat = true;
				}
			if (StringTools.startsWith(line, "Weapon Power +"))
				weapon += number(line.substr(14));
			else if (StringTools.startsWith(line, "Basic Atk. Power +"))
				flat += number(line.substr(18));
		}
		var slot = "";
		for (i in 0...WORDS.length)
			if (slot == "" && name.indexOf(WORDS[i]) >= 0)
				slot = WORD_SLOTS[i];
		if (slot == "" && weapon > 0 && !hasStat && quality >= 0)
			slot = "weapon";
		if (slot != "weapon" && UNPRICED.indexOf(slot) < 0 && !hasStat)
			slot = "";
		return {slot: slot, name: name, text: name + "|" + parts.join("|"), stats: stats, weapon: weapon, flat: flat,
			quality: slot == "weapon" ? quality : -1, refine: refine, enlightenment: enlightenment, exclusive: exclusive};
	}

	/** A refinement line's Combat Power factor (0.02 for +2.00%), or 0 when the tweak doesn't price that line. */
	static function refinement(line:String):Float {
		for (i in 0...REFINE_LABELS.length) {
			var label = REFINE_LABELS[i] + " +";
			if (!StringTools.startsWith(line, label))
				continue;
			var rest = line.substr(label.length);
			var amount = number(rest);
			if (amount <= 0)
				return 0;
			var percent = rest.indexOf("%") >= 0;
			if (REFINE_STATS[i] == -1)
				return percent ? amount / 100 : 0;
			if (!percent)
				return REFINE_STATS[i] == 49 ? amount * weight(FLAT_ATTACK_STAT) / 100000000 : 0;
			// The table's stats are in hundredths of a percent, its weights in ten-thousandths.
			return amount * 100 * weight(REFINE_STATS[i]) / 100000000;
		}
		return 0;
	}

	/** A refinement stat's weight in the game's table (7692 for Additional Damage), or 0. */
	static function weight(stat:Int):Float {
		var found = table();
		if (found == null)
			return 0;
		var at = 0;
		while (at + 4 < found.length) {
			if (found[at] == 1 && found[at + 1] == REFINEMENT && found[at + 3] == stat)
				return found[at + 4];
			at += 5;
		}
		return 0;
	}

	/** Your equipment by profile slot, read from the equipment slots' own tooltips. */
	static function readGear(window:Dynamic):Array<Dynamic> {
		var items:Array<Dynamic> = [];
		for (i in 0...SLOTS.length)
			items.push(null);
		var slots:Dynamic = window == null || window.equipPanel == null || window.equipPanel.itemPage == null ? null
			: window.equipPanel.itemPage.equipItemSlotManager;
		if (slots == null)
			return items;
		var count:Int = slots.slotLength;
		for (i in 0...count) {
			if (i >= SLOTS.length)
				break;
			var slot:Dynamic = slots.getSlotAtIndex(i);
			var boxes = slot == null ? null : Tooltips.boxes(slot.binding, slot);
			var item:Dynamic = boxes == null ? null : itemOf(boxes);
			if (item == null)
				continue;
			item.slot = SLOTS[i];
			if (i == 0)
				item.quality = qualityOf(boxes);
			items[i] = item;
		}
		var armour:Dynamic = items[1];
		if (armour != null)
			for (stat in STATS)
				if (Text.has(armour.stats, stat))
					mainStat = stat;
		return items;
	}

	static function qualityOf(boxes:Array<Dynamic>):Int {
		for (box in boxes) {
			if (box == null || box.type != "ItemTitle" || box.value == null)
				continue;
			var shown:Dynamic = box.value.qualityValue;
			if (shown != null) {
				var at:Float = shown;
				return Math.floor(at);
			}
		}
		return -1;
	}

	/**
		How much Combat Power a set of equipment and gems gives, up to a constant: base attack (summed main stat, weapon
		power and flat Basic Atk. Power, raised by the gems' Basic Attack Power) times weapon quality, each refinement
		line, Enlightenment points and each gem.
	**/
	static function value(items:Array<Dynamic>, gems:Array<Dynamic>):Float {
		var stat:Float = 0;
		var weapon:Float = 0;
		var flat:Float = 0;
		var points:Float = 0;
		var factors:Float = 1;
		for (item in items) {
			if (item == null)
				continue;
			if (mainStat != "" && Text.has(item.stats, mainStat)) {
				var own:Float = untyped item.stats[mainStat];
				stat += own;
			}
			var itemWeapon:Float = item.weapon;
			weapon += itemWeapon;
			var itemFlat:Float = item.flat;
			flat += itemFlat;
			var itemPoints:Float = item.enlightenment;
			points += itemPoints;
			var quality:Int = item.quality;
			if (quality >= 0)
				factors *= 1 + qualityBonus(quality);
			var refine:Array<Float> = item.refine;
			for (factor in refine)
				factors *= 1 + factor;
		}
		factors *= 1 + points * perPoint(ENLIGHTENMENT);
		var base = stat > 0 && weapon > 0 ? Math.sqrt(stat * weapon / 6) + flat : 1;
		return base * factors * gemRatio([], gems);
	}

	/** Weapon quality's Combat Power factor (0.30 at 100). */
	static function qualityBonus(quality:Int):Float {
		var found = table();
		if (found == null)
			return 0;
		var at = 0;
		while (at + 4 < found.length) {
			if (found[at] == 1 && found[at + 1] == QUALITY && found[at + 2] == quality)
				return found[at + 3] / 10000;
			at += 5;
		}
		return 0;
	}

	/** An Ark Passive tree's Combat Power per point (0.007 for Enlightenment). */
	static function perPoint(type:Int):Float {
		var found = table();
		if (found == null)
			return 0;
		var at = 0;
		while (at + 4 < found.length) {
			if (found[at] == 1 && found[at + 1] == type)
				return found[at + 2] / 10000;
			at += 5;
		}
		return 0;
	}

	static function table():Array<Int> {
		if (rows == null)
			rows = Options.get(TWEAK, "rows", null);
		return rows;
	}

	static function gearSignature(items:Array<Dynamic>):String {
		var parts:Array<String> = [];
		for (item in items) {
			var text:String = item == null ? "-" : item.text;
			parts.push(text);
		}
		return parts.join(";");
	}

	/** Tooltip text lines: <BR> breaks, tags removed, trimmed, empty lines dropped. */
	static function splitLines(html:String):Array<String> {
		var found:Array<String> = [];
		var joined = html.split("<BR>").join(NEWLINE).split("<br>").join(NEWLINE);
		for (piece in joined.split(NEWLINE)) {
			var line = Text.trim(Text.stripTags(piece));
			if (line != "")
				found.push(line);
		}
		return found;
	}

	static function copyOf(items:Array<Dynamic>):Array<Dynamic> {
		var raw:Dynamic = items;
		return raw.concat();
	}

	// ---- Gems ----

	/** The gem a tooltip describes ({tier, level, kind, base, name}), or null when it isn't a tier 3 or 4 gem. */
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

	/** Your equipped gems, through the equipped-gem slots (their tooltips carry only the gem's name). */
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
			var price = gemFactor(owned);
			if (best == null || price < bestPrice) {
				best = owned;
				bestPrice = price;
			}
		}
		if (best != null || equipped.length < GEM_SLOTS)
			return best;
		for (owned in equipped) {
			var price = gemFactor(owned);
			if (best == null || price < bestPrice) {
				best = owned;
				bestPrice = price;
			}
		}
		return best;
	}

	/** How much Combat Power a set of gems gives compared with another: each gem's factor and the gems' Basic Attack Power. */
	static function gemRatio(before:Array<Dynamic>, after:Array<Dynamic>):Float {
		var value:Float = 1;
		var baseBefore:Float = 0;
		var baseAfter:Float = 0;
		for (gem in before) {
			value = value / (1 + gemFactor(gem));
			baseBefore += gem.base;
		}
		for (gem in after) {
			value = value * (1 + gemFactor(gem));
			baseAfter += gem.base;
		}
		return value * (1 + baseAfter / 100) / (1 + baseBefore / 100);
	}

	/** A gem's Combat Power factor (0.064 for +6.40%) for your role, from the game's table. */
	static function gemFactor(gem:Dynamic):Float {
		var found = table();
		if (found == null)
			return 0;
		var key = role == 1 ? 2 : 1;
		var at = 0;
		while (at + 4 < found.length) {
			if (found[at + 1] == GEM && found[at] == key && found[at + 2] == gem.tier && found[at + 3] == gem.level)
				return found[at + 4] / 10000;
			at += 5;
		}
		return 0;
	}

	/** One text per gem, sorted, so two readings compare equal when the same gems are equipped. */
	static function signature(gems:Array<Dynamic>):String {
		var parts:Array<String> = [];
		for (gem in gems) {
			var name:String = gem.name;
			parts.push(name);
		}
		var plain:Dynamic = parts;
		plain.sort();
		return parts.join(";");
	}

	// ---- The profile check ----

	/**
		The check at the left of your Combat Power figure, on two small lines: what Arkive predicted from the gear
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
		check.y = label.y + 4;
		check.width = 100;
		check.height = 34;
		var off = Math.abs(predicted - shown);
		check.textColor = off < MATCH ? CHECK_OK : CHECK_OFF;
		check.text = (off < MATCH ? "Arkive: match" : "Arkive predicted") + NEWLINE + money(predicted);
	}

	// ---- Numbers and text ----

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

	static function font(color:String, text:String):String {
		return "<FONT COLOR='" + color + "'>" + text + "</FONT>";
	}

	// ---- showData ----

	static function showData():Bool {
		return Options.get(TWEAK, "showData", false) == true;
	}

	/** For showData: one grey box at the end of an item tooltip listing each box, what the tweak read, and the profile. */
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
		var parsed = itemOf(list);
		if (parsed != null) {
			var slot:String = parsed.slot;
			var weapon:Float = parsed.weapon;
			var flat:Float = parsed.flat;
			var points:Int = parsed.enlightenment;
			var quality:Int = parsed.quality;
			var refine:Array<Float> = parsed.refine;
			var factors:Array<String> = [];
			for (factor in refine)
				factors.push(money(factor * 100) + "%");
			var shownPoints:String = untyped __global__["String"](points);
			var shownQuality:String = untyped __global__["String"](quality);
			lines.push("Read: slot " + slot + ", weapon " + money(weapon) + ", flat " + money(flat) + ", quality " + shownQuality
				+ ", Enlightenment " + shownPoints + ", refinement " + factors.join(" "));
		}
		var text = "Arkive data" + NEWLINE + lines.join(NEWLINE) + NEWLINE + (profileData == "" ? "(profile not read yet)" : profileData);
		if (text.length > 3500)
			text = text.substr(0, 3500) + " …";
		list.push({type: "SingleTextBox", value: "<FONT SIZE='10' COLOR='#9A9A9A'>" + escape(text).split(NEWLINE).join("<BR>") + "</FONT>", dirty: true});
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

	/** For showData: your role and Combat Power, the profile's stat lists, what the tweak read from your gear, and gems. */
	static function readProfile(label:Dynamic, window:Dynamic, items:Array<Dynamic>, gems:Array<Dynamic>):String {
		var lines:Array<String> = [];
		var shownRole:String = untyped __global__["String"](role);
		lines.push("Profile: role " + shownRole + " (0 DPS, 1 support), Combat Power " + money(power) + ", main stat " + mainStat);
		var page:Dynamic = label.parent;
		var basic:Dynamic = page == null || page.abilityDefaultList == null ? null : page.abilityDefaultList.dataProvider;
		lines.push("Basic: " + rowsOf(basic, "titleStr", "valueStr", "tooltip"));
		var stats:Dynamic = window == null || window.detailPanel == null || window.detailPanel.statPage == null ? null
			: window.detailPanel.statPage.detailStatList.dataProvider;
		lines.push("Detail: " + rowsOf(stats, "statName", "value", "addValue"));
		var stat:Float = 0;
		var weapon:Float = 0;
		var shown:Array<String> = [];
		for (item in items) {
			if (item == null)
				continue;
			var name:String = item.name;
			var itemWeapon:Float = item.weapon;
			var own:Float = mainStat != "" && Text.has(item.stats, mainStat) ? untyped item.stats[mainStat] : 0;
			stat += own;
			weapon += itemWeapon;
			shown.push(name);
		}
		lines.push("Gear read: " + shown.join("; ") + "; main stat total " + money(stat) + ", weapon power total " + money(weapon));
		var names:Array<String> = [];
		for (gem in gems) {
			var name:String = gem.name;
			names.push(name);
		}
		lines.push("Gems: " + names.join(", "));
		return lines.join(NEWLINE);
	}

	static function rowsOf(list:Dynamic, name:String, value:String, extra:String):String {
		if (list == null)
			return "none";
		var shown:Array<String> = [];
		var count:Int = list.length;
		for (i in 0...count) {
			var row:Dynamic = list[i];
			if (row == null)
				continue;
			shown.push(describe(untyped row[name], 3) + "=" + describe(untyped row[value], 3) + " (" + describe(untyped row[extra], 3) + ")");
		}
		return shown.join("; ");
	}

	static function escape(text:String):String {
		return text.split("&").join("&amp;").split("<").join("&lt;").split(">").join("&gt;");
	}
}
