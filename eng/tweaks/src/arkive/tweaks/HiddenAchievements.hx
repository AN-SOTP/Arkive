package arkive.tweaks;

import flash.utils.Dictionary;

/**
	Hidden Achievements Revealed. The Achievements window lists hidden achievements with "Complete the hidden achievement
	to see this info." where the condition goes; the game ships the real text, so before a row draws, this puts the
	condition back in its description, the real objectives in its sub-rows (keeping the game's ticks and counts) and,
	when the game shows no reward icons, the reward after the condition. Everything stays in the game's own labels.

	Rows are matched by name against the hidden achievements Arkive reads from the game's tables at install
	(AchievementTable.cs): "name|condition|shared|reward|objectives", objectives by level split with "^" and within a
	level with "~". A name a shown achievement also uses ("shared") only matches when the row says it is hidden.
**/
@:keep
class HiddenAchievements {
	static inline var TWEAK = "hiddenachievements";

	static var byName:Dictionary;
	static var guide:String;

	/** AchievementNewTaskListItem.setData, before the row draws: the row's data from the game. */
	public static function onSetData(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var data:Dynamic = args[0];
		if (data == null)
			return null;
		load();
		var title:String = Text.has(data, "titleStr") ? Text.trim(Text.stripTags(data.titleStr)) : "";
		var desc:String = Text.has(data, "descStr") ? Text.trim(Text.stripTags(data.descStr)) : "";
		var hidden = guide != "" && desc == guide;
		var sub:Array<Dynamic> = Text.has(data, "subListData") && (untyped __is__(data.subListData, Array)) ? data.subListData : null;
		var found:Array<Dynamic> = untyped byName[title];
		var entry:Array<String> = pick(found, sub);
		if (entry != null && entry[2] == "1" && !hidden)
			entry = null;
		if (entry != null && desc != entry[1])
			reveal(data, entry, sub);
		if (showData() && (hidden || entry != null))
			dump(data, title, desc, sub, entry);
		return null;
	}

	static function reveal(data:Dynamic, entry:Array<String>, sub:Array<Dynamic>):Void {
		var condition = entry[1];
		if (entry[3] != "" && !hasRewardIcon(data))
			condition = condition + "  " + entry[3];
		data.descStr = condition;
		if (sub == null || sub.length == 0 || entry[4] == "")
			return;
		var levels:Array<String> = entry[4].split("^");
		var lines:Array<String>;
		if (sub.length == levels.length) {
			lines = [];
			for (level in levels)
				lines.push(level.split("~").join(", "));
		} else {
			lines = entry[4].split("^").join("~").split("~");
			if (lines.length != sub.length)
				return;
		}
		for (i in 0...sub.length) {
			var row:Dynamic = sub[i];
			if (row != null)
				row.subTitleStr = lines[i];
		}
	}

	/** The hidden achievement with this name; for a name used twice, the one with as many levels as the row has sub-rows. */
	static function pick(found:Array<Dynamic>, sub:Array<Dynamic>):Array<String> {
		if (found == null || found.length == 0)
			return null;
		if (found.length > 1 && sub != null)
			for (entry in found) {
				var parts:Array<String> = entry;
				if (parts[4].split("^").length == sub.length)
					return parts;
			}
		return found[0];
	}

	static function hasRewardIcon(data:Dynamic):Bool {
		for (i in 0...3) {
			var slot:Dynamic = untyped data["rewardSlotData_" + i];
			if (slot != null && Text.has(slot, "iconPath") && slot.iconPath != null && slot.iconPath != "")
				return true;
		}
		return false;
	}

	static function load():Void {
		if (byName != null)
			return;
		byName = new Dictionary();
		var text:Dynamic = Options.get(TWEAK, "guide", "");
		guide = text != null ? Text.trim(text) : "";
		var rows:Array<Dynamic> = Options.get(TWEAK, "hidden", null);
		if (rows == null)
			return;
		for (row in rows) {
			var line:String = row;
			var parts:Array<String> = line.split("|");
			if (parts.length < 5)
				continue;
			var list:Array<Dynamic> = untyped byName[parts[0]];
			if (list == null) {
				list = [];
				untyped byName[parts[0]] = list;
			}
			list.push(parts);
		}
	}

	static function showData():Bool {
		return Options.get(TWEAK, "showData", false) == true;
	}

	/** For testing: what the row came with, as an extra sub-row (seen by expanding the row). */
	static function dump(data:Dynamic, title:String, desc:String, sub:Array<Dynamic>, entry:Array<String>):Void {
		var parts:Array<String> = ["Arkive: title=" + title, "desc=" + desc, "match=" + (entry != null ? "yes" : "no")];
		var count:String = sub == null ? "none" : "" + sub.length;
		parts.push("sub=" + count);
		if (sub != null && sub.length > 0 && sub[0] != null && Text.has(sub[0], "subTitleStr")) {
			var first:String = sub[0].subTitleStr;
			parts.push("sub0=" + first);
		}
		var slot:Dynamic = Text.has(data, "achievementSlotData") ? data.achievementSlotData : null;
		if (slot != null && Text.has(slot, "iconPath")) {
			var icon:String = slot.iconPath;
			var u:String = untyped String(slot.iconU);
			var v:String = untyped String(slot.iconV);
			parts.push("icon=" + icon + " " + u + "," + v);
		}
		parts.push("reward=" + (hasRewardIcon(data) ? "icon" : "none"));
		var rows:Array<Dynamic> = sub != null ? sub : [];
		var last:Dynamic = rows.length > 0 ? rows[rows.length - 1] : null;
		if (last != null && Text.has(last, "arkive"))
			rows.pop();
		rows.push({isClear: false, subTitleStr: parts.join("  "), clearCntStr: "", arkive: true});
		data.subListData = rows;
	}
}
