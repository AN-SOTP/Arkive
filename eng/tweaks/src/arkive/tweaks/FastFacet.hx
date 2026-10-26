package arkive.tweaks;

import flash.utils.Dictionary;

/**
	Auto-Dismiss Stone Faceting Results: after the automatic faceting button cuts a stone, its result page is closed by
	itself on the next frame (as if OK was pressed). Cutting by hand, or picking another stone, turns this off again, so
	those results still show.
**/
@:keep
class FastFacet {
	/** True from a click on automatic faceting until its result arrives, or a manual cut or another stone is picked. */
	static var auto:Bool = false;

	/** Faceting windows already listened to (addedToStage runs again each time the window opens). */
	static var wired:Dictionary = new Dictionary(true);

	/** MarbleContent.addedToStage: listen to the automatic button, the three manual cut buttons and the stone list. */
	public static function onAddedToStage(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var seen:Dynamic = untyped wired[self];
		if (seen == true)
			return;
		untyped wired[self] = true;
		var autoButton:Dynamic = self.autoCarveBtn;
		if (autoButton != null)
			autoButton.addEventListener("buttonClick", startAuto);
		for (i in 0...3) {
			var line:Dynamic = untyped self["abilityRenderer" + i];
			if (line != null && line.activeBtn != null)
				line.activeBtn.addEventListener("buttonClick", stopAuto);
		}
		var stones:Dynamic = self.stoneList;
		if (stones != null)
			stones.addEventListener("listIndexChange", stopAuto);
	}

	/** MarbleContent.marbleStoneComplete(result): the result page is showing; press its OK a frame later when automatic. */
	public static function onStoneComplete(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		if (!auto)
			return;
		auto = false;
		function dismiss(event:Dynamic):Void {
			self.removeEventListener("enterFrame", dismiss);
			var page:Dynamic = self.resultPage;
			if (page != null && page.visible == true && page.ok_btn != null)
				GameUi.press(page.ok_btn);
		}
		self.addEventListener("enterFrame", dismiss);
	}

	static function startAuto(event:Dynamic):Void {
		auto = true;
	}

	static function stopAuto(event:Dynamic):Void {
		auto = false;
	}
}
