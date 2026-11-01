using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Fable2Launcher;

/// <summary>
/// Transactional, non-WPF file layer, also linked into the standalone tests.
/// Recognises only the three shipped PubGames movies. All writes are beside
/// the original files; unrelated game data, BNKs and user saves are untouched.
/// </summary>
internal static class PubGameFileSet
{
    internal const string BackupSuffix = ".xbox-prompts-backup";
    internal const string StateSuffix = ".launcher-pubgame-prompts.json";
    internal static readonly string[] Games = { "wizardstower", "keystone", "magedice" };
    private static readonly HashSet<string> ManagedRevisions = new(StringComparer.Ordinal)
    {
        "pubgames-prompts-v7", "pubgames-prompts-v8", "pubgames-prompts-v9",
        "pubgames-safe-original-v12", "pubgames-prompts-v19",
        "pubgames-prompts-v20-tower-safe", "pubgames-prompts-v20",
        "pubgames-prompts-v21", "pubgames-prompts-v21.1",
        "pubgames-prompts-v22", PubGameSwfPatch.Revision
    };
    private sealed class State
    {
        public string Revision { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string InstalledSha256 { get; set; } = "";
        public string Mode { get; set; } = "";
        public State() { }
        public State(string revision, string originalSha256, string installedSha256, string mode)
        { Revision = revision; OriginalSha256 = originalSha256; InstalledSha256 = installedSha256; Mode = mode; }
    }
    private sealed record Change(string Path, byte[]? Before, byte[] After);
    private sealed record PendingBackup(string Path, byte[] Bytes);

    internal static string Apply(string gameRoot, string mode, Func<string, byte[]> artwork)
    {
        if (mode is not ("xbox360" or "playstation" or "keyboardmouse"))
            throw new ArgumentException("Unsupported PubGames prompt mode.", nameof(mode));
        string root = Path.GetFullPath(gameRoot);
        string logPath = Path.Combine(root, "launcher-pubgames-prompts.log");
        var log = new StringBuilder();
        log.AppendLine("Fable II PubGames SWF prompts " + PubGameSwfPatch.Revision);
        log.AppendLine("v23: Wizard's Tower uses selected prompt artwork. Crashes also occurred with original SWF; root cause is unconfirmed.");
        log.AppendLine("Mode: " + mode);
        log.AppendLine("Game root: " + root);
        var changes = new List<Change>();
        var backups = new List<PendingBackup>();
        int found = 0, changed = 0, skipped = 0;
        try
        {
            foreach (string game in Games)
            {
                string effectiveMode = mode;
                string path = Path.Combine(root, "data", "pubgames", game, "game.swf");
                if (!File.Exists(path)) { log.AppendLine("ABSENT: " + path); continue; }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing to replace a symbolic-link SWF: " + path);
                found++;
                string backupPath = path + BackupSuffix, statePath = path + StateSuffix;
                byte[] live = ReadBounded(path);
                if (effectiveMode == "xbox360" && !File.Exists(backupPath))
                {
                    log.AppendLine((game == "wizardstower" ? "TOWER SAFE ORIGINAL" : "XBOX ORIGINAL") + " (no managed backup): " + path);
                    continue;
                }
                bool hasBackup = File.Exists(backupPath);
                byte[] original = hasBackup ? ReadBounded(backupPath) : live;
                PubGameSwfPatch.ValidateOriginal(original);
                string originalHash = PubGameSwfPatch.Hash(original), liveHash = PubGameSwfPatch.Hash(live);
                byte[] target = effectiveMode == "xbox360" ? original : PubGameSwfPatch.Patch(original, artwork);
                string targetHash = PubGameSwfPatch.Hash(target);
                byte[]? oldState = File.Exists(statePath) ? ReadBounded(statePath, 32768) : null;
                if (!live.AsSpan().SequenceEqual(original))
                {
                    State? state = null;
                    if (oldState is not null)
                    {
                        try { state = JsonSerializer.Deserialize<State>(oldState); }
                        catch (JsonException ex)
                        {
                            // Stale/corrupt sidecar is not grounds to corrupt a movie or
                            // prevent game launch. If live bytes match our exact target,
                            // the state can be rebuilt below; otherwise skip this movie.
                            log.AppendLine("WARN corrupt prompt state, checking SWF bytes: " + ex.Message);
                        }
                    }
                    bool stateHashesMatch = state is not null &&
                        string.Equals(state.OriginalSha256, originalHash, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(state.InstalledSha256, liveHash, StringComparison.OrdinalIgnoreCase) &&
                        state.Mode is ("xbox360" or "playstation" or "keyboardmouse");

                    // Compatibility rule: previous launcher revisions are trusted by content, not merely
                    // by their revision label. If the saved state hashes exactly match both the validated
                    // original backup and the live SWF, this is a launcher-managed file and can be migrated.
                    // This prevents our own v19/v20/v21 files from being rejected after an upgrade while
                    // still refusing genuinely unknown SWF modifications.
                    bool exactTargetMatch = live.AsSpan().SequenceEqual(target);
                    if (!stateHashesMatch && !exactTargetMatch)
                    {
                        // A user mod or an unrecognised old patch is not ours to overwrite.
                        // Skip only this movie; the rest of the game can still launch.
                        skipped++;
                        log.AppendLine("SKIPPED UNMANAGED SWF (not overwritten): " + path);
                        log.AppendLine("  current SHA256: " + liveHash);
                        log.AppendLine("  backup SHA256:  " + originalHash);
                        log.AppendLine("  Keep both files and the state sidecar for manual inspection.");
                        continue;
                    }

                    if (exactTargetMatch && !stateHashesMatch)
                        log.AppendLine("MIGRATE EXACT TARGET: live SWF already matches the selected launcher-generated movie; state will be repaired.");
                    else if (!ManagedRevisions.Contains(state!.Revision))
                        log.AppendLine("MIGRATE MANAGED STATE: " + state.Revision + " -> " + PubGameSwfPatch.Revision);
                }
                byte[] newState = JsonSerializer.SerializeToUtf8Bytes(new State(
                    PubGameSwfPatch.Revision, originalHash, targetHash, effectiveMode), new JsonSerializerOptions { WriteIndented = true });
                // Backups are created only after EVERY present movie has passed validation.
                if (!hasBackup) backups.Add(new PendingBackup(backupPath, original));
                if (!live.AsSpan().SequenceEqual(target))
                {
                    changes.Add(new Change(path, live, target)); changed++;
                    log.AppendLine((effectiveMode == "xbox360" ? "RESTORE: " : "PATCH: ") + path);
                }
                else log.AppendLine("ALREADY CURRENT: " + path);
                if (oldState is null || !oldState.AsSpan().SequenceEqual(newState))
                    changes.Add(new Change(statePath, oldState, newState));
                log.AppendLine("  original SHA256: " + originalHash);
                log.AppendLine("  target SHA256:   " + targetHash);
                log.AppendLine("  A/B/X/Y/LS/RS/LT/RT/LB/RB; non-prompt tags preserved byte-for-byte.");
            }
            foreach (PendingBackup backup in backups)
            {
                if (File.Exists(backup.Path))
                {
                    if (!File.ReadAllBytes(backup.Path).AsSpan().SequenceEqual(backup.Bytes))
                        throw new IOException("A different original backup already exists: " + backup.Path);
                }
                else WriteNewFile(backup.Path, backup.Bytes);
                if (!File.ReadAllBytes(backup.Path).AsSpan().SequenceEqual(backup.Bytes))
                    throw new IOException("Original backup verification failed: " + backup.Path);
            }
            Commit(changes);
            string result = found == 0
                ? "PubGames: no installed mini-game SWFs were found; nothing changed."
                : $"PubGames: {found} movies checked, {changed} changed, {skipped} unmanaged skipped; {mode}.";
            log.AppendLine("SUCCESS: " + result);
            return result;
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
            throw;
        }
        finally
        {
            try { File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] ReadBounded(string path, long maximum = 32 * 1024 * 1024)
    {
        // Backups and state files must be regular files just like the live SWFs:
        // never follow a junction/symlink out of the selected game folder.
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing a symbolic-link PubGames file: " + path);
        if (info.Length < 0 || info.Length > maximum)
            throw new InvalidDataException("Unexpected file size: " + path);
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > maximum)
            throw new InvalidDataException("PubGames file grew while reading: " + path);
        return bytes;
    }

    private static void Commit(List<Change> changes)
    {
        if (changes.Count == 0) return;
        string transaction = Guid.NewGuid().ToString("N");
        var staged = new List<(Change Item, string Temp)>();
        var committed = new List<Change>();
        try
        {
            // Stage and verify ALL outputs before changing the first live file.
            foreach (Change change in changes)
            {
                string temp = change.Path + ".pubgames-" + transaction + ".tmp";
                staged.Add((change, temp));
                WriteNewFile(temp, change.After);
                if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(change.After))
                    throw new IOException("Staged file verification failed: " + temp);
            }
            foreach ((Change item, string _) in staged) VerifyCurrent(item);
            foreach ((Change item, string temp) in staged)
            {
                VerifyCurrent(item);
                File.Move(temp, item.Path, overwrite: item.Before is not null);
                committed.Add(item);
                if (!File.ReadAllBytes(item.Path).AsSpan().SequenceEqual(item.After))
                    throw new IOException("Installed file verification failed: " + item.Path);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (Change item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.Before is null) File.Delete(item.Path);
                    else
                    {
                        string rollback = item.Path + ".pubgames-" + transaction + ".rollback";
                        WriteNewFile(rollback, item.Before);
                        File.Move(rollback, item.Path, overwrite: true);
                    }
                }
                catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            }
            if (errors.Count > 1)
                throw new AggregateException("PubGames installation failed; rollback was incomplete. Original .xbox-prompts-backup files are retained.", errors);
            throw new IOException("PubGames installation failed; any completed replacements were rolled back.", failure);
        }
        finally
        {
            foreach ((Change _, string temp) in staged)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void VerifyCurrent(Change item)
    {
        if (item.Before is null)
        {
            if (File.Exists(item.Path)) throw new IOException("File appeared during preparation: " + item.Path);
        }
        else if (!File.Exists(item.Path) || !File.ReadAllBytes(item.Path).AsSpan().SequenceEqual(item.Before))
            throw new IOException("File changed during preparation: " + item.Path);
    }
    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length); stream.Flush(flushToDisk: true);
    }
}
