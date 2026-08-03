using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options => { options.IdleTimeout = TimeSpan.FromHours(2); options.Cookie.HttpOnly = true; options.Cookie.IsEssential = true; });
builder.Services.AddSingleton<DataStore>();
var app = builder.Build();
app.UseSession();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/me", (HttpContext ctx) => Results.Ok(new { signedIn = ctx.Session.GetString("userId") is not null, name = ctx.Session.GetString("userName") }));

app.MapPost("/api/analyse", async (HttpContext ctx, HttpRequest request, DataStore store) =>
{
    var form = await request.ReadFormAsync();
    var text = form["text"].ToString();
    var file = form.Files.GetFile("file");
    if (file is not null && file.Length > 0)
    {
        try { text = await DocumentText.ReadAsync(file); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "Add some writing or upload a TXT/DOCX file first." });
    if (text.Length > 150_000) return Results.BadRequest(new { error = "For the MVP, please check up to 150,000 characters at a time." });

    var report = WritingEngine.Analyse(text, file?.FileName);
    ctx.Session.SetString("lastReport", JsonSerializer.Serialize(report));
    var userId = ctx.Session.GetString("userId");
    if (userId is not null) store.SaveReport(userId, report);
    return Results.Ok(report);
});

app.MapPost("/api/auth/register", async (HttpContext ctx, Credentials input, DataStore store) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || !input.Email.Contains('@') || input.Password.Length < 8)
        return Results.BadRequest(new { error = "Use your name, a valid email, and a password of at least 8 characters." });
    var user = store.CreateUser(input.Name.Trim(), input.Email.Trim().ToLowerInvariant(), input.Password);
    if (user is null) return Results.Conflict(new { error = "An account with that email already exists." });
    ctx.Session.SetString("userId", user.Id); ctx.Session.SetString("userName", user.Name);
    var saved = ClaimLatestReport(ctx, user.Id, store);
    return Results.Ok(new { name = user.Name, saved });
});

app.MapPost("/api/auth/login", (HttpContext ctx, Credentials input, DataStore store) =>
{
    var user = store.Validate(input.Email.Trim().ToLowerInvariant(), input.Password);
    if (user is null) return Results.Unauthorized();
    ctx.Session.SetString("userId", user.Id); ctx.Session.SetString("userName", user.Name);
    var saved = ClaimLatestReport(ctx, user.Id, store);
    return Results.Ok(new { name = user.Name, saved });
});

app.MapPost("/api/auth/logout", (HttpContext ctx) => { ctx.Session.Clear(); return Results.NoContent(); });

app.MapGet("/api/history", (HttpContext ctx, DataStore store) =>
{
    var userId = ctx.Session.GetString("userId");
    return userId is null ? Results.Unauthorized() : Results.Ok(store.GetReports(userId));
});

app.MapPost("/api/revision", (HttpContext ctx, RevisionRequest input) =>
{
    if (ctx.Session.GetString("userId") is null) return Results.Unauthorized();
    return Results.Ok(WritingEngine.RevisionPrompts(input.Text));
});

app.Run();

static bool ClaimLatestReport(HttpContext ctx, string userId, DataStore store)
{
    var json = ctx.Session.GetString("lastReport");
    if (json is null) return false;
    var report = JsonSerializer.Deserialize<Report>(json);
    if (report is null) return false;
    store.SaveReport(userId, report); ctx.Session.Remove("lastReport"); return true;
}

record Credentials(string Name, string Email, string Password);
record RevisionRequest(string Text);
record Report(string Id, string? FileName, int WordCount, int Score, string Level, string Headline, string Summary, List<Signal> Signals, List<Passage> Passages, DateTimeOffset CreatedAt);
record Signal(string Tone, string Title, string Detail);
record Passage(string Text, string Note);

static class DocumentText
{
    public static async Task<string> ReadAsync(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is ".txt" or ".md") { using var reader = new StreamReader(file.OpenReadStream()); return await reader.ReadToEndAsync(); }
        if (ext != ".docx") throw new InvalidOperationException("PDF support is coming next. For now, use DOCX, TXT, or paste your text.");
        using var zip = new ZipArchive(file.OpenReadStream(), ZipArchiveMode.Read);
        var document = zip.GetEntry("word/document.xml") ?? throw new InvalidOperationException("This DOCX file could not be read.");
        using var stream = document.Open(); var xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join(" ", xml.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
    }
}

static class WritingEngine
{
    static readonly string[] GenericPhrases = ["it is important to note", "in conclusion", "delve into", "multifaceted", "ever-evolving", "plays a crucial role", "in today's world", "a testament to"];
    public static Report Analyse(string text, string? fileName)
    {
        var clean = text.Trim(); var words = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sentences = System.Text.RegularExpressions.Regex.Split(clean, @"(?<=[.!?])\s+").Where(s => s.Length > 5).ToList();
        var lengths = sentences.Select(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length).ToList();
        var variation = lengths.Count > 1 ? Math.Sqrt(lengths.Sum(n => Math.Pow(n - lengths.Average(), 2)) / lengths.Count) : 0;
        var genericHits = GenericPhrases.Sum(phrase => System.Text.RegularExpressions.Regex.Matches(clean, System.Text.RegularExpressions.Regex.Escape(phrase), System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
        var repeatedStarts = sentences.GroupBy(s => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2)).ToLowerInvariant()).Count(g => g.Key.Length > 4 && g.Count() > 1);
        var score = Math.Clamp(38 - (int)Math.Min(16, variation * 2) + genericHits * 7 + repeatedStarts * 5 + (words.Length < 50 ? 8 : 0), 12, 84);
        var level = score < 35 ? "Low risk" : score < 60 ? "Review suggested" : "More context needed";
        var signals = new List<Signal> {
            new("good", "Natural sentence variation", variation >= 4 ? "Your sentence lengths show healthy variation." : "Try mixing shorter observations with more developed explanations."),
            new(genericHits > 0 ? "warm" : "good", genericHits > 0 ? $"{genericHits} general phrase{(genericHits == 1 ? "" : "s")} to strengthen" : "Specific language present", genericHits > 0 ? "Add evidence, a named example, or your own interpretation where it fits." : "Your writing includes useful detail rather than relying on broad claims."),
            new("good", "This is a writing signal, not proof", "Results cannot establish who wrote a document or whether AI tools were used.") };
        var passages = sentences.Where(s => GenericPhrases.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase))).Take(3).Select(s => new Passage(s, "Consider replacing this broad phrasing with a concrete example or source.")).ToList();
        var headline = score < 35 ? "Your writing shows a distinct voice." : "A few patterns are worth revisiting.";
        var summary = score < 35 ? "This text contains natural variation and useful specificity." : "This is not proof of AI use. It highlights patterns you may want to make more personal and specific.";
        return new Report(Guid.NewGuid().ToString("N"), fileName, words.Length, score, level, headline, summary, signals, passages, DateTimeOffset.UtcNow);
    }
    public static object RevisionPrompts(string text)
    {
        var prompts = new List<string>();
        if (text.Length < 300) prompts.Add("Develop one key claim with an example, a source, or a brief explanation of why it matters.");
        if (GenericPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))) prompts.Add("Replace general phrases with the specific person, evidence, theory, or event you are referring to.");
        prompts.Add("Read one paragraph aloud. Does it sound like something you would naturally say? If not, revise it in your own words.");
        prompts.Add("Check that each factual claim is supported by a citation or your own analysis.");
        return new { prompts };
    }
}

class DataStore
{
    readonly string path; readonly object gate = new(); StoreData data;
    public DataStore(IWebHostEnvironment env) { var directory = Path.Combine(env.ContentRootPath, "App_Data"); Directory.CreateDirectory(directory); path = Path.Combine(directory, "verity-data.json"); data = File.Exists(path) ? JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path)) ?? new StoreData() : new StoreData(); }
    public UserRecord? CreateUser(string name, string email, string password) { lock (gate) { if (data.Users.Any(u => u.Email == email)) return null; var user = new UserRecord { Id = Guid.NewGuid().ToString("N"), Name = name, Email = email, PasswordHash = Passwords.Hash(password) }; data.Users.Add(user); Save(); return user; } }
    public UserRecord? Validate(string email, string password) { lock (gate) { var user = data.Users.FirstOrDefault(u => u.Email == email); return user is not null && Passwords.Verify(password, user.PasswordHash) ? user : null; } }
    public void SaveReport(string userId, Report report) { lock (gate) { if (!data.Reports.Any(r => r.Id == report.Id)) { data.Reports.Add(new StoredReport { UserId = userId, Report = report }); Save(); } } }
    public IEnumerable<Report> GetReports(string userId) { lock (gate) return data.Reports.Where(r => r.UserId == userId).Select(r => r.Report).OrderByDescending(r => r.CreatedAt).Take(20).ToList(); }
    void Save() => File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
}
class StoreData { public List<UserRecord> Users { get; set; } = []; public List<StoredReport> Reports { get; set; } = []; }
class UserRecord { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Email { get; set; } = ""; public string PasswordHash { get; set; } = ""; }
class StoredReport { public string UserId { get; set; } = ""; public Report Report { get; set; } = null!; }
static class Passwords { public static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32); return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash); } public static bool Verify(string password, string stored) { var parts = stored.Split(':'); if (parts.Length != 2) return false; var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[0]), 120_000, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(parts[1])); } }
