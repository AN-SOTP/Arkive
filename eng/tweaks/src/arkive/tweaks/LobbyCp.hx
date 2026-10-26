package arkive.tweaks;

/** Lobby CP: shows each party member's combat power next to their item level in the party finder preview. */
class LobbyCp {
	public static function register(options:Dynamic):Void {
		Hooks.onAfter("ark.ui.findPartyNew.FindPartyNewPreviewListRendererMember.memberData", function(self:Dynamic, args:Array<Dynamic>, result:Dynamic) {
			var data:Dynamic = args[0];
			if (data == null || self.itemLevel_txt == null)
				return result;
			var tooltip:Dynamic = data.memberTooltip;
			var power = untyped __is__(tooltip, String) ? combatPower(tooltip) : null;
			var level:String = untyped __is__(data.itemLevel, String) ? data.itemLevel : "";
			if (power != null)
				self.itemLevel_txt.htmlText = level + "  <font color='#ffd200'>" + power + "</font>";
			#if arkive_diag
			else
				self.itemLevel_txt.htmlText = level + "  <font color='#ff5050'>no CP</font>";
			#end
			return result;
		});
	}

	/** The number after "Power:" in the member tooltip ("Combat Power: <font color='#ffd200'>1757.50</font>"). */
	public static function combatPower(tooltip:String):Null<String> {
		var at = tooltip.indexOf("Power:");
		return at < 0 ? null : Text.numberAfter(tooltip, at + 6);
	}
}
