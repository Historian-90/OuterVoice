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
    public const string LegacyFileName = "mika_lines.json";
    public const string SharedJob = "ALL";

    /// <summary>Display name of the pack.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Character this pack voices ("First Last@World"). Used later for sharing packs with friends.</summary>
    public string Character { get; set; } = string.Empty;

    public string Version { get; set; } = "1";
    public int GlobalCooldownSeconds { get; set; } = 20;
    /// <summary>Optional timing overrides per event; anything missing uses the built-in defaults.</summary>
    public Dictionary<string, EventDef> Events { get; set; } = new();

    /// <summary>Lines grouped by job abbreviation. "ALL" = lines usable on any job.</summary>
    public Dictionary<string, List<Line>> Jobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public string Folder { get; set; } = string.Empty;
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
        else if (File.Exists(Path.Combine(folder, LegacyFileName)))
        {
            // Old single-job format: treat every line as a Sage line.
            var legacy = JsonConvert.DeserializeObject<LegacyPack>(File.ReadAllText(Path.Combine(folder, LegacyFileName)))
                         ?? throw new InvalidDataException($"{LegacyFileName} is empty.");
            pack = new VoicePack
            {
                Name = legacy.Name,
                Version = legacy.Version,
                GlobalCooldownSeconds = legacy.GlobalCooldownSeconds,
                Events = legacy.Events,
                Jobs = new Dictionary<string, List<Line>>(StringComparer.OrdinalIgnoreCase) { ["SGE"] = legacy.Lines },
            };
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
                line.Job = job.ToUpperInvariant();
                line.AudioPath = ResolveAudio(folder, line);
            }
        }

        return pack;
    }

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

    public EventDef GetEvent(string name)
    {
        if (Events.TryGetValue(name, out var def))
            return def;
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

    private class LegacyPack
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public int GlobalCooldownSeconds { get; set; } = 20;
        public Dictionary<string, EventDef> Events { get; set; } = new();
        public List<Line> Lines { get; set; } = new();
    }
}
