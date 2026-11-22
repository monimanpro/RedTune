using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RedTune
{
    // 22.11 (the user, after removing ChatGPT/Codex): what a program leaves in Windows itself, beside files and keys —
    // firewall rules named after it or pointing into its folders, Windows accounts and groups it made (Codex made two
    // sandbox accounts with profiles), and its package still staged for those accounts. Offered after a removal and
    // among the leftovers of programs removed earlier; nothing goes without the user's tick.
    static partial class Uninst
    {
        // ---------- firewall rules: read from the registry (Get-NetFirewallRule with its filters took 26 s for 569 rules) ----------

        internal class FwRule { public string Id, Name, App, Dir, Action; public List<string> Sids = new List<string>(); }

        const string FwKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

        internal static List<FwRule> FirewallRules()
        {
            var list = new List<FwRule>();
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = root.OpenSubKey(FwKey))
            {
                if (k == null) return list;
                foreach (var id in k.GetValueNames())
                {
                    var v = k.GetValue(id) as string; if (v == null) continue;
                    var r = new FwRule { Id = id };
                    foreach (var part in v.Split('|'))
                    {
                        int eq = part.IndexOf('='); if (eq <= 0) continue;
                        string key = part.Substring(0, eq), val = part.Substring(eq + 1);
                        if (key == "Name") r.Name = val; else if (key == "App") r.App = val; else if (key == "Dir") r.Dir = val; else if (key == "Action") r.Action = val;
                        else if (key == "LUAuth") foreach (Match m in Regex.Matches(val, @"S-1-5-21-[\d-]+")) r.Sids.Add(m.Value);
                    }
                    if (!string.IsNullOrEmpty(r.Name)) list.Add(r);
                }
            }
            return list;
        }

        static string FwWhat(FwRule r)
        {
            string act = r.Action == "Block" ? "запрещает" : "разрешает", dir = r.Dir == "Out" ? "исходящие" : "входящие";
            return "Брандмауэр  «" + r.Name + "» — " + act + " " + dir + (string.IsNullOrEmpty(r.App) ? "" : " · " + r.App);
        }

        // a name at the start of the rule's or account's name, as a whole word: «Codex» takes «Codex», «codex_sandbox_…»,
        // «CodexSandboxOnline» (a capital letter starts a word), not «Codexia»
        static bool NamedAfter(string s, string n)
        {
            if (string.IsNullOrEmpty(s) || n.Length < 4 || !s.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Length == n.Length) return true;
            char c = s[n.Length];
            return !char.IsLetterOrDigit(c) || char.IsUpper(c);
        }

        // ---------- local accounts and groups, packages staged for other accounts (one PowerShell call) ----------

        internal class Acct { public string Name, Sid; public bool Group; public DateTime LastLogon; }

        static void AccountsAndStaged(out List<Acct> accts, out List<Pkg> staged, string onlyPackage)
        {
            accts = new List<Acct>(); staged = new List<Pkg>();
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            int exit; string err;
            // onlyPackage: null all the packages, "" none (a program that is not from Store), a name just that one
            string pkgs = onlyPackage == "" ? "" : "foreach ($p in Get-AppxPackage -AllUsers" + (onlyPackage != null ? " -Name '" + onlyPackage.Replace("'", "''") + "'" : "") + ") { if ($p.IsFramework -or $p.IsResourcePackage -or $p.NonRemovable -or $p.SignatureKind -eq 'System') { continue }; " +
                "$who = ($p.PackageUserInformation | ForEach-Object { $_.UserSecurityId.Sid + '=' + $_.InstallState }) -join ','; [Console]::WriteLine(('P', $p.PackageFullName, $p.Name, $who) -join [char]9) }";
            var text = Ps("foreach ($u in Get-LocalUser) { $l = ''; if ($u.LastLogon) { $l = $u.LastLogon.ToString('yyyyMMdd') }; [Console]::WriteLine(('U', $u.Name, $u.SID.Value, $l) -join [char]9) }; " +
                "foreach ($g in Get-LocalGroup) { [Console]::WriteLine(('G', $g.Name, $g.SID.Value, '') -join [char]9) }; " + pkgs, 60000, out exit, out err);
            foreach (var line in text.Split('\n'))
            {
                var c = line.TrimEnd('\r').Split('\t'); if (c.Length < 4) continue;
                if (c[0] == "U" || c[0] == "G")
                {
                    // the built-in ones (500, 501, 503, 504 …) and the user himself never; groups made by Windows have short SIDs
                    var rid = c[2].Substring(c[2].LastIndexOf('-') + 1); int n; int.TryParse(rid, out n);
                    if (!c[2].StartsWith("S-1-5-21-") || n < 1000 || c[2] == me) continue;
                    DateTime d; DateTime.TryParseExact(c[3], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out d);
                    accts.Add(new Acct { Name = c[1], Sid = c[2], Group = c[0] == "G", LastLogon = d });
                }
                else if (c[0] == "P")
                {
                    // staged for others and not the user's own: what an app left for the accounts it made
                    bool mine = c[3].Split(',').Any(x => x.StartsWith(me + "="));
                    if (!mine && c[3].Length > 0) staged.Add(new Pkg { Full = c[1], Name = c[2], Root = c[3] });
                }
            }
        }

        static string AcctWhat(Acct a)
        {
            return a.Group ? "Группа Windows  " + a.Name
                : "Учётная запись Windows  " + a.Name + " (с её профилем в C:\\Users" + (a.LastLogon > DateTime.MinValue ? ", последний вход " + a.LastLogon.ToString("dd.MM.yyyy") : "") + ")";
        }

        // ---------- what one program left (called from Leftovers) ----------

        static void SystemLeftovers(InstalledApp a, List<string> names, ICollection<string> folders, List<Leftover> found)
        {
            var sids = new HashSet<string>();
            try
            {
                string pkgDir = a.IsStore ? "\\windowsapps\\" + a.PackageName.ToLowerInvariant() + "_" : null;
                foreach (var r in FirewallRules())
                {
                    var app = Environment.ExpandEnvironmentVariables(r.App ?? "");
                    bool inside = app.Length > 3 && (folders.Any(f => app.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) || (pkgDir != null && app.ToLowerInvariant().Contains(pkgDir)));
                    bool named = names.Any(n => NamedAfter(r.Name, n));
                    if (!inside && !named) continue;
                    found.Add(new Leftover { Kind = "fw", Path = r.Id, Group = r.Name + "|" + r.Dir + "|" + r.Action, What = FwWhat(r) });
                    foreach (var s in r.Sids) sids.Add(s);
                }
            }
            catch (Exception ex) { Store.Log("удаление программ: брандмауэр: " + ex.Message); }
            try
            {
                List<Acct> accts; List<Pkg> staged;
                AccountsAndStaged(out accts, out staged, a.IsStore ? a.PackageName : "");
                foreach (var p in staged) found.Add(new Leftover { Kind = "appxall", Path = p.Full, What = "Пакет у других учётных записей  " + p.Name });
                // an account named after it, or one its firewall rules were written for
                foreach (var u in accts.Where(x => names.Any(n => NamedAfter(x.Name, n)) || sids.Contains(x.Sid)))
                    found.Add(new Leftover { Kind = u.Group ? "group" : "user", Path = u.Sid + "|" + u.Name, What = AcctWhat(u) });
            }
            catch (Exception ex) { Store.Log("удаление программ: учётные записи: " + ex.Message); }
        }

        // ---------- what programs removed earlier left (called from Orphans; nothing ticked) ----------

        // the evidence: packages staged only for other accounts and firewall rules whose program is gone; then what is named
        // like those — more rules, accounts, groups, and AppData folders whatever their age (LocalAppData\OpenAI was in use 9 days ago)
        static List<Leftover> SystemOrphans(HashSet<string> installed, out HashSet<string> tokensOut)
        {
            var found = new List<Leftover>();
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase); tokensOut = tokens;
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Action<string> token = t => { if (t != null && t.Length >= 4 && !Generic.Contains(t) && !installed.Contains(Norm(t)) && !t.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) tokens.Add(t); };
            List<Acct> accts = new List<Acct>(); List<Pkg> staged = new List<Pkg>();
            try { AccountsAndStaged(out accts, out staged, null); } catch (Exception ex) { Store.Log("удаление программ: учётные записи: " + ex.Message); }
            foreach (var p in staged)
            {
                found.Add(new Leftover { Kind = "appxall", Path = p.Full, Checked = false, What = "Пакет только у других учётных записей  " + p.Name });
                foreach (var t in p.Name.Split('.')) token(t);
            }
            List<FwRule> rules = new List<FwRule>();
            try { rules = FirewallRules(); } catch (Exception ex) { Store.Log("удаление программ: брандмауэр: " + ex.Message); }
            var taken = new HashSet<string>(); var sids = new HashSet<string>();
            foreach (var r in rules)
            {
                var app = Environment.ExpandEnvironmentVariables(r.App ?? "");
                if (app.Length < 4 || !Path.IsPathRooted(app) || app.StartsWith(win, StringComparison.OrdinalIgnoreCase) || File.Exists(app)) continue;
                try { var drive = Path.GetPathRoot(app); if (!Directory.Exists(drive)) continue; } catch { continue; } // a drive not plugged in
                found.Add(new Leftover { Kind = "fw", Path = r.Id, Group = r.Name + "|" + r.Dir + "|" + r.Action, Checked = false, What = FwWhat(r) + " (программы уже нет)" }); taken.Add(r.Id);
                foreach (var s in r.Sids) sids.Add(s);
                var m = Regex.Match(app, @"\\windowsapps\\([^\\_]+)_", RegexOptions.IgnoreCase);
                if (m.Success) foreach (var t in m.Groups[1].Value.Split('.')) token(t);
                else token(Path.GetFileNameWithoutExtension(app));
            }
            foreach (var r in rules)
                if (!taken.Contains(r.Id) && (tokens.Any(t => NamedAfter(r.Name, t)) || r.Sids.Any(s => accts.Any(u => u.Sid == s && tokens.Any(t => NamedAfter(u.Name, t))))))
                { found.Add(new Leftover { Kind = "fw", Path = r.Id, Group = r.Name + "|" + r.Dir + "|" + r.Action, Checked = false, What = FwWhat(r) }); foreach (var s in r.Sids) sids.Add(s); }
            foreach (var u in accts.Where(x => tokens.Any(t => NamedAfter(x.Name, t)) || sids.Contains(x.Sid)))
                found.Add(new Leftover { Kind = u.Group ? "group" : "user", Path = u.Sid + "|" + u.Name, Checked = false, What = AcctWhat(u) });
            return found;
        }

        // rules alike (one program's TCP and UDP, the same name for every old version of it) as one line: «правил: N»;
        // a merged line keeps the first one's text without its path when the paths differ
        static List<Leftover> MergeFw(List<Leftover> found)
        {
            var outList = new List<Leftover>(); var groups = new Dictionary<string, Leftover>(); var count = new Dictionary<string, int>(); var paths = new Dictionary<string, HashSet<string>>();
            foreach (var f in found)
            {
                if (f.Kind != "fw" || f.Group == null) { outList.Add(f); continue; }
                var app = f.What.Contains(" · ") ? f.What.Substring(f.What.IndexOf(" · ") + 3) : "";
                Leftover g;
                if (!groups.TryGetValue(f.Group, out g)) { groups[f.Group] = f; count[f.Group] = 1; paths[f.Group] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { app }; outList.Add(f); continue; }
                g.Path += "\n" + f.Path; count[f.Group]++; paths[f.Group].Add(app);
            }
            foreach (var kv in groups)
            {
                int n = count[kv.Key]; if (n < 2) continue;
                var f = kv.Value; var head = f.What.Contains(" · ") && paths[kv.Key].Count > 1 ? f.What.Substring(0, f.What.IndexOf(" · ")) : f.What.Replace(" (программы уже нет)", "");
                bool dead = f.What.EndsWith("(программы уже нет)");
                f.What = head + " · правил: " + n + (dead ? " (программы уже нет)" : "");
            }
            return outList;
        }

        // ---------- removing them ----------

        static int RemoveRank(Leftover it)
        {
            // the packages first (they are staged for the accounts), then the accounts with their profiles, their groups, the rules
            switch (it.Kind) { case "appxall": return 0; case "appx": return 1; case "svc": return 2; case "user": return 3; case "group": return 4; case "fw": return 5; default: return 6; }
        }

        static void RemoveSystem(Leftover it)
        {
            int exit; string err; string q = it.Path.Replace("'", "''");
            switch (it.Kind)
            {
                case "fw": Ps("Remove-NetFirewallRule -Name " + string.Join(", ", it.Path.Split('\n').Select(id => "'" + id.Replace("'", "''") + "'")) + " -ErrorAction Stop", 60000, out exit, out err); break;
                case "appxall": Ps("Remove-AppxPackage -Package '" + q + "' -AllUsers -ErrorAction Stop", 300000, out exit, out err); break;
                case "user":
                {
                    var sid = it.Path.Split('|')[0].Replace("'", "''");
                    // its profile (the folder in C:\Users and its entry) first, then the account
                    Ps("Get-CimInstance Win32_UserProfile -Filter \"SID='" + sid + "'\" | Remove-CimInstance -ErrorAction Stop; Remove-LocalUser -SID '" + sid + "' -ErrorAction Stop", 120000, out exit, out err);
                    break;
                }
                case "group": Ps("Remove-LocalGroup -SID '" + it.Path.Split('|')[0].Replace("'", "''") + "' -ErrorAction Stop", 60000, out exit, out err); break;
                default: return;
            }
            if (exit != 0) { var m = (err ?? "").Trim().Split('\n')[0].Trim(); throw new Exception(m.Length > 0 ? m : it.Kind + ": " + exit); }
        }
    }
}
