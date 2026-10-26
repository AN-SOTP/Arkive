package arkive.tweaks;

import flash.events.Event;
import flash.net.URLLoader;
import flash.net.URLRequest;

/**
	The Data Bus: short messages from tweaks to Arkive. The game's interface can't write files or go online, but it can
	read files by their full path, and Arkive notices each read of the small files in <home>/bus (see Arkive's
	TweakBus). A message "channel<tab>text" is spelled as "go", two hex digits per character (characters past 254 as
	"ff" and four digits), a four-digit checksum (the sum of the character codes) and "end", one file read after the
	other. Every movie shares this class, so messages from any window queue up and are spelled one at a time.
	It takes about two reads per character, a few milliseconds each: keep messages short and batch them.
**/
@:keep
class Bus {
	/** Arkive's file sets, cycled so a file is read again only sixteen reads later. */
	static inline var SETS = "abcdefghijklmnop";

	static var queue:Array<String> = [];
	static var spelling:Array<String> = [];
	static var spelled = 0;
	static var busy = false;
	static var reads = 0;

	/** Queues a message for Arkive. Nothing arrives when Arkive isn't running. */
	public static function send(channel:String, text:String):Void {
		if (Options.home() == "")
			return;
		queue.push(encode(channel + "\t" + text));
		if (!busy)
			next();
	}

	/** The hex digits for a message, checksum included (Arkive's TweakBus.Decode reads them back). */
	public static function encode(message:String):String {
		var parts:Array<String> = [];
		var sum = 0;
		for (i in 0...message.length) {
			var code = StringTools.fastCodeAt(message, i);
			sum = (sum + code) & 0xFFFF;
			if (code < 0xFF)
				parts.push(StringTools.hex(code, 2));
			else
				parts.push("ff" + StringTools.hex(code, 4));
		}
		parts.push(StringTools.hex(sum, 4));
		var digits:String = parts.join("");
		return digits.toLowerCase();
	}

	static function next():Void {
		if (queue.length == 0) {
			busy = false;
			return;
		}
		busy = true;
		var digits:String = queue.shift();
		spelling = ["go"];
		for (i in 0...digits.length)
			spelling.push(digits.charAt(i));
		spelling.push("end");
		spelled = 0;
		spellNext();
	}

	/** Reads the next file once the previous read has finished, so Arkive sees them in order. */
	static function spellNext():Void {
		if (spelled >= spelling.length) {
			next();
			return;
		}
		var set:String = SETS.charAt(reads % SETS.length);
		reads++;
		var path:String = Options.home() + "/bus/" + set + "-" + spelling[spelled] + ".txt";
		spelled++;
		var loader = new URLLoader();
		loader.addEventListener(Event.COMPLETE, function(e:Dynamic) spellNext());
		loader.addEventListener("ioError", function(e:Dynamic) spellNext());
		loader.addEventListener("securityError", function(e:Dynamic) spellNext());
		loader.load(new URLRequest(path));
	}
}
