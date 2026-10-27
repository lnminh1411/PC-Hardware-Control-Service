using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Lasso;
using PcHwCtl.Core.Rgb;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class FakeLasso : ILassoControl
{
    public List<string> Calls { get; } = [];
    public LassoState State => new(5, 1, "brave.exe");

    public IReadOnlyList<(string Name, string Cpus)> Rules() => [("brave.exe", "0-9")];

    public LassoResult SetAffinity(string exe, string cpus)
    {
        Calls.Add($"set {exe} {cpus}");
        return new LassoResult($"{LassoIni.ExeName(exe)} on {LassoIni.NormalizeCpus(cpus, 20)}", 2);
    }

    public LassoResult ClearAffinity(string exe)
    {
        Calls.Add($"clear {exe}");
        return new LassoResult("cleared", 1);
    }

    public LassoResult Load(string profile)
    {
        Calls.Add($"load {profile}");
        LassoIni.ProfileFile(profile);
        return new LassoResult("loaded", 3);
    }

    public LassoResult Save(string profile)
    {
        Calls.Add($"save {profile}");
        return new LassoResult("saved", 0);
    }

    public IReadOnlyList<string> Profiles() => ["day", "night"];
}

public class LassoTests
{
    private const string Ini =
        "[Advanced]\r\n" +
        "DefaultAffinitiesEx=svchost.exe (netprofm -p),0,14-17,brave.exe,0,0-9,lightroom.exe,0,0-1;6-9;18-19\r\n" +
        "Other=1\r\n";

    [Fact]
    public void Test01_Masks()
    {
        Assert.Equal(0b111111L << 12, LassoIni.CoreMask("12-17"));
        Assert.Equal(0b11L | (0b1111L << 6) | (0b11L << 18), LassoIni.CoreMask("0-1;6-9;18-19"));
        Assert.Equal(0L, LassoIni.CoreMask("x"));
    }

    [Fact]
    public void Test02_InputValidation()
    {
        Assert.Equal("brave.exe", LassoIni.ExeName("Brave"));
        Assert.Equal("brave.exe", LassoIni.ExeName("brave.EXE"));
        Assert.Throws<LassoException>(() => LassoIni.ExeName(@"C:\x\brave.exe"));
        Assert.Throws<LassoException>(() => LassoIni.ExeName("a,b"));

        Assert.Equal("2-5;10-17", LassoIni.NormalizeCpus("2-5,10-17", 20));
        Assert.Equal("10-15", LassoIni.NormalizeCpus(" 10-15 ", 20));
        Assert.Throws<LassoException>(() => LassoIni.NormalizeCpus("10-25", 20));
        Assert.Throws<LassoException>(() => LassoIni.NormalizeCpus("ten", 20));

        Assert.Equal("night.ini", LassoIni.ProfileFile("night"));
        Assert.Equal("night.ini", LassoIni.ProfileFile("night.ini"));
        Assert.Throws<LassoException>(() => LassoIni.ProfileFile(@"..\..\Windows\x"));
        Assert.Throws<LassoException>(() => LassoIni.ProfileFile("C:/x"));
    }

    [Fact]
    public void Test03_SetRule_ReplacesWholeExeRuleOrAppends()
    {
        string replaced = LassoIni.SetRule(Ini, "brave.exe", "10-15");
        Assert.Equal("svchost.exe (netprofm -p),0,14-17,brave.exe,0,10-15,lightroom.exe,0,0-1;6-9;18-19",
            LassoIni.Value(replaced, "DefaultAffinitiesEx"));
        Assert.EndsWith("Other=1\r\n", replaced);

        string added = LassoIni.SetRule(Ini, "svchost.exe", "0-3");
        Assert.EndsWith(",svchost.exe,0,0-3", LassoIni.Value(added, "DefaultAffinitiesEx"));
        Assert.Contains("svchost.exe (netprofm -p),0,14-17", added); // instance rule untouched

        Assert.Equal("[Advanced]\r\nDefaultAffinitiesEx=code.exe,0,12-17\r\nA=1\r\n",
            LassoIni.SetRule("[Advanced]\r\nA=1\r\n", "code.exe", "12-17"));
    }

    [Fact]
    public void Test04_RemoveRule_AndRuleMasks()
    {
        var (text, removed) = LassoIni.RemoveRule(Ini, "brave.exe");
        Assert.True(removed);
        Assert.DoesNotContain("brave", text);
        Assert.False(LassoIni.RemoveRule(Ini, "svchost.exe").Removed); // only an instance rule

        var masks = LassoIni.RuleMasks(Ini);
        Assert.Equal(LassoIni.CoreMask("0-9"), masks["brave"]);
        Assert.False(masks.ContainsKey("svchost"));
    }

    [Fact]
    public void Test05_Ipc_AffAndLassoCommands()
    {
        var lasso = new FakeLasso();
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(), null, null, lasso);

        Assert.Contains("\"result\":\"brave.exe on 10-15\",\"changed\":2", proc.Execute("aff set brave.exe 10-15"));
        Assert.Contains("\"changed\":1", proc.Execute("AFF CLEAR brave"));
        Assert.Contains("\"cpus\":\"0-9\"", proc.Execute("aff list"));
        Assert.Contains("\"changed\":3", proc.Execute("lasso load night"));
        Assert.Contains("\"result\":\"saved\"", proc.Execute("lasso save day"));
        Assert.Contains("\"profiles\":[\"day\",\"night\"]", proc.Execute("lasso profiles"));
        Assert.Equal(["set brave.exe 10-15", "clear brave", "load night", "save day"], lasso.Calls);

        Assert.Contains("outside 0-19", proc.Execute("aff set brave 0-40"));
        Assert.Contains("not a profile name", proc.Execute("lasso load ../x"));
        Assert.Contains("Usage: aff", proc.Execute("aff set brave"));
        Assert.Contains("\"lasso\":{\"rules\":5,\"displayRequest\":1", proc.StateJson());

        var off = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock());
        Assert.Contains("Process Lasso control is off", off.Execute("aff list"));
    }
}
