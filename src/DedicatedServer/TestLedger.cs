// Tests only: a test server started by a test client (or the Server Manager's self test) lives in
// another game copy, so the harness never adopts it (it adopts new processes only under its own
// target's folder). Unrecorded, it looked like Ethan playing to every other chat's verify.py, and
// the run's cleanup would not close it. So whoever starts it writes it into the shared process
// ledger at once, under the SAME run and owner as its own entry: it then belongs to that run, and
// is closed with it.
using System;
using System.Diagnostics;
using System.IO;

namespace DedicatedServer
{
    internal static class TestLedger
    {
        public static string Register(int serverPid, int startedByPid, string note)
        {
            string ledger = Environment.GetEnvironmentVariable("SDD_DS_LEDGER");
            if (string.IsNullOrEmpty(ledger)) ledger = @"D:\Claude\_verify\lib\ledger.ps1";
            if (!File.Exists(ledger)) return "no ledger script at " + ledger;
            string ps = ". '" + ledger.Replace("'", "''") + "'\n"
                + "$r = Invoke-WithLedgerLock {\n"
                + "  $l = @(Read-Ledger)\n"
                + "  $me = $l | Where-Object { [int]$_.pid -eq " + startedByPid + " } | Select-Object -First 1\n"
                + "  if ($null -eq $me) { return 'the starting process is not in the ledger' }\n"
                + "  if ($l | Where-Object { [int]$_.pid -eq " + serverPid + " }) { return 'already recorded' }\n"
                + "  $p = Get-Process -Id " + serverPid + " -ErrorAction Stop\n"
                + "  $e = New-LedgerEntry -ProcessId $p.Id -Name $p.ProcessName -StartUtc $p.StartTime.ToUniversalTime().ToString('o') -LaunchedBy 'claude' -Note '" + note.Replace("'", "''") + "'\n"
                + "  $e.runId = $me.runId; $e.ownerPid = $me.ownerPid; $e.ownerName = $me.ownerName; $e.ownerStartUtc = $me.ownerStartUtc\n"
                + "  Write-Ledger -Entries ($l + $e)\n"
                + "  return ('recorded under run ' + $me.runId)\n"
                + "}\n"
                + "Write-Output $r\n";
            string script = Path.Combine(Path.GetTempPath(), "ds-ledger-" + serverPid + ".ps1");
            try
            {
                File.WriteAllText(script, ps);
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    return output.Trim();
                }
            }
            catch (Exception e) { return "could not record it: " + e.Message; }
            finally { try { File.Delete(script); } catch { } }
        }
    }
}
