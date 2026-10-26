package arkive.tweaks;

import flash.events.TimerEvent;
import flash.utils.Dictionary;
import flash.utils.Timer;

/**
	Extended Escape Menu: in the escape menu's content status (the character's weekly list and the roster tree), a row
	under "Paradise" with its power and rank, and a row under "Hourglass" with Sands of Trial as a 0-5 gauge. Both come
	from the tooltip of the row's symbol. The lists keep the game's rows (the "base"); ours are added to a copy.
**/
@:keep
class EscapeMenu {
	static inline var GOLD = "<font color='#ffd200'>";

	/** Per list: the game's own rows, and the copy with our rows that the list shows. */
	static var bases = new Dictionary(true);

	static var shown = new Dictionary(true);

	/** ExitMenuChildContentsPage.configUI: remember the weekly lists' own rows. */
	public static function onPageConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		remember(self.childWeeklyContentsList0);
		remember(self.childWeeklyContentsList1);
		remember(self.childWeeklyContentsList2);
		remember(self.childWeeklyContentsList3);
	}

	/** ExitMenuChildContentsPage.contentsStatusInfo / updateTextPosition: the character's rows were (re)filled. */
	public static function onPageUpdate(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var list:Dynamic = self.childWeeklyContentsList1;
		later(function(event:Dynamic) extend(list, false));
	}

	/** ExitMEnuContentsGroup.configUI: remember the roster tree's own rows. */
	public static function onGroupConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		remember(self.contentsTreeList);
	}

	/** ExitMEnuContentsGroup.setLoading: the roster tree is being (re)filled. */
	public static function onGroupLoading(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var tree:Dynamic = self.contentsTreeList;
		later(function(event:Dynamic) extend(tree, true));
	}

	static function remember(list:Dynamic):Void {
		if (list != null)
			untyped bases[list] = list.dataProvider;
	}

	/** The game fills the rows a moment after these calls: add ours three times, 100 ms apart. */
	static function later(run:Dynamic->Void):Void {
		var timer = new Timer(100, 3);
		timer.addEventListener(TimerEvent.TIMER, run);
		timer.start();
	}

	/** Shows the list's own rows with ours added after each Paradise and Hourglass row. */
	static function extend(list:Dynamic, tree:Bool):Void {
		if (list == null)
			return;
		// The game may have given the list new rows since; those are the base now.
		var current:Dynamic = list.dataProvider;
		var made:Dynamic = untyped shown[list];
		if (current != null && current != made)
			untyped bases[list] = current;
		var base:Dynamic = untyped bases[list];
		if (base == null || base.length == 0)
			return;
		var rollingData:Dynamic = GameUi.definition("ark.data.RollingData");
		if (rollingData == null)
			return;
		var rows:Dynamic = untyped __new__(rollingData);
		var added = false;
		var count:Int = base.length;
		for (i in 0...count) {
			var row:Dynamic = base[i];
			rows.push(row);
			if (row == null || row.isCustom == true)
				continue;
			var name:String = Tooltips.isString(row.nameStr) ? row.nameStr : "";
			var symbol:String = Tooltips.isString(row.symbolTooltip) ? row.symbolTooltip : "";
			if (symbol == "")
				continue;
			if (name.indexOf("Paradise") >= 0) {
				var extra:Dynamic = tree ? treeRow() : weeklyRow();
				var pieces = symbol.split(GOLD);
				var power = pieces.length > 1 ? pieces[1].split("</font>")[0] : "0";
				var rank = pieces.length > 2 ? pieces[2].split("</font>")[0] : "N/A";
				extra.nameStr = "Power: " + GOLD + power + "</font>";
				extra.stateStr = "Rank: " + GOLD + rank + "</font>";
				rows.push(extra);
				added = true;
			} else if (name.indexOf("Hourglass") >= 0) {
				var extra:Dynamic = tree ? treeRow() : weeklyRow();
				// The tooltip starts "<font …>N</font>…": the number of sands is the text of the first tag.
				var close = symbol.indexOf(">");
				var end = symbol.indexOf("<", close + 1);
				var sands = close < 0 ? 0 : Tooltips.leadingInt(symbol.substring(close + 1, end < 0 ? symbol.length : end), 0);
				extra.nameStr = "Sands of Trial";
				extra.maxCount = 5;
				extra.curCount = sands;
				extra.bonusGaugeType = 1;
				extra.bonusGaugeValue = 20 * sands;
				rows.push(extra);
				added = true;
			}
		}
		if (!added)
			return;
		if (tree)
			renumber(rows);
		untyped shown[list] = rows;
		list.dataProvider = rows;
	}

	/** Tree rows point at each other by index (index, parentIndex, childIndex); move those past the inserted rows. */
	static function renumber(rows:Dynamic):Void {
		var moved:Array<Dynamic> = [];
		var count:Int = rows.length;
		for (i in 0...count) {
			var row:Dynamic = rows[i];
			if (!Text.has(row, "index"))
				continue;
			var old:Int = row.index;
			if (old != -1)
				moved[old] = i;
			row.index = i;
		}
		for (i in 0...count) {
			var row:Dynamic = rows[i];
			if (Text.has(row, "parentIndex")) {
				var parent:Int = row.parentIndex;
				var now:Dynamic = parent < 0 ? null : moved[parent];
				if (now != null)
					row.parentIndex = now;
			}
			if (Text.has(row, "childIndex")) {
				var children:Array<Dynamic> = row.childIndex;
				var renumbered:Array<Dynamic> = [];
				for (child in children) {
					var now:Dynamic = moved[child];
					if (now != null)
						renumbered.push(now);
				}
				row.childIndex = renumbered;
			}
		}
	}

	/** An empty row of the character's weekly list (ExitMenuChildContentsWeeklyListItem). */
	static function weeklyRow():Dynamic {
		return {
			curCount: 1, maxCount: 0, stateStr: "", nameStr: "", titleStr: "", emoticonStr: "", emoticonTooltip: "",
			symbolStr: "", symbolTooltip: "", blockStr: "", blockTooltip: "", bonusValue: -1, bonusGaugeType: 0, bonusTooltip: "",
			isNoReward: false, isNoGold: false, isDisabled: false, isTitleBg: false, isCustom: true
		};
	}

	/** An empty row of the roster tree (ExitMenuContentsTreeListItem), a child row with no parent. */
	static function treeRow():Dynamic {
		return {
			curCount: 0, maxCount: 0, stateStr: "", nameStr: "", titleStr: "", tooltip: "", emoticonStr: "", emoticonTooltip: "",
			symbolStr: "", symbolTooltip: "", blockTooltip: "", bonusValue: -1, bonusGaugeType: 0, bonusTooltip: "",
			isNoReward: false, isNoGold: false, isDisabled: false, isShowLine: false, isLog: false, isEnd: false,
			expanded: false, parentIndex: -1, childIndex: [], index: -1, bgType: 1, treeType: 1, isCustom: true
		};
	}
}
