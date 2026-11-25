using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Knovara.Local.Models;

/// <summary>
/// The BERT uncased WordPiece tokenizer both local models use (bge-small and the MiniLM cross-encoder), reimplemented so the
/// runtime needs no Python or native tokenizer library. It follows the HuggingFace "BertNormalizer + BertPreTokenizer +
/// WordPiece" pipeline that the model service's fastembed uses, so token ids, and therefore vectors, match.
/// </summary>
public sealed class WordPieceTokenizer
{
    private const int MaxCharsPerWord = 100;
    private const string ContinuationPrefix = "##";

    private readonly Dictionary<string, int> _vocab;
    private readonly int _unk;

    public int ClsId { get; }
    public int SepId { get; }
    public int PadId { get; }

    private WordPieceTokenizer(Dictionary<string, int> vocab)
    {
        _vocab = vocab;
        _unk = vocab["[UNK]"];
        ClsId = vocab["[CLS]"];
        SepId = vocab["[SEP]"];
        PadId = vocab["[PAD]"];
    }

    /// <summary>Loads the vocabulary from a HuggingFace tokenizer.json (model.type must be WordPiece).</summary>
    public static WordPieceTokenizer FromTokenizerJson(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var model = doc.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "WordPiece")
            throw new InvalidOperationException($"{path} is not a WordPiece tokenizer.");

        var vocab = new Dictionary<string, int>(30522, StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject()) vocab[entry.Name] = entry.Value.GetInt32();
        return new WordPieceTokenizer(vocab);
    }

    /// <summary>Token ids for the text, without [CLS]/[SEP].</summary>
    public List<int> Tokenize(string text)
    {
        var ids = new List<int>();
        foreach (var word in PreTokenize(Normalize(text))) AppendWordPieces(word, ids);
        return ids;
    }

    /// <summary>[CLS] text [SEP], truncated to <paramref name="maxLength"/> tokens in total.</summary>
    public int[] EncodeSingle(string text, int maxLength)
    {
        var tokens = Tokenize(text);
        if (tokens.Count > maxLength - 2) tokens.RemoveRange(maxLength - 2, tokens.Count - (maxLength - 2));

        var ids = new int[tokens.Count + 2];
        ids[0] = ClsId;
        for (var i = 0; i < tokens.Count; i++) ids[i + 1] = tokens[i];
        ids[^1] = SepId;
        return ids;
    }

    /// <summary>
    /// [CLS] a [SEP] b [SEP] with HuggingFace's default "longest first" truncation: while over budget, drop a token from the
    /// longer sequence. Returns the ids and the segment (token type) of each position.
    /// </summary>
    public (int[] Ids, int[] Types) EncodePair(string a, string b, int maxLength)
    {
        var first = Tokenize(a);
        var second = Tokenize(b);

        var excess = first.Count + second.Count + 3 - maxLength;
        while (excess > 0)
        {
            if (first.Count > second.Count) first.RemoveAt(first.Count - 1);
            else second.RemoveAt(second.Count - 1);
            excess--;
        }

        var ids = new int[first.Count + second.Count + 3];
        var types = new int[ids.Length];
        var p = 0;
        ids[p++] = ClsId;
        foreach (var t in first) ids[p++] = t;
        ids[p++] = SepId;
        foreach (var t in second) { types[p] = 1; ids[p++] = t; }
        types[p] = 1;
        ids[p] = SepId;
        return (ids, types);
    }

    // ---------------------------------------------------------------- normalization and splitting

    /// <summary>clean_text, CJK spacing, lowercasing and accent stripping, in the BertNormalizer's order.</summary>
    internal static string Normalize(string text)
    {
        var cleaned = new StringBuilder(text.Length + 8);
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp == 0 || cp == 0xFFFD || IsControl(rune)) continue;
            if (Rune.IsWhiteSpace(rune)) cleaned.Append(' ');
            else if (IsChineseChar(cp)) cleaned.Append(' ').Append(rune.ToString()).Append(' ');
            else cleaned.Append(rune.ToString());
        }

        // Lowercase, then decompose and drop combining marks (strip_accents defaults to following lowercase).
        var decomposed = cleaned.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) result.Append(ch);
        }
        return result.ToString();
    }

    /// <summary>Splits on whitespace, and makes every punctuation character its own word.</summary>
    internal static IEnumerable<string> PreTokenize(string text)
    {
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
            }
            else if (IsPunctuation(rune))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return rune.ToString();
            }
            else
            {
                word.Append(rune.ToString());
            }
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private void AppendWordPieces(string word, List<int> ids)
    {
        if (word.Length > MaxCharsPerWord) { ids.Add(_unk); return; }

        var pieces = new List<int>();
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var found = -1;
            while (start < end)
            {
                var candidate = word.Substring(start, end - start);
                if (start > 0) candidate = ContinuationPrefix + candidate;
                if (_vocab.TryGetValue(candidate, out var id)) { found = id; break; }
                end--;
            }

            // A word with any unmatchable piece is one unknown token, not a partial split.
            if (found < 0) { ids.Add(_unk); return; }
            pieces.Add(found);
            start = end;
        }
        ids.AddRange(pieces);
    }

    // BERT treats \t \n \r as whitespace rather than control characters.
    private static bool IsControl(Rune r)
    {
        if (r.Value is '\t' or '\n' or '\r') return false;
        var cat = Rune.GetUnicodeCategory(r);
        return cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse
            or UnicodeCategory.Surrogate;
    }

    private static bool IsPunctuation(Rune r)
    {
        var cp = r.Value;
        // All ASCII non-alphanumerics count (including symbols such as $ + < = > ^ ` | ~), plus Unicode punctuation.
        if (cp is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126) return true;
        return Rune.GetUnicodeCategory(r) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsChineseChar(int cp) =>
        cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F
        or >= 0x2B740 and <= 0x2B81F or >= 0x2B820 and <= 0x2CEAF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;
}
