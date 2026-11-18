using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace OuterVoice;

public class EventDef
{
    public int Priority { get; set; } = 1;
    public int CooldownSeconds { get; set; }
    public string Trigger { get; set; } = string.Empty;
}

public class Line
{
    public string Id { get; set; } = string.Empty;
    public string Event { get; set; } = string.Empty;
    public string Mood { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
    public string Audio { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// For partner events (Kardia, Dance Partner): true = plays when the partner is first chosen,
    /// false = plays when the partner is swapped to someone else.
    /// </summary>
    public bool Initial { get; set; }

    /// <summary>Job this line belongs to ("SGE", "WHM", ... or "ALL"). Filled in on load.</summary>
    [JsonIgnore] public string Job { get; set; } = string.Empty;

    /// <summary>Resolved full path to the clip, or null if no clip exists yet.</summary>
    [JsonIgnore] public string? AudioPath { get; set; }
}

/// <summary>
/// A character's voice pack: one folder containing voicepack.json plus audio clips.
///
///   voicepack.json      name, character, events, and lines grouped by job
///   SGE\raise_01.mp3    clips for a job live in a folder named after the job...
///   ALL\wipe_01.mp3     ...shared lines (any job) live in ALL\
///   raise_01.mp3        ...or loose in the pack folder, which also works.
/// </summary>
public class VoicePack
{
    public const string FileName = "voicepack.json";
    public const string SharedJob = "ALL";

    /// <summary>Display name of the pack.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Character this pack voices ("First Last@World"). Used later for sharing packs with friends.</summary>
    public string Character { get; set; } = string.Empty;

    public string Version { get; set; } = "1";
    public int GlobalCooldownSeconds { get; set; } = 20;
    /// <summary>Optional timing overrides per event; anything missing uses the built-in defaults.</summary>
    public Dictionary<string, EventDef> Events { get; set; } = new();

    /// <summary>Player-made events (emotes, casts, statuses, Moodles).</summary>
    public List<CustomEvent> CustomEvents { get; set; } = new();

    /// <summary>Lines grouped by job abbreviation. "ALL" = lines usable on any job.</summary>
    public Dictionary<string, List<Line>> Jobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public string Folder { get; set; } = string.Empty;

    /// <summary>The character's portrait for battle-talk pop-ups: an image file in the pack folder.</summary>
    public string Portrait { get; set; } = string.Empty;

    /// <summary>Full path of the portrait image, if there is one (only ever inside the pack folder).</summary>
    [JsonIgnore]
    public string? PortraitPath
    {
        get
        {
            var name = Path.GetFileName(Portrait); // never let a pack point outside its own folder
            if (string.IsNullOrEmpty(name) || Folder.Length == 0) return null;
            var full = Path.Combine(Folder, name);
            return File.Exists(full) ? full : null;
        }
    }

    /// <summary>Portrait size in the pop-up, as a percentage of the portrait area (10-100).</summary>
    public int PortraitSize { get; set; } = 55;

    /// <summary>Portrait position: 0 = far left, 100 = far right.</summary>
    public int PortraitX { get; set; } = 30;

    /// <summary>Portrait position: 0 = top, 100 = bottom (resting on the text box).</summary>
    public int PortraitY { get; set; } = 100;

    public static readonly string[] PortraitExtensions = [".png", ".jpg", ".jpeg"];
    public const long MaxPortraitBytes = 8 * 1024 * 1024;

    /// <summary>Copies an image into the pack folder as its portrait.</summary>
    public void ImportPortrait(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!PortraitExtensions.Contains(ext))
            throw new InvalidDataException("Portraits must be .png or .jpg images.");
        if (new FileInfo(sourcePath).Length > MaxPortraitBytes)
            throw new InvalidDataException("That image is over 8 MB. Try a smaller one.");

        foreach (var old in PortraitExtensions.Select(e => Path.Combine(Folder, "portrait" + e)).Where(File.Exists))
            if (!string.Equals(old, sourcePath, StringComparison.OrdinalIgnoreCase)) File.Delete(old);

        var name = "portrait" + ext;
        var dest = Path.Combine(Folder, name);
        if (!string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, dest, overwrite: true);
        Portrait = name;
    }
    [JsonIgnore] public IEnumerable<Line> AllLines => Jobs.Values.SelectMany(l => l);

    public static VoicePack Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        VoicePack pack;

        if (File.Exists(path))
        {
            pack = JsonConvert.DeserializeObject<VoicePack>(File.ReadAllText(path))
                   ?? throw new InvalidDataException($"{FileName} is empty.");
            pack.Jobs = new Dictionary<string, List<Line>>(pack.Jobs, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            throw new FileNotFoundException($"No {FileName} in that folder.");
        }

        pack.Folder = folder;
        foreach (var (job, lines) in pack.Jobs)
        {
            foreach (var line in lines)
            {
                line.Text = CleanText(line.Text);
                line.Job = job.ToUpperInvariant();
                line.AudioPath = ResolveAudio(folder, line);
            }
        }

        return pack;
    }

    /// <summary>
    /// Removes control characters. The game's text system uses invisible control codes, and a
    /// crafted line could try to smuggle them into chat or the pop-up.
    /// </summary>
    public static string CleanText(string? text)
    {
        // Line breaks and tabs become spaces; every other control character is dropped.
        var chars = (text ?? string.Empty)
                    .Select(c => c is '\n' or '\r' or '\t' ? ' ' : c)
                    .Where(c => !char.IsControl(c))
                    .ToArray();
        return System.Text.RegularExpressions.Regex.Replace(new string(chars), " {2,}", " ").Trim();
    }

    /// <summary>
    /// A line whose event no longer exists (a retired built-in event, or a deleted custom event).
    /// It never plays; the Lines tab lists these so they can be moved or deleted.
    /// </summary>
    public bool IsUnused(Line line) =>
        KnownEvents.Get(line.Event) == null && CustomEvents.All(c => c.Id != line.Event);

    public bool HasJob(string job) => Jobs.TryGetValue(job, out var lines) && lines.Count > 0;

    /// <summary>Lines for an event on a job: that job's own lines plus shared lines.</summary>
    public List<Line> LinesFor(string job, string eventName, bool includeShared = true)
    {
        var result = new List<Line>();
        if (Jobs.TryGetValue(job, out var own))
            result.AddRange(own.Where(l => l.Event == eventName));
        if (includeShared && !job.Equals(SharedJob, StringComparison.OrdinalIgnoreCase) && Jobs.TryGetValue(SharedJob, out var shared))
            result.AddRange(shared.Where(l => l.Event == eventName));
        return result;
    }

    /// <summary>Built-in events followed by this pack's custom events.</summary>
    public IEnumerable<KnownEvents.Info> AllEvents() =>
        KnownEvents.All.Concat(CustomEvents.Select(c => c.ToInfo()));

    public EventDef GetEvent(string name)
    {
        if (Events.TryGetValue(name, out var def))
            return def;
        if (CustomEvents.FirstOrDefault(c => c.Id == name) is { } custom)
            return new EventDef { Priority = custom.Priority, CooldownSeconds = custom.CooldownSeconds, Trigger = custom.Summary };

        var known = KnownEvents.Get(name);
        return known == null
            ? new EventDef()
            : new EventDef { Priority = known.Priority, CooldownSeconds = known.CooldownSeconds, Trigger = known.Trigger };
    }

    /// <summary>An empty pack for a folder that doesn't have one yet.</summary>
    public static VoicePack CreateNew(string folder, string character)
    {
        var first = character.Split('@')[0].Trim();
        var pack = new VoicePack
        {
            Name = first.Length > 0 ? first.Split(' ')[0] : "New voice pack",
            Character = character,
            Jobs = new Dictionary<string, List<Line>>(StringComparer.OrdinalIgnoreCase) { [SharedJob] = new() },
            Folder = folder,
        };
        pack.Save();
        return pack;
    }

    public static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        ContractResolver = new DefaultContractResolver
        {
            // camelCase property names, but leave job keys ("SGE") and event names alone.
            NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false },
        },
    };

    public void Save() => SaveTo(Folder);

    public void SaveTo(string folder)
    {
        Directory.CreateDirectory(folder);
        // Write to a temp file first so a crash mid-save can't corrupt the pack.
        var path = Path.Combine(folder, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(this, JsonSettings));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A deep copy (no audio paths resolved) for exporting.</summary>
    public VoicePack CloneForExport()
    {
        var copy = JsonConvert.DeserializeObject<VoicePack>(JsonConvert.SerializeObject(this, JsonSettings))!;
        copy.Jobs = new Dictionary<string, List<Line>>(copy.Jobs, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>A new line id like "raise_04" that isn't used yet in this job.</summary>
    public string NextLineId(string job, string eventName)
    {
        var used = Jobs.TryGetValue(job, out var lines) ? lines.Select(l => l.Id).ToHashSet() : new HashSet<string>();
        for (var i = 1; ; i++)
        {
            var id = $"{eventName}_{i:00}";
            if (!used.Contains(id)) return id;
        }
    }

    /// <summary>
    /// Copies an audio file into the pack (into a folder named after the line's job) and points
    /// the line at it, so the whole pack folder can be zipped and shared.
    /// </summary>
    public void ImportAudio(Line line, string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var dir = Path.Combine(Folder, line.Job);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, line.Id + ext);

        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, dest, overwrite: true);

        line.Audio = line.Id + ext;
        line.AudioPath = ResolveAudio(Folder, line);
    }

    public void RefreshAudio(Line line) => line.AudioPath = ResolveAudio(Folder, line);

    // Looks in <pack>\<JOB>\ first, then the pack folder (and a voice\ subfolder), by the
    // audio field or simply by <id>.mp3 / <id>.wav.
    private static string? ResolveAudio(string folder, Line line)
    {
        var dirs = new[] { Path.Combine(folder, line.Job), folder, Path.Combine(folder, "voice") };
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(line.Audio))
            names.Add(line.Audio);
        names.Add(line.Id + ".mp3");
        names.Add(line.Id + ".wav");

        return dirs.SelectMany(d => names.Select(n => Path.Combine(d, n))).FirstOrDefault(File.Exists);
    }
}
