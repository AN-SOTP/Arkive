package arkive.tweaks;

/**
	Lobby CP: shows each party member's combat power after their item level in the party finder, using the line the
	game's own member tooltip shows (green with the support mark for supports, red for damage dealers).
**/
@:keep
class LobbyCp {
	/** FindPartyNewPreviewListRendererMember.configUI: make the item level field wide enough for both, keeping its right edge. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var field:Dynamic = self.itemLevel_txt;
		if (field == null)
			return;
		var right:Float = field.x + field.width;
		field.width = field.width * 3;
		field.x = right - field.width;
		field.y = field.y - 2;
	}

	/** FindPartyNewPreviewListRendererMember.memberData(data): put the tooltip's combat power line after the item level. */
	public static function onMemberData(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var data:Dynamic = args[0];
		if (data == null || !untyped data.hasOwnProperty("itemLevel") || !untyped data.hasOwnProperty("memberTooltip"))
			return;
		var level:String = data.itemLevel;
		var tooltip:String = data.memberTooltip;
		var lines = tooltip.split("\n");
		if (lines.length < 3)
			return;
		// The tooltip's icon and text are sized for a bigger line; shrink them to fit beside the item level.
		var text = (level + "  " + lines[1]).split("'19'").join("'17'").split("'-4'").join("'-3'");
		#if arkive_diag
		if (Diag.log != "")
			text = text + " <font color='#ff5050'>" + Diag.log + "</font>";
		#end
		self.itemLevel = "<font size='13'>" + text + "</font>";
	}
}
