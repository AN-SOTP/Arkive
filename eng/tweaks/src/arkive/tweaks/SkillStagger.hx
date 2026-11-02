package arkive.tweaks;

import flash.utils.Dictionary;

/**
	Stagger numbers on skill tooltips. The game only shows a grade ("Stagger: Mid"); this adds the stagger each hit
	deals right after it, in the same text, so it keeps the game's font and colour: "Stagger: Mid · 149 per hit", or
	"21 to 107 per hit" for a skill with several hits (hits under a tenth of the biggest are left out), and for frontal
	skills the head attack, "(163 head)". The numbers are what players hit: the skill's base, raised by the stagger
	tripods you picked, then by the +2% every player has (PLAYER_STAT; Kazeros-era raids add 3% more), then × 1.1 for a
	head attack, rounded down at each step. Buffs and other stagger bonuses aren't counted.

	Arkive copies the numbers from data2.lpk into the options (SkillStaggerTable): "grades" (the five labels, Low first),
	"skills" ("id|grade|name|effect:stagger,…", a hit a tripod brings as "effect:stagger:slot/slot") and "tripods"
	("skill|slot|name|effect:percent,…", effect 0 for every hit) and "frontal" (attack type labels of frontal skills).
	A tripod's hits count only when it is picked.

	A skill tooltip is a NameTagBox with the skill's name, a CommonSkillTitle (its kind, "Charge" or "Normal", and icon;
	no id), a SingleTextBox whose description ends in lines like "Weak Point: Lv. 2", "Stagger: High", and a
	TripodSkillCustom list of the picked tripods ({name, desc, lock}). The skill window's tooltip repeats the description
	for the next level. The skill is found by its name, narrowed by the grade the tooltip shows, then by picked tripods.

	The "showData" option (for testing) lists every box of skill tooltips at the end of the tooltip and sends it to
	Arkive's log once per skill.
**/
@:keep
class SkillStagger {
	static inline var TWEAK = "stagger";
	static inline var CHANNEL = "stagger";
	static inline var DOT = " · ";
	static inline var NEWLINE = "\n";
	/** Hits under this share of the skill's biggest hit are left out of the range. */
	static inline var SMALL = 0.1;
	/**
		The stagger every player adds to every hit, which no client table gives (LOA Logs: +2.0-2.1% everywhere, +5% in
		Kazeros-era raids from an endgame buff), so the line shows what players hit outside those raids. 0 shows the
		skill's base numbers instead.
	**/
	static inline var PLAYER_STAT = 0.02;
	/** A head attack (a frontal skill hitting the front) staggers this much more, after the player's stat. */
	static inline var HEAD = 1.1;
	/** How much of a skill tooltip's data the log gets. */
	static inline var LOG_LIMIT = 3000;

	/** Skills by name and by id: {id, grade, effects:Array<Int>, values:Array<Int>}. Null until the options were read. */
	static var byName:Dictionary;
	static var byId:Dictionary;
	/** Tripods by skill id: Array of {name, effects:Array<Int>, percents:Array<Int>}. */
	static var tripods:Dictionary;
	/** The grade labels, grade 1 first; empty ones are left out of the search. */
	static var grades:Array<String>;
	/** The attack type labels of frontal skills ("Attack type: Frontal Attack"), whose hits can be head attacks. */
	static var frontal:Array<String>;
	/** Skill names already sent to Arkive's log by showData. */
	static var logged = new Dictionary();

	/** TooltipDocument.setMainTooltipData, before the game draws it: adds the stagger after a skill's grade. */
	public static function onMainTooltip(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var boxes:Dynamic = args[0];
		if (boxes == null || !(untyped __is__(boxes, Array)))
			return null;
		var list:Array<Dynamic> = boxes;
		try {
			show(list);
		} catch (error:Dynamic) {
			// Reading the error would bring in Haxe's exception wrapper; showData says where it happened.
			if (showData()) {
				Diag.note("stagger: a skill tooltip failed");
				try {
					list.push({type: "SingleTextBox", value: "<FONT SIZE='10' COLOR='#9A9A9A'>Arkive stagger: this tooltip failed</FONT>", dirty: true});
				} catch (again:Dynamic) {}
			}
		}
		return null;
	}

	static function show(list:Array<Dynamic>):Void {
		var title:Dynamic = null;
		var titleAt = -1;
		var nameTag:String = null;
		var skillish = false;
		for (i in 0...list.length) {
			var box:Dynamic = list[i];
			if (box == null || !Tooltips.isString(box.type))
				continue;
			var type:String = box.type;
			if (type.indexOf("Skill") >= 0)
				skillish = true;
			if (type == "NameTagBox" && nameTag == null)
				nameTag = Tooltips.htmlOf(box.value);
			if (type == "CommonSkillTitle" && title == null) {
				title = box.value;
				titleAt = i;
			}
		}
		if (!skillish || !load()) {
			if (skillish && showData())
				addData(list, null, null, "the stagger table isn't in the settings (no data2.lpk when the tweak was installed?)");
			return;
		}
		var labels = findLabels(list);
		var label:Dynamic = labels.length > 0 ? labels[0] : null;
		var grade = label == null ? 0 : (label.grade : Int);
		// The title box's name is the kind of skill ("Charge", "Normal"); the skill's own name is the name tag above it.
		var name = Text.trim(Text.stripTags(nameTag != null ? nameTag : title == null ? null : Tooltips.htmlOf(title.name)));
		var picked = pickedTripods(list);
		var skill:Dynamic = skillFor(title, name, grade, picked);
		var text = skill == null ? null : staggerText(skill, picked, says(list, frontal));
		if (showData())
			addData(list, name, skill, "name '" + name + "', grade shown " + gradeText(grade) + ", labels "
				+ (label == null ? "not found" : "in " + indexText(labels.length) + " box(es), first " + indexText(label.box) + " field " + (label.field : String))
				+ ", tripods in the tooltip: " + picked.join(" / ") + ", line: " + (text == null ? "none" : text));
		if (text == null)
			return;
		// Every grade line gets it: the skill window's tooltip repeats the description for the next level.
		for (found in labels) {
			var holder:Dynamic = found.holder;
			var field:String = found.field;
			var value:String = untyped holder[field];
			var end:Int = found.end;
			// The game can hand the same boxes again; they already have the number then.
			if (value.substr(end, DOT.length) != DOT)
				untyped holder[field] = value.substring(0, end) + DOT + text + value.substring(end);
			var box:Dynamic = list[found.box];
			box.dirty = true;
		}
		if (label != null)
			return;
		// No grade in the text the tweak can see: a line of its own under the skill's title.
		var skillGrade:Int = skill.grade;
		var line = (skillGrade >= 1 && skillGrade <= grades.length ? grades[skillGrade - 1] + DOT : "") + text;
		var html = "<FONT COLOR='#A9A9A9'>" + escape(line) + "</FONT>";
		for (box in list)
			if (box != null && box.arkiveStagger == true) {
				box.value = html;
				box.dirty = true;
				return;
			}
		var boxes:Dynamic = list;
		untyped boxes.splice(titleAt + 1, 0, {type: "SingleTextBox", value: html, dirty: true, arkiveStagger: true});
	}

	/** Reads the tables from the options once; false when they aren't there. */
	static function load():Bool {
		if (byName != null)
			return true;
		var skillRows:Dynamic = Options.get(TWEAK, "skills", null);
		var tripodRows:Dynamic = Options.get(TWEAK, "tripods", null);
		var labels:Dynamic = Options.get(TWEAK, "grades", null);
		if (skillRows == null || labels == null || !(untyped __is__(skillRows, Array)) || !(untyped __is__(labels, Array)))
			return false;
		var names = new Dictionary();
		var ids = new Dictionary();
		var rows:Array<Dynamic> = skillRows;
		for (row in rows) {
			var fields = (row : String).split("|");
			if (fields.length < 4)
				continue;
			var effects:Array<Int> = [];
			var values:Array<Int> = [];
			// The tripod slots that bring each hit; empty for the skill's own hits.
			var owners:Array<Array<Int>> = [];
			for (hit in fields[3].split(",")) {
				var parts = hit.split(":");
				if (parts.length < 2)
					continue;
				effects.push(whole(parts[0]));
				values.push(whole(parts[1]));
				var slots:Array<Int> = [];
				if (parts.length > 2)
					for (slot in parts[2].split("/"))
						slots.push(whole(slot));
				owners.push(slots);
			}
			var skill:Dynamic = {id: whole(fields[0]), grade: whole(fields[1]), name: fields[2], effects: effects, values: values,
				owners: owners};
			untyped ids[fields[0]] = skill;
			var same:Array<Dynamic> = untyped names[fields[2]];
			if (same == null) {
				same = [];
				untyped names[fields[2]] = same;
			}
			same.push(skill);
		}
		var bySkill = new Dictionary();
		if (tripodRows != null && (untyped __is__(tripodRows, Array))) {
			var listed:Array<Dynamic> = tripodRows;
			for (row in listed) {
				var fields = (row : String).split("|");
				if (fields.length < 4)
					continue;
				var effects:Array<Int> = [];
				var percents:Array<Int> = [];
				for (bonus in fields[3].split(",")) {
					var pair = bonus.split(":");
					if (pair.length == 2) {
						effects.push(whole(pair[0]));
						percents.push(whole(pair[1]));
					}
				}
				var own:Array<Dynamic> = untyped bySkill[fields[0]];
				if (own == null) {
					own = [];
					untyped bySkill[fields[0]] = own;
				}
				own.push({slot: whole(fields[1]), name: fields[2], effects: effects, percents: percents});
			}
		}
		var shown:Array<String> = [];
		var given:Array<Dynamic> = labels;
		for (label in given) {
			var text:String = Tooltips.isString(label) ? label : "";
			shown.push(text);
		}
		grades = shown;
		var front:Array<String> = [];
		var listedFront:Dynamic = Options.get(TWEAK, "frontal", null);
		if (listedFront != null && (untyped __is__(listedFront, Array))) {
			var given:Array<Dynamic> = listedFront;
			for (label in given)
				if (Tooltips.isString(label) && (label : String) != "")
					front.push(label);
		}
		frontal = front;
		tripods = bySkill;
		byId = ids;
		byName = names;
		return true;
	}

	/**
		The grade label in each box of the tooltip that has one: {holder, field, end (just after the label), box (its
		index), grade}. Longer labels are tried first, since one label can start another ("Stagger: Mid", "Stagger: Mid-high").
	**/
	static function findLabels(list:Array<Dynamic>):Array<Dynamic> {
		var all:Array<Dynamic> = [];
		var order:Array<Int> = [];
		for (i in 0...grades.length)
			if (grades[i] != "")
				order.push(i);
		// Longest first (a few labels: a plain insertion sort).
		for (i in 1...order.length) {
			var j = i;
			while (j > 0 && grades[order[j]].length > grades[order[j - 1]].length) {
				var kept = order[j];
				order[j] = order[j - 1];
				order[j - 1] = kept;
				j--;
			}
		}
		for (i in 0...list.length) {
			var box:Dynamic = list[i];
			// Tripod descriptions can mention stagger too; the grade is in the skill's own text.
			if (box == null || box.type == "TripodSkillCustom")
				continue;
			var found = labelIn(box, "value", order, 0);
			if (found != null) {
				found.box = i;
				all.push(found);
			}
		}
		return all;
	}

	static function labelIn(holder:Dynamic, field:String, order:Array<Int>, depth:Int):Dynamic {
		var value:Dynamic = untyped holder[field];
		if (value == null || depth > 4)
			return null;
		if (Tooltips.isString(value)) {
			var text:String = value;
			for (grade in order) {
				var at = text.indexOf(grades[grade]);
				if (at >= 0)
					return {holder: holder, field: field, end: at + grades[grade].length, grade: grade + 1, box: -1};
			}
			return null;
		}
		var type:String = untyped __typeof__(value);
		if (type != "object")
			return null;
		if (untyped __is__(value, Array)) {
			var items:Array<Dynamic> = value;
			for (i in 0...items.length) {
				var index:String = untyped __global__["String"](i);
				var found = labelIn(items, index, order, depth + 1);
				if (found != null)
					return found;
			}
			return null;
		}
		var keys:Array<String> = untyped __keys__(value);
		for (key in keys) {
			if (key == "slotData")
				continue;
			var found = labelIn(value, key, order, depth + 1);
			if (found != null)
				return found;
		}
		return null;
	}

	/** Tripod names the tooltip lists (its tripod box, leaving out locked ones). */
	static function pickedTripods(list:Array<Dynamic>):Array<String> {
		var names:Array<String> = [];
		for (box in list) {
			if (box == null || box.type != "TripodSkillCustom" || !(untyped __is__(box.value, Array)))
				continue;
			var items:Array<Dynamic> = box.value;
			for (item in items) {
				if (item == null || item.lock == true)
					continue;
				var name = Tooltips.htmlOf(item.name);
				if (name != null)
					names.push(Text.trim(Text.stripTags(name)));
			}
		}
		return names;
	}

	/**
		The skill this tooltip is for: one whose id is in the title's data (none seen so far), else the ones with the
		tooltip's name (and the grade shown, when it is). Several left with different numbers: the one with a picked tripod, else none.
	**/
	static function skillFor(title:Dynamic, name:String, grade:Int, picked:Array<String>):Dynamic {
		if (title != null) {
			var byData = idIn(title);
			if (byData == null && title.slotData != null)
				byData = idIn(title.slotData);
			if (byData != null)
				return byData;
		}
		if (name == "")
			return null;
		var named:Array<Dynamic> = untyped byName[name];
		if (named == null)
			return null;
		var left:Array<Dynamic> = [];
		for (skill in named)
			if (grade == 0 || skill.grade == grade)
				left.push(skill);
		if (left.length == 0)
			return null;
		var first = staggerText(left[0], picked, false);
		var same = true;
		for (skill in left)
			if (staggerText(skill, picked, false) != first)
				same = false;
		if (same)
			return left[0];
		var withTripod:Array<Dynamic> = [];
		for (skill in left)
			if (pickedOf(skill, picked).length > 0)
				withTripod.push(skill);
		return withTripod.length == 1 ? withTripod[0] : null;
	}

	/** A skill whose id is one of the object's numbers. */
	static function idIn(data:Dynamic):Dynamic {
		var type:String = untyped __typeof__(data);
		if (type != "object" || untyped __is__(data, Array))
			return null;
		var keys:Array<String> = untyped __keys__(data);
		for (key in keys) {
			var value:Dynamic = untyped data[key];
			var kind:String = untyped __typeof__(value);
			if (kind != "number" && kind != "string")
				continue;
			var text:String = untyped __global__["String"](value);
			if (text.length < 4 || text.length > 9)
				continue;
			var skill:Dynamic = untyped byId[text];
			if (skill != null)
				return skill;
		}
		return null;
	}

	/** The skill's tripods (that bring hits or raise stagger) whose names the tooltip lists. */
	static function pickedOf(skill:Dynamic, picked:Array<String>):Array<Dynamic> {
		var id:String = untyped __global__["String"](skill.id);
		var own:Array<Dynamic> = untyped tripods[id];
		var found:Array<Dynamic> = [];
		if (own == null)
			return found;
		for (tripod in own)
			for (name in picked)
				if (name == tripod.name) {
					found.push(tripod);
					break;
				}
		return found;
	}

	/**
		"149 per hit" or "21 to 107 per hit", and for a frontal skill its head attack ("149 per hit (163 head)"): the
		skill's own hits and those of the picked tripods, each raised by the picked tripods' stagger bonuses and then by
		the player's stat, rounded down at each step as the game does; null when none staggers.
	**/
	static function staggerText(skill:Dynamic, picked:Array<String>, front:Bool):String {
		var effects:Array<Int> = skill.effects;
		var values:Array<Int> = skill.values;
		var owners:Array<Array<Int>> = skill.owners;
		var bonuses = pickedOf(skill, picked);
		var hits:Array<Int> = [];
		var most = 0;
		for (i in 0...effects.length) {
			if (owners[i].length > 0 && !brought(owners[i], bonuses))
				continue;
			var percent:Float = 0;
			for (tripod in bonuses) {
				var changes:Array<Int> = tripod.effects;
				var by:Array<Int> = tripod.percents;
				for (j in 0...changes.length)
					if (changes[j] == 0 || changes[j] == effects[i])
						percent += by[j];
			}
			var hit = down(down(values[i] * (1 + percent / 100)) * (1 + PLAYER_STAT));
			hits.push(hit);
			if (hit > most)
				most = hit;
		}
		if (most <= 0)
			return null;
		var low = most;
		for (hit in hits)
			if (hit >= most * SMALL && hit < low)
				low = hit;
		var text = (low == most ? Tooltips.grouped(most) : Tooltips.grouped(low) + " to " + Tooltips.grouped(most)) + " per hit";
		if (!front)
			return text;
		var head:String = Tooltips.grouped(down(most * HEAD));
		return text + (low == most ? " (" + head + " head)" : " (up to " + head + " head)");
	}

	/** Rounded down; a product that should be whole (110 × 1.1) can come out a hair under it in floating point. */
	static function down(value:Float):Int {
		return Math.floor(value + 0.000001);
	}

	/** True when any text of the tooltip (tripods aside) holds one of these labels. */
	static function says(list:Array<Dynamic>, labels:Array<String>):Bool {
		if (labels == null || labels.length == 0)
			return false;
		for (box in list)
			if (box != null && box.type != "TripodSkillCustom" && textHas(box.value, labels, 0))
				return true;
		return false;
	}

	static function textHas(value:Dynamic, labels:Array<String>, depth:Int):Bool {
		if (value == null || depth > 4)
			return false;
		if (Tooltips.isString(value)) {
			var text:String = value;
			for (label in labels)
				if (text.indexOf(label) >= 0)
					return true;
			return false;
		}
		var type:String = untyped __typeof__(value);
		if (type != "object")
			return false;
		if (untyped __is__(value, Array)) {
			var items:Array<Dynamic> = value;
			for (item in items)
				if (textHas(item, labels, depth + 1))
					return true;
			return false;
		}
		var keys:Array<String> = untyped __keys__(value);
		for (key in keys)
			if (key != "slotData" && textHas(untyped value[key], labels, depth + 1))
				return true;
		return false;
	}

	/** True when one of the picked tripods is in one of these slots. */
	static function brought(slots:Array<Int>, tripods:Array<Dynamic>):Bool {
		for (tripod in tripods)
			for (slot in slots)
				if (tripod.slot == slot)
					return true;
		return false;
	}

	static function whole(text:String):Int {
		var value = 0;
		var negative = false;
		for (i in 0...text.length) {
			var c = StringTools.fastCodeAt(text, i);
			if (c == "-".code && i == 0)
				negative = true;
			else if (c >= "0".code && c <= "9".code)
				value = value * 10 + (c - "0".code);
		}
		return negative ? -value : value;
	}

	static function gradeText(grade:Int):String {
		var shown:String = untyped __global__["String"](grade);
		return shown;
	}

	static function indexText(index:Dynamic):String {
		var shown:String = untyped __global__["String"](index);
		return shown;
	}

	// ---- showData ----

	static function showData():Bool {
		return Options.get(TWEAK, "showData", false) == true;
	}

	/**
		For showData: one grey box at the end of a skill tooltip with each box's data and what the tweak matched, also sent
		to Arkive's log the first time each skill is shown.
	**/
	static function addData(list:Array<Dynamic>, name:String, skill:Dynamic, read:String):Void {
		var lines:Array<String> = [];
		for (i in 0...list.length) {
			var box:Dynamic = list[i];
			if (box == null)
				continue;
			if (box.arkiveStaggerData == true)
				return;
			var type:String = box.type;
			lines.push("[" + indexText(i) + "] " + type + ": " + describe(box.value, 0));
		}
		var matched = "none";
		if (skill != null) {
			var hits:Array<Int> = skill.values;
			var shownHits:Array<String> = [];
			for (hit in hits)
				shownHits.push(indexText(hit));
			matched = "skill " + indexText(skill.id) + " grade " + indexText(skill.grade) + " hits " + shownHits.join(",");
		}
		lines.push("Read: " + read + ", matched " + matched);
		var text = "Arkive stagger data" + NEWLINE + lines.join(NEWLINE);
		var key = name == null || name == "" ? text : name;
		if (untyped logged[key] != true) {
			untyped logged[key] = true;
			Bus.send(CHANNEL, text.length > LOG_LIMIT ? text.substr(0, LOG_LIMIT) + " …" : text);
		}
		var shown = text.length > 4000 ? text.substr(0, 4000) + " …" : text;
		list.push({type: "SingleTextBox", value: "<FONT SIZE='10' COLOR='#9A9A9A'>" + escape(shown).split(NEWLINE).join("<BR>") + "</FONT>",
			dirty: true, arkiveStaggerData: true});
	}

	/** A tooltip value as text: strings without tags, arrays as [a | b], objects as {key=value} for a few levels. */
	static function describe(value:Dynamic, depth:Int):String {
		if (value == null)
			return "null";
		var type:String = untyped __typeof__(value);
		if (type == "string") {
			var plain:String = Text.stripTags((value : String).split("<BR>").join(" / ").split("<br>").join(" / "));
			return plain.length > 240 ? plain.substr(0, 240) + "…" : plain;
		}
		if (type != "object" || depth > 4) {
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
			parts.push(key + "=" + describe(untyped value[key], depth + 1));
		return "{" + parts.join(", ") + "}";
	}

	static function escape(text:String):String {
		return text.split("&").join("&amp;").split("<").join("&lt;").split(">").join("&gt;");
	}
}
