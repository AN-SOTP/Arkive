package arkive.tweaks;

/** Lobby CP: shows each party member's combat power next to their item level in the party finder preview. */
class LobbyCp {
	public static function register(options:Dynamic):Void {
		Hooks.on("ark.ui.findPartyNew.FindPartyNewPreviewListRendererMember.memberData", function(self:Dynamic, args:Array<Dynamic>, next) {
			var result = next();
			var data:Dynamic = args[0];
			if (data == null || self.itemLevel_txt == null)
				return result;
			var power = combatPower(Std.string(data.memberTooltip));
			if (power != null)
				self.itemLevel_txt.htmlText = Std.string(data.itemLevel) + "  <font color='#ffd200'>" + power + "</font>";
			return result;
		});
	}

	/** The number after "Power:" in the member tooltip ("Combat Power: <font color='#ffd200'>1757.50</font>"). */
	public static function combatPower(tooltip:String):Null<String> {
		if (tooltip == null)
			return null;
		var at = tooltip.indexOf("Power:");
		if (at < 0)
			return null;
		var text = Text.stripTags(tooltip.substr(at + 6));
		var start = 0;
		while (start < text.length && " \t\r\n".indexOf(text.charAt(start)) >= 0)
			start++;
		var end = start;
		while (end < text.length && "0123456789.,".indexOf(text.charAt(end)) >= 0)
			end++;
		return end > start ? text.substring(start, end) : null;
	}
}
