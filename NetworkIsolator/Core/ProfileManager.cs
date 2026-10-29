using System.IO;
using System.Text.Json;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>Profiles saved in %APPDATA%\NetworkIsolator\profiles.json (+ export / import).</summary>
public sealed class ProfileManager
{
    private readonly List<LaunchProfile> _profiles = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ProfileManager() => Load();

    /// <summary>Copy of the current profiles (safe to iterate even if another thread is saving).</summary>
    public IReadOnlyList<LaunchProfile> Profiles
    {
        get
        {
            lock (_profiles) return [.. _profiles];
        }
    }

    public LaunchProfile? FindProfile(string name)
    {
        lock (_profiles)
            return _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds or updates a profile. Same name + same exe = update.
    /// Same name but different exe = automatic new name "Name (2)" (no silent overwrite).
    /// Returns the profile as saved.
    /// </summary>
    public async Task<LaunchProfile> UpsertAsync(LaunchProfile profile)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            LaunchProfile saved = UpsertCore(profile);
            await PersistAsync().ConfigureAwait(false);
            return saved;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteProfileAsync(string name)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            bool removed;
            lock (_profiles)
                removed = _profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed) await PersistAsync().ConfigureAwait(false);
            return removed;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Records the time a profile was last started.</summary>
    public async Task MarkLaunchedAsync(string name)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            LaunchProfile? p = FindProfile(name);
            if (p is null) return;
            p.LastLaunchedAt = DateTime.Now;
            await PersistAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task ExportAsync(string filePath)
    {
        string json = JsonSerializer.Serialize(Profiles, JsonDefaults.Options);
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
    }

    /// <summary>Imports a profile file. Returns (added, updated).</summary>
    public async Task<(int Added, int Updated)> ImportAsync(string filePath)
    {
        string json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        List<LaunchProfile> incoming = JsonSerializer.Deserialize<List<LaunchProfile>>(json, JsonDefaults.Options)
            ?? throw new InvalidDataException(Loc.T("Err_NoProfiles"));

        int added = 0, updated = 0;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (LaunchProfile p in incoming.Where(q => !string.IsNullOrWhiteSpace(q.Name) && !string.IsNullOrWhiteSpace(q.ExePath)))
            {
                bool existed;
                lock (_profiles)
                    existed = _profiles.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)
                                                 && string.Equals(x.ExePath, p.ExePath, StringComparison.OrdinalIgnoreCase));
                UpsertCore(p);
                if (existed) updated++; else added++;
            }
            await PersistAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }

        return (added, updated);
    }

    private LaunchProfile UpsertCore(LaunchProfile profile)
    {
        lock (_profiles)
        {
            string name = string.IsNullOrWhiteSpace(profile.Name)
                ? Path.GetFileNameWithoutExtension(profile.ExePath)
                : profile.Name.Trim();

            LaunchProfile? same = _profiles.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (same is not null && !string.Equals(same.ExePath, profile.ExePath, StringComparison.OrdinalIgnoreCase))
            {
                // Other exe, same name: look for a free name.
                int n = 2;
                string candidate;
                do { candidate = $"{name} ({n++})"; }
                while (_profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(p.ExePath, profile.ExePath, StringComparison.OrdinalIgnoreCase)));
                name = candidate;
                same = _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            LaunchProfile toStore = profile.Clone();
            toStore.Name = name;
            if (same is not null)
            {
                toStore.CreatedAt = same.CreatedAt;
                toStore.LastLaunchedAt ??= same.LastLaunchedAt;
                _profiles[_profiles.IndexOf(same)] = toStore;
            }
            else
            {
                _profiles.Add(toStore);
            }
            return toStore;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ProfilesFile)) return;

            var loaded = JsonSerializer.Deserialize<List<LaunchProfile>>(
                File.ReadAllText(AppPaths.ProfilesFile), JsonDefaults.Options);
            if (loaded is not null)
                lock (_profiles) _profiles.AddRange(loaded);
        }
        catch (Exception ex)
        {
            // Corrupted file: set it aside instead of overwriting it on the next save.
            try
            {
                string backup = Path.Combine(AppPaths.DataDir, $"profiles.corrompu-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Move(AppPaths.ProfilesFile, backup);
                ActivityLog.Error(Loc.T("Log_ProfilesUnreadable", ex.Message, backup));
            }
            catch { /* tant pis */ }
        }
    }

    /// <summary>Atomic write: temporary file, then replace.</summary>
    private async Task PersistAsync()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        string json;
        lock (_profiles) json = JsonSerializer.Serialize(_profiles, JsonDefaults.Options);

        string tmp = AppPaths.ProfilesFile + ".tmp";
        await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
        File.Move(tmp, AppPaths.ProfilesFile, overwrite: true);
    }
}
