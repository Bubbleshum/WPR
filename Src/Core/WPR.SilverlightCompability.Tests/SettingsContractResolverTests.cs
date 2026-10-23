using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// <see cref="IsolatedStorageSettings2"/> saves each value's type as a known type and reads with
/// <see cref="LoadedTypeContractResolver"/>, which has no list. A setting holding a game's own
/// class must come back: iRunner keeps its whole save in one (<c>Classes.Model.AppSetting</c>).
/// </summary>
public class SettingsContractResolverTests
{
    [Fact]
    public void A_settings_document_with_a_game_class_reads_back()
    {
        var settings = new Dictionary<string, object>
        {
            ["AppSetting"] = new GameSetting { Coins = 100, Name = "runner" },
            ["Plain"] = 7,
        };

        var stream = new MemoryStream();
        new DataContractSerializer(typeof(Dictionary<string, object>), new[] { typeof(GameSetting) })
            .WriteObject(stream, settings);
        stream.Position = 0;

        var read = (Dictionary<string, object>)new DataContractSerializer(
                typeof(Dictionary<string, object>),
                new DataContractSerializerSettings { DataContractResolver = new LoadedTypeContractResolver() })
            .ReadObject(stream)!;

        var setting = Assert.IsType<GameSetting>(read["AppSetting"]);
        Assert.Equal(100, setting.Coins);
        Assert.Equal("runner", setting.Name);
        Assert.Equal(7, read["Plain"]);
    }

    [Fact]
    public void Collections_of_framework_types_read_back()
    {
        // Gravity Guy 2's settings hold a Dictionary<string, int> and a Dictionary<string, Guid>
        // beside its save. Their contracts (ArrayOfKeyValueOfstringint, ...Ofstringguid) are named
        // in the Arrays namespace and defined by no loaded type, so the read failed and the whole
        // file, save included, was thrown away at every launch.
        var settings = new Dictionary<string, object>
        {
            ["Counts"] = new Dictionary<string, int> { ["a"] = 1 },
            ["Ids"] = new Dictionary<string, System.Guid> { ["b"] = System.Guid.Empty },
            ["Nested"] = new Dictionary<string, object> { ["n"] = 2 },
            ["Scores"] = new List<int> { 3, 4 },
            ["Names"] = new[] { "x", "y" },
        };

        var stream = new MemoryStream();
        new DataContractSerializer(typeof(Dictionary<string, object>),
                new[] { typeof(Dictionary<string, int>), typeof(Dictionary<string, System.Guid>), typeof(List<int>), typeof(string[]) })
            .WriteObject(stream, settings);
        stream.Position = 0;

        var read = (Dictionary<string, object>)new DataContractSerializer(
                typeof(Dictionary<string, object>),
                new DataContractSerializerSettings { DataContractResolver = new LoadedTypeContractResolver() })
            .ReadObject(stream)!;

        Assert.Equal(1, Assert.IsType<Dictionary<string, int>>(read["Counts"])["a"]);
        Assert.Equal(System.Guid.Empty, Assert.IsType<Dictionary<string, System.Guid>>(read["Ids"])["b"]);
        Assert.Equal(2, Assert.IsType<Dictionary<string, object>>(read["Nested"])["n"]);
        Assert.Equal(new[] { 3, 4 }, (IEnumerable<int>)read["Scores"]);
        Assert.Equal(new[] { "x", "y" }, (IEnumerable<string>)read["Names"]);
    }

    [Fact]
    public void A_named_contract_is_found_by_its_attribute()
    {
        Assert.Equal(typeof(RenamedSetting), LoadedTypeContractResolver.Find("Renamed", "urn:wpr-test"));
        Assert.Equal(typeof(GameSetting), LoadedTypeContractResolver.Find(
            "SettingsContractResolverTests.GameSetting", "http://schemas.datacontract.org/2004/07/WPR.SilverlightCompability.Tests"));
        Assert.Null(LoadedTypeContractResolver.Find("NoSuchContract", "urn:wpr-test"));
    }

    public class GameSetting
    {
        public int Coins { get; set; }

        public string? Name { get; set; }
    }

    [DataContract(Name = "Renamed", Namespace = "urn:wpr-test")]
    public class RenamedSetting
    {
        [DataMember]
        public int Value { get; set; }
    }
}
