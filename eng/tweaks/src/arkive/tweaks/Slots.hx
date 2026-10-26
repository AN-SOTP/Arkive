package arkive.tweaks;

/**
	Reading and moving the player's item slots through the client's own functions: the tooltip query the game's
	tooltips use, and the drag-end call a drag and drop makes. Slot types: 4 bag, 61 roster storage, 341 equipped gems.
**/
class Slots {
	public static inline var BAG = 4;
	public static inline var ROSTER_STORAGE = 61;
	public static inline var GEMS = 341;

	/**
		Asking for an empty slot's tooltip with a fallback item gives that item's tooltip, so a slot is empty when the
		tooltip names the fallback (an unobtainable test package).
	**/
	static inline var PROBE_ITEM = "40111980";
	static inline var PROBE_NAME = "Alpha Test: Platinum Package";
	static inline var TABLE_ID = 73;
	static inline var SEARCH_DEPTH = 8;

	/** The slot's tooltip entries, or null. */
	static function tooltip(type:Int, id:Int):Array<Dynamic> {
		var manager:Dynamic = GameUi.definition("ark.managers.TooltipManager");
		var tooltips:Dynamic = manager == null ? null : manager.getInstance();
		if (tooltips == null || tooltips.nativeGetTooltip == null)
			return null;
		var query:Dynamic = {slotType: type, slotID: id, tableID: TABLE_ID, bindID: PROBE_ITEM};
		var found:Array<Dynamic> = tooltips.nativeGetTooltip(query, query);
		if (found == null || found.length == 0 || found[0] == null)
			return null;
		return found[0];
	}

	public static function filled(type:Int, id:Int):Bool {
		var entries = tooltip(type, id);
		if (entries == null)
			return false;
		for (entry in entries)
			if (entry != null && entry.type == "NameTagBox" && shows(entry.value, PROBE_NAME.toLowerCase(), 0))
				return false;
		return true;
	}

	/** True when the slot's tooltip shows this text anywhere (any case, tags ignored). */
	public static function tooltipShows(type:Int, id:Int, text:String):Bool {
		var entries = tooltip(type, id);
		return entries != null && shows(entries, text.toLowerCase(), 0);
	}

	static function shows(value:Dynamic, text:String, depth:Int):Bool {
		if (value == null || depth > SEARCH_DEPTH)
			return false;
		var kind:String = untyped __typeof__(value);
		if (kind == "string") {
			var plain:String = Text.stripTags(value);
			return plain.toLowerCase().indexOf(text) >= 0;
		}
		if (kind != "object")
			return false;
		if (untyped __is__(value, Array)) {
			var items:Array<Dynamic> = value;
			for (item in items)
				if (shows(item, text, depth + 1))
					return true;
			return false;
		}
		var keys:Array<String> = untyped __keys__(value);
		for (key in keys)
			if (shows(untyped value[key], text, depth + 1))
				return true;
		return false;
	}

	/** The first empty slot of this type from `from` up to (not including) `count`, or -1. */
	public static function firstEmpty(type:Int, from:Int, count:Int):Int {
		for (id in from...count)
			if (!filled(type, id))
				return id;
		return -1;
	}

	/** Asks the client to move an item as a drag and drop from one slot to the other would; false when it can't ask. */
	public static function move(fromType:Int, fromId:Int, toType:Int, toId:Int):Bool {
		var drag:Dynamic = GameUi.definition("ark.managers.ARKDragManager");
		if (drag == null || drag.nativeSlotDragEnd == null)
			return false;
		drag.nativeSlotDragEnd({slotType: fromType, slotID: fromId}, {slotType: toType, slotID: toId});
		return true;
	}
}
