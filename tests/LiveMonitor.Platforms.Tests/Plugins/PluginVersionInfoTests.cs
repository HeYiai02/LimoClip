using System.Reflection;
using System.Reflection.Emit;
using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Plugins;

public sealed class PluginVersionInfoTests
{
    private static readonly ConstructorInfo InformationalVersionCtor =
        typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!;

    [Fact]
    public void Resolve_TypeNull_Throws()
        => Assert.Throws<ArgumentNullException>(() => PluginVersionInfo.Resolve((Type)null!));

    [Fact]
    public void Resolve_AssemblyNull_Throws()
        => Assert.Throws<ArgumentNullException>(() => PluginVersionInfo.Resolve((Assembly)null!));

    [Fact]
    public void Resolve_AssemblyWithoutInformationalVersion_FallsBackToAssemblyVersion()
    {
        var assembly = BuildAssembly("VersionOnly", new Version(2, 3, 4), informationalVersion: null);

        Assert.Equal("2.3.4", PluginVersionInfo.Resolve(assembly));
    }

    [Fact]
    public void Resolve_AssemblyWithoutAnyVersion_ReturnsFallback()
    {
        var assembly = BuildAssembly("NoVersion", version: null, informationalVersion: null);

        Assert.Equal(PluginVersionInfo.FallbackVersion, PluginVersionInfo.Resolve(assembly));
    }

    [Theory]
    [InlineData("3.1.4+sha256abcdef", "3.1.4")]
    [InlineData("3.1.4", "3.1.4")]
    [InlineData(" 4.0.0 ", "4.0.0")]
    [InlineData("1.2.3+", "1.2.3")]
    public void Resolve_InformationalVersion_StripsBuildMetadataAndWhitespace(
        string informationalVersion,
        string expected)
    {
        var assembly = BuildAssembly("Informational", new Version(0, 0, 0), informationalVersion);

        Assert.Equal(expected, PluginVersionInfo.Resolve(assembly));
    }

    [Fact]
    public void Resolve_LeadingPlusIsNotTreatedAsMetadataSeparator()
    {
        // 只有 + 出现在非首位时才视为构建元数据分隔符。
        var assembly = BuildAssembly("LeadingPlus", new Version(0, 0, 0), "+suffix");

        Assert.Equal("+suffix", PluginVersionInfo.Resolve(assembly));
    }

    [Fact]
    public void Resolve_WhitespaceInformationalVersion_FallsBackToAssemblyVersion()
    {
        var assembly = BuildAssembly("BlankInformational", new Version(5, 6, 7), "   ");

        Assert.Equal("5.6.7", PluginVersionInfo.Resolve(assembly));
    }

    [Fact]
    public void Resolve_TypeOverload_UsesContainingAssembly()
    {
        var resolved = PluginVersionInfo.Resolve(typeof(PluginVersionComparer));

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.DoesNotContain('+', resolved);
    }

    private static Assembly BuildAssembly(string name, Version? version, string? informationalVersion)
    {
        var assemblyName = new AssemblyName(name);
        if (version is not null)
        {
            assemblyName.Version = version;
        }

        var builder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        if (informationalVersion is not null)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                InformationalVersionCtor,
                new object[] { informationalVersion }));
        }

        return builder;
    }
}
