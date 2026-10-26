package arkive.tweaks;

/**
	Favorite Raids: a "Favorites" section at the top of the party finder's category tree, holding copies of the raids
	picked in the Tweaks tab (matched by the title the tree shows). The copies are added at the end of the tree's data,
	so the game's own entries keep their indices, and the tree is told to show the Favorites entries first.
**/
@:keep
class FavoriteRaids {
	static inline var TITLE = "Favorites";
	static inline var ICON = 8;

	/** The party finder's category tree; other trees are left alone. */
	static var tree:Dynamic;

	/** The section's own entry and its raids, reused on every redraw so their expanded and selected states stay. */
	static var section:Dynamic = {};
	static var raids:Array<Dynamic> = [];

	static var picked:Array<String>;

	/** FindPartyNewContent.configUI: remember which tree is the party finder's. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		tree = self.findPartyCategoryList;
	}

	/** RollingTreeList.draw, before the game's: put the Favorites section back at the end of the tree's data. */
	public static function onDraw(self:Dynamic, args:Array<Dynamic>):Dynamic {
		if (self == tree)
			addSection(self.dataProvider);
		return null;
	}

	/** RollingTreeList.pushRootIndex, after the game's: list the Favorites entries before the game's. */
	public static function onPushRootIndex(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (self != tree)
			return;
		var shown:Array<Int> = self._filteredIndexDataArray;
		var data:Dynamic = self.dataProvider;
		var first:Array<Int> = [];
		var rest:Array<Int> = [];
		for (index in shown) {
			if (isOurs(data[index]))
				first.push(index);
			else
				rest.push(index);
		}
		// The game keeps this array, so reorder it in place.
		shown.splice(0, shown.length);
		for (index in first)
			shown.push(index);
		for (index in rest)
			shown.push(index);
	}

	/**
		RollingTreeList.getFilteredParentData, instead of the game's: the next shown entry in the order of the shown
		list (the game's walks the data in index order, which would put the Favorites section last).
	**/
	public static function onGetFilteredParentData(self:Dynamic, args:Array<Dynamic>):Dynamic {
		if (self != tree)
			return null;
		var shown:Array<Int> = self._filteredIndexDataArray;
		var current:Int = self._nextIndex;
		var at = shown.indexOf(current);
		if (current != -1 && at == -1)
			return {"return": true, "value": null};
		if (at + 1 >= shown.length)
			return {"return": true, "value": null};
		var next = shown[at + 1];
		self._nextIndex = next;
		return {"return": true, "value": self.dataProvider[next]};
	}

	static function addSection(data:Dynamic):Void {
		if (data == null)
			return;
		// Take off the section added by the last redraw; the game's own entries come before it.
		var count:Int = data.length;
		while (count > 0 && isOurs(data[count - 1]))
			count--;
		data.splice(count, data.length - count);

		var found:Array<Int> = [];
		for (name in pickedRaids()) {
			for (index in 0...count) {
				var entry:Dynamic = data[index];
				var title:String = entry.title;
				if (entry.parentIndex != null && entry.parentIndex >= 0 && title != null && StringTools.trim(Text.stripTags(title)) == name) {
					found.push(index);
					break;
				}
			}
		}
		if (found.length == 0)
			return;

		// The section looks like the category of the first raid, with its own title, icon and children.
		copyFields(data[data[found[0]].parentIndex], section, ["index", "expanded", "selected"]);
		section.title = TITLE;
		section.iconType = ICON;
		section.parentIndex = -1;
		section.highLight = false;
		section.searching = false;
		var children:Array<Int> = [];
		for (i in 0...found.length)
			children.push(count + 1 + i);
		section.childIndex = children;
		if (section.expanded == null)
			section.expanded = false;
		data.push(section);
		for (i in 0...found.length) {
			if (raids.length <= i)
				raids.push({});
			var raid:Dynamic = raids[i];
			copyFields(data[found[i]], raid, ["selected"]);
			raid.parentIndex = count;
			data.push(raid);
		}
	}

	static function isOurs(entry:Dynamic):Bool {
		return entry == section || raids.indexOf(entry) != -1;
	}

	/** The raid titles picked in the Tweaks tab, read once. */
	static function pickedRaids():Array<String> {
		if (picked == null) {
			picked = [];
			var chosen:Dynamic = Options.get("favoriteraids", "raids", null);
			if (chosen != null) {
				var list:Array<Dynamic> = chosen;
				for (name in list)
					if (name != null)
						picked.push(name);
			}
		}
		return picked;
	}

	static function copyFields(from:Dynamic, to:Dynamic, keep:Array<String>):Void {
		var fields:Array<String> = untyped __keys__(from);
		for (field in fields)
			if (keep.indexOf(field) == -1)
				untyped to[field] = from[field];
	}
}
