namespace Arkive.Core.Audio;

/// <summary>
/// The scrambled file names Lost Ark gives loose audio files in WwiseAudioPackage. A music file Wwise would call
/// "583232.wem" is stored as "WHIBIBCQF2F29ZUBUBOQR.wem": the decimal id, an end mark, then filler taken from the
/// output itself, all shifted by a position rule. Rebuilt from the client's own name/id pairs; it reproduces every
/// identified file.
/// </summary>
public static class LostArkAudioFileNames
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int Length = 21;

    // The end mark is written as two characters chosen by its position.
    private static readonly string[] EndMarks = ["XW", "XS", "XZ", "X0"];

    public static string ForSource(uint sourceId) => ForName(sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ".wem";

    private static string ForName(string name)
    {
        var plain = new List<char>(name);
        plain.AddRange(EndMarks[name.Length % 4]);
        var output = new char[Length];
        for (var index = 0; index < Length; index++)
        {
            if (index >= plain.Count) plain.Add(output[index - name.Length]);
            var value = Alphabet.IndexOf(plain[index]);
            output[index] = Alphabet[(Length + 7 * value) % Alphabet.Length];
        }
        return new string(output);
    }
}
