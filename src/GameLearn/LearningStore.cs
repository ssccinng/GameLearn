using Microsoft.Data.Sqlite;

namespace GameLearn;

public sealed class OfflineDictionary : IDisposable
{
    private readonly SqliteConnection? connection;
    private readonly Dictionary<string, DictionaryEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    public bool Available => connection is not null;
    public OfflineDictionary(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "Assets", "ecdict.sqlite");
        if (!File.Exists(path)) return;
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
    }
    public DictionaryEntry Lookup(string observed)
    {
        var word = observed.Trim().Replace('’', '\'').ToLowerInvariant();
        if (cache.TryGetValue(word, out var cached)) return cached with { Observed = observed };
        (string Word, string Phonetic, string Translation, string Lemma)? Query(string text)
        {
            if (connection is null) return null;
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT word,phonetic,translation,lemma FROM entries WHERE word=$w"; cmd.Parameters.AddWithValue("$w", text);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
        }
        var direct = Query(word);
        var lemma = direct is { } directEntry && !string.IsNullOrWhiteSpace(directEntry.Lemma) ? directEntry.Lemma : null;
        if (lemma is null && connection is not null)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT lemma FROM forms WHERE form=$w"; cmd.Parameters.AddWithValue("$w", word);
            if (cmd.ExecuteScalar() is string formLemma) lemma = formLemma;
        }
        var canonical = (lemma is null ? null : Query(lemma)) ?? direct;
        var result = canonical is { } entry
            ? new DictionaryEntry(entry.Word, entry.Phonetic, string.IsNullOrWhiteSpace(entry.Translation) ? "词典暂无中文释义，可使用 AI 语境解释。" : entry.Translation, observed,
                direct is { } form && !form.Word.Equals(entry.Word, StringComparison.OrdinalIgnoreCase) ? form.Phonetic : null,
                direct is { } formWithDefinition && !formWithDefinition.Word.Equals(entry.Word, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(formWithDefinition.Translation) ? formWithDefinition.Translation : null)
            : new DictionaryEntry(word, "", Available ? "离线词典未收录，可能是专有名词。可修正拼写或使用 AI 解释。" : "离线词典文件缺失，请重新解压完整的 GameLearn 程序包。", observed);
        cache[word] = result; return result;
    }
    public void Dispose() => connection?.Dispose();
}

public sealed class LearningStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly string directory;
    public LearningStore(string? directory = null)
    {
        this.directory = directory ?? AppSettings.DataDirectory;
        Directory.CreateDirectory(this.directory); Directory.CreateDirectory(Path.Combine(this.directory, "scenes"));
        db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.directory, "learning.db") }.ToString()); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS words(id INTEGER PRIMARY KEY, word TEXT UNIQUE COLLATE NOCASE, phonetic TEXT NOT NULL, translation TEXT NOT NULL, mastered INTEGER NOT NULL DEFAULT 0, notes TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS scenes(id TEXT PRIMARY KEY, game TEXT NOT NULL, source TEXT NOT NULL, at TEXT NOT NULL, image TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS encounters(id INTEGER PRIMARY KEY, word_id INTEGER NOT NULL REFERENCES words(id) ON DELETE CASCADE, scene_id TEXT NOT NULL REFERENCES scenes(id), observed TEXT NOT NULL, sentence TEXT NOT NULL, x REAL,y REAL,w REAL,h REAL, UNIQUE(word_id,scene_id));
            CREATE INDEX IF NOT EXISTS ix_encounters_word ON encounters(word_id,id DESC);
            CREATE TABLE IF NOT EXISTS explanations(cache_key TEXT PRIMARY KEY, explanation TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recent_recognitions(id INTEGER PRIMARY KEY, game TEXT NOT NULL, source TEXT NOT NULL, at TEXT NOT NULL, text TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<SavedWord> Words(string search = "", string game = "")
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT w.id,w.word,w.phonetic,w.translation,w.mastered,w.notes,COUNT(e.id),
              COALESCE((SELECT s2.game FROM encounters e2 JOIN scenes s2 ON s2.id=e2.scene_id WHERE e2.word_id=w.id ORDER BY e2.id DESC LIMIT 1),'')
            FROM words w LEFT JOIN encounters e ON e.word_id=w.id
            WHERE (w.word LIKE $q OR w.translation LIKE $q)
            AND ($g='' OR EXISTS(SELECT 1 FROM encounters eg JOIN scenes sg ON sg.id=eg.scene_id WHERE eg.word_id=w.id AND sg.game=$g))
            GROUP BY w.id ORDER BY MAX(e.id) DESC,w.word
            """;
        cmd.Parameters.AddWithValue("$q", "%" + search + "%"); cmd.Parameters.AddWithValue("$g", game);
        using var reader = cmd.ExecuteReader(); var list = new List<SavedWord>();
        while (reader.Read()) list.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7)));
        return list;
    }
    public void SaveRecentRecognition(RecognitionResult result)
    {
        var text = string.Join("\n", result.Lines.Where(l => l.Words.Any()).Select(l => l.Text.Trim()));
        if (string.IsNullOrWhiteSpace(text)) return;
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "SELECT game,source,text FROM recent_recognitions ORDER BY id DESC LIMIT 1";
        using (var r = cmd.ExecuteReader())
            if (r.Read() && r.GetString(0) == result.Frame.Game && r.GetString(1) == result.Frame.Source && r.GetString(2) == text) return;
        cmd.CommandText = "INSERT INTO recent_recognitions(game,source,at,text) VALUES($g,$s,$a,$t); DELETE FROM recent_recognitions WHERE id NOT IN (SELECT id FROM recent_recognitions ORDER BY id DESC LIMIT 200);";
        cmd.Parameters.AddWithValue("$g", result.Frame.Game); cmd.Parameters.AddWithValue("$s", result.Frame.Source);
        cmd.Parameters.AddWithValue("$a", result.Frame.Timestamp.ToString("O")); cmd.Parameters.AddWithValue("$t", text);
        cmd.ExecuteNonQuery(); transaction.Commit();
    }
    public IReadOnlyList<RecentRecognition> RecentRecognitions(string search = "")
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,game,source,at,text FROM recent_recognitions WHERE text LIKE $q OR game LIKE $q ORDER BY id DESC LIMIT 200";
        cmd.Parameters.AddWithValue("$q", "%" + search + "%");
        using var r = cmd.ExecuteReader(); var rows = new List<RecentRecognition>();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3)), r.GetString(4)));
        return rows;
    }
    public IReadOnlyList<Encounter> History(long wordId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT e.id,w.word,e.observed,e.sentence,s.game,s.at,s.image,e.x,e.y,e.w,e.h FROM encounters e JOIN words w ON w.id=e.word_id JOIN scenes s ON s.id=e.scene_id WHERE w.id=$id ORDER BY e.id DESC";
        cmd.Parameters.AddWithValue("$id", wordId); using var r = cmd.ExecuteReader(); var list = new List<Encounter>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5)),
            Path.Combine(directory, r.GetString(6)), new(r.GetDouble(7), r.GetDouble(8), r.GetDouble(9), r.GetDouble(10))));
        return list;
    }
    public SavedWord EnsureWord(DictionaryEntry entry)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO words(word,phonetic,translation) VALUES($w,$p,$t) ON CONFLICT(word) DO NOTHING";
        cmd.Parameters.AddWithValue("$w", entry.Word); cmd.Parameters.AddWithValue("$p", entry.Phonetic); cmd.Parameters.AddWithValue("$t", entry.Translation); cmd.ExecuteNonQuery();
        return Words(entry.Word).First(w => w.Word.Equals(entry.Word, StringComparison.OrdinalIgnoreCase));
    }
    public Encounter SaveEncounter(SavedWord word, string observed, RecognizedLine line, CapturedFrame frame)
    {
        var relative = Path.Combine("scenes", frame.Id + ".png");
        var path = Path.Combine(directory, relative);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path + ".tmp", frame.FullPng); File.Move(path + ".tmp", path, true);
        }
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "INSERT OR IGNORE INTO scenes VALUES($id,$g,$s,$at,$image)";
        cmd.Parameters.AddWithValue("$id", frame.Id.ToString()); cmd.Parameters.AddWithValue("$g", frame.Game); cmd.Parameters.AddWithValue("$s", frame.Source);
        cmd.Parameters.AddWithValue("$at", frame.Timestamp.ToString("O")); cmd.Parameters.AddWithValue("$image", relative); cmd.ExecuteNonQuery();
        cmd.Parameters.Clear(); cmd.CommandText = "INSERT OR IGNORE INTO encounters(word_id,scene_id,observed,sentence,x,y,w,h) VALUES($w,$s,$o,$text,$x,$y,$width,$height)";
        cmd.Parameters.AddWithValue("$w", word.Id); cmd.Parameters.AddWithValue("$s", frame.Id.ToString()); cmd.Parameters.AddWithValue("$o", observed); cmd.Parameters.AddWithValue("$text", line.Text);
        cmd.Parameters.AddWithValue("$x", line.Bounds.X); cmd.Parameters.AddWithValue("$y", line.Bounds.Y); cmd.Parameters.AddWithValue("$width", line.Bounds.Width); cmd.Parameters.AddWithValue("$height", line.Bounds.Height);
        cmd.ExecuteNonQuery(); transaction.Commit();
        return History(word.Id).First(e => e.ImagePath == path);
    }
    public void UpdateWord(long id, bool mastered, string notes)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE words SET mastered=$m,notes=$n WHERE id=$id";
        cmd.Parameters.AddWithValue("$m", mastered); cmd.Parameters.AddWithValue("$n", notes); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public void DeleteWord(long id)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM words WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); CleanupUnusedScenes();
    }
    public void Clear()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM words; DELETE FROM explanations; DELETE FROM recent_recognitions;"; cmd.ExecuteNonQuery(); CleanupUnusedScenes();
    }
    public void CleanupUnusedScenes()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM scenes WHERE id NOT IN (SELECT scene_id FROM encounters); SELECT image FROM scenes;";
        using var reader = cmd.ExecuteReader(); var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) retained.Add(Path.GetFullPath(Path.Combine(directory, reader.GetString(0))));
        foreach (var image in Directory.EnumerateFiles(Path.Combine(directory, "scenes"), "*.png"))
            if (!retained.Contains(Path.GetFullPath(image))) File.Delete(image);
    }
    public string? GetExplanation(string key)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT explanation FROM explanations WHERE cache_key=$k"; cmd.Parameters.AddWithValue("$k", key); return cmd.ExecuteScalar() as string;
    }
    public void SaveExplanation(string key, string explanation)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT OR REPLACE INTO explanations VALUES($k,$e)"; cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$e", explanation); cmd.ExecuteNonQuery();
    }
    public void Dispose() => db.Dispose();
}

public sealed class EncounterTracker(LearningStore store, OfflineDictionary dictionary)
{
    private readonly Dictionary<(long Word, string Game), (DateTimeOffset LastSeen, string Sentence, DateTimeOffset? MissingSince)> seen = new();
    private readonly Dictionary<long, DateTimeOffset> reminded = new();
    public void Reset() { seen.Clear(); reminded.Clear(); }
    public void Forget(long id)
    {
        foreach (var key in seen.Keys.Where(k => k.Word == id).ToArray()) seen.Remove(key);
        reminded.Remove(id);
    }
    public Encounter Learn(string observed, RecognizedLine line, CapturedFrame frame)
    {
        var word = store.EnsureWord(dictionary.Lookup(observed));
        var last = store.History(word.Id).FirstOrDefault(e => e.Game == frame.Game);
        seen[(word.Id, frame.Game)] = (frame.Timestamp, line.Text, null);
        if (last is not null && last.Sentence == line.Text && frame.Timestamp >= last.At && frame.Timestamp - last.At < TimeSpan.FromMinutes(5)) return last;
        return store.SaveEncounter(word, observed, line, frame);
    }
    public IReadOnlyList<Recall> Observe(RecognitionResult result, TriggerKind trigger)
    {
        var saved = store.Words().ToDictionary(w => w.Word, StringComparer.OrdinalIgnoreCase);
        var recalls = new List<Recall>(); var handled = new HashSet<long>();
        foreach (var line in result.Lines.Where(l => l.Confidence >= 0.85))
        foreach (var observed in line.Words)
        {
            var contextLine = SceneContext.ForLine(result, line);
            var lemma = dictionary.Lookup(observed).Word;
            if (!saved.TryGetValue(lemma, out var word) || !handled.Add(word.Id)) continue;
            var frame = result.Frame; var key = (word.Id, frame.Game);
            var hasSeen = seen.TryGetValue(key, out var previousSeen);
            var lastForGame = store.History(word.Id).FirstOrDefault(e => e.Game == frame.Game);
            bool add = lastForGame is null || lastForGame.Sentence != contextLine.Text;
            if (!add)
            {
                add = trigger == TriggerKind.Manual
                    ? frame.Timestamp - lastForGame!.At >= TimeSpan.FromMinutes(5)
                    : hasSeen ? previousSeen.MissingSince is { } missing && frame.Timestamp - missing >= TimeSpan.FromSeconds(10)
                    : frame.Timestamp - lastForGame!.At >= TimeSpan.FromSeconds(10);
            }
            seen[key] = (frame.Timestamp, contextLine.Text, null);
            if (!add) continue;
            var previous = store.History(word.Id).FirstOrDefault();
            var current = store.SaveEncounter(word, observed, contextLine, frame);
            if (previous is null || word.Mastered || (reminded.TryGetValue(word.Id, out var at) && frame.Timestamp - at < TimeSpan.FromMinutes(5))) continue;
            reminded[word.Id] = frame.Timestamp; recalls.Add(new(word, previous, current));
        }
        if (trigger == TriggerKind.Automatic)
        {
            foreach (var key in seen.Keys.Where(k => k.Game == result.Frame.Game && !handled.Contains(k.Word)).ToArray())
            {
                var presence = seen[key];
                seen[key] = (presence.LastSeen, presence.Sentence, presence.MissingSince ?? result.Frame.Timestamp);
            }
        }
        return recalls;
    }
}
