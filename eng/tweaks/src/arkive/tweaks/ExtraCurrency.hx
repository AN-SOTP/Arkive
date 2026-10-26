package arkive.tweaks;

import flash.events.Event;
import flash.events.TimerEvent;
import flash.utils.Dictionary;
import flash.utils.Timer;

/**
	Additional Top Currencies: progression materials after the currencies in the top bar, each in the bar's own
	currency item with the material's icon and the amount its item tooltip shows. Amounts are read once a second; a
	material the tooltip shows no amount for stays hidden.
**/
@:keep
class ExtraCurrency {
	/** Option key, item id and name (the item's tooltip in the bar), in the order they show. */
	static var MATERIALS:Array<Dynamic> = [
		{option: "showGuardianStones", id: "66102117", name: "Destiny Crystallized Guardian Stone (Bound)"},
		{option: "showDestructionStones", id: "66102017", name: "Destiny Crystallized Destruction Stone (Bound)"},
		{option: "showLeapstones", id: "66110026", name: "Great Destiny Leapstone (Bound)"},
		{option: "showFusionMaterials", id: "6861513", name: "Superior Abidos Fusion Material (Bound)"},
		{option: "showCoreEssence", id: "67300002", name: "Core Essence"},
		{option: "showGracedShards", id: "52327001", name: "Graced Shard"},
	];

	static inline var ICON_SCALE = 0.75;

	static var container:Dynamic;
	static var itemClass:Dynamic;
	static var entries:Array<Dynamic> = [];
	static var timer:Timer;

	/** The material icon drawn over each of our items' coin icon, and the icon it shows ("path|u|v|w|h"). */
	static var icons = new Dictionary(true);

	static var iconKeys = new Dictionary(true);

	/** TopHUDContentsContainer.configUI: add an item for each chosen material after the game's currency list. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var list:Dynamic = self.currencyList;
		if (list == null)
			return;
		itemClass = GameUi.definition("TopHUDCurrencyListItem");
		if (itemClass == null)
			return;
		container = self;
		entries = [];
		var parent:Dynamic = list.parent;
		var at:Int = parent.getChildIndex(list);
		for (material in MATERIALS) {
			if (!GameUi.on("extracurrency", material.option))
				continue;
			var view:Dynamic = untyped __new__(itemClass);
			view.visible = false;
			at++;
			parent.addChildAt(view, at);
			var entry:Dynamic = {id: material.id, name: material.name, view: view, count: -1, icon: ""};
			view.setData(dataFor(entry, null));
			entries.push(entry);
		}
		if (entries.length == 0)
			return;
		list.addEventListener(Event.ENTER_FRAME, onFrame);
		if (timer == null) {
			timer = new Timer(1000);
			timer.addEventListener(TimerEvent.TIMER, onTick);
			timer.start();
		}
	}

	/**
		TopHUDContentsContainer.getTotalW: count our shown items in the way the game counts its own (each item's
		background), so the bar's background ends after them as it does after the game's currencies.
	**/
	public static function onGetTotalW(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Dynamic {
		if (self != container)
			return null;
		var total:Float = result;
		for (entry in entries) {
			if (entry.count == -1 || entry.view.visible != true)
				continue;
			var background:Dynamic = entry.view.background;
			var width:Float = background != null ? background.width : entry.view.listWidth + 15;
			if (width > 0)
				total += width;
		}
		return {value: total};
	}

	/** TopHUDCurrencyListItem.setData: on our items, show the material's icon in place of the coin. */
	public static function onSetData(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var data:Dynamic = args[0];
		var coin:Dynamic = self.moneyIcon;
		if (coin == null)
			return;
		var extra:Dynamic = untyped icons[self];
		var key = data != null && data.arkive == true ? iconKey(data) : "";
		if (key == "") {
			coin.visible = true;
			if (extra != null)
				extra.visible = false;
			return;
		}
		var size:Float = coin.width * ICON_SCALE;
		if (extra == null) {
			var iconClass:Dynamic = GameUi.definition("ark.controls.ExtendIcon");
			if (iconClass == null)
				return;
			extra = untyped __new__(iconClass);
			extra.iconType = "Type_NORMAL";
			untyped icons[self] = extra;
			var parent:Dynamic = coin.parent;
			parent.addChildAt(extra, parent.getChildIndex(coin) + 1);
		}
		var shown:Dynamic = untyped iconKeys[self];
		if (shown != key) {
			untyped iconKeys[self] = key;
			extra.canvasWidth = size;
			extra.canvasHeight = size;
			extra.loadImageEx(data.iconPath, data.iconU, data.iconV, data.iconWidth, data.iconHeight, false);
		}
		extra.x = coin.x + (coin.width - size) / 2;
		extra.y = coin.y + (coin.height - size) / 2 - 2;
		extra.visible = true;
		coin.visible = false;
	}

	/** Once a second: read each material's amount and icon, and redraw the items that changed. */
	static function onTick(event:Dynamic):Void {
		var changed = false;
		for (entry in entries) {
			var info:Dynamic = Tooltips.item(entry.id);
			var slot:Dynamic = info == null ? null : info.slotData;
			var count:Int = info == null ? -1 : info.count;
			var icon = iconKey(slot);
			if (count == entry.count && icon == entry.icon)
				continue;
			entry.count = count;
			entry.icon = icon;
			entry.view.setData(dataFor(entry, slot));
			changed = true;
		}
		if (!changed)
			return;
		layout();
		if (container != null && container.callUpdateBgSizeFunction != null)
			container.callUpdateBgSizeFunction();
	}

	static function onFrame(event:Dynamic):Void {
		layout();
	}

	/**
		Places the shown items after the game's currency list with the list's own spacing (an item's listWidth, then the
		margin), and hides the rest. When an item shows or hides, the bar's background is resized to match.
	**/
	static function layout():Void {
		var list:Dynamic = container == null ? null : container.currencyList;
		if (list == null)
			return;
		var x:Float = list.x + nextX(list);
		var resized = false;
		for (entry in entries) {
			var view:Dynamic = entry.view;
			var shown = list.visible == true && entry.count != -1;
			if (view.visible != shown) {
				view.visible = shown;
				resized = true;
			}
			if (entry.count == -1)
				continue;
			if (view.x != x || view.y != list.y) {
				view.x = x;
				view.y = list.y;
			}
			x += view.listWidth + list.margin;
		}
		if (resized && container.callUpdateBgSizeFunction != null)
			container.callUpdateBgSizeFunction();
	}

	/** Where the list would put one more item: after its last shown item and the margin (its getTotalW when unsure). */
	static function nextX(list:Dynamic):Float {
		var end:Float = -1;
		try {
			var count:Int = list.numChildren;
			for (i in 0...count) {
				var item:Dynamic = list.getChildAt(i);
				if (item == null || item.visible != true || !(untyped __is__(item, itemClass)))
					continue;
				var right:Float = item.x + item.listWidth;
				if (right > end)
					end = right;
			}
		} catch (error:Dynamic) {
			end = -1;
		}
		return end < 0 ? list.getTotalW() : end + list.margin;
	}

	/** The data a TopHUDCurrencyListItem takes, marked as ours, with the material's icon when known. */
	static function dataFor(entry:Dynamic, slot:Dynamic):Dynamic {
		var data:Dynamic = {
			currencyType: 1,
			moneyString: Tooltips.grouped(entry.count),
			tooltip: entry.name,
			arkive: true,
			iconPath: null,
			iconU: 0,
			iconV: 0,
			iconWidth: 0,
			iconHeight: 0
		};
		if (slot != null) {
			data.iconPath = slot.iconPath;
			data.iconU = slot.iconU;
			data.iconV = slot.iconV;
			data.iconWidth = slot.iconWidth;
			data.iconHeight = slot.iconHeight;
		}
		return data;
	}

	static function iconKey(slot:Dynamic):String {
		if (slot == null || slot.iconPath == null)
			return "";
		var parts:Array<Dynamic> = [slot.iconPath, slot.iconU, slot.iconV, slot.iconWidth, slot.iconHeight];
		return parts.join("|");
	}
}
