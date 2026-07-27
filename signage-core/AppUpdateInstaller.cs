namespace PiSignage.Signage;

/// <summary>Swaps a staged release in for the running executable.
///
/// Windows will not let anything write to a running exe, but it will happily
/// rename one, and an already-open handle survives the rename. So the swap is
/// three file operations inside the app's own process: move the current exe
/// aside, copy the staged one into its place, restart. No helper process, no
/// batch file, no console window flashing on the store laptop.</summary>
public static class AppUpdateInstaller
{
    public enum Outcome
    {
        /// <summary>No verified update was waiting.</summary>
        Nothing,
        /// <summary>Swapped in. The caller must relaunch and exit.</summary>
        Applied,
        /// <summary>Something is in the way; try again next launch.</summary>
        Blocked,
    }

    public static string BackupPath(string exePath) => exePath + ".old";

    /// <summary>Install the pending update over <paramref name="exePath"/>.
    /// <paramref name="current"/> is the version of the process calling this.</summary>
    public static Outcome TryApplyPending(string exePath, string stageDir, Version current)
    {
        var pending = AppUpdate.LoadPending(stageDir);
        if (pending is null) return Outcome.Nothing;

        var staged = AppUpdate.StagedExePath(stageDir);
        if (!File.Exists(staged))
        {
            AppUpdate.DiscardPending(stageDir);
            return Outcome.Nothing;
        }

        // Someone may have hand-copied a newer exe over this install since the
        // download. Staging an older one on top of it would be a downgrade.
        if (!Version.TryParse(pending.Version, out var target) || target <= current)
        {
            AppUpdate.DiscardPending(stageDir);
            return Outcome.Nothing;
        }

        // Re-verify immediately before executing it. StageAsync already checked,
        // but the file has been sitting on disk since then.
        try
        {
            if (AppUpdate.Sha256File(staged) != pending.Sha256)
            {
                AppUpdate.DiscardPending(stageDir);
                return Outcome.Nothing;
            }
        }
        catch { return Outcome.Blocked; }

        // The backup from the previous update is deleted on the first successful
        // launch of that update. If it is still here and locked, another copy of
        // the app is running from it -- swapping now would strand that process, so
        // wait rather than accumulate .old.old.
        var backup = BackupPath(exePath);
        if (File.Exists(backup))
        {
            try { File.Delete(backup); }
            catch { return Outcome.Blocked; }
        }

        try { File.Move(exePath, backup); }
        catch { return Outcome.Blocked; }

        try
        {
            File.Copy(staged, exePath);
        }
        catch
        {
            // Put the working exe back; a failed update must not remove the app.
            try { File.Move(backup, exePath); } catch { /* nothing left to try */ }
            return Outcome.Blocked;
        }

        AppUpdate.DiscardPending(stageDir);
        return Outcome.Applied;
    }

    /// <summary>Delete the previous version, which is what commits an update.
    /// Called once the new build has actually started.</summary>
    public static void CommitPreviousLaunch(string exePath)
    {
        try
        {
            var backup = BackupPath(exePath);
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch { /* still running from it, or locked; next launch gets it */ }
    }

    /// <summary>Can this install replace its own executable? An app dropped into
    /// Program Files cannot, and there is no point downloading 150 MB to find
    /// that out.</summary>
    public static bool CanSelfUpdate(string exeDir)
    {
        try
        {
            var probe = Path.Combine(exeDir, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
