package arkive.tweaks;

import flash.display.Sprite;
import flash.text.TextField;
import flash.text.TextFormat;
import scaleform.gfx.InteractiveObjectEx;

/**
	Next Item: a button beside the main button of accessory refining, bracelet refining and ability stone faceting that
	selects the next item in the window's list (wrapping around, skipping locked and disabled rows), by clicking its row
	as a player would.
**/
@:keep
class NextButton {
	/** The game's buttons to copy, best first; when none exists the button is drawn. */
	static var SYMBOLS:Array<String> = [
		"AnimatedButton_renew_FC_small",
		"AnimatedButton_renew_01_small",
		"AnimatedButton_renew_FC",
		"AnimatedButton_renew_01"
	];

	static inline var WIDTH:Int = 110;
	static inline var HEIGHT:Int = 26;
	static inline var GAP:Int = 8;

	/** ItemAccUpgradeContent.configUI */
	public static function onAccUpgradeConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		attach(self, self.accUpgrade_btn, self.itemAccList, "Next Item");
	}

	/** ItemBEWndContent.configUI */
	public static function onBraceletConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		attach(self, self.requestButton, self.itemList, "Next Item");
	}

	/** MarbleContent.configUI (ability stone faceting) */
	public static function onMarbleConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		attach(self, self.autoCarveBtn, self.stoneList, "Next Stone");
	}

	/** Puts the button right of `beside`, centred on it, in the window `owner`. */
	static function attach(owner:Dynamic, beside:Dynamic, list:Dynamic, label:String):Void {
		if (owner == null || beside == null || list == null)
			return;
		var onClick = function(event:Dynamic):Void {
			selectNext(list);
		};
		var made:Dynamic = GameUi.buttonOf(label, SYMBOLS);
		if (made != null) {
			if (made.width < WIDTH)
				made.width = WIDTH;
			made.addEventListener("buttonClick", onClick);
		} else {
			made = drawnButton(label, onClick);
		}
		made.x = beside.x + beside.width + GAP;
		made.y = beside.y + (beside.height - made.height) / 2;
		var at:Int = beside.parent == owner ? owner.getChildIndex(beside) + 1 : owner.numChildren;
		owner.addChildAt(made, at);
	}

	/** A plain framed button for when the game's button symbols aren't loaded. */
	static function drawnButton(label:String, onClick:Dynamic->Void):Dynamic {
		var button = new Sprite();
		button.buttonMode = true;
		button.mouseChildren = false;
		paint(button, 0x1C1C1C);
		var format = new TextFormat();
		format.font = "$YG760";
		format.size = 12;
		var text = new TextField();
		text.defaultTextFormat = format;
		text.textColor = 0xFFFFFF;
		text.autoSize = flash.text.TextFieldAutoSize.CENTER;
		text.selectable = false;
		text.mouseEnabled = false;
		text.text = label;
		text.x = (WIDTH - text.width) / 2;
		text.y = (HEIGHT - text.height) / 2;
		InteractiveObjectEx.setHitTestDisable(text, true);
		button.addChild(text);
		button.addEventListener("rollOver", function(event:Dynamic):Void {
			paint(button, 0x3A3A3A);
		});
		button.addEventListener("rollOut", function(event:Dynamic):Void {
			paint(button, 0x1C1C1C);
		});
		button.addEventListener("click", onClick);
		return button;
	}

	static function paint(button:Sprite, fill:Int):Void {
		var g = button.graphics;
		g.clear();
		g.beginFill(0x8A8A8A);
		g.drawRect(0, 0, WIDTH, HEIGHT);
		g.endFill();
		g.beginFill(fill);
		g.drawRect(1, 1, WIDTH - 2, HEIGHT - 2);
		g.endFill();
	}

	/** Clicks the next row after the selected one that isn't locked or disabled, wrapping around. */
	static function selectNext(list:Dynamic):Void {
		var rows:Dynamic = list.dataProvider;
		if (rows == null)
			return;
		var count:Int = rows.length;
		if (count == 0)
			return;
		var selected:Int = list.selectedIndex;
		var step = 1;
		while (step <= count) {
			var index = (selected + step) % count;
			if (index < 0)
				index += count;
			var row:Dynamic = rows[index];
			if (row != null && !flag(row.isLock) && !flag(row.isDisabled)) {
				GameUi.clickRow(list, index);
				return;
			}
			step++;
		}
	}

	/** The game marks rows with true or 1. */
	static function flag(value:Dynamic):Bool {
		return value == true || value == 1;
	}
}
