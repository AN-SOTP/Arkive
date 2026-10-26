package arkive.tweaks;

/**
	Text helpers for game objects and HTML. They find positions with indexOf and copy whole pieces rather than adding
	one character at a time, and use no regular expressions.
**/
class Text {
	/**
		True when the object has this field. Use this instead of writing `untyped o.hasOwnProperty(f)` inside a condition:
		Haxe's `untyped` swallows the rest of the expression (`!untyped a || b` became `!(a || b)`, which broke Lobby CP).
	**/
	public static function has(o:Dynamic, field:String):Bool {
		return o != null && (untyped o.hasOwnProperty(field));
	}

	/**
		The text without spaces, tabs and line breaks at either end. Use this instead of StringTools.trim, which calls
		String.charCodeAtHX: a method Haxe's own Boot adds to String, which Arkive's empty Boot leaves out.
	**/
	public static function trim(text:String):String {
		var start = 0;
		var end = text.length;
		while (start < end && blank(StringTools.fastCodeAt(text, start)))
			start++;
		while (end > start && blank(StringTools.fastCodeAt(text, end - 1)))
			end--;
		return start == 0 && end == text.length ? text : text.substring(start, end);
	}

	static function blank(c:Int):Bool {
		return c == " ".code || (c > 8 && c < 14);
	}

	/** Removes <tags> from game HTML text. */
	public static function stripTags(html:String):String {
		if (html == null)
			return "";
		var pieces:Array<String> = [];
		var at = 0;
		while (at < html.length) {
			var open = html.indexOf("<", at);
			if (open < 0) {
				pieces.push(html.substr(at));
				break;
			}
			if (open > at)
				pieces.push(html.substring(at, open));
			var close = html.indexOf(">", open);
			if (close < 0)
				break;
			at = close + 1;
		}
		return pieces.join("");
	}

	/** The number (digits, '.' and ',') that starts at or after `from`, skipping spaces and tags; null when none. */
	public static function numberAfter(html:String, from:Int):Null<String> {
		var i = from;
		while (i < html.length) {
			var c = StringTools.fastCodeAt(html, i);
			if (c == "<".code) {
				var close = html.indexOf(">", i);
				if (close < 0)
					return null;
				i = close + 1;
			} else if (c == " ".code || c == "\t".code || c == "\r".code || c == "\n".code) {
				i++;
			} else {
				break;
			}
		}
		var end = i;
		while (end < html.length) {
			var c = StringTools.fastCodeAt(html, end);
			if ((c >= "0".code && c <= "9".code) || c == ".".code || c == ",".code)
				end++;
			else
				break;
		}
		return end > i ? html.substring(i, end) : null;
	}
}
