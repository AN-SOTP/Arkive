package arkive.tweaks;

import flash.events.Event;
import flash.text.TextField;
import flash.text.TextFormat;

/**
	Combat Power on item tooltips. A gem, weapon, armour piece, vambrace, accessory, bracelet or ability stone gets a
	line under its title: how much your Combat Power would change if you equipped it in place of what you wear in that
	slot (for gems your weakest gem of the same kind; for earrings and rings the one of the pair that counts for less),
	green for a gain, red for a loss. An engraving book says what its next five books would add.

	The game's Combat Power is a base attack times one factor per source, each from its BattlePoint rows. Arkive copies
	what it prices from data2.lpk into the options: "rows" (key, type, A, B, C; key 1 DPS, 2 support; values are
	hundredths of a percent), "gemBase" (a gem's Basic Attack Power by level), "engravings" ("Name|key|v0,…,v93", the
	value at 20 × stone level + 1 + 4 × (grade − 1) + level) and "bracelet" (an effect's plain text and its DPS and
	support values). The base is the square root of main stat times weapon power over six, plus flat Basic Atk. Power,
	raised by Basic Attack Power % (gems, the stone's level bonus). Factors priced: gems (type 22), weapon quality (4),
	refinement lines (15 by stat; Outgoing Damage as shown), bracelet stats (19) and effects (20), Enlightenment (6) and
	Leap (7) points, combat stats (26) and engravings with the stone's levels (10). Main stat and weapon power are summed
	over your equipment, which leaves out your character's own stats, so gear lines start with "~" until the profile
	check confirms them. Support characters get gem lines only for now; Ark Grid and paradise orbs aren't priced yet.

	Your Combat Power, DPS or support, equipment, gems, engravings and Ark Passive points are read from your own
	Character Profile (an inspected player's window is ignored), so the lines ask you to open it once. When the profile
	shows a new figure and your gear has changed since the last one, the "showData" option (for testing) puts a note at
	the left of the figure with what Arkive predicted, and lists under every item tooltip what the tweak can read from it
	and from the profile. Tooltip text is matched in English.
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
	static inline var STONE_SLOT = 12;
	static inline var PENALTY = "Atk. Power Reduction";

	/** BattlePoint types. */
	static inline var QUALITY = 4;
	static inline var ENLIGHTENMENT = 6;
	static inline var LEAP = 7;
	static inline var REFINEMENT = 15;
	static inline var BRACELET_STAT = 19;
	static inline var GEM = 22;
	static inline var COMBAT_STAT = 26;

	/** Gem names (English) and the tier and kind each one is. "both" is a gem that can sit in either kind of slot. */
	static var NAMES:Array<String> = ["Annihilation", "Crimson Flame", "Doomfire", "Blazing", "Brilliant"];
	static var TIERS:Array<Int> = [3, 3, 4, 4, 4];
	static var KINDS:Array<String> = ["damage", "cooldown", "damage", "cooldown", "both"];

	/** The profile's equipment slots, in its order. */
	static var SLOTS:Array<String> = ["weapon", "helm", "chest", "pants", "gloves", "shoulder", "necklace", "earring", "earring",
		"ring", "ring", "bracelet", "stone", "orb", "vambrace"];
	/** Words in an item's name that say which slot it goes in (English), checked in this order. */
	static var WORDS:Array<String> = ["Vambrace", "Necklace", "Earring", "Ring", "Bracelet", "Stone", "Orb"];
	static var WORD_SLOTS:Array<String> = ["vambrace", "necklace", "earring", "ring", "bracelet", "stone", "orb"];
	/**
		The last word of armour names (English), by slot: every ending the game's item table uses (Bards and other
		classes wear a Headpiece or Hat and a Shoulderpiece, for example).
	**/
	static var ARMOUR_ENDINGS:Array<String> = ["Headpiece", "Helm", "Hat", "Headguard", "Bandana", "Chestpiece", "Tunic", "Chestguard",
		"Breastplate", "Robe", "Mail", "Armor", "Pants", "Greaves", "Legguards", "Chausses", "Gloves", "Armguards", "Pauldrons",
		"Shoulderpiece", "Amice", "Spaulders", "Shoulderguards", "Shoulders", "Mantle"];
	static var ARMOUR_SLOTS:Array<String> = ["helm", "helm", "helm", "helm", "helm", "chest", "chest", "chest", "chest", "chest", "chest",
		"chest", "pants", "pants", "pants", "pants", "gloves", "gloves", "shoulder", "shoulder", "shoulder", "shoulder", "shoulder",
		"shoulder", "shoulder"];
	/** Slots the tweak doesn't price yet. */
	static var UNPRICED:Array<String> = [];
	static var STATS:Array<String> = ["Strength", "Dexterity", "Intelligence"];
	/** Combat stats that count for a DPS character, and the table's number for each. */
	static var COMBAT_LABELS:Array<String> = ["Crit", "Specialization", "Swiftness"];
	/** Refinement lines and the stat each one is in the game's table (Outgoing Damage, -1, counts as shown). */
	static var REFINE_LABELS:Array<String> = ["Outgoing Damage", "Additional Damage", "Crit Rate", "Crit Damage", "Atk. Power"];
	static var REFINE_STATS:Array<Int> = [-1, 50, 74, 76, 49];
	/** Bracelet stat lines (type 19). */
	static var BRACELET_LABELS:Array<String> = ["Additional Damage", "Crit Rate", "Crit Damage"];
	static var BRACELET_STATS:Array<Int> = [50, 74, 76];
	/** The table's stat for flat Atk. Power, which a refinement line without % adds. */
	static inline var FLAT_ATTACK_STAT = 124;
	/** Astrogem options (English) and their ids in the game's table (type 31: by the grid's total level). */
	static var ASTRO_NAMES:Array<String> = ["Atk. Power", "Additional Damage", "Boss Damage", "Ally Damage Enh.", "Brand Power",
		"Ally Attack Enh."];
	static var ASTRO_IDS:Array<Int> = [2001, 2002, 2003, 2011, 2012, 2013];
	static inline var ASTROGEM_OPTION = 31;
	/** Points a core's steps need, and grade names by the game's grade numbers (3 epic … 6 ancient). */
	static var CORE_STEPS:Array<Int> = [10, 14, 17, 18, 19, 20];
	static var GRADE_NAMES:Array<String> = ["Epic", "Legendary", "Relic", "Ancient"];
	static var GRADE_VALUES:Array<Int> = [3, 4, 5, 6];

	/** Engraving book grades by the word they start with. */
	static var BOOK_GRADES:Array<String> = ["Heroic", "Epic", "Legendary", "Relic"];
	static var BOOK_GRADE_VALUES:Array<Int> = [1, 1, 2, 3];

	/** Your Combat Power as the profile last showed it, or -1. */
	static var power:Float = -1;
	/** 0 DPS, 1 support, -1 not known yet. */
	static var role = -1;
	/** Your equipment by profile slot (parsed items, null where empty) and gems, when the profile last showed your Combat Power. */
	static var gear:Array<Dynamic>;
	static var equipped:Array<Dynamic>;
	/** Your engravings from the profile: {name, grade (1 heroic … 3 relic), level (0-4)}. */
	static var engravings:Array<Dynamic> = [];
	/** Ark Passive points and combat stats that don't come from your gear (profile total minus gear), or -1 when unknown. */
	static var otherEnlightenment:Float = -1;
	static var otherLeap:Float = -1;
	static var otherCombat:Float = -1;
	/** "Strength", "Dexterity" or "Intelligence": the stat your armour has. */
	static var mainStat:String = "";
	static var rows:Array<Int>;
	static var basics:Array<Int>;
	static var engravingRows:Array<String>;
	static var braceletTexts:Array<String>;
	static var braceletValues:Array<Float>;
	static var orbRows:Array<String>;
	/** Your Ark Grid's total level per astrogem option, from the profile's Ark Grid tab; null until it was read. */
	static var gridLevels:Dynamic;
	/** Your Ark Grid's cores: {name, family, grade, points, willpower} from the Ark Grid tab; null until it was read. */
	static var gridCores:Array<Dynamic>;
	static var coreRows:Array<String>;
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
		if (line != null)
			TooltipLines.add(list, TWEAK, line, TooltipLines.COMBAT_POWER);
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
		// The prediction check is for testing only: it shows with the showData option and nowhere else.
		if (!showData() && check != null && check.parent != null)
			check.parent.removeChild(check);
		if (showData() && power > 0 && gear != null && equipped != null
			&& (signature(gems) != signature(equipped) || gearSignature(items) != gearSignature(gear)))
			showCheck(label, power * value(items, gems) / value(gear, equipped), shown);
		power = shown;
		gear = items;
		equipped = gems;
		readEngravings(window);
		readTotals(label.parent, window, items);
		readGrid(window);
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
		var found = priced(list);
		if (found == null)
			return null;
		var note:String = found.note;
		if (note != null)
			return font(DIM, "Combat Power: " + note);
		var change:Float = found.change;
		var rough:Bool = found.rough;
		var against:String = found.against;
		return changeText(change, rough) + " " + font(DIM, against);
	}

	/**
		What equipping an item (or reading five more of an engraving book) would do to your Combat Power, for other
		tweaks too (CP per Gold): null when the tweak doesn't price that kind of item, else {change (a fraction of your
		Combat Power; NaN when it can't compare), note (the short reason when NaN, else null), book (an engraving book,
		where change is its next five books), rough (rests on summed main stat and weapon power), against (what it was
		compared with)}.
	**/
	public static function priced(list:Array<Dynamic>):Dynamic {
		if (list == null)
			return null;
		var gem = gemOf(list);
		if (gem != null)
			return gemPrice(gem);
		var book = bookOf(list);
		if (book != null)
			return bookPrice(book);
		var grid = gridItemOf(list);
		if (grid != null)
			return gridPrice(grid);
		var item = itemOf(list);
		if (item == null || item.slot == "" || UNPRICED.indexOf(item.slot) >= 0)
			return null;
		if (table() == null)
			return unpriced("Arkive couldn't read the game's table", false);
		if (gear == null || role < 0)
			return unpriced("open your Character Profile once to compare", false);
		if (role == 1)
			return unpriced("gear isn't priced for support yet", false);
		// An item you wear in either slot of a pair (earrings, rings) is yours, not one to compare with the other.
		var itemText:String = item.text;
		for (owned in gear) {
			var ownedText:String = owned == null ? "" : owned.text;
			if (owned != null && ownedText == itemText)
				return unpriced("this is what you wear", false);
		}
		var slot = targetSlot(item);
		if (slot < 0)
			return null;
		var worn:Dynamic = gear[slot];
		var after = copyOf(gear);
		after[slot] = item;
		var change = value(after, equipped) / value(gear, equipped) - 1;
		var wornName:String = worn == null ? "" : worn.name;
		var against:String = worn == null ? "in an empty slot" : "vs your " + wornName;
		var combat:Float = item.combat;
		if (otherCombat < 0 && combat > 0)
			against += " (combat stats not counted)";
		return {change: change, note: null, book: false, rough: true, against: against};
	}

	static function unpriced(note:String, book:Bool):Dynamic {
		// Number.NaN, not Haxe's Math.NaN: Haxe's Boot sets that up, and Arkive's empty Boot doesn't.
		var none:Float = untyped __global__["Number"].NaN;
		return {change: none, note: note, book: book, rough: false, against: ""};
	}

	static function gemPrice(gem:Dynamic):Dynamic {
		if (table() == null)
			return unpriced("Arkive couldn't read the game's gem table", false);
		if (equipped == null || role < 0)
			return unpriced("open your Character Profile once to compare", false);
		var target:Dynamic = weakest(gem);
		var after:Array<Dynamic> = [];
		for (owned in equipped)
			if (owned != target)
				after.push(owned);
		after.push(gem);
		var change = gemRatio(equipped, after) - 1;
		var targetName:String = target == null ? "" : target.name;
		var against:String = target == null ? "in an empty slot" : "vs your " + targetName;
		return {change: change, note: null, book: false, rough: false, against: against};
	}

	/** What reading five more books of an engraving you have equipped would add. */
	static function bookPrice(book:Dynamic):Dynamic {
		if (table() == null)
			return unpriced("Arkive couldn't read the game's table", true);
		if (gear == null || role < 0)
			return unpriced("open your Character Profile once to compare", true);
		if (role == 1)
			return unpriced("books aren't priced for support yet", true);
		var name:String = book.engraving;
		var mine:Dynamic = null;
		for (engraving in engravings)
			if (engraving.name == name)
				mine = engraving;
		if (mine == null)
			return unpriced(engravings.length == 0 ? "open your profile's Engravings tab once to compare"
				: "not one of your equipped engravings", true);
		var grade:Int = mine.grade;
		var level:Int = mine.level;
		var bookGrade:Int = book.grade;
		// Books of an engraving's grade raise it to level 4; at level 4 the next grade's books carry on.
		var useful = (bookGrade == grade && level < 4) || (bookGrade == grade + 1 && level == 4);
		if (!useful)
			return unpriced("these books add nothing at your engraving's grade and level", true);
		var stone = stoneLevel(gear[STONE_SLOT], name);
		var now = 20 * stone + index(grade, level);
		var before = engravingValue(name, now);
		var after = engravingValue(name, now + 1);
		var change = (1 + after / 10000) / (1 + before / 10000) - 1;
		return {change: change, note: null, book: true, rough: false, against: "for the next 5 books"};
	}

	/** "Combat Power +12.34 (+0.53%)" in green or red; "~" in front when it rests on summed main stat and weapon power. */
	static function changeText(change:Float, rough:Bool):String {
		// A change that rounds to 0.00% is no change: a grey 0, without sign or "~".
		if (Math.abs(change) < 0.00005)
			return font(DIM, "Combat Power 0");
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
		An item from its tooltip boxes, or null when the tooltip isn't an item's: {slot, name, text, stats {Strength, …},
		weapon, weaponPct, flat, basicPct, quality, refine (factors), effects (factors), combat (Crit + Specialization +
		Swiftness), enlightenment, leap, stone {engraving: level}}. slot is "" when the tweak can't tell where it goes.
	**/
	static function itemOf(boxes:Array<Dynamic>):Dynamic {
		var name:String = null;
		var quality = -1;
		var basic:Array<String> = [];
		var refine:Array<Float> = [];
		var effects:Array<Float> = [];
		var weaponPct:Float = 0;
		var basicPct:Float = 0;
		var combat:Float = 0;
		var enlightenment = 0;
		var leap = 0;
		var stone:Dynamic = {};
		var paradise:Float = 0;
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
			} else if (type == "IndentStringGroup") {
				var found = strings(box.value, [], 0);
				parts.push(found.join(","));
				for (text in found) {
					var line = Text.trim(Text.stripTags(text));
					var close = line.indexOf("] Lv.");
					if (StringTools.startsWith(line, "[") && close > 1) {
						var level = number(line.substr(close + 5));
						untyped stone[line.substring(1, close)] = level < 0 ? 0 : level;
					}
					var bonus = line.indexOf("Basic Atk. Power +");
					if (bonus >= 0 && line.indexOf("%") > bonus)
						basicPct += number(line.substr(bonus + 18));
				}
			} else if (type == "ItemPartBox" && (untyped __is__(box.value, Array))) {
				var part:Array<Dynamic> = box.value;
				if (part.length < 2 || !Tooltips.isString(part[0]) || !Tooltips.isString(part[1]))
					continue;
				var heading = Text.trim(Text.stripTags(part[0]));
				var body = splitLines(part[1]);
				parts.push(heading + ":" + body.join(","));
				for (line in body) {
					// "Final Season 3 Paradise Power: 25,660,633", "Season 4 Max Achieved Paradise Power 34,413,883".
					var at = line.indexOf("Paradise Power");
					if (at >= 0 && number(line.substr(at + 14)) > 0)
						paradise = number(line.substr(at + 14));
				}
				if (heading.indexOf("Refinement") >= 0) {
					for (line in body) {
						if (StringTools.startsWith(line, "Weapon Power +") && line.indexOf("%") >= 0)
							weaponPct += number(line.substr(14));
						var factor = refinement(line);
						if (factor != 0)
							refine.push(factor);
					}
				} else if (heading.indexOf("Bracelet") >= 0) {
					for (line in body) {
						var mainLine = false;
						for (stat in STATS)
							if (StringTools.startsWith(line, stat + " +"))
								mainLine = true;
						if (mainLine) {
							basic.push(line);
							continue;
						}
						var points = combatStat(line);
						if (points > 0) {
							combat += points;
							continue;
						}
						var factor = braceletEffect(line);
						if (factor != 0)
							effects.push(factor);
					}
				} else if (heading.indexOf("Ark Passive") >= 0) {
					for (line in body) {
						if (StringTools.startsWith(line, "Enlightenment +"))
							enlightenment += Math.floor(number(line.substr(15)));
						else if (StringTools.startsWith(line, "Leap +"))
							leap += Math.floor(number(line.substr(6)));
					}
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
		for (i in 0...ARMOUR_ENDINGS.length)
			if (slot == "" && StringTools.endsWith(name, " " + ARMOUR_ENDINGS[i]))
				slot = ARMOUR_SLOTS[i];
		if (slot == "" && weapon > 0 && !hasStat && quality >= 0)
			slot = "weapon";
		// Armour and accessories always have a main stat; a name word alone (a quest item "Ring") isn't gear.
		if (slot != "weapon" && slot != "bracelet" && slot != "stone" && slot != "orb" && !hasStat)
			slot = "";
		return {slot: slot, name: name, text: name + "|" + parts.join("|"), stats: stats, weapon: weapon, weaponPct: weaponPct,
			flat: flat, basicPct: basicPct, quality: slot == "weapon" ? quality : -1, refine: refine, effects: effects,
			combat: combat, enlightenment: enlightenment, leap: leap, stone: stone, paradise: paradise};
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
				return REFINE_STATS[i] == 49 ? amount * weight(REFINEMENT, FLAT_ATTACK_STAT) / 100000000 : 0;
			// The table's stats are in hundredths of a percent, its weights in ten-thousandths.
			return amount * 100 * weight(REFINEMENT, REFINE_STATS[i]) / 100000000;
		}
		return 0;
	}

	/** Crit, Specialization or Swiftness points on a line ("Swiftness +102"), or 0. */
	static function combatStat(line:String):Float {
		for (label in COMBAT_LABELS) {
			var start = label + " +";
			if (StringTools.startsWith(line, start) && line.indexOf("%") < 0)
				return number(line.substr(start.length));
		}
		return 0;
	}

	/** A bracelet line's factor: an effect from the game's bracelet table, else a stat line (type 19); 0 when unpriced. */
	static function braceletEffect(line:String):Float {
		if (braceletTexts == null) {
			braceletTexts = [];
			braceletValues = [];
			var listed:Array<Dynamic> = tableOption("bracelet");
			if (listed != null)
				for (entry in listed) {
					var fields = (entry : String).split("|");
					if (fields.length < 3)
						continue;
					braceletTexts.push(fields[0]);
					braceletValues.push(number(fields[1]));
				}
		}
		var shown = plain(line);
		var at = braceletTexts.indexOf(shown);
		if (at >= 0)
			return braceletValues[at] / 10000;
		for (i in 0...BRACELET_LABELS.length) {
			var label = BRACELET_LABELS[i] + " +";
			if (!StringTools.startsWith(line, label) || line.indexOf("%") < 0)
				continue;
			var rest = line.substr(label.length);
			// A plain stat line has nothing after its number; longer lines are effects the table didn't match.
			if (rest.indexOf(".") >= 0 && rest.indexOf("%") < rest.length - 1)
				continue;
			return number(rest) * 100 * weight(BRACELET_STAT, BRACELET_STATS[i]) / 100000000;
		}
		return 0;
	}

	/** A stat's weight in the game's table for this type (7692 for Additional Damage), or 0. */
	static function weight(type:Int, stat:Int):Float {
		var found = table();
		if (found == null)
			return 0;
		var at = 0;
		while (at + 4 < found.length) {
			if (found[at] == 1 && found[at + 1] == type && found[at + 2] == 1 && found[at + 3] == stat)
				return found[at + 4];
			at += 5;
		}
		return 0;
	}

	/** Every string inside a tooltip value (arrays and objects searched a few levels deep). */
	static function strings(value:Dynamic, into:Array<String>, depth:Int):Array<String> {
		if (value == null || depth > 6)
			return into;
		var type:String = untyped __typeof__(value);
		if (type == "string") {
			into.push(value);
			return into;
		}
		if (type != "object")
			return into;
		var keys:Array<String> = untyped __keys__(value);
		for (key in keys)
			strings(untyped value[key], into, depth + 1);
		return into;
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

	/** Your engravings from the profile's Engravings tab list: name, grade and level. */
	static function readEngravings(window:Dynamic):Void {
		var list:Dynamic = window == null || window.engravePanel == null || window.engravePanel.engraveEquipList == null ? null
			: window.engravePanel.engraveEquipList.dataProvider;
		if (list == null)
			return;
		var found:Array<Dynamic> = [];
		var count:Int = list.length;
		for (i in 0...count) {
			var row:Dynamic = list[i];
			if (row == null || row.label == null)
				continue;
			var name = Text.trim(Text.stripTags(row.label));
			if (name == "")
				continue;
			var grade:Int = row.engraveGrade == null ? 0 : row.engraveGrade;
			var level:Int = row.curActivity == null ? 0 : row.curActivity;
			// The list counts 2 heroic, 3 legendary, 4 relic (a relic Lv. 4 engraving shows as 4/4); the table from 1.
			grade = grade - 1;
			if (grade > 3)
				grade = 3;
			found.push({name: name, grade: grade, level: level});
		}
		if (found.length > 0)
			engravings = found;
	}

	/**
		Ark Passive points and combat stats that don't come from your gear: the profile's totals minus what your gear
		gives. Enlightenment and Leap come from the Ark Passive list on the gear page, combat stats from any profile list
		that names Crit, Specialization and Swiftness. -1 when the profile doesn't show them.
	**/
	static function readTotals(page:Dynamic, window:Dynamic, items:Array<Dynamic>):Void {
		var gearEnlightenment:Float = 0;
		var gearLeap:Float = 0;
		var gearCombat:Float = 0;
		for (item in items) {
			if (item == null)
				continue;
			var e:Float = item.enlightenment;
			var l:Float = item.leap;
			var c:Float = item.combat;
			gearEnlightenment += e;
			gearLeap += l;
			gearCombat += c;
		}
		var passive:Dynamic = window == null || window.equipPanel == null || window.equipPanel.infoPage == null
			|| window.equipPanel.infoPage.arkPassiveGroup == null ? null
			: window.equipPanel.infoPage.arkPassiveGroup.equipInfoArkPassiveList.dataProvider;
		if (passive != null && passive.length >= 3) {
			var enlightened = number(Text.stripTags(passive[1].pointStr));
			var leaped = number(Text.stripTags(passive[2].pointStr));
			if (enlightened >= 0)
				otherEnlightenment = Math.max(0, enlightened - gearEnlightenment);
			if (leaped >= 0)
				otherLeap = Math.max(0, leaped - gearLeap);
		}
		var total = combatTotal(window);
		if (total >= 0)
			otherCombat = Math.max(0, total - gearCombat);
	}

	/**
		Crit + Specialization + Swiftness from the gear page's combat stat list (equipItemAbilityList: titleStr "Crit",
		valueStr "75"), or -1 when it doesn't show all three.
	**/
	static function combatTotal(window:Dynamic):Float {
		var list:Dynamic = combatList(window);
		if (list == null)
			return -1;
		var sum:Float = 0;
		var seen = 0;
		var count:Int = list.length;
		for (i in 0...count) {
			var row:Dynamic = list[i];
			if (row == null || row.titleStr == null || row.valueStr == null)
				continue;
			var title = Text.trim(Text.stripTags(row.titleStr));
			var shown:String = untyped __global__["String"](row.valueStr);
			if (COMBAT_LABELS.indexOf(title) >= 0) {
				var points = number(Text.stripTags(shown));
				if (points >= 0) {
					sum += points;
					seen++;
				}
			}
		}
		return seen == COMBAT_LABELS.length ? sum : -1;
	}

	static function combatList(window:Dynamic):Dynamic {
		return window == null || window.equipPanel == null || window.equipPanel.itemPage == null
			|| window.equipPanel.itemPage.equipItemAbilityList == null ? null
			: window.equipPanel.itemPage.equipItemAbilityList.dataProvider;
	}

	/**
		How much Combat Power a set of equipment and gems gives, up to a constant: base attack (summed main stat, weapon
		power raised by Weapon Power %, flat Basic Atk. Power, then the gems' and stone's Basic Attack Power %) times weapon
		quality, refinement lines, bracelet effects, Enlightenment and Leap points, combat stats, engravings with the
		stone's levels, and each gem.
	**/
	static function value(items:Array<Dynamic>, gems:Array<Dynamic>):Float {
		var stat:Float = 0;
		var weapon:Float = 0;
		var weaponPct:Float = 0;
		var flat:Float = 0;
		var basicPct:Float = 0;
		var enlightenment:Float = otherEnlightenment < 0 ? 0 : otherEnlightenment;
		var leap:Float = otherLeap < 0 ? 0 : otherLeap;
		var combat:Float = otherCombat < 0 ? 0 : otherCombat;
		var factors:Float = 1;
		for (item in items) {
			if (item == null)
				continue;
			stat += mainStatOf(item);
			var itemWeapon:Float = item.weapon;
			weapon += itemWeapon;
			var itemPct:Float = item.weaponPct;
			weaponPct += itemPct;
			var itemFlat:Float = item.flat;
			flat += itemFlat;
			var itemBasic:Float = item.basicPct;
			basicPct += itemBasic;
			var itemEnlightenment:Float = item.enlightenment;
			enlightenment += itemEnlightenment;
			var itemLeap:Float = item.leap;
			leap += itemLeap;
			var itemCombat:Float = item.combat;
			combat += itemCombat;
			var quality:Int = item.quality;
			if (quality >= 0)
				factors *= 1 + qualityBonus(quality);
			var refine:Array<Float> = item.refine;
			for (factor in refine)
				factors *= 1 + factor;
			var effects:Array<Float> = item.effects;
			for (factor in effects)
				factors *= 1 + factor;
			factors *= 1 + orbFactor(item);
		}
		factors *= 1 + enlightenment * perPoint(ENLIGHTENMENT);
		factors *= 1 + leap * perPoint(LEAP);
		if (otherCombat >= 0)
			factors *= 1 + combat * combatPerPoint();
		factors *= engravingFactor(items[STONE_SLOT]);
		var base = stat > 0 && weapon > 0 ? Math.sqrt(stat * weapon * (1 + weaponPct / 100) / 6) + flat : 1;
		var gemBase:Float = 0;
		for (gem in gems) {
			var own:Float = gem.base;
			gemBase += own;
		}
		return base * (1 + (gemBase + basicPct) / 100) * factors * gemRatio([], gems) / (1 + gemBase / 100);
	}

	/**
		An item's main stat for you: your class's stat, or for another class's gear the stat it has instead (the same
		item for your class would have the same number).
	**/
	static function mainStatOf(item:Dynamic):Float {
		var stats:Dynamic = item.stats;
		if (mainStat != "" && Text.has(stats, mainStat))
			return untyped stats[mainStat];
		for (stat in STATS)
			if (Text.has(stats, stat))
				return untyped stats[stat];
		return 0;
	}

	/** Your engravings' factors with this stone's levels, and the stone's Atk. Power Reduction. */
	static function engravingFactor(stone:Dynamic):Float {
		var factor:Float = 1;
		for (engraving in engravings) {
			var name:String = engraving.name;
			var grade:Int = engraving.grade;
			var level:Int = engraving.level;
			factor *= 1 + engravingValue(name, 20 * stoneLevel(stone, name) + index(grade, level)) / 10000;
		}
		var penalty = stoneLevel(stone, PENALTY);
		if (penalty > 0)
			factor *= 1 + engravingValue(PENALTY, penalty) / 10000;
		return factor;
	}

	/** The table's index for an engraving's grade (1 heroic … 3 relic) and level (0-4). */
	static function index(grade:Int, level:Int):Int {
		if (grade < 1)
			return 0;
		return 1 + 4 * (grade - 1) + level;
	}

	static function stoneLevel(stone:Dynamic, name:String):Int {
		if (stone == null || stone.stone == null || !Text.has(stone.stone, name))
			return 0;
		var level:Float = untyped stone.stone[name];
		return Math.floor(level);
	}

	/** An engraving's value at an index for your role, from the "engravings" option, or 0. */
	static function engravingValue(name:String, at:Int):Float {
		if (engravingRows == null) {
			engravingRows = [];
			var listed:Array<Dynamic> = tableOption("engravings");
			if (listed != null)
				for (entry in listed)
					engravingRows.push(entry);
		}
		var key:String = role == 1 ? "2" : "1";
		var start = name + "|" + key + "|";
		for (entry in engravingRows) {
			if (!StringTools.startsWith(entry, start))
				continue;
			var values = entry.substr(start.length).split(",");
			return at >= 0 && at < values.length ? number(values[at]) * (values[at].indexOf("-") == 0 ? -1 : 1) : 0;
		}
		return 0;
	}

	/**
		A paradise orb's factor: its cap (type 33's C) times how far its paradise power is towards B million, my reading
		of the table (C is 40 × B for most orbs); support (type 34) has a fixed value in B. 0 for other items.
	**/
	static function orbFactor(item:Dynamic):Float {
		var slot:String = item.slot;
		if (slot != "orb")
			return 0;
		if (orbRows == null) {
			orbRows = [];
			var listed:Array<Dynamic> = tableOption("orbs");
			if (listed != null)
				for (entry in listed)
					orbRows.push(entry);
		}
		var name:String = item.name;
		var start:String = name + "|" + (role == 1 ? "2" : "1") + "|";
		for (entry in orbRows) {
			if (!StringTools.startsWith(entry, start))
				continue;
			var fields = entry.substr(start.length).split("|");
			var b = number(fields[0]);
			var c = fields.length > 1 ? number(fields[1]) : 0;
			if (c <= 0)
				return b / 10000;
			var power:Float = item.paradise;
			return b <= 0 ? 0 : c / 10000 * Math.min(1, power / (b * 1000000));
		}
		return 0;
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

	/** Combat Power per point of Crit, Specialization or Swiftness (0.0003). */
	static function combatPerPoint():Float {
		var found = table();
		if (found == null)
			return 0;
		var at = 0;
		while (at + 4 < found.length) {
			if (found[at] == 1 && found[at + 1] == COMBAT_STAT)
				return found[at + 3] / 10000;
			at += 5;
		}
		return 0;
	}

	/** A table Arkive copied into the options: Combat Power's own, or CP per Gold's when only that tweak is on. */
	static function tableOption(key:String):Dynamic {
		var found:Dynamic = Options.get(TWEAK, key, null);
		return found != null ? found : Options.get("cpgold", key, null);
	}

	static function table():Array<Int> {
		if (rows == null)
			rows = tableOption("rows");
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

	/** Text as the bracelet table has it: tags, whitespace, slashes and commas removed, lower-cased. */
	static function plain(text:String):String {
		var kept:Array<String> = [];
		var stripped = Text.stripTags(text);
		for (i in 0...stripped.length) {
			var c = StringTools.fastCodeAt(stripped, i);
			if (c == " ".code || (c > 8 && c < 14) || c == "/".code || c == ",".code || c == 160)
				continue;
			kept.push(stripped.charAt(i));
		}
		return kept.join("").toLowerCase();
	}

	static function copyOf(items:Array<Dynamic>):Array<Dynamic> {
		var raw:Dynamic = items;
		return raw.concat();
	}

	// ---- Ark Grid ----

	/** An Ark Grid core or astrogem: {kind "core"|"astrogem", name, levels {option: level}}, or null. */
	static function gridItemOf(boxes:Array<Dynamic>):Dynamic {
		var name:String = null;
		for (box in boxes)
			if (box != null && box.type == "NameTagBox") {
				name = Text.trim(Text.stripTags(Tooltips.htmlOf(box.value)));
				break;
			}
		if (name == null)
			return null;
		var kind = name.indexOf(" Core:") > 0 ? "core" : (name.indexOf("Astrogem:") >= 0 ? "astrogem" : "");
		if (kind == "")
			return null;
		var levels:Dynamic = {};
		var points:Float = 0;
		var willpower:Float = 0;
		var supply:Float = 0;
		var grade = 0;
		for (box in boxes) {
			if (box == null)
				continue;
			if (box.type == "ItemTitle" && box.value != null && Tooltips.isString(box.value.leftStr0)) {
				var shown = Text.trim(Text.stripTags(box.value.leftStr0));
				var at = GRADE_NAMES.indexOf(shown);
				if (at >= 0)
					grade = GRADE_VALUES[at];
			}
			if (box.type != "ItemPartBox")
				continue;
			var heading = "";
			if ((untyped __is__(box.value, Array)) && Tooltips.isString(box.value[0]))
				heading = Text.trim(Text.stripTags(box.value[0]));
			for (text in strings(box.value, [], 0))
				for (line in splitLines(text)) {
					addLevel(levels, line);
					if (StringTools.startsWith(line, "Order Points:") || StringTools.startsWith(line, "Chaos Points:"))
						points += number(line.substr(13));
					else if (StringTools.startsWith(line, "Required Willpower:"))
						willpower += number(line.substr(19));
					else if (heading.indexOf("Willpower") >= 0 && line.indexOf("Points") >= 0)
						supply = number(line);
				}
		}
		var family = kind == "core" ? name.split(":")[0] : "";
		return {kind: kind, name: name, levels: levels, points: points, willpower: willpower, supply: supply, grade: grade,
			family: family};
	}

	/** Point steps a core lights: 10, 14, 17, 18, 19 and 20 points of its astrogems. */
	static function coreSteps(points:Float):Int {
		var steps = 0;
		for (need in CORE_STEPS)
			if (points >= need)
				steps++;
		return steps;
	}

	/** A core's value with this many steps lit, from the "cores" option (by its name, else its kind), for your role. */
	static function coreValue(name:String, family:String, grade:Int, steps:Int):Float {
		if (steps <= 0)
			return 0;
		if (coreRows == null) {
			coreRows = [];
			var listed:Array<Dynamic> = tableOption("cores");
			if (listed != null)
				for (entry in listed)
					coreRows.push(entry);
		}
		var shownGrade:String = untyped __global__["String"](grade);
		var tail:String = "|" + shownGrade + "|" + (role == 1 ? "2" : "1") + "|";
		var starts:Array<String> = [name + tail, family + tail];
		for (start in starts)
			for (entry in coreRows) {
				if (!StringTools.startsWith(entry, start))
					continue;
				var values = entry.substr(start.length).split(",");
				// A core of a low grade has no options past its first steps; it keeps the value of the last one it has.
				var best:Float = 0;
				for (i in 0...values.length)
					if (i < steps && number(values[i]) > 0)
						best = number(values[i]);
				return best;
			}
		return 0;
	}

	/** Reads "[Atk. Power] Lv. 3" (an astrogem) or "Atk. Power Lv.12" (the grid's totals) into levels. */
	static function addLevel(levels:Dynamic, line:String):Void {
		var at = line.indexOf("Lv.");
		if (at <= 0)
			return;
		var option = Text.trim(line.substring(0, at).split("[").join("").split("]").join(""));
		if (ASTRO_NAMES.indexOf(option) < 0)
			return;
		var level = number(line.substr(at + 3));
		if (level > 0) {
			var had:Float = Text.has(levels, option) ? untyped levels[option] : 0;
			untyped levels[option] = had + level;
		}
	}

	/**
		An astrogem: what its options add on top of your grid's totals (the table prices each option by the grid's total
		level). A core's worth depends on how many of its point steps your astrogems light up, which the tweak can't read
		yet, so cores say what's missing.
	**/
	static function gridPrice(grid:Dynamic):Dynamic {
		if (table() == null)
			return unpriced("Arkive couldn't read the game's table", false);
		if (role < 0)
			return unpriced("open your Character Profile once to compare", false);
		var kind:String = grid.kind;
		if (kind == "core")
			return corePrice(grid);
		if (gridLevels == null)
			return unpriced("open your profile's Ark Grid tab once to compare", false);
		var change:Float = 1;
		var levels:Dynamic = grid.levels;
		var keys:Array<String> = untyped __keys__(levels);
		for (option in keys) {
			var id = ASTRO_IDS[ASTRO_NAMES.indexOf(option)];
			var have:Float = Text.has(gridLevels, option) ? untyped gridLevels[option] : 0;
			var adds:Float = untyped levels[option];
			var before = astroValue(id, Math.floor(have));
			var after = astroValue(id, Math.floor(have + adds));
			change *= (1 + after / 10000) / (1 + before / 10000);
		}
		return {change: change - 1, note: null, book: false, rough: true, against: "on top of your Ark Grid"};
	}

	/**
		A core in place of yours of the same kind (Order Sun, Chaos Moon …): both priced with the steps your astrogems in
		that slot light now. Needs the profile's Ark Grid tab, which lists your cores and their astrogems.
	**/
	static function corePrice(core:Dynamic):Dynamic {
		if (gridCores == null)
			return unpriced("open your profile's Ark Grid tab once to compare", false);
		var family:String = core.family;
		var mine:Dynamic = null;
		for (owned in gridCores)
			if (owned.family == family)
				mine = owned;
		var points:Float = mine == null ? 0 : mine.points;
		var steps = coreSteps(points);
		var name:String = core.name;
		var grade:Int = core.grade;
		var after = coreValue(name, family, grade, steps);
		var before:Float = 0;
		if (mine != null) {
			var mineName:String = mine.name;
			var mineGrade:Int = mine.grade;
			before = coreValue(mineName, family, mineGrade, steps);
		}
		var change = (1 + after / 10000) / (1 + before / 10000) - 1;
		var wornName:String = mine == null ? "" : mine.name;
		var against:String = (mine == null ? "in an empty slot" : "vs your " + wornName) + " at your " + money(points).split(".")[0]
			+ " points";
		// A higher grade often adds only its later steps (17P and up), so say what it gives once your astrogems reach them.
		var best = change;
		var bestPoints = 0;
		for (i in 0...CORE_STEPS.length) {
			var need = CORE_STEPS[i];
			if (need <= points)
				continue;
			var beforeThen:Float = 0;
			if (mine != null) {
				var mineName:String = mine.name;
				var mineGrade:Int = mine.grade;
				beforeThen = coreValue(mineName, family, mineGrade, i + 1);
			}
			var then = (1 + coreValue(name, family, grade, i + 1) / 10000) / (1 + beforeThen / 10000) - 1;
			if (then > best + 0.00005) {
				best = then;
				bestPoints = need;
			}
		}
		if (bestPoints > 0) {
			var shownPoints:String = untyped __global__["String"](bestPoints);
			against += "; " + (power > 0 ? signed(power * best, "") + " (" + signed(best * 100, "%") + ")" : signed(best * 100, "%"))
				+ " at " + shownPoints + " points";
		}
		var supply:Float = core.supply;
		var needs:Float = mine == null ? 0 : mine.willpower;
		if (supply > 0 && needs > supply)
			against += " (your astrogems there need more willpower than it supplies)";
		return {change: change, note: null, book: false, rough: false, against: against};
	}

	/** An astrogem option's value at a total level for your role (type 31), or 0. */
	static function astroValue(id:Int, level:Int):Float {
		var found = table();
		if (found == null || level <= 0)
			return 0;
		var key = role == 1 ? 2 : 1;
		var best:Float = 0;
		var bestLevel = 0;
		var at = 0;
		while (at + 4 < found.length) {
			if (found[at] == key && found[at + 1] == ASTROGEM_OPTION && found[at + 2] == id && found[at + 3] <= level
				&& found[at + 3] > bestLevel) {
				best = found[at + 4];
				bestLevel = found[at + 3];
			}
			at += 5;
		}
		return best;
	}

	/** Your grid's option totals from the Ark Grid tab's level list ("Atk. Power Lv.12"). */
	static function readGrid(window:Dynamic):Void {
		var grid:Dynamic = window == null ? null : window.arkGridPanel;
		var info:Dynamic = grid == null || grid.imageView == null ? null : grid.imageView.preResource;
		var list:Dynamic = info == null || info.arkGridAbilityList == null ? null : info.arkGridAbilityList.dataProvider;
		if (list != null && list.length > 0) {
			var levels:Dynamic = {};
			var count:Int = list.length;
			for (i in 0...count) {
				var row:Dynamic = list[i];
				if (row != null && row.label != null)
					addLevel(levels, Text.trim(Text.stripTags(row.label)));
			}
			gridLevels = levels;
		}
		// Each core with the points and willpower of its astrogems, read through their slots' own tooltips.
		var rows:Dynamic = grid == null || grid.arkGridList == null ? null : grid.arkGridList.dataProvider;
		if (rows == null || rows.length == 0)
			return;
		var cores:Array<Dynamic> = [];
		var rowCount:Int = rows.length;
		for (i in 0...rowCount) {
			var row:Dynamic = rows[i];
			if (row == null || row.slotData == null)
				continue;
			var boxes = Tooltips.boxes(row.slotData, row.slotData);
			var core:Dynamic = boxes == null ? null : gridItemOf(boxes);
			if (core == null || core.kind != "core")
				continue;
			var points:Float = 0;
			var willpower:Float = 0;
			var gems:Array<Dynamic> = row.gemSlotData == null ? [] : row.gemSlotData;
			for (data in gems) {
				if (data == null)
					continue;
				var gemBoxes = Tooltips.boxes(data, data);
				var gem:Dynamic = gemBoxes == null ? null : gridItemOf(gemBoxes);
				if (gem == null)
					continue;
				var gemPoints:Float = gem.points;
				var gemWillpower:Float = gem.willpower;
				points += gemPoints;
				willpower += gemWillpower;
			}
			core.points = points;
			core.willpower = willpower;
			cores.push(core);
		}
		if (cores.length > 0)
			gridCores = cores;
	}

	// ---- Engraving books ----

	/** An engraving book ("Relic Mass Increase Engraving Recipe [X 2]"): {engraving, grade}, or null. */
	static function bookOf(boxes:Array<Dynamic>):Dynamic {
		var name:String = null;
		for (box in boxes)
			if (box != null && box.type == "NameTagBox") {
				name = Text.trim(Text.stripTags(Tooltips.htmlOf(box.value)));
				break;
			}
		if (name == null)
			return null;
		var end = name.indexOf(" Engraving Recipe");
		if (end < 0)
			return null;
		for (i in 0...BOOK_GRADES.length) {
			var word = BOOK_GRADES[i] + " ";
			if (StringTools.startsWith(name, word))
				return {engraving: name.substring(word.length, end), grade: BOOK_GRADE_VALUES[i]};
		}
		return null;
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
			basics = tableOption("gemBase");
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
			var weaponPct:Float = parsed.weaponPct;
			var flat:Float = parsed.flat;
			var basicPct:Float = parsed.basicPct;
			var combat:Float = parsed.combat;
			var points:Int = parsed.enlightenment;
			var leap:Int = parsed.leap;
			var quality:Int = parsed.quality;
			var refine:Array<Float> = parsed.refine;
			var effects:Array<Float> = parsed.effects;
			var factors:Array<String> = [];
			for (factor in refine)
				factors.push(money(factor * 100) + "%");
			var bracelet:Array<String> = [];
			for (factor in effects)
				bracelet.push(money(factor * 100) + "%");
			var shownPoints:String = untyped __global__["String"](points);
			var shownLeap:String = untyped __global__["String"](leap);
			var shownQuality:String = untyped __global__["String"](quality);
			lines.push("Read: slot " + slot + ", weapon " + money(weapon) + " +" + money(weaponPct) + "%, flat " + money(flat)
				+ ", basic " + money(basicPct) + "%, quality " + shownQuality + ", Enlightenment " + shownPoints + ", Leap " + shownLeap
				+ ", combat " + money(combat) + ", refinement " + factors.join(" ") + ", bracelet " + bracelet.join(" ")
				+ ", stone " + describe(parsed.stone, 0));
		}
		var text = "Arkive data" + NEWLINE + lines.join(NEWLINE) + NEWLINE + (profileData == "" ? "(profile not read yet)" : profileData);
		if (text.length > 4000)
			text = text.substr(0, 4000) + " …";
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

	/** For showData: what the tweak read from the profile. */
	static function readProfile(label:Dynamic, window:Dynamic, items:Array<Dynamic>, gems:Array<Dynamic>):String {
		var lines:Array<String> = [];
		var shownRole:String = untyped __global__["String"](role);
		lines.push("Profile: role " + shownRole + " (0 DPS, 1 support), Combat Power " + money(power) + ", main stat " + mainStat);
		lines.push("Other than gear: Enlightenment " + money(otherEnlightenment) + ", Leap " + money(otherLeap) + ", combat stats "
			+ money(otherCombat) + " (-1 = not found)");
		var page:Dynamic = label.parent;
		var basic:Dynamic = page == null || page.abilityDefaultList == null ? null : page.abilityDefaultList.dataProvider;
		lines.push("Basic: " + rowsOf(basic, "titleStr", "valueStr", "tooltip"));
		var passive:Dynamic = window == null || window.equipPanel == null || window.equipPanel.infoPage == null
			|| window.equipPanel.infoPage.arkPassiveGroup == null ? null
			: window.equipPanel.infoPage.arkPassiveGroup.equipInfoArkPassiveList.dataProvider;
		lines.push("Ark Passive: " + rowsOf(passive, "iconType", "pointStr", "stigmaStr"));
		var engraved:Dynamic = window == null || window.engravePanel == null || window.engravePanel.engraveEquipList == null ? null
			: window.engravePanel.engraveEquipList.dataProvider;
		lines.push("Engravings: " + rowsOf(engraved, "label", "engraveGrade", "curActivity"));
		var read:Array<String> = [];
		for (engraving in engravings) {
			var name:String = engraving.name;
			var grade:String = untyped __global__["String"](engraving.grade);
			var level:String = untyped __global__["String"](engraving.level);
			read.push(name + " " + grade + "/" + level);
		}
		lines.push("Engravings read: " + read.join(", "));
		lines.push("Combat stats: " + rowsOf(combatList(window), "titleStr", "valueStr", "tooltip"));
		lines.push("Page lists: " + pageLists(page, 0));
		var grid:Dynamic = window == null ? null : window.arkGridPanel;
		var cores:Dynamic = grid == null || grid.arkGridList == null ? null : grid.arkGridList.dataProvider;
		var info:Dynamic = grid == null || grid.imageView == null ? null : grid.imageView.preResource;
		var read:Array<String> = [];
		if (gridCores != null)
			for (core in gridCores) {
				var name:String = core.name;
				var grade:String = untyped __global__["String"](core.grade);
				var points:Float = core.points;
				read.push(name + " grade " + grade + " " + money(points) + "P");
			}
		lines.push("Ark Grid cores read: " + read.join(", "));
		lines.push("Ark Grid cores: " + listOf(cores));
		lines.push("Ark Grid effects: " + listOf(info == null || info.arkGridEffectList == null ? null : info.arkGridEffectList.dataProvider));
		lines.push("Ark Grid levels: " + listOf(info == null || info.arkGridAbilityList == null ? null : info.arkGridAbilityList.dataProvider));
		var stat:Float = 0;
		var weapon:Float = 0;
		for (item in items) {
			if (item == null)
				continue;
			var itemWeapon:Float = item.weapon;
			stat += mainStatOf(item);
			weapon += itemWeapon;
		}
		lines.push("Gear: main stat total " + money(stat) + ", weapon power total " + money(weapon));
		var names:Array<String> = [];
		for (gem in gems) {
			var name:String = gem.name;
			names.push(name);
		}
		lines.push("Gems: " + names.join(", "));
		return lines.join(NEWLINE);
	}

	/** For showData: every list under the page, by name, with its first rows. */
	static function pageLists(container:Dynamic, depth:Int):String {
		if (container == null || depth > 3 || !(untyped __is__(container, flash.display.DisplayObjectContainer)))
			return "";
		var found:Array<String> = [];
		var data:Dynamic = (untyped __in__("dataProvider", container)) ? container.dataProvider : null;
		if (data != null && (untyped __is__(data, Array))) {
			var name:String = container.name;
			var rows:Array<String> = [];
			var count:Int = data.length;
			for (i in 0...count)
				if (i < 8)
					rows.push(describe(data[i], 2));
			found.push(name + " " + rows.join(" "));
		}
		var children:Int = container.numChildren;
		for (i in 0...children) {
			var inner = pageLists(container.getChildAt(i), depth + 1);
			if (inner != "")
				found.push(inner);
		}
		return found.join("; ");
	}

	/** For showData: each row of a list, a few levels deep. */
	static function listOf(list:Dynamic):String {
		if (list == null)
			return "none";
		var shown:Array<String> = [];
		var count:Int = list.length;
		for (i in 0...count)
			shown.push(describe(list[i], 1));
		return shown.join("; ");
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
