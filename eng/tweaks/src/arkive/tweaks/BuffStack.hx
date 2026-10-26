package arkive.tweaks;

import flash.events.Event;
import flash.utils.Dictionary;

/**
	Buff Icon Stacking: in the buff bars above the skill bar, buffs that supports give (their buff ids below) go in a
	row of their own above the other buffs. The game shows as many icons as before; the group button moves to the end
	of the bottom row.
**/
@:keep
class BuffStack {
	/** Buff ids (the first part of a buff slot's bindID, "id:…") of support skills, awakenings and identity buffs. */
	static var SUPPORT:Array<Int> = [
		211606, 211607, 211749, 211750, 361708, 361710, 362006, 362008, 314004, 314010, 314181, 314184, 480030, 480031, 480033,
		480034, 210230, 210241, 210615, 211476, 212610, 212906, 310526, 312002, 314260, 314650, 360506, 360804, 361004, 361207,
		361505, 480010, 480011, 480012, 2360110, 2360111, 2360112, 2360113, 2360114, 3367601, 362600, 212305, 319503, 319504,
		485100, 362601, 212306, 319506, 485101, 211400, 211410, 211420, 310501, 310502, 310504, 310505, 310506, 310507, 310508,
		310509, 313010, 313011, 313012, 480018, 480019, 480020, 214020, 214021, 214022, 214023, 214024, 214025, 214026, 214027,
		214028, 360102, 500131, 500149, 500153, 2360031, 2360034, 2360037, 2360101, 2360104, 2360107, 2360121, 2360123, 2360125,
		480024, 480025, 480026,
	];

	/** The quick slot's buff bars (QuickSLotBuffSlotManager), and where each one's group button goes. */
	static var bars = new Dictionary(true);

	static var buttonX = new Dictionary(true);

	/** QuickSLotBuffSlotManager.configUI: remember the bar, and keep its group button in place every frame. */
	public static function onBarConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		untyped bars[self] = true;
		self.addEventListener(Event.ENTER_FRAME, onFrame);
	}

	/** BaseList.updateAlign (before): lay out the quick slot's buff bars in two rows instead of the game's one. */
	public static function onUpdateAlign(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var bar:Dynamic = untyped bars[self];
		if (bar != true)
			return null;
		align(self);
		return {"return": true};
	}

	static function onFrame(event:Dynamic):Void {
		placeButton(event.currentTarget);
	}

	static function align(bar:Dynamic):Void {
		var pool:Dynamic = bar._pool;
		if (bar.ignoreAlign == true || pool == null)
			return;
		var step:Float = bar.direction == "horizontal" ? 1 : -1;
		var margin:Float = bar.margin;
		var counts = [0, 0];
		var active:Int = pool.activeList.length;
		for (i in 0...active) {
			var item:Dynamic = pool.getActiveItem(i);
			if (item == null || item.data == null)
				continue;
			var row = rowOf(item.data);
			var column = counts[row];
			counts[row] = column + 1;
			if (item.alpha != 1)
				item.alpha = 1;
			item.x = column * (item.width + margin) * step;
			item.y = -(row * (item.height + margin));
		}
		untyped buttonX[bar] = (counts[0] * bar.perItemWidth + bar.buttonMargin) * step;
		placeButton(bar);
	}

	static function placeButton(bar:Dynamic):Void {
		var x:Dynamic = untyped buttonX[bar];
		var button:Dynamic = bar.groupBtn;
		if (x == null || button == null || button.x == x)
			return;
		button.x = x;
	}

	/** 1 (the top row) for a support buff, 0 for the rest. */
	static function rowOf(data:Dynamic):Int {
		if (!Text.has(data, "bindID"))
			return 0;
		var bind:String = data.bindID;
		if (bind == null)
			return 0;
		var parts = bind.split(":");
		if (parts.length < 2)
			return 0;
		return SUPPORT.indexOf(Tooltips.leadingInt(parts[0], -1)) >= 0 ? 1 : 0;
	}
}
