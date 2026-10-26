package arkive.tweaks;

/**
	Fast Advanced Honing: skips the steps of the result animation (gauge fill, blessing bubble, fade) so the next
	advanced honing can be clicked straight away. On the next frame it runs the game's own end of the animation
	(ItemAmpContent.checkAmpComplete, exposed by the hook), which tells the game the result has been shown.
**/
@:keep
class FastAdvHone {
	static var pending:Bool = false;

	/** ItemAmpContent.successEffectState (before): skip the step and finish the animation once, a frame later. */
	public static function onSuccessEffectState(self:Dynamic, args:Array<Dynamic>):Dynamic {
		if (!pending) {
			pending = true;
			function finish(event:Dynamic):Void {
				self.removeEventListener("enterFrame", finish);
				pending = false;
				self.checkAmpComplete();
			}
			self.addEventListener("enterFrame", finish);
		}
		return {"return": true};
	}
}
