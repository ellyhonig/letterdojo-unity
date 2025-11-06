using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Central repository for shared sight-word resources (word list, hint textures, audio clips).
/// Keeps loading logic in one place so mini-games and drills stay in sync.
/// </summary>
public static class SharedWordLibrary
{
    public static readonly string[] DefaultWords =
    {
        "rat","cat","pan","bag","mat","Hat","sad","bad","tap","dad","pad","yam","fax",
        "if","big","lip","rip","dig","hit","Him","six","Fix","Kid",
        "Up","Cut","Run","Bug","Dug","Cub","Tub","Fun","Mud","Sun",
        "On","Hot","Box","Lot","Dog","Pot","Fox","Mop","Mom","Hop","Job","Jog","Top",
        "Get","Red","Bed","Yes","Men","Wet","Pen","Ten","Jet","Pet","Leg","Met","Fed","Let","Yet"
    };

    public const string DefaultHintFolder = "wordphotos";
    public const string DefaultAudioFolder = "wordaudio";

    sealed class HintEntry
    {
        public string normalizedName;
        public string resourcePath;
    }

    sealed class AudioEntry
    {
        public string normalizedName;
        public string resourcePath;
    }

    static readonly Dictionary<string, List<HintEntry>> s_hintEntriesByFolder = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<HintEntry> s_allHintEntries = new();
    static readonly Dictionary<string, Texture2D> s_loadedHintTextures = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<Texture2D, string> s_hintTextureToPath = new();
    static readonly HashSet<string> s_indexedHintFolders = new(StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<string, List<AudioEntry>> s_audioEntriesByFolder = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<AudioEntry> s_allAudioEntries = new();
    static readonly Dictionary<string, AudioClip> s_loadedAudioClips = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<AudioClip, string> s_audioClipToPath = new();
    static readonly HashSet<string> s_indexedAudioFolders = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Words => DefaultWords;

    public static Texture2D FindHintTexture(string word, params string[] additionalFolders)
    {
        if (string.IsNullOrWhiteSpace(word))
            return null;

        EnsureHintFolders(additionalFolders);
        string query = NormalizeKey(word);

        var candidates = GatherHintEntries(additionalFolders);
        var matches = candidates
            .Where(e => e.normalizedName.Contains(query))
            .ToList();

        if (matches.Count == 0)
            return null;

        int index = UnityEngine.Random.Range(0, matches.Count);
        var entry = matches[index];
        if (s_loadedHintTextures.TryGetValue(entry.resourcePath, out var cached) && cached)
            return cached;

        var texture = Resources.Load<Texture2D>(entry.resourcePath);
        if (texture)
        {
            s_loadedHintTextures[entry.resourcePath] = texture;
            s_hintTextureToPath[texture] = entry.resourcePath;
        }
        return texture;
    }

    public static AudioClip FindWordClip(string word, params string[] additionalFolders)
    {
        if (string.IsNullOrWhiteSpace(word))
            return null;

        EnsureAudioFolders(additionalFolders);
        string query = NormalizeKey(word);

        var candidates = GatherAudioEntries(additionalFolders);
        var matches = candidates
            .Where(e => e.normalizedName.Contains(query))
            .ToList();

        if (matches.Count == 0)
            return null;

        int index = UnityEngine.Random.Range(0, matches.Count);
        var entry = matches[index];
        if (s_loadedAudioClips.TryGetValue(entry.resourcePath, out var cached) && cached)
            return cached;

        var clip = Resources.Load<AudioClip>(entry.resourcePath);
        if (clip)
        {
            s_loadedAudioClips[entry.resourcePath] = clip;
            s_audioClipToPath[clip] = entry.resourcePath;
        }
        return clip;
    }

    public static void ReleaseHintTexture(Texture2D texture)
    {
        if (!texture)
            return;

        if (s_hintTextureToPath.TryGetValue(texture, out var path))
        {
            s_hintTextureToPath.Remove(texture);
            s_loadedHintTextures.Remove(path);
            Resources.UnloadAsset(texture);
        }
    }

    public static void ReleaseWordClip(AudioClip clip)
    {
        if (!clip)
            return;

        if (s_audioClipToPath.TryGetValue(clip, out var path))
        {
            s_audioClipToPath.Remove(clip);
            s_loadedAudioClips.Remove(path);
            Resources.UnloadAsset(clip);
        }
    }

    static void EnsureHintFolders(IEnumerable<string> extraFolders)
    {
        EnsureHintFolderIndexed(DefaultHintFolder);

        if (extraFolders == null) return;
        foreach (var folder in extraFolders)
        {
            EnsureHintFolderIndexed(folder);
        }
    }

    static void EnsureAudioFolders(IEnumerable<string> extraFolders)
    {
        EnsureAudioFolderIndexed(DefaultAudioFolder);

        if (extraFolders == null) return;
        foreach (var folder in extraFolders)
        {
            EnsureAudioFolderIndexed(folder);
        }
    }

    static void EnsureHintFolderIndexed(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        folder = folder.Trim();
        if (s_indexedHintFolders.Contains(folder))
            return;

        var loaded = Resources.LoadAll<Texture2D>(folder);
        if (loaded != null && loaded.Length > 0)
        {
            if (!s_hintEntriesByFolder.TryGetValue(folder, out var list))
            {
                list = new List<HintEntry>();
                s_hintEntriesByFolder[folder] = list;
            }

            foreach (var tex in loaded)
            {
                if (!tex) continue;
                var entry = new HintEntry
                {
                    normalizedName = NormalizeKey(tex.name),
                    resourcePath = $"{folder}/{tex.name}"
                };
                list.Add(entry);
                s_allHintEntries.Add(entry);
                Resources.UnloadAsset(tex);
            }
        }

        s_indexedHintFolders.Add(folder);
    }

    static void EnsureAudioFolderIndexed(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        folder = folder.Trim();
        if (s_indexedAudioFolders.Contains(folder))
            return;

        var loaded = Resources.LoadAll<AudioClip>(folder);
        if (loaded != null && loaded.Length > 0)
        {
            if (!s_audioEntriesByFolder.TryGetValue(folder, out var list))
            {
                list = new List<AudioEntry>();
                s_audioEntriesByFolder[folder] = list;
            }

            foreach (var clip in loaded)
            {
                if (!clip) continue;
                var entry = new AudioEntry
                {
                    normalizedName = NormalizeKey(clip.name),
                    resourcePath = $"{folder}/{clip.name}"
                };
                list.Add(entry);
                s_allAudioEntries.Add(entry);
                Resources.UnloadAsset(clip);
            }
        }

        s_indexedAudioFolders.Add(folder);
    }

    static List<HintEntry> GatherHintEntries(IEnumerable<string> extraFolders)
    {
        var result = new List<HintEntry>();
        if (s_hintEntriesByFolder.TryGetValue(DefaultHintFolder, out var defaults))
            result.AddRange(defaults);

        if (extraFolders != null)
        {
            foreach (var folder in extraFolders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                if (s_hintEntriesByFolder.TryGetValue(folder.Trim(), out var entries))
                    result.AddRange(entries);
            }
        }

        return result.Count > 0 ? result : s_allHintEntries;
    }

    static List<AudioEntry> GatherAudioEntries(IEnumerable<string> extraFolders)
    {
        var result = new List<AudioEntry>();
        if (s_audioEntriesByFolder.TryGetValue(DefaultAudioFolder, out var defaults))
            result.AddRange(defaults);

        if (extraFolders != null)
        {
            foreach (var folder in extraFolders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                if (s_audioEntriesByFolder.TryGetValue(folder.Trim(), out var entries))
                    result.AddRange(entries);
            }
        }

        return result.Count > 0 ? result : s_allAudioEntries;
    }

    static string NormalizeKey(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var chars = value.ToLowerInvariant()
                         .Where(c => char.IsLetterOrDigit(c));
        return new string(chars.ToArray());
    }
}
