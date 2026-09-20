using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace IsaacModInstaller;

// Opt-in compatibility workarounds. Player count is deliberately conservative:
// local co-op and single-player characters with extra player entities also qualify.
public static class ModSafetyPatcher {
    private const string Multi = "Game():GetNumPlayers() > 1";
    public sealed record Rule(string WorkshopId, string File, string Before, string After);
    private static Rule Guard(string id, string file, string signature, string body) =>
        new(id, file, signature, signature + "\n\t-- IsaacOnlineModded: co-op safety\n\t" + body);

    public static IReadOnlyList<Rule> Rules { get; } = new Rule[] {
        Guard("836319872", "features/eid_bagofcrafting_search.lua",
            "function EID:BoCSSetSearchInputEnabled(newState, force)",
            $"if {Multi} then\n\t\tsearchInputEnabled = false\n\t\tEID:RemoveCallback(ModCallbacks.MC_INPUT_ACTION, EID.BoCSBlockInputAction)\n\t\treturn\n\tend"),
        Guard("836319872", "features/eid_bagofcrafting_search.lua",
            "function EID:BoCSBlockInputAction(_, inputHook, _)",
            $"if {Multi} then return nil end"),
        new("836319872", "features/eid_holdmapdesc.lua",
            "if EID.Config[\"ItemReminderDisableInputs\"] then EID.holdTabPlayer.ControlsCooldown = 2 end",
            "if Game():GetNumPlayers() <= 1 and EID.Config[\"ItemReminderDisableInputs\"] then EID.holdTabPlayer.ControlsCooldown = 2 end -- IsaacOnlineModded: co-op safety"),
        new("836319872", "features/eid_bagofcrafting.lua",
            "\t\tEID.bagPlayer.ControlsCooldown = 2",
            "\t\tif Game():GetNumPlayers() <= 1 then EID.bagPlayer.ControlsCooldown = 2 end -- IsaacOnlineModded: recipe UI must not block co-op movement"),
        Guard("3034946842", "main.lua", "function EmoteBinds:KeybindManager(player)",
            $"if {Multi} then return end"),
        new("3034946842", "scheduler.lua",
            "YourMod:AddCallback(ModCallbacks.MC_POST_GAME_END, Scheduler.Empty)",
            "YourMod:AddCallback(ModCallbacks.MC_POST_GAME_END, Scheduler.Empty)\n    -- IsaacOnlineModded: discard callbacks holding players from a previous run (R restart)\n    YourMod:AddCallback(ModCallbacks.MC_POST_GAME_STARTED, Scheduler.Empty)"),
        Guard("3701683951", "scripts/modconfig.lua", "function MCM.OpenConfigMenu()",
            $"if {Multi} then return end"),
        Guard("3701683951", "scripts/modconfig.lua", "function MCM.PostRender()",
            $"if {Multi} then MCM.IsVisible = false; return end"),
        Guard("3701683951", "scripts/modconfig.lua", "function MCM.InputAction(_, entity, inputHook, buttonAction)",
            $"if {Multi} then return nil end"),
        Guard("3701683951", "scripts/modconfig.lua", "function MCM.HandleForceActionPressed(_, entity, inputHook, buttonAction)",
            $"if {Multi} then return nil end"),
        new("2575911103", "main.lua", "for playerNum = 1, game:GetNumPlayers() do",
            "for playerNum = 0, game:GetNumPlayers() - 1 do -- IsaacOnlineModded: valid player indices"),
        new("2878352867", "main.lua", "    data.ZoneLink:Remove()",
            "    if data.ZoneLink ~= nil then data.ZoneLink:Remove() end -- IsaacOnlineModded: nil-safe Coming Down cleanup"),
        new("2878352867", "content/entities2.xml",
            "<entities anm2root=\"gfx/\" version=\"1\">",
            "<entities anm2root=\"gfx/\" version=\"5\"> <!-- IsaacOnlineModded: Repentance+ entities schema -->"),
        new("2900345009", "cuerlib/class/netcoop.lua",
            "                local info = table.remove(infos, index);\n                table.insert(infos, 0, info);",
            "                local info = table.remove(infos, index + 1); -- IsaacOnlineModded: GetPlayerIndex is zero-based\n                table.insert(infos, 1, info); -- IsaacOnlineModded: Lua tables are one-based"),
        Guard("2900345009", "cuerlib/class/netcoop.lua",
            "    local function PostGameStarted(mod, isContinued)",
            "actionRecords = {}; -- Discard input samples from the previous run.\n\tplayerCheckCooldown = MAX_RECORD_COUNT * 2;"),
    };

    public sealed record Change(string Path, byte[] Original, byte[] Patched);

    // Preflight the entire selected directory before writing anything. No third-party
    // source is bundled: rules are applied only to explicitly identified installed mods.
    public static List<Change> Plan(string modsDirectory) {
        var changes = new List<Change>();
        bool foundSupportedMod = false;
        foreach (string directory in Directory.EnumerateDirectories(modsDirectory).OrderBy(x => x, StringComparer.Ordinal)) {
            string metadata = Path.Combine(directory, "metadata.xml");
            if (!System.IO.File.Exists(metadata)) continue;
            string? id = XDocument.Load(metadata).Root?.Element("id")?.Value.Trim();
            foreach (var group in Rules.Where(r => r.WorkshopId == id).GroupBy(r => r.File)) {
                foundSupportedMod = true;
                string path = Path.Combine(directory, group.Key);
                byte[] original = System.IO.File.ReadAllBytes(path);
                bool bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
                string text = new UTF8Encoding(false, true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
                string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
                foreach (Rule rule in group) normalized = Apply(normalized, rule, path);
                string result = normalized.Replace("\n", newline, StringComparison.Ordinal);
                if (result == text) continue;
                byte[] encoded = new UTF8Encoding(false).GetBytes(result);
                byte[] patched = bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(encoded).ToArray() : encoded;
                changes.Add(new(path, original, patched));
            }
        }
        if (!foundSupportedMod)
            throw new InvalidOperationException("No supported mods found. Select the mods folder beside the isaac-ng.exe used to launch the game, not an individual mod folder. No files were patched.");
        return changes;
    }

    // Read-only evidence of the selected files; it does not prove what another PC loaded.
    public static string CreateReport(string modsDirectory) {
        var report = new StringBuilder();
        report.AppendLine("Isaac Online Modded " + typeof(ModSafetyPatcher).Assembly.GetName().Version);
        report.AppendLine("Mods folder: " + Path.GetFullPath(modsDirectory));
        report.AppendLine("disable.it is recorded when present; file presence alone does not prove a mod ran.");
        int found = 0;
        foreach (string directory in Directory.EnumerateDirectories(modsDirectory).OrderBy(x => x, StringComparer.Ordinal)) {
            string metadata = Path.Combine(directory, "metadata.xml");
            if (!System.IO.File.Exists(metadata)) continue;
            report.AppendLine();
            report.AppendLine(Path.GetFileName(directory) + (System.IO.File.Exists(Path.Combine(directory, "disable.it")) ? " [disable.it present]" : " [no disable.it]"));
            try {
                string? id = XDocument.Load(metadata).Root?.Element("id")?.Value.Trim();
                report.AppendLine("Workshop ID: " + id);
                foreach (var group in Rules.Where(r => r.WorkshopId == id).GroupBy(r => r.File)) {
                    found++;
                    string path = Path.Combine(directory, group.Key);
                    if (!System.IO.File.Exists(path)) {
                        report.AppendLine("MISSING: " + group.Key);
                        continue;
                    }
                    string text = System.IO.File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
                    int applied = 0, pending = 0, unknown = 0;
                    foreach (Rule rule in group) {
                        try {
                            if (Apply(text, rule, path) == text) applied++;
                            else pending++;
                        } catch (InvalidOperationException) { unknown++; }
                    }
                    report.AppendLine($"{group.Key}: applied={applied}, pending={pending}, unsupported={unknown}");
                }
                // Relative names and hashes allow comparison across machines without sharing mod code.
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(p => Path.GetExtension(p).Equals(".lua", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.Ordinal)) {
                    report.AppendLine("SHA256 " + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))).ToLowerInvariant()
                        + "  " + Path.GetRelativePath(directory, path));
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) {
                report.AppendLine("READ ERROR: " + ex.Message);
            }
        }
        if (found == 0) report.AppendLine("NO SUPPORTED MOD FILES FOUND. This does not mean the fixes are installed.");
        report.AppendLine("Matching files do not guarantee matching mod settings or online compatibility.");
        return report.ToString();
    }

    private static string Apply(string text, Rule rule, string path) {
        int patched = Count(text, rule.After);
        if (patched == 1 && Count(text.Replace(rule.After, "", StringComparison.Ordinal), rule.Before) == 0)
            return text;
        if (patched != 0 || Count(text, rule.Before) != 1)
            throw new InvalidOperationException($"Unsupported or ambiguous mod code in {path}. No files were patched.");
        // A partial/mangled guard must not receive another copy.
        int index = text.IndexOf(rule.Before, StringComparison.Ordinal) + rule.Before.Length;
        if (text[index..].TrimStart().StartsWith("-- IsaacOnlineModded:", StringComparison.Ordinal))
            throw new InvalidOperationException($"Incomplete safety patch in {path}. Restore its backup first.");
        return text.Replace(rule.Before, rule.After, StringComparison.Ordinal);
    }

    private static int Count(string text, string value) {
        int count = 0;
        for (int i = 0; (i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0; i += value.Length) count++;
        return count;
    }

    public static string BackupPath(Change change) => change.Path + ".iom-" +
        Convert.ToHexString(SHA256.HashData(change.Original)).ToLowerInvariant() + ".bak";

    public static void Commit(IReadOnlyList<Change> changes) {
        // Back up every input before the first replacement. Never overwrite a backup.
        foreach (Change change in changes) {
            if (!System.IO.File.ReadAllBytes(change.Path).SequenceEqual(change.Original))
                throw new IOException($"File changed since preflight: {change.Path}. Close the game and retry.");
            string backup = BackupPath(change);
            if (System.IO.File.Exists(backup)) {
                if (!System.IO.File.ReadAllBytes(backup).SequenceEqual(change.Original))
                    throw new IOException($"Backup does not match its checksum: {backup}");
            } else {
                using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write);
                stream.Write(change.Original);
            }
        }
        var written = new List<Change>();
        try {
            foreach (Change change in changes) {
                Replace(change.Path, change.Patched);
                written.Add(change);
            }
        } catch (Exception error) {
            var errors = new List<Exception> { error };
            foreach (Change change in written.AsEnumerable().Reverse()) {
                try { Replace(change.Path, change.Original); }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            throw new AggregateException("Patching failed. Rollback was attempted; original files are in .iom-*.bak backups.", errors);
        }
    }

    private static void Replace(string path, byte[] bytes) {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            System.IO.File.WriteAllBytes(temporary, bytes);
            System.IO.File.Move(temporary, path, true);
        } finally {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }
    }
}
