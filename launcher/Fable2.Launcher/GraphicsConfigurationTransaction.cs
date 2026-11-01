using System.IO;

namespace Fable2Launcher;

/// <summary>
/// Saves the three launcher-owned configurations as one recoverable operation.
/// The existing serializers run on private staging copies FIRST, so invalid
/// TOML or an unwritable destination cannot leave graphics and game speed out
/// of sync. A failure during the commit rolls back already replaced files.
/// This is an exception-safe transaction, not a power-loss-safe disk journal.
/// </summary>
internal static class GraphicsConfigurationTransaction
{
    private static readonly string[] BackupSuffixes =
    [
        ".launcher-backup", ".nul-recovery-backup", ".sdk-path-recovery-backup"
    ];

    private sealed class Change(string target, string stage, Action<string> prepare)
    {
        internal readonly string Target = target;
        internal readonly string Stage = stage;
        internal readonly Action<string> Prepare = prepare;
        internal byte[]? Before;
        internal bool Committed;
    }

    internal static void Save(string executableDirectory,
        IReadOnlyDictionary<string, string> graphics, IReadOnlyList<string> managedKeys,
        double gameSpeed, Action<int>? beforeCommit = null)
    {
        string root = Path.GetFullPath(executableDirectory);
        string txid = Guid.NewGuid().ToString("N");
        Change NewChange(string name, Action<string> prepare)
        {
            string target = Path.Combine(root, name);
            return new Change(target, target + ".launcher-txn-" + txid + ".stage", prepare);
        }
        var changes = new[]
        {
            NewChange("fable_2.toml", path => LauncherConfigFile.WriteValues(path, graphics, managedKeys)),
            NewChange("launcher-settings.toml", path => LauncherConfigFile.WriteValues(path, graphics, managedKeys)),
            NewChange("fable2_config.toml", path => Fable2ConfigFile.WriteGameSpeed(path, gameSpeed)),
        };

        try
        {
            // Prepare every output (including all TOML parsing) without
            // modifying any of the actual user configuration files.
            foreach (Change change in changes)
            {
                if (File.Exists(change.Target))
                {
                    if ((File.GetAttributes(change.Target) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Cannot update a symbolic-link configuration: " + change.Target);
                    change.Before = File.ReadAllBytes(change.Target);
                    using var stage = new FileStream(change.Stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stage.Write(change.Before);
                }
                change.Prepare(change.Stage);
                if (!File.Exists(change.Stage))
                    throw new IOException("Configuration was not staged: " + change.Target);
            }

            // Retain the existing per-file backup behavior, including the
            // separate originals for NUL / SDK Windows-path recovery. Creating
            // a backup may fail, but never after a live file has been changed.
            foreach (Change change in changes)
            {
                VerifyUnchanged(change);
                foreach (string suffix in BackupSuffixes)
                {
                    string stagedBackup = change.Stage + suffix;
                    string realBackup = change.Target + suffix;
                    if (File.Exists(stagedBackup) && !File.Exists(realBackup))
                        File.Copy(stagedBackup, realBackup, overwrite: false);
                }
            }

            for (int i = 0; i < changes.Length; i++)
            {
                Change change = changes[i];
                beforeCommit?.Invoke(i);  // optional failure injection for regression tests
                VerifyUnchanged(change);
                File.Move(change.Stage, change.Target, overwrite: change.Before is not null);
                change.Committed = true;
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (Change change in changes.Reverse())
            {
                if (!change.Committed) continue;
                try
                {
                    if (change.Before is null)
                        File.Delete(change.Target);
                    else
                    {
                        string rollback = change.Stage + ".rollback";
                        using (var stream = new FileStream(rollback, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            stream.Write(change.Before);
                        File.Move(rollback, change.Target, overwrite: true);
                    }
                }
                catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            }
            if (errors.Count > 1)
                throw new AggregateException("Failed to save configuration; automatic rollback was incomplete. Restore the .launcher-backup files.", errors);
            throw;
        }
        finally
        {
            foreach (Change change in changes)
            {
                foreach (string path in new[] { change.Stage, change.Stage + ".rollback" }
                    .Concat(BackupSuffixes.Select(suffix => change.Stage + suffix)))
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static void VerifyUnchanged(Change change)
    {
        if (change.Before is null)
        {
            if (File.Exists(change.Target))
                throw new IOException("Configuration appeared while preparing to save: " + change.Target);
        }
        else if (!File.Exists(change.Target) ||
            !File.ReadAllBytes(change.Target).AsSpan().SequenceEqual(change.Before))
            throw new IOException("Configuration was modified while preparing to save: " + change.Target);
    }
}
