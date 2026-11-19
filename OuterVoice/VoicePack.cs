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

    /// <summary>
    /// For a line in the Every job group: the jobs it's limited to (say, all healers). Empty = every
    /// job. Lines for a single job live in that job's own group instead.
    /// </summary>
    public List<string> OnlyJobs { get; set; } = new();

    [JsonIgnore]
    public bool IsLimited => OnlyJobs.Count > 0;

    /// <summary>Whether this line can play on a job.</summary>
    public bool PlaysOn(string job) =>
        !string.Equals(Job, VoicePack.SharedJob, StringComparison.OrdinalIgnoreCase)
            ? string.Equals(Job, job, StringComparison.OrdinalIgnoreCase)
            : OnlyJobs.Count == 0 || OnlyJobs.Contains(job, StringComparer.OrdinalIgnoreCase);

    /// <summary>The jobs it plays on, for display: "Every job", "SGE", "Healers", "WHM, SGE"...</summary>
    [JsonIgnore]
    public string JobsLabel
    {
        get
        {
            if (!string.Equals(Job, VoicePack.SharedJob, StringComparison.OrdinalIgnoreCase)) return Job;
            if (OnlyJobs.Count == 0) return "Every job";
            var set = OnlyJobs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();
            foreach (var (role, jobs) in KnownEvents.Roles)
                if (jobs.All(set.Contains))
                {
                    parts.Add(role);
                    set.ExceptWith(jobs);
                }
            parts.AddRange(KnownEvents.CombatJobs.Where(set.Contains));
            return string.Join(", ", parts);
        }
    }

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
            result.AddRange(shared.Where(l => l.Event == eventName && l.PlaysOn(job)));
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

        // Jobs whose lines all moved elsewhere don't need an empty group in the file.
        foreach (var empty in Jobs.Where(j => j.Value.Count == 0 && !j.Key.Equals(SharedJob, StringComparison.OrdinalIgnoreCase))
                                  .Select(j => j.Key).ToList())
            Jobs.Remove(empty);

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

    /// <summary>
    /// Renames lines whose id doesn't follow "&lt;event&gt;_NN" (older packs used names like "start_01"
    /// for duty start). Recordings are copied to the new name; returns the old files that can be
    /// deleted once the pack is saved.
    /// </summary>
    public List<string> NormalizeLineIds()
    {
        var oldFiles = new List<string>();
        foreach (var (job, lines) in Jobs)
        {
            foreach (var line in lines)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line.Id, $"^{System.Text.RegularExpressions.Regex.Escape(line.Event)}_\\d+$"))
                    continue;

                var oldAudio = line.AudioPath;
                line.Id = NextLineId(job, line.Event);
                if (oldAudio != null && File.Exists(oldAudio))
                {
                    var name = line.Id + Path.GetExtension(oldAudio).ToLowerInvariant();
                    var dest = Path.Combine(Path.GetDirectoryName(oldAudio)!, name);
                    File.Copy(oldAudio, dest, overwrite: true);
                    line.Audio = name;
                    if (oldAudio.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) oldFiles.Add(oldAudio);
                }
                line.AudioPath = ResolveAudio(Folder, line);
            }
        }
        return oldFiles;
    }

    private static readonly string[] AudioExtensions = [".mp3", ".wav"];

    /// <summary>
    /// Recordings in the pack folder that no line uses (old copies left when lines moved or were
    /// renamed, deleted lines...), plus old portrait images. Exports never include these.
    /// </summary>
    public List<FileInfo> UnusedFiles()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Jobs.Values.SelectMany(l => l))
            if (line.AudioPath != null) used.Add(Path.GetFullPath(line.AudioPath));
        if (PortraitPath is { } portrait) used.Add(Path.GetFullPath(portrait));

        if (Folder.Length == 0 || !Directory.Exists(Folder)) return new List<FileInfo>();
        return Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(f =>
                        {
                            var ext = f.Extension.ToLowerInvariant();
                            var isAudio = AudioExtensions.Contains(ext);
                            var isOldPortrait = PortraitExtensions.Contains(ext) &&
                                                f.Name.StartsWith("portrait", StringComparison.OrdinalIgnoreCase);
                            return (isAudio || isOldPortrait) && !used.Contains(f.FullName);
                        })
                        .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
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

    /// <summary>
    /// Moves a line to another job (or to Every job). Its recording is copied into that job's folder
    /// (the old copy is left alone, so Discard still finds it), and the id changes only if the new job
    /// already has a line with the same id.
    /// </summary>
    /// <summary>
    /// Sets the jobs a line plays on: none = every job, one = that job's own group, several = the
    /// Every job group limited to them.
    /// </summary>
    public void SetLineJobs(Line line, IReadOnlyCollection<string> jobs)
    {
        var picked = KnownEvents.CombatJobs.Where(j => jobs.Contains(j, StringComparer.OrdinalIgnoreCase)).ToList();
        if (picked.Count == 1)
        {
            MoveLine(line, picked[0]);
            line.OnlyJobs = new List<string>();
        }
        else
        {
            MoveLine(line, SharedJob);
            line.OnlyJobs = picked;
        }
    }

    public void MoveLine(Line line, string newJob)
    {
        if (string.Equals(line.Job, newJob, StringComparison.OrdinalIgnoreCase)) return;
        if (Jobs.TryGetValue(line.Job, out var from)) from.Remove(line);
        if (!Jobs.TryGetValue(newJob, out var to)) Jobs[newJob] = to = new List<Line>();

        var oldAudio = line.AudioPath;
        var id = to.Any(l => l.Id == line.Id) ? NextLineId(newJob, line.Event) : line.Id;
        line.Job = newJob;
        line.Id = id;

        if (oldAudio != null && File.Exists(oldAudio))
        {
            var dir = Path.Combine(Folder, newJob);
            Directory.CreateDirectory(dir);
            var name = id + Path.GetExtension(oldAudio).ToLowerInvariant();
            var dest = Path.Combine(dir, name);
            if (!string.Equals(Path.GetFullPath(dest), Path.GetFullPath(oldAudio), StringComparison.OrdinalIgnoreCase))
                File.Copy(oldAudio, dest, overwrite: true);
            line.Audio = name;
        }
        to.Add(line);
        line.AudioPath = ResolveAudio(Folder, line);
    }

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
