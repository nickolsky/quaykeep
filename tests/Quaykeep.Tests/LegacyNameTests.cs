using Quaykeep.Core.Backup;
using Quaykeep.Core.Updates;

namespace Quaykeep.Tests;

/// <summary>What keeps working from SSH Manager, the name before Quaykeep.</summary>
public class LegacyNameTests
{
    [Fact]
    public void Old_Backups_Are_Found_And_Sorted_By_Time_With_The_New_Ones()
    {
        Assert.True(BackupService.IsBackupName("quaykeep-data-20260929-101500.zip"));
        Assert.True(BackupService.IsBackupName("sshmanager-data-20260920-080000.zip"));
        Assert.False(BackupService.IsBackupName("before-restore-20260920-080000.zip"));
        Assert.False(BackupService.IsBackupName("quaykeep-data-20260929-101500.zip.tmp"));

        string[] names = ["sshmanager-data-20260927-235959.zip", "quaykeep-data-20260929-101500.zip", "sshmanager-data-20260920-080000.zip"];
        // by name the old prefix would sort as the newest ('s' > 'q'); by time stamp it does not
        Assert.Equal(["quaykeep-data-20260929-101500.zip", "sshmanager-data-20260927-235959.zip", "sshmanager-data-20260920-080000.zip"],
            names.OrderByDescending(BackupService.Stamp, StringComparer.Ordinal));
    }

    [Fact]
    public void Old_Program_Files_Go_Only_Next_To_Quaykeep_Exe()
    {
        using var tmp = new TempDir();
        foreach (var f in new[] { "SshManager.exe", "SshManager.Core.dll", "sshm.exe", "Renci.SshNet.dll", "notes.txt" })
            File.WriteAllText(tmp.File(f), "x");

        UpdateService.CleanupOld(tmp.Path); // not an install of the new version: nothing is touched
        Assert.True(File.Exists(tmp.File("SshManager.exe")));

        File.WriteAllText(tmp.File("Quaykeep.exe"), "x");
        UpdateService.CleanupOld(tmp.Path);
        Assert.False(File.Exists(tmp.File("SshManager.exe")));
        Assert.False(File.Exists(tmp.File("SshManager.Core.dll")));
        Assert.False(File.Exists(tmp.File("sshm.exe")));
        Assert.True(File.Exists(tmp.File("Renci.SshNet.dll"))); // shared libraries stay: the new version uses them
        Assert.True(File.Exists(tmp.File("notes.txt")));
        Assert.True(File.Exists(tmp.File("Quaykeep.exe")));
    }
}
