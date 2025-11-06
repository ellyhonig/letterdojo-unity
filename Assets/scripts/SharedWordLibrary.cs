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

    static readonly List<Texture2D> s_hintTextures = new();
    static readonly HashSet<string> s_loadedHintFolders = new(StringComparer.OrdinalIgnoreCase);

    static readonly List<AudioClip> s_wordClips = new();
    static readonly HashSet<string> s_loadedAudioFolders = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Words => DefaultWords;

    public static Texture2D FindHintTexture(string word, params string[] additionalFolders)
    {
        if (string.IsNullOrWhiteSpace(word))
            return null;

        EnsureHintFolders(additionalFolders);
        string query = NormalizeKey(word);

        var matches = s_hintTextures
            .Where(t => t && NormalizeKey(t.name).Contains(query))
            .ToList();

        if (matches.Count == 0)
            return null;

        int index = UnityEngine.Random.Range(0, matches.Count);
        return matches[index];
    }

    public static AudioClip FindWordClip(string word, params string[] additionalFolders)
    {
        if (string.IsNullOrWhiteSpace(word))
            return null;

        EnsureAudioFolders(additionalFolders);
        string query = NormalizeKey(word);

        var matches = s_wordClips
            .Where(c => c && NormalizeKey(c.name).Contains(query))
            .ToList();

        if (matches.Count == 0)
            return null;

        int index = UnityEngine.Random.Range(0, matches.Count);
        return matches[index];
    }

    static void EnsureHintFolders(IEnumerable<string> extraFolders)
    {
        EnsureFolderLoaded(DefaultHintFolder, s_loadedHintFolders, folder =>
        {
            var loaded = Resources.LoadAll<Texture2D>(folder);
            if (loaded != null && loaded.Length > 0)
                s_hintTextures.AddRange(loaded.Where(t => t));
        });

        if (extraFolders == null) return;
        foreach (var folder in extraFolders)
        {
            EnsureFolderLoaded(folder, s_loadedHintFolders, f =>
            {
                var loaded = Resources.LoadAll<Texture2D>(f);
                if (loaded != null && loaded.Length > 0)
                    s_hintTextures.AddRange(loaded.Where(t => t));
            });
        }
    }

    static void EnsureAudioFolders(IEnumerable<string> extraFolders)
    {
        EnsureFolderLoaded(DefaultAudioFolder, s_loadedAudioFolders, folder =>
        {
            var loaded = Resources.LoadAll<AudioClip>(folder);
            if (loaded != null && loaded.Length > 0)
                s_wordClips.AddRange(loaded.Where(c => c));
        });

        if (extraFolders == null) return;
        foreach (var folder in extraFolders)
        {
            EnsureFolderLoaded(folder, s_loadedAudioFolders, f =>
            {
                var loaded = Resources.LoadAll<AudioClip>(f);
                if (loaded != null && loaded.Length > 0)
                    s_wordClips.AddRange(loaded.Where(c => c));
            });
        }
    }

    static void EnsureFolderLoaded(string folder, HashSet<string> loadedSet, Action<string> loader)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        folder = folder.Trim();
        if (loadedSet.Contains(folder))
            return;

        loader(folder);
        loadedSet.Add(folder);
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
