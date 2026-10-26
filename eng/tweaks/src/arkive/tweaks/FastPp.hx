package arkive.tweaks;

import flash.utils.Timer;

/**
	Fast Paradise: paradise enhancing shows its result without the loading animation, and resonance (alteration grade)
	plays its graph quickly and lets its button be clicked again 50 ms after a click.
**/
@:keep
class FastPp {
	/** TrinityOrbManagement_Child_Enhance's result type for a finished enhance (its TRINITY_ORB_MANAGEMENT_ENHANCE_TYPE_CONFIRM). */
	static inline var CONFIRM:Int = 1;

	/** TrinityOrbManagement_Child_Enhance.resultData(type, data) (before): show the result at once, without the progress animation. */
	public static function onResultData(self:Dynamic, args:Array<Dynamic>):Dynamic {
		var type:Int = args[0];
		var data:Dynamic = args[1];
		self.genomeData = data;
		var progress:Dynamic = self.result_progress;
		if (progress != null)
			progress.visible = false;
		var confirm:Dynamic = self.result_confirm;
		if (confirm != null) {
			confirm.visible = type == CONFIRM;
			if (type == CONFIRM) {
				confirm.gotoAndPlay(2);
				confirm.data = data;
			}
		}
		return {"return": true};
	}

	/** TrinityOrbManagement_Child_AlterationGrade.configUI: shorten the graph animation and re-enable the button after a click. */
	public static function onConfigUI(self:Dynamic, args:Array<Dynamic>, result:Dynamic):Void {
		var graph:Dynamic = self.polygonGraph;
		if (graph != null) {
			graph.polygonTweenTime = 0.1;
			graph.highlightTime = 0.1;
		}
		var button:Dynamic = self.childAlterationGradeBtn;
		if (button == null)
			return;
		button.addEventListener("buttonClick", function(event:Dynamic):Void {
			var timer = new Timer(50, 1);
			timer.addEventListener("timer", function(tick:Dynamic):Void {
				button.enabled = true;
			});
			timer.start();
		});
	}
}
