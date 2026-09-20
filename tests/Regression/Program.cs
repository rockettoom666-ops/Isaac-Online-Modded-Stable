using IsaacModInstaller;
using System.Reflection;
using System.Text;

if (args.Length == 2 && args[0] == "--patch-fixture-directory") {
    var plan = ModSafetyPatcher.Plan(args[1]);
    ModSafetyPatcher.Commit(plan);
    Console.WriteLine($"Patched {plan.Count} fixture files.");
    return;
}

int checks = 0;
void Check(bool condition, string name) {
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
void Reject(Action action, string name) {
    try { action(); } catch (Exception) { checks++; Console.WriteLine("PASS " + name); return; }
    throw new Exception("Expected rejection: " + name);
}
string root = Path.Combine(Path.GetTempPath(), "iom-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    foreach (bool bom in new[] { false, true }) foreach (string newline in new[] { "\n", "\r\n" }) {
        string mods = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mods);
        foreach (var mod in ModSafetyPatcher.Rules.GroupBy(r => r.WorkshopId)) {
            string folder = Path.Combine(mods, mod.Key);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "metadata.xml"), $"<metadata><id>{mod.Key}</id></metadata>");
            foreach (var file in mod.GroupBy(r => r.File)) {
                string path = Path.Combine(folder, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Join(newline, file.Select(r => r.Before)) + newline, new UTF8Encoding(bom));
            }
        }
        var changes = ModSafetyPatcher.Plan(mods);
        Check(changes.Count == 10, "preflight plans every supported file");
        string beforeReport = ModSafetyPatcher.CreateReport(mods);
        Check(beforeReport.Contains("pending=1") && !beforeReport.Contains("unsupported=1"), "report identifies pending fixes without writing");
        foreach (var c in changes) Check(File.ReadAllBytes(c.Path).SequenceEqual(c.Original), "preflight is read-only");
        ModSafetyPatcher.Commit(changes);
        Check(ModSafetyPatcher.Plan(mods).Count == 0, "second patch is a no-op");
        Check(!ModSafetyPatcher.CreateReport(mods).Contains("pending=1"), "report reflects applied fixes");
        foreach (var c in changes) {
            Check(File.ReadAllBytes(ModSafetyPatcher.BackupPath(c)).SequenceEqual(c.Original), "backup is byte-exact");
            Check(File.ReadAllBytes(c.Path).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) == bom, "BOM preserved");
            var text = File.ReadAllText(c.Path);
            Check(newline == "\r\n" ? !text.Replace("\r\n", "").Contains('\n') : !text.Contains('\r'), "line endings preserved");
        }
        var first = changes[0];
        File.WriteAllBytes(first.Path, first.Original);
        Check(ModSafetyPatcher.Plan(mods).Count == 1, "partial installation is repairable");
        File.WriteAllBytes(first.Path, first.Original.Concat(first.Original).ToArray());
        Reject(() => ModSafetyPatcher.Plan(mods), "duplicate anchors rejected");
        File.WriteAllText(first.Path, "unsupported update");
        Reject(() => ModSafetyPatcher.Plan(mods), "unknown mod version rejected");
        Check(ModSafetyPatcher.CreateReport(mods).Contains("unsupported=1"), "report identifies unsupported mod code");
        File.WriteAllBytes(first.Path, first.Original);
        var plan = ModSafetyPatcher.Plan(mods);
        File.AppendAllText(first.Path, "changed by workshop");
        Reject(() => ModSafetyPatcher.Commit(plan), "stale preflight rejected");
        File.WriteAllBytes(first.Path, first.Original);
        File.WriteAllText(ModSafetyPatcher.BackupPath(first), "corrupt backup");
        Reject(() => ModSafetyPatcher.Commit(plan), "corrupt backup rejected");
        Check(File.ReadAllBytes(first.Path).SequenceEqual(first.Original), "backup failure leaves source untouched");
    }

    string emptyMods = Path.Combine(root, "empty-mods");
    Directory.CreateDirectory(emptyMods);
    Reject(() => ModSafetyPatcher.Plan(emptyMods), "empty or wrong folder never reports already patched");
    Check(ModSafetyPatcher.CreateReport(emptyMods).Contains("NO SUPPORTED MOD FILES FOUND"), "empty report explains missing mods");

    // An unsupported analytics signature used to leave co-op half-applied on disk.
    byte[] Pattern(string field) => (byte[])typeof(GamePatcher).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    string exe = Path.Combine(root, "synthetic.exe");
    byte[] coop = Pattern("CoopOriginal"), analytics = Pattern("AnalyticsOriginal");
    File.WriteAllBytes(exe, coop);
    Reject(() => GamePatcher.PatchGameWithAnalytics(exe), "missing analytics signature rejected");
    Check(File.ReadAllBytes(exe).SequenceEqual(coop), "failed binary patch is atomic");
    File.WriteAllBytes(exe, coop.Concat(analytics).Concat(analytics).ToArray());
    byte[] ambiguous = File.ReadAllBytes(exe);
    Reject(() => GamePatcher.PatchGameWithAnalytics(exe), "ambiguous analytics signature rejected");
    Check(File.ReadAllBytes(exe).SequenceEqual(ambiguous), "ambiguous binary unchanged");
    File.WriteAllBytes(exe, coop.Concat(analytics).ToArray());
    Check(GamePatcher.PatchGameWithAnalytics(exe), "both binary regions patched");
    Check(!GamePatcher.PatchGameWithAnalytics(exe), "binary patch idempotent");
    Check(GamePatcher.GetCoopPatchStatus(exe) == PatchStatus.Patched, "binary status agrees");
    Check(File.ReadAllBytes(exe).SequenceEqual(Pattern("CoopPatched").Concat(Pattern("AnalyticsPatched"))), "exact expected binary bytes");

    string eid = Path.Combine(root, "eid");
    Directory.CreateDirectory(Path.Combine(eid, "features"));
    string api = Path.Combine(eid, "features/eid_api.lua");
    File.WriteAllText(api, "if player == nil then\nreturn listUpdatedForPlayers -- dont evaluate when bad data is present\nend\n", new UTF8Encoding(true));
    Check(EIDPatcher.Patch(eid), "legacy EID stage protection still applies");
    Check(!EIDPatcher.Patch(eid), "legacy EID patch idempotent");
    Check(EIDPatcher.GetPatchStatus(eid) == PatchStatus.Patched, "legacy EID status");
    Console.WriteLine($"All {checks} checks passed.");
} finally { Directory.Delete(root, true); }
