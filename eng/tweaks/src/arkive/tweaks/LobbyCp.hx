package arkive.tweaks;

/** Lobby CP: shows each party member's combat power next to their item level in the party finder preview. */
class LobbyCp {
	/** Name of the label Arkive adds beside a member's item level. */
	static inline var LABEL = "arkiveLobbyCp";

	public static function register(options:Dynamic):Void {
		Hooks.onAfter("ark.ui.findPartyNew.FindPartyNewPreviewListRendererMember.memberData", function(self:Dynamic, args:Array<Dynamic>, result:Dynamic) {
			#if arkive_diag_noop
			return result;
			#else
			var data:Dynamic = args[0];
			var level:flash.text.TextField = self.itemLevel_txt;
			if (data == null || level == null || level.parent == null)
				return result;
			var tooltip:Dynamic = data.memberTooltip;
			var power = untyped __is__(tooltip, String) ? combatPower(tooltip) : null;
			label(level).text = power == null ? "" : power;
			return result;
			#end
		});
	}

	/**
		A one-line label just left of the item level, made once per member slot. The game's own text fields are left
		exactly as they are: changing their text can change their size, and the party finder lays itself out again.
	**/
	static function label(level:flash.text.TextField):flash.text.TextField {
		var parent = level.parent;
		var existing = parent.getChildByName(LABEL);
		if (existing != null)
			return cast existing;
		var format = level.getTextFormat();
		format.color = 0xFFD200;
		format.align = flash.text.TextFormatAlign.RIGHT;
		var field = new flash.text.TextField();
		field.name = LABEL;
		field.embedFonts = level.embedFonts;
		field.defaultTextFormat = format;
		field.selectable = false;
		field.mouseEnabled = false;
		field.multiline = false;
		field.wordWrap = false;
		field.width = 90;
		field.height = level.height;
		field.x = level.x - 95;
		field.y = level.y;
		parent.addChild(field);
		return field;
	}

	/** The number after "Power:" in the member tooltip ("Combat Power: <font color='#ffd200'>1757.50</font>"). */
	public static function combatPower(tooltip:String):Null<String> {
		var at = tooltip.indexOf("Power:");
		return at < 0 ? null : Text.numberAfter(tooltip, at + 6);
	}
}
