using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// What a WP7 library learns about its own package: the entry assembly from
/// <c>Deployment.Current</c>, an assembly by name from <c>Assembly.Load(string)</c>, and a module's
/// file name. AE Fruit Slash's AEMobile framework and Microsoft's ad SDK use all three.
/// </summary>
public class ReflectionAndDeploymentTests
{
    [Fact]
    public void AStreamLoadedModuleIsNamedAfterItsFile()
    {
        // Game assemblies are loaded from a stream, so .NET calls their module "<Unknown>".
        byte[] bytes = File.ReadAllBytes(typeof(ReflectionAndDeploymentTests).Assembly.Location);
        var alc = new AssemblyLoadContext("module-name-test", isCollectible: true);
        try
        {
            Module module = alc.LoadFromStream(new MemoryStream(bytes)).ManifestModule;
            Assert.Equal(ReflectionModules.UnknownName, module.Name);
            Assert.Equal("WPR.SilverlightCompability.Tests.dll", ReflectionModules.GetName(module));
        }
        finally
        {
            alc.Unload();
        }
    }

    [Fact]
    public void AFileLoadedModuleKeepsItsName()
    {
        Module module = typeof(ReflectionAndDeploymentTests).Assembly.ManifestModule;
        Assert.Equal(module.Name, ReflectionModules.GetName(module));
    }

    [Fact]
    public void AssemblyLoadHandsBackAnAssemblyLoadedInAnotherContext()
    {
        // The phone had one domain. Here the game's entry assembly sits in a collectible context,
        // and binding to it by name from the Default context fails (the ad SDK's AdManager..cctor).
        string name = "WprEntryAssembly" + Guid.NewGuid().ToString("N");
        var stream = new MemoryStream();
        using (var built = Mono.Cecil.AssemblyDefinition.CreateAssembly(
                   new Mono.Cecil.AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name + ".dll", Mono.Cecil.ModuleKind.Dll))
        {
            built.Write(stream);
        }
        stream.Position = 0;

        var alc = new AssemblyLoadContext("assembly-load-test", isCollectible: true);
        try
        {
            Assembly game = alc.LoadFromStream(stream);
            Assert.Throws<FileNotFoundException>(() => Assembly.Load(name));
            Assembly found = Assembly2.Load(name + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
            Assert.Same(game, found);
        }
        finally
        {
            alc.Unload();
        }
    }

    [Fact]
    public void AssemblyLoadOfSomethingNotLoadedStillThrows()
    {
        Assert.ThrowsAny<IOException>(() => Assembly2.Load("No.Such.Assembly.Anywhere"));
    }

    [Fact]
    public void DeploymentIsReadFromTheAppManifest()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-deployment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(System.IO.Path.Combine(dir, "AppManifest.xaml"),
                "<Deployment xmlns=\"http://schemas.microsoft.com/client/2007/deployment\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" EntryPointAssembly=\"CutFruit\" EntryPointType=\"CutFruit.CutFruitGame\" RuntimeVersion=\"3.0\">"
                + "<Deployment.Parts><AssemblyPart x:Name=\"CutFruit\" Source=\"CutFruit.dll\" /><AssemblyPart x:Name=\"Library\" Source=\"Library.dll\" /></Deployment.Parts></Deployment>");

            Deployment.LoadForLaunch(dir);
            Assert.Equal("CutFruit", Deployment.Current.EntryPointAssembly);
            Assert.Equal("CutFruit.CutFruitGame", Deployment.Current.EntryPointType);
            Assert.Equal("3.0", Deployment.Current.RuntimeVersion);
            Assert.Equal(new[] { "CutFruit.dll", "Library.dll" }, Deployment.Current.Parts.Select(p => p.Source));

            Deployment.ResetForNewLaunch();
            Assert.Null(Deployment.Current.EntryPointAssembly);
            Assert.Empty(Deployment.Current.Parts);
        }
        finally
        {
            Deployment.ResetForNewLaunch();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoManifestLeavesDeploymentEmpty()
    {
        Deployment.LoadForLaunch(System.IO.Path.GetTempPath());
        Assert.Null(Deployment.Current.EntryPointAssembly);
    }
}
