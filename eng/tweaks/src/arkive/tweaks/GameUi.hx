package arkive.tweaks;

import flash.system.ApplicationDomain;

/**
	Helpers for adding the game's own controls to its windows and pressing them. Classes are looked up in the shared
	ApplicationDomain (every efui movie loads into efui_base's), with hasDefinition first, so nothing needs a try.
**/
class GameUi {
	/** The game's plain button, the style its own windows use for extra actions. */
	public static inline var PLAIN_BUTTON = "AnimatedButton_renew_V2";

	/** A class or symbol any loaded movie defines, or null. */
	public static function definition(name:String):Dynamic {
		var domain = ApplicationDomain.currentDomain;
		if (!domain.hasDefinition(name))
			return null;
		return domain.getDefinition(name);
	}

	/** A new plain game button with this label, or null when no movie has the symbol. */
	public static function button(label:String):Dynamic {
		var type:Dynamic = definition(PLAIN_BUTTON);
		if (type == null)
			return null;
		var made:Dynamic = untyped __new__(type);
		made.label = label;
		return made;
	}

	/** A new button of the first of these library symbols that a loaded movie has, with this label; null when none. */
	public static function buttonOf(label:String, symbols:Array<String>):Dynamic {
		for (symbol in symbols) {
			var type:Dynamic = definition(symbol);
			if (type != null) {
				var made:Dynamic = untyped __new__(type);
				made.label = label;
				return made;
			}
		}
		return null;
	}

	/** True when the object is an instance of the named class or of a class that extends it. */
	public static function isA(o:Dynamic, className:String):Bool {
		if (o == null)
			return false;
		var type:Dynamic = definition(className);
		return type != null && (untyped __is__(o, type));
	}

	/** The object itself or its nearest parent that is an instance of the named class; null when none is. */
	public static function ancestor(o:Dynamic, className:String):Dynamic {
		var type:Dynamic = definition(className);
		if (type == null)
			return null;
		var at:Dynamic = o;
		while (at != null) {
			if (untyped __is__(at, type))
				return at;
			at = at.parent;
		}
		return null;
	}

	/** Sends a plain mouse click to a display object, as the game's check boxes and dialog buttons receive one. */
	public static function click(target:Dynamic):Void {
		target.dispatchEvent(new scaleform.gfx.MouseEventEx(flash.events.MouseEvent.CLICK));
	}

	/** A new button of the same kind as one of the window's own, with this label. */
	public static function copy(like:Dynamic, label:String):Dynamic {
		var type:Dynamic = like.constructor;
		var made:Dynamic = untyped __new__(type);
		made.label = label;
		return made;
	}

	/** Adds the item to the other's parent, drawn just above it. */
	public static function addAbove(item:Dynamic, below:Dynamic):Void {
		var parent:Dynamic = below.parent;
		var index:Int = parent.getChildIndex(below);
		parent.addChildAt(item, index + 1);
	}

	/** Sets enabled only when it changes, so the button doesn't redraw every frame. */
	public static function enable(button:Dynamic, on:Bool):Void {
		if (button.enabled != on)
			button.enabled = on;
	}

	/** Clicks a game button the way the game's own click does. */
	public static function press(button:Dynamic):Void {
		button.dispatchEvent(new scaleform.clik.events.ButtonEvent(scaleform.clik.events.ButtonEvent.CLICK));
	}

	/** Clicks a list's row (scrolling it into view first), as the mouse would; false when it has no row there. */
	public static function clickRow(list:Dynamic, index:Int):Bool {
		var row:Dynamic = list.getRendererAtDataIndex(index);
		if (row == null) {
			list.scrollPosition = index;
			list.validateNow();
			row = list.getRendererAtDataIndex(index);
		}
		if (row == null)
			return false;
		row.dispatchEvent(new scaleform.gfx.MouseEventEx(flash.events.MouseEvent.CLICK));
		return true;
	}

	/** The option's switch, on unless the settings turn it off. */
	public static function on(tweak:String, key:String):Bool {
		return Options.get(tweak, key, true) != false;
	}
}
