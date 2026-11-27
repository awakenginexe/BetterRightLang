using BetterRightLang.Core;

var directory = Path.Combine(Path.GetTempPath(), "BetterRightLang-Core-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    var en = Path.Combine(directory, "en.txt");
    var th = Path.Combine(directory, "th.txt");
    File.WriteAllLines(en, ["twitter", "correct", "hello", "world", "test", "we", "help", "hello-world", "cat", "catalog", "God", "he", "go", "responsibility"]);
    File.WriteAllLines(th, ["ไป", "มา", "สวัสดี", "ครับ", "ทดสอบ", "เทพ", "้ำส", "แคท"]);
    var engine = new LayoutEngine(en, th);
    var checks = 0;
    void Equal<T>(T expected, T actual, string label)
    {
        checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected '{expected}', actual '{actual}'");
    }

    Equal("twitter", engine.Convert("ะไระะำพ"), "official Thai-English");
    Equal("correct", engine.Convert("แนพพำแะ"), "official correct");
    Equal("ไปมา", engine.Convert("wx,k"), "official compound English-Thai");
    Equal("สวัสดีพี่เจได", engine.Convert("l;ylfurujg0wf"), "user acceptance manual phrase");
    Equal("ะไระะำพ", engine.Convert("twitter"), "reverse");
    Equal("twitter\r\ncorrect\tไปมา", engine.Convert("ะไระะำพ\r\nแนพพำแะ\twx,k"), "mixed directions and whitespace");
    Equal("twitter!", engine.Convert("ะไระะำพ!"), "Thai run preserves English punctuation");
    Equal("", engine.Convert(""), "empty selection");
    Equal("😀", engine.Convert("😀"), "unmapped Unicode");
    const string usKeys = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:\"ZXCVBNM<>?";
    const string thaiKeys = "_ๅ/-ภถุึคตจขชๆไำพะัีรนยบลฃฟหกดเ้่าสวงผปแอิืทมใฝ%+๑๒๓๔ู฿๕๖๗๘๙๐\"ฎฑธํ๊ณฯญฐ,ฅฤฆฏโฌ็๋ษศซ.()ฉฮฺ์?ฒฬฦ";
    Equal(thaiKeys, engine.Convert(usKeys), "all physical keys and Shift");
    Equal(usKeys, engine.Convert(thaiKeys), "all keys reverse");
    Equal("้ำสสนl;ylfu", engine.Convert("helloสวัสดี"), "adjacent mixed scripts");
    Equal("twitter", engine.Suggest("ะไระะำพ"), "typing English target");
    Equal("correct", engine.Suggest("แนพพำแะ"), "typing exact target");
    Equal("ไปมา", engine.Suggest("wx,k"), "compound target");
    Equal("สวัสดี", engine.Suggest("l;ylfu"), "Thai target with semicolon");
    Equal("twitter!", engine.Suggest("ะไระะำพ!", true), "boundary punctuation");
    Equal<string?>(null, engine.Suggest("hello"), "correct English");
    Equal<string?>(null, engine.Suggest("สวัสดีครับ"), "correct Thai compound");
    Equal<string?>(null, engine.Suggest("สวัสดีคร"), "Thai compound prefix");
    Equal<string?>(null, engine.Suggest("เี"), "short word");
    Equal<string?>(null, engine.Suggest("เี", true), "short boundary word");
    Equal<string?>(null, engine.Suggest("hel"), "valid English prefix protects typing");
    Equal<string?>(null, engine.Suggest("้ำส"), "both layouts valid protects source");
    Equal<string?>(null, engine.Suggest("cat"), "prefix collision keeps original");
    Equal<string?>(null, engine.Suggest("catalog"), "continued prefix original");
    Equal<string?>(null, engine.Suggest("https://wx,k.com", true), "URL");
    Equal<string?>(null, engine.Suggest("wx,k@example.com", true), "email");
    Equal<string?>(null, engine.Suggest("wx,k_value", true), "identifier");
    Equal<string?>(null, engine.Suggest("wx,k123", true), "identifier digits");
    Equal<string?>(null, engine.Suggest("hello wx,k", true), "multiword rejected");
    Equal<string?>(null, engine.Suggest("l;ylfuX", true), "unknown target");
    Equal<string?>(null, engine.Suggest(new string('x', 129), true), "bounded auto work");
    Equal("responsi", engine.Suggest(engine.Convert("responsi")), "long target prefix at eighth key");
    Equal<string?>(null, engine.Suggest(engine.Convert("responsi"), true), "boundary requires complete target");
    var span = LayoutEngine.GetWordBeforeCaret("hello l;ylfu ", 13)!.Value;
    Equal(6, span.Start, "span start");
    Equal(6, span.Length, "span length");
    Equal("l;ylfu", span.Text, "span text");
    Equal(true, span.Matches("hello l;ylfu "), "span current");
    Equal(false, span.Matches("hello l;ylfx "), "span stale");
    Equal(false, span.Matches("x"), "span out of bounds");
    Equal<WordSpan?>(null, LayoutEngine.GetWordBeforeCaret("hello", 0), "empty left");
    Equal<WordSpan?>(null, LayoutEngine.GetWordBeforeCaret("hello", 2), "caret inside word");
    Equal<WordSpan?>(null, LayoutEngine.GetWordBeforeCaret("hello", 10), "invalid caret");
    Equal("wx,k", LayoutEngine.GetWordBeforeCaret("wx,k", 4)!.Value.Text, "internal punctuation");
    Equal("สวัสดี", LayoutEngine.GetWordBeforeCaret("สวัสดี", 6)!.Value.Text, "combining marks");
    Equal("l;ylfurujg0wf", LayoutEngine.GetWordBeforeCaret("l;ylfurujg0wf", "l;ylfurujg0wf".Length)!.Value.Text, "user acceptance span");
    WordSpan? Pick(string text) => LayoutEngine.GetWordBeforeCaret(text, text.Length);
    Equal("l;ylfu", Pick("สวัสดีl;ylfu")!.Value.Text, "current English run after Thai");
    Equal(6, Pick("สวัสดีl;ylfu")!.Value.Start, "English run offset");
    Equal("สวัสดี", Pick("helloสวัสดี")!.Value.Text, "current Thai run after English");
    Equal("l;ylfu", Pick("สวัสดี;l;ylfu")!.Value.Text, "ambiguous separator before English stays");
    Equal("สวัสดี", Pick("hello;สวัสดี")!.Value.Text, "ambiguous separator before Thai stays");
    Equal("wx,k", Pick("สวัสดีwx,k")!.Value.Text, "Kedmanee comma inside English run");
    Equal("สวัสดี", engine.Convert(Pick("สวัสดีl;ylfu")!.Value.Text), "current-word conversion protects preceding Thai");
    Equal("hello", engine.Convert(Pick("hello้ำสสน")!.Value.Text), "current-word conversion protects preceding English");
    Equal("สวัสดี", Pick("helloสวัสดี  ")!.Value.Text, "mixed run before same-line spaces");
    Equal<WordSpan?>(null, Pick("hello\n"), "line break stops selection");
    Equal<WordSpan?>(null, Pick("hello\r\n   "), "blank paragraph stops selection");
    Equal<WordSpan?>(null, Pick("hello\u2029  "), "Unicode paragraph stops selection");
    Equal<WordSpan?>(null, Pick("hello\u2028"), "Unicode line separator stops selection");
    Equal("l;ylfu", Pick("hello\r\nl;ylfu  ")!.Value.Text, "current line after paragraph");
    var data = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Data"));
    var real = new LayoutEngine(Path.Combine(data, "en.txt"), Path.Combine(data, "th.txt"));
    Equal("twitter", real.Suggest("ะไระะำพ", true), "real corpus twitter");
    Equal("correct", real.Suggest("แนพพำแะ", true), "real corpus correct");
    Equal("ไปมา", real.Suggest("wx,k", true), "real corpus compound");
    Equal("สวัสดี", real.Suggest("l;ylfu", true), "real corpus Thai");
    Equal("เข้าใจ", real.Suggest("g-hk.0", true), "a period and digit are Thai letters on Kedmanee");
    Equal("ใหม่", real.Suggest(".s,j", true), "leading period in a Thai word");
    Equal("ซื้อ", real.Suggest(real.Convert("ซื้อ"), true), "shifted colon is a Thai consonant");
    Equal<string?>(null, real.Suggest("www.example.com", true), "dotted web address stays literal");
    Equal("สวัสดีพี่เจได", real.Suggest("l;ylfurujg0wf", true), "user acceptance boundary");
    Equal<string?>(null, real.Suggest("สวัสดีครับ", true), "real corpus correct compound");
    Equal("เห้ย", real.Suggest("gshp", true), "hand-added informal spelling from th-extra.txt");
    File.WriteAllLines(Path.Combine(directory, "th-extra.txt"), ["# added by hand", "เห้ย"]);
    Equal("เห้ย", new LayoutEngine(en, th).Suggest("gshp", true), "an -extra list beside the main list is loaded too");
    Equal<string?>(null, engine.Suggest("gshp", true), "an engine built before the -extra list existed is unchanged");
    var keys = "l;ylfurujg0wf";
    var typed = "";
    var switched = false;
    foreach (var key in keys)
    {
        typed += switched ? real.Convert(key.ToString()) : key;
        var suggestion = real.Suggest(typed);
        if (suggestion is null) continue;
        typed = suggestion;
        switched = true;
    }
    Equal(true, switched, "user acceptance switched while typing");
    Equal("สวัสดีพี่เจได", typed, "user acceptance continued typing in target layout");
    var saved = WordWhitelist.Empty.With(keys, true);
    real.Whitelist = saved;
    for (var i = 1; i <= keys.Length; i++)
        Equal<string?>(null, real.Suggest(keys[..i]), "saved spelling survives typing through key " + i);
    Equal<string?>(null, real.Suggest(keys, true), "saved full word at a boundary");
    Equal("สวัสดี", real.Suggest("l;ylfu", true), "shorter completed word can still correct");
    Equal("สวัสดีพี่เจได", real.Convert(keys), "explicit manual conversion still works");
    Equal(true, ReferenceEquals(saved, saved.With(keys.ToUpperInvariant(), true)), "case-insensitive duplicate is a no-op");
    Equal(true, ReferenceEquals(saved, saved.With("two words", true)), "custom entry must be a single word");
    real.Whitelist = saved.With("L;YLFU", true).With("ะไระะำพ", true);
    Equal<string?>(null, real.Suggest("l;ylfu!", true), "punctuation after a protected word at a boundary");
    Equal<string?>(null, real.Suggest("l;ylfu!"), "punctuation after a protected word mid-typing");
    Equal<string?>(null, real.Suggest("ะไระะำพ", true), "Thai source spelling is protected");
    Equal("twitter", real.Convert("ะไระะำพ"), "Thai spelling remains manually convertible");
    real.Whitelist = WordWhitelist.Empty.With("ไทยl;ylfurujg0wf", true);
    var mixed = "before ไทยl;ylfu";
    Equal<string?>(null, real.Suggest(Pick(mixed)!.Value, mixed, false), "mixed-script custom word protects its current run");
    real.Whitelist = real.Whitelist.With("ไทยl;ylfurujg0wf", false);
    Equal("สวัสดี", real.Suggest(Pick(mixed)!.Value, mixed, false), "removing a word enables auto correction again");
    Console.WriteLine($"PASS: {checks} core checks");
}
finally
{
    Directory.Delete(directory, true);
}
