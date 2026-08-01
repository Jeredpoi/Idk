namespace Keyboop.Core.Layout;

/// <summary>
/// Курируемые списки слов, на которых стоит детектор раскладки. Перенесены из macOS-версии
/// без изменений — каждый список там собирался по реальным промахам, и менять его состав
/// без такого же замера нельзя.
/// </summary>
public static class ExtraWords
{
    /// <summary>
    /// Частые русские сокращения без точек. Двухбуквенные тоже нужны: детектор сперва идёт в
    /// словарь, а латинская форма сокращения часто лежит в EN-словаре мусором (тк→«nr», шт→«in»),
    /// и без этих записей выходила ложная конверсия.
    /// </summary>
    public static readonly IReadOnlySet<string> RuAbbr = new HashSet<string>(StringComparer.Ordinal)
    {
        "тк", "тд", "тп", "тн", "тч", "др", "пр", "см", "ср", "гл", "гг", "шт", "кв", "эт", "оф", "пп",
        "зы", "напр", "прим", "примеч", "сокр", "рис", "табл", "стр", "разд", "итд", "итп", "чтд", "гос",
        "тыс", "млн", "млрд", "руб", "коп", "экз", "изд", "вып", "пер", "просп", "наб", "корп", "каб",
        "ауд", "тел", "факс", "имхо", "спс", "пжл", "плз", "инфа",
    };

    /// <summary>
    /// Короткие слова и междометия (2–4 буквы), которых нет в hunspell-словаре. Слова короче
    /// четырёх букв не проходят триграммную проверку, поэтому спасти их может только словарь.
    /// </summary>
    public static readonly IReadOnlySet<string> RuShort = new HashSet<string>(StringComparer.Ordinal)
    {
        "ого", "агага", "ага", "угу", "угум", "эге", "эх", "эхх", "ах", "ахах", "ох", "ой", "ай", "эй",
        "уф", "фу", "фух", "ха", "хах", "хаха", "хе", "хех", "хи", "хих", "опа", "упс", "ауч", "ау",
        "эге-гей", "оу", "ну", "го", "че", "чё", "ща", "щас", "ок", "тоже", "тож", "норм", "лан", "оке",
        "окей", "ясн", "оки", "угусь", "неа", "нее", "неее", "даа", "дада", "нуу", "воу", "вау", "офк",
        "омг", "емое", "ёмое", "блин", "капец",
    };

    /// <summary>
    /// Латинские написания, которые формально есть в EN-словаре, но почти всегда набраны как
    /// русское слово в неверной раскладке. Правило пополнения: если латинская форма — реальное
    /// английское слово, класть сюда можно ТОЛЬКО когда она редкая, а русская частотная.
    /// Частые английские слова (in, here, vs, of) не класть никогда — сломаем английский.
    /// </summary>
    public static readonly IReadOnlySet<string> ForceRuAmb = new HashSet<string>(StringComparer.Ordinal)
    {
        "ghb", "ds", "ns", "yt", "pf", "bp", "nj", "kb", "yb", "nt", "nf", "ult", "lt", "lf", "yj", "yf",
        "gj", "jn", "jy", "ye", "tot",
    };

    /// <summary>
    /// Зеркало ForceRuAmb: частые английские сокращения и контракции, которых нет в EN-словаре,
    /// а их кириллическая форма — гиббериш. Набраны на русской раскладке → возвращаем в латиницу.
    /// </summary>
    public static readonly IReadOnlySet<string> ForceEnAmb = new HashSet<string>(StringComparer.Ordinal)
    {
        "idk", "tbh", "ngl", "lmao", "smh", "tldr", "brb", "wtf", "ffs", "istg", "nvm", "ikr", "irl", "rly",
        "pls", "plz", "thx", "dm", "i'm", "i'll", "i've", "i'd", "you're", "you'll", "you've", "we're",
        "we'll", "we've", "they're", "they'll", "he's", "she's", "it's", "that's", "there's", "here's",
        "what's", "who's", "let's", "don't", "doesn't", "didn't", "won't", "can't", "couldn't", "wouldn't",
        "shouldn't", "isn't", "aren't", "wasn't", "weren't", "haven't", "hasn't", "hadn't", "ain't",
        "y'all", "gonna", "wanna",
    };

    /// <summary>
    /// Слова, которые не конвертируем никогда и ни в каком контексте. Обычно это бренды, чья
    /// раскладочная пара — валидное слово другого языка (вк → dr, а «dr» есть в EN-словаре).
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultKeep = new HashSet<string>(StringComparer.Ordinal)
    {
        "вк", "тг", "ок", "оз", "лс", "сек", "мск", "зп", "дз", "дс", "ндфл", "тс",
    };

    /// <summary>
    /// Дефисные иностранные термины в канонической латинской форме. Дефисное слово конвертим
    /// целиком только если его перевод точно здесь — иначе «из-за», «что-то», «по-русски» пострадали бы.
    /// </summary>
    public static readonly IReadOnlySet<string> HyphenTerms = new HashSet<string>(StringComparer.Ordinal)
    {
        "e-ink", "e-mail", "e-book", "e-books", "e-commerce", "e-sport", "e-sports", "e-paper", "wi-fi",
        "hi-fi", "sci-fi", "lo-fi", "t-shirt", "x-ray", "know-how", "co-op", "t-rex", "non-stop",
        "must-have", "all-in", "all-in-one", "plug-in", "add-on", "follow-up",
    };

    /// <summary>
    /// Русские слова-классификаторы: одиночная латинская буква после них — маркер, а не предлог.
    /// «витамин d» не должен превратиться в «витамин в».
    /// </summary>
    public static readonly IReadOnlySet<string> RuLabelClassifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "витамин", "витамины", "группа", "группы", "тип", "типа", "класс", "вариант", "версия", "модель",
        "серия", "раздел", "пункт", "номер", "уровень", "этап", "раунд", "часть", "рис", "таблица",
        "формат",
    };

    /// <summary>
    /// Короткие английские токены, которые русскоязычные реально вставляют в русский текст
    /// («Спартак vs Зенит», «смотрю tv»). Не конвертируем даже в русском контексте фразы.
    /// </summary>
    public static readonly IReadOnlySet<string> EnKeepShort = new HashSet<string>(StringComparer.Ordinal)
    {
        "in", "on", "at", "to", "of", "is", "it", "if", "as", "an", "or", "by", "we", "he", "me", "be",
        "my", "up", "us", "do", "go", "no", "so", "and", "the", "for", "you", "ok", "tv", "ps", "vs", "dj",
        "gg", "id", "ip", "pm", "am", "hr", "pr", "ai", "uk", "eu", "cv", "afk", "ceo", "ru", "jar", "ns",
        "pf", "bp", "nj", "kb", "yb", "nt", "nf", "ult", "lt", "ah", "oh", "eh", "hm", "ha", "lol", "omg",
        "wow",
    };

    /// <summary>
    /// Английские слова-классификаторы: после них одиночная буква — маркер («vitamin d», «plan b»),
    /// а не русский предлог, набранный в английской раскладке.
    /// </summary>
    public static readonly IReadOnlySet<string> LabelClassifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "vitamin", "vitamins", "hepatitis", "gen", "generation", "grade", "grades", "type", "types",
        "series", "model", "models", "plan", "plans", "option", "options", "answer", "answers", "choice",
        "choices", "question", "questions", "section", "sections", "appendix", "exhibit", "schedule",
        "annex", "figure", "figures", "variant", "variants", "size", "sizes", "tier", "phase", "blood",
        "omega", "chapter", "clause", "paragraph", "exam", "quiz", "mark", "round", "rounds", "part",
        "parts", "item", "items", "version", "class", "category", "level", "stage", "band", "division",
        "league", "point", "note", "notes", "step", "steps", "unit", "units",
    };

    /// <summary>
    /// Русские слова, которых нет в hunspell-словаре: жаргон, сленг, мемы, мат.
    /// </summary>
    public static readonly IReadOnlySet<string> Ru = new HashSet<string>(StringComparer.Ordinal)
    {
        "абьюз", "абьюзер", "абьюзить", "агрить", "агриться", "альтушка", "анк", "ауф", "ауфный", "афк",
        "ахуенно", "ахуеть", "ахуительно", "байтинг", "байтить", "бан", "банить", "банхаммер", "бафнуть",
        "бафф", "баффать", "бести", "бимбо", "бля", "бляди", "блядина", "блядки", "блядский", "блядство",
        "блядун", "блять", "бомбануло", "бомбит", "буллинг", "бумер", "вайб", "вайбовый", "вайп",
        "вайпнуть", "вебка", "винрейт", "вкрашиться", "войс", "впизде", "впизду", "въебать", "выебать",
        "выебок", "выебон", "газлайтинг", "газлайтить", "ганк", "ганкать", "гг", "гигачад", "гиф", "гифка",
        "гифки", "гифку", "гифке", "гифкой", "гифок", "гифкам", "гифками", "гифках", "глоуап", "говноед",
        "гондон", "гостинг", "гринфлаг", "данж", "делулу", "делюжншип", "додик", "доебался", "доебаться",
        "долбоеб", "долбоёб", "донат", "донатить", "дохуя", "дроп", "дропнуть", "думер", "душнила",
        "душнить", "ебало", "ебальник", "ебанарот", "ебанат", "ебанатик", "ебанулся", "ебанутый", "ебануть",
        "ебанушка", "ебанько", "ебарь", "ебать", "ебаться", "ебическая", "еблан", "ебло", "ебля", "ебнутый",
        "ебнуть", "ебнуться", "ебырь", "жиза", "жыза", "забайтить", "забанить", "задрот", "задротить",
        "заеб", "заебал", "заебала", "заебато", "заебать", "заебашить", "заебенить", "заебись", "заебумба",
        "заебца", "заебцовый", "залутать", "запара", "захуярить", "зашибато", "зашибись", "зашквар",
        "зашкварно", "зашкварный", "зумер", "изи", "изян", "имба", "имбаланс", "имбовый", "катка", "кек",
        "кекать", "кекв", "кемпер", "кемперить", "кибербуллинг", "кикнуть", "кин", "киннить", "краболюб",
        "краш", "крашиха", "крашка", "крашнуться", "кринге", "кринж", "кринжовать", "кринжовый", "крипово",
        "крипота", "кумер", "лагать", "лейм", "ливать", "ливнуть", "лол", "лут", "лутать", "масик", "мейн",
        "мемас", "мемчик", "мерч", "милфа", "моргенштерн", "мудак", "мудила", "мудозвон", "наебать",
        "наебка", "наебнуть", "наебнуться", "найкпро", "напиздеть", "нафармить", "нахуй", "нахуя",
        "нахуярить", "нерф", "нерфить", "нихуя", "норми", "нормис", "нуб", "нубас", "объебать", "объебос",
        "орнул", "ору", "оскуфиться", "отхуярить", "охуевать", "охуевший", "охуенно", "охуенный", "охуеть",
        "охуительно", "охуительный", "пизда", "пиздабол", "пиздануть", "пиздануться", "пиздато", "пиздатый",
        "пиздеть", "пиздец", "пиздить", "пиздобол", "пиздос", "пизду", "пиздуй", "пизды", "пиздюк",
        "пиздюлей", "пиздюли", "пиздюлина", "пиздюлька", "пиздёж", "пикми", "пов", "пог", "погчамп",
        "потрачено", "похуй", "похую", "почиллить", "приебаться", "припизднутый", "проебать", "проебаться",
        "пруф", "пуш", "разъебать", "рандом", "рандомный", "распиздец", "распиздон", "распиздяй",
        "распиздяйство", "рачить", "раш", "рашить", "редфлаг", "рофел", "рофл", "рофлить", "рэдфлаг",
        "сасно", "сасный", "сигмабой", "симп", "ситуэйшеншип", "ситуэйшншип", "скилл", "скилловый",
        "скиллы", "скуф", "скуфидон", "скуфьян", "слэй", "смурф", "спамить", "спиздил", "спиздить",
        "ссанина", "стэн", "стэнить", "съебать", "съебаться", "тащер", "твинк", "тильт", "тильтовать",
        "токс", "токсик", "топчик", "треш", "трэш", "фарм", "фармить", "флекс", "флексер", "флексить",
        "флуд", "флудить", "хайп", "хайпить", "хайповый", "харизмат", "харош", "хуевертить", "хуевый",
        "хуеплет", "хуесос", "хуета", "хуила", "хуйло", "хуйнуть", "хуйня", "хули", "хуярить", "хуячить",
        "хуёво", "хуёвый", "чечик", "чилл", "чиллить", "чиназес", "чит", "читер", "чушпан", "шеймить",
        "шипперинг", "шипперить", "шмот",
    };

    /// <summary>
    /// Английский сленг и интернет-лексика, которой нет в hunspell-словаре.
    /// </summary>
    public static readonly IReadOnlySet<string> En = new HashSet<string>(StringComparer.Ordinal)
    {
        "afk", "ahh", "ahhh", "alphamale", "amongus", "aurafarming", "auraing", "backrooms", "baka",
        "banger", "bangers", "based", "bbg", "beigeflag", "benching", "betamale", "blackpill",
        "blackpilled", "blud", "bluepill", "bluepilled", "boomer", "bopper", "boppers", "boujee",
        "brainrot", "brainrotted", "brainrotting", "breadcrumbing", "bruh", "bussin", "bussing", "chadlike",
        "chadly", "chads", "cheugy", "chronicallyonline", "clickbaity", "cookin", "coomer", "copege",
        "copepill", "copers", "copium", "crashing", "crashout", "cringemax", "cringey", "cringy", "cuffing",
        "dawg", "deadass", "delu", "delulu", "delusionship", "doomer", "doomscroll", "doomscrolled",
        "doomscrolling", "drippin", "driptastic", "edgemaxxing", "fam", "famo", "fanum", "fanumtax",
        "fanumtaxed", "finna", "fitcheck", "fomo", "frfr", "fuckboy", "gaslighter", "gaslighting", "gaslit",
        "gatekeep", "gatekeeping", "gatekept", "gg", "ggwp", "ghosted", "ghosting", "gigachad",
        "gigachadded", "girlboss", "girlbossing", "glazed", "glazer", "glazers", "glhf", "glizzy", "glowup",
        "gng", "goated", "goatworthy", "goblincore", "goonmaxxing", "greenflag", "griddy", "grindset",
        "grindsetmindset", "gyat", "gyatt", "gyatted", "gyattzilla", "hardlaunch", "highkey", "hodl",
        "hopium", "huzz", "huzzmaxxing", "ick", "idk", "ily", "incel", "irl", "kek", "lmao", "lmfao",
        "lockin", "locking", "looksmax", "looksmaxing", "looksmaxxer", "looksmaxxing", "lowkey",
        "maldening", "malding", "mewed", "mewer", "mewers", "mewing", "midcurve", "midkey", "mids", "mog",
        "mogged", "mogger", "moggers", "mogging", "moots", "ngl", "nocap", "nocapping", "noob", "npc",
        "npcbehavior", "npcmode", "npcs", "ohioed", "omegamale", "oomf", "oppblock", "opps", "orbiting",
        "owo", "pepe", "pepega", "periodt", "phantomping", "pickme", "pilled", "pog", "pogchamp", "poggers",
        "pookie", "pookies", "pov", "pwned", "ragebait", "ragebaiting", "ratiod", "ratioed", "ratioing",
        "receipts", "redflag", "redpill", "redpilled", "rizz", "rizzed", "rizzgod", "rizzing", "rizzler",
        "rizzlord", "rizzmaxxing", "roaching", "sadge", "sheesh", "sheeshed", "sheeshing", "shmlawg",
        "sidequest", "sigmagrind", "sigmamale", "sigmas", "simped", "simping", "simps", "situationship",
        "situationships", "skibidi", "skibidied", "slappers", "slaps", "slayed", "smh", "smol", "snatched",
        "softboy", "softlaunch", "soyboy", "squadup", "stanned", "stanning", "stans", "stonks", "sus",
        "sussy", "terminallyonline", "thicc", "thot", "tldr", "touchgrass", "tweakin", "tweaking", "twins",
        "uncooked", "uwu", "vibecheck", "vibed", "vibeshift", "vibey", "vibing", "washedup", "whitepill",
        "whitepilled", "wojak", "yapfest", "yapper", "yappers", "yappin", "yass", "yasss", "yeet", "yeeted",
        "yeeting", "yolo", "zang", "zombieing", "zoomer",
    };

    /// <summary>
    /// Аббревиатуры без гласных — их статистика триграмм не вытягивает.
    /// ⚠️ Применять ТОЛЬКО когда исходник не является настоящим словом своего языка: иначе
    /// список съедал валидные русские слова, чья латинская форма совпала с аббревиатурой
    /// («еды» → tls, «св» → cd, «шву» → ide).
    /// </summary>
    public static readonly IReadOnlySet<string> AbbreviationForceSwap = new HashSet<string>(StringComparer.Ordinal)
    {
        "http", "https", "url", "uri", "api", "rest", "json", "xml", "yaml", "csv", "html", "css", "sdk",
        "cli", "gui", "ide", "ssh", "ftp", "tcp", "udp", "ip", "dns", "vpn", "ssl", "tls", "smtp", "jwt",
        "cors", "sql", "nosql", "git", "npm", "yarn", "k8s", "aws", "gcp", "kpi", "crm", "seo", "smm",
        "mvp", "cpu", "gpu", "ram", "ssd", "hdd", "usb", "pdf", "mp3", "mp4", "png", "jpg", "jpeg", "svg",
        "gif", "ddos", "iot", "llm", "gpt", "ml", "ai", "ui", "ux", "db", "os", "io", "qa", "ci", "cd",
        "webp", "mvc", "orm", "cdn", "dom",
    };

}
