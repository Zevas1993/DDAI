using System.Text;

namespace DDAI.App.Tests;

public sealed class DungeondraftConfigEditorTests
{
    private const string ManagedModsDirectory = @"C:\Users\Chris\AppData\Local\DDAI\DungeondraftMods";

    [Fact]
    public void PlanSetup_PreservesCustomSnapCommentsUtf8BomAndCrLf()
    {
        const string text = "; keep\r\n[Display]\r\nwindow_width=1920\r\n\r\n[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\", \"Other.Mod\" ]\r\nmods_directory=\"D:\\\\OldMods\"\r\n";
        var preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
        var original = preamble.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        var edit = DungeondraftConfigEditor.PlanSetup(original, ManagedModsDirectory);
        var result = Encoding.UTF8.GetString(edit.ReplacementBytes.AsSpan(preamble.Length));

        Assert.True(edit.Changed);
        Assert.True(edit.ReplacementBytes.AsSpan().StartsWith(preamble));
        Assert.Contains("; keep\r\n[Display]\r\nwindow_width=1920", result);
        Assert.Contains("active_mods=[ \"Lievven.Snappy_Mod\", \"Other.Mod\", \"org.ddai.status_bridge\" ]\r\n", result);
        Assert.Contains("mods_directory=\"C:\\\\Users\\\\Chris\\\\AppData\\\\Local\\\\DDAI\\\\DungeondraftMods\"\r\n", result);
        Assert.True(DungeondraftConfigEditor.IsConfigured(edit.ReplacementBytes, ManagedModsDirectory));

        var second = DungeondraftConfigEditor.PlanSetup(edit.ReplacementBytes, ManagedModsDirectory);
        Assert.False(second.Changed);
        Assert.Equal(edit.ReplacementBytes, second.ReplacementBytes);
    }

    [Fact]
    public void PlanSetup_InsertsMissingModsSectionWithoutChangingLfConvention()
    {
        var original = Encoding.UTF8.GetBytes("[Display]\nwindow_width=1920\n");

        var edit = DungeondraftConfigEditor.PlanSetup(original, @"C:\DDAI\Mods");

        Assert.Equal(
            "[Display]\nwindow_width=1920\n\n[Mods]\nactive_mods=[ \"org.ddai.status_bridge\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n",
            Encoding.UTF8.GetString(edit.ReplacementBytes));
    }

    [Fact]
    public void PlanSetup_InsertsOnlyMissingOwnedKeyInsideExistingSection()
    {
        var original = Encoding.UTF8.GetBytes("[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\nother_setting=true\n");

        var edit = DungeondraftConfigEditor.PlanSetup(original, @"C:\DDAI\Mods");

        Assert.Equal(
            "[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\", \"org.ddai.status_bridge\" ]\nother_setting=true\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n",
            Encoding.UTF8.GetString(edit.ReplacementBytes));
    }

    [Fact]
    public void PlanSetup_InsertsOwnedKeysBeforeTheNextSection()
    {
        var original = Encoding.UTF8.GetBytes("[Mods]\nother_setting=true\n[Display]\nwindow_width=1920\n");

        var edit = DungeondraftConfigEditor.PlanSetup(original, @"C:\DDAI\Mods");

        Assert.Equal(
            "[Mods]\nother_setting=true\nactive_mods=[ \"org.ddai.status_bridge\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n[Display]\nwindow_width=1920\n",
            Encoding.UTF8.GetString(edit.ReplacementBytes));
    }

    [Theory]
    [InlineData("[Mods]\nactive_mods=[ bare ]\nmods_directory=\"C:\\\\Mods\"\n")]
    [InlineData("[Mods]\nactive_mods=[ \"A\" ]\nactive_mods=[ \"B\" ]\nmods_directory=\"C:\\\\Mods\"\n")]
    [InlineData("[Mods]\nmods_directory=\"C:\\\\Mods\"\n[Mods]\nactive_mods=[ ]\n")]
    [InlineData("[Mods]\nactive_mods=[ \"A\", ]\nmods_directory=\"C:\\\\Mods\"\n")]
    [InlineData("[Mods]\nactive_mods=[ \"A\" ] trailing\nmods_directory=\"C:\\\\Mods\"\n")]
    [InlineData("[Mods]\nactive_mods=[ \"A\\q\" ]\nmods_directory=\"C:\\\\Mods\"\n")]
    public void PlanSetup_RejectsAmbiguousOwnedSyntax(string text)
    {
        Assert.Throws<DungeondraftConfigException>(() =>
            DungeondraftConfigEditor.PlanSetup(Encoding.UTF8.GetBytes(text), @"C:\DDAI\Mods"));
    }

    [Fact]
    public void PlanSetup_RejectsMixedNewlinesAndInvalidUtf8()
    {
        Assert.Throws<DungeondraftConfigException>(() =>
            DungeondraftConfigEditor.PlanSetup(Encoding.UTF8.GetBytes("[Mods]\r\nactive_mods=[ ]\n"), @"C:\DDAI\Mods"));
        Assert.Throws<DungeondraftConfigException>(() =>
            DungeondraftConfigEditor.PlanSetup([0x5B, 0x4D, 0x6F, 0x64, 0x73, 0x5D, 0x0A, 0xC3, 0x28], @"C:\DDAI\Mods"));
    }

    [Fact]
    public void PlanSetup_CollapsesOnlyDuplicateDdaiIds()
    {
        var original = Encoding.UTF8.GetBytes(
            "[Mods]\nactive_mods=[ \"Other.Mod\", \"Other.Mod\", \"org.ddai.status_bridge\", \"org.ddai.status_bridge\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n");

        var edit = DungeondraftConfigEditor.PlanSetup(original, @"C:\DDAI\Mods");

        Assert.Contains(
            "active_mods=[ \"Other.Mod\", \"Other.Mod\", \"org.ddai.status_bridge\" ]",
            Encoding.UTF8.GetString(edit.ReplacementBytes));
    }

    [Fact]
    public void PlanUninstall_RemovesOnlyDdaiAndRestoresProvenPriorDirectory()
    {
        var original = Encoding.UTF8.GetBytes(
            "[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\", \"org.ddai.status_bridge\", \"Other.Mod\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n");
        var ownership = new DungeondraftConfigOwnership(
            @"C:\DDAI\Mods",
            PreviousModsDirectoryPresent: true,
            PreviousModsDirectoryLiteral: "\"D:\\\\PreviousMods\"",
            DungeondraftConfigEditor.DdaiModId);

        var edit = DungeondraftConfigEditor.PlanUninstall(original, ownership);
        var result = Encoding.UTF8.GetString(edit.ReplacementBytes);

        Assert.Contains("active_mods=[ \"Lievven.Snappy_Mod\", \"Other.Mod\" ]", result);
        Assert.Contains("mods_directory=\"D:\\\\PreviousMods\"", result);
    }

    [Fact]
    public void PlanUninstall_PreservesUserChangedDirectoryAndNonDdaiDuplicates()
    {
        var original = Encoding.UTF8.GetBytes(
            "[Mods]\nactive_mods=[ \"Other.Mod\", \"org.ddai.status_bridge\", \"Other.Mod\" ]\nmods_directory=\"E:\\\\UserChanged\"\n");
        var ownership = new DungeondraftConfigOwnership(
            @"C:\DDAI\Mods",
            PreviousModsDirectoryPresent: true,
            PreviousModsDirectoryLiteral: "\"D:\\\\PreviousMods\"",
            DungeondraftConfigEditor.DdaiModId);

        var edit = DungeondraftConfigEditor.PlanUninstall(original, ownership);
        var result = Encoding.UTF8.GetString(edit.ReplacementBytes);

        Assert.Contains("active_mods=[ \"Other.Mod\", \"Other.Mod\" ]", result);
        Assert.Contains("mods_directory=\"E:\\\\UserChanged\"", result);
    }

    [Fact]
    public void PlanUninstall_RemovesOwnedDirectoryKeyWhenItWasPreviouslyAbsent()
    {
        var original = Encoding.UTF8.GetBytes(
            "[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\", \"org.ddai.status_bridge\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\nother_setting=true\n");
        var ownership = new DungeondraftConfigOwnership(
            @"C:\DDAI\Mods",
            PreviousModsDirectoryPresent: false,
            PreviousModsDirectoryLiteral: null,
            DungeondraftConfigEditor.DdaiModId);

        var edit = DungeondraftConfigEditor.PlanUninstall(original, ownership);

        Assert.Equal(
            "[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\nother_setting=true\n",
            Encoding.UTF8.GetString(edit.ReplacementBytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanSetup_RoundTripsUtf16Bom(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
        const string text = "[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\r\nmods_directory=\"D:\\\\Old\"\r\n";
        var original = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var edit = DungeondraftConfigEditor.PlanSetup(original, @"C:\DDAI\Mods");

        Assert.True(edit.ReplacementBytes.AsSpan().StartsWith(encoding.GetPreamble()));
        var result = encoding.GetString(edit.ReplacementBytes.AsSpan(encoding.GetPreamble().Length));
        Assert.Contains("Lievven.Snappy_Mod", result);
        Assert.Contains("org.ddai.status_bridge", result);
        Assert.Contains("mods_directory=\"C:\\\\DDAI\\\\Mods\"", result);
    }

    [Fact]
    public void IsConfigured_ReturnsFalseWhenEitherOwnedValueDoesNotMatch()
    {
        var wrongDirectory = Encoding.UTF8.GetBytes(
            "[Mods]\nactive_mods=[ \"org.ddai.status_bridge\" ]\nmods_directory=\"D:\\\\Other\"\n");
        var missingMod = Encoding.UTF8.GetBytes(
            "[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n");

        Assert.False(DungeondraftConfigEditor.IsConfigured(wrongDirectory, @"C:\DDAI\Mods"));
        Assert.False(DungeondraftConfigEditor.IsConfigured(missingMod, @"C:\DDAI\Mods"));
    }
}
