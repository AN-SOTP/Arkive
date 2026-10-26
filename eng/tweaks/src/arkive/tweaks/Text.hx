package arkive.tweaks;

/** Small text helpers that avoid regular expressions, which Scaleform's player may not have. */
class Text {
	/** Removes <tags> from game HTML text. */
	public static function stripTags(html:String):String {
		var out = new StringBuf();
		var inTag = false;
		for (i in 0...html.length) {
			var c = html.charAt(i);
			if (c == "<")
				inTag = true;
			else if (c == ">")
				inTag = false;
			else if (!inTag)
				out.add(c);
		}
		return out.toString();
	}
}
