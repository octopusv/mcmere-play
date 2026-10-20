using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public static class RuntimeCatalog
{
    public static readonly RuntimeArtifact Prism = new("prism-11.1.0-win-x64", "11.1.0",
        "https://github.com/PrismLauncher/PrismLauncher/releases/download/11.1.0/PrismLauncher-Windows-MinGW-w64-Portable-11.1.0.zip",
        "2bf5e879ea1c3f6a1aaaa43539667ce296308abf3e6a984d5cc4c48bfe3c431c", 43926838, "prismlauncher.exe",
        "https://prismlauncher.org/wiki/overview/copying/");
    public static readonly RuntimeArtifact Java21 = new("temurin-21-d35f31e712f0", "21.0.12.1+1-LTS",
        "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip",
        "d35f31e712f0fcf6ac5a093edc90204fbff22f720ba3950bd09d331d5e621636", 48999141, "jdk-21.0.12.1+1-jre/bin/java.exe",
        "https://openjdk.org/legal/gplv2+ce.html");
    public static readonly RuntimeArtifact Java25 = new("temurin-25-4c95451cea98", "25.0.4.1+1-LTS",
        "https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4.1%2B1/OpenJDK25U-jre_x64_windows_hotspot_25.0.4.1_1.zip",
        "4c95451cea98556def2c54f7782933f52a26d4a36bd85e1d59f0364464828b07", 58475080, "jdk-25.0.4.1+1-jre/bin/java.exe",
        "https://openjdk.org/legal/gplv2+ce.html");
    public static RuntimeArtifact Java(JavaRequirement requirement)
    {
        var artifact = requirement.Major switch { 21 => Java21, 25 => Java25, _ => null };
        if (artifact is null || requirement.Architecture != "x64" || requirement.RuntimeCatalogId != artifact.Id)
            throw new DistributionException("runtime_catalog_required", "このJava構成に対応するアプリの更新が必要です。");
        return artifact;
    }
}
